namespace RIoT2.Net.Orchestrator.Services.Outbox
{
    public enum DeliveryOutcome
    {
        /// <summary>The receiver accepted the message.</summary>
        Delivered,

        /// <summary>A transient failure; the message is retried with backoff until it expires.</summary>
        Retry,

        /// <summary>The receiver rejected the message; it is never retried.</summary>
        Failed
    }

    public readonly record struct DeliveryResult(DeliveryOutcome Outcome, string Detail = null)
    {
        public static DeliveryResult Delivered(string result = null) => new(DeliveryOutcome.Delivered, result);
        public static DeliveryResult Retry(string error) => new(DeliveryOutcome.Retry, error);
        public static DeliveryResult Failed(string error) => new(DeliveryOutcome.Failed, error);
    }

    /// <summary>
    /// Delivers one target's pending outbox messages, oldest first (design 7.1). One dispatcher runs per
    /// target, so deliveries to a target keep their order. A transient failure is retried after 1 s,
    /// 2 s, 4 s … at most 30 s; a message whose time-to-live passes is marked <c>expired</c> and never
    /// retried. While the target is unavailable the messages wait, subject to their time-to-live.
    /// </summary>
    public sealed class OutboxDispatcher
    {
        public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);
        private static readonly TimeSpan ExpiryLogInterval = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan ErrorWait = TimeSpan.FromSeconds(5);

        private readonly OutboxStore _store;
        private readonly string _target;
        private readonly string _description;
        private readonly Func<bool> _isAvailable;
        private readonly Func<OutboxMessage, CancellationToken, Task<DeliveryResult>> _deliver;
        private readonly ILogger _logger;
        private readonly TimeProvider _time;
        private readonly SemaphoreSlim _signal = new(0, 1);
        private long _nextCleanup;
        private long _nextExpiryLog;
        private int _unloggedExpired;
        private OutboxMessage _lastExpired;
        private bool _waitingLogged;

        /// <param name="store">The outbox.</param>
        /// <param name="target">The target whose messages this dispatcher delivers.</param>
        /// <param name="description">What a message is, for log messages, e.g. "workflow trigger".</param>
        /// <param name="isAvailable">Whether the target can be reached now; while it can't, messages wait.</param>
        /// <param name="deliver">Delivers one message. An exception counts as <see cref="DeliveryOutcome.Retry"/>.</param>
        public OutboxDispatcher(OutboxStore store, string target, string description, Func<bool> isAvailable,
            Func<OutboxMessage, CancellationToken, Task<DeliveryResult>> deliver, ILogger logger, TimeProvider time)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _target = target ?? throw new ArgumentNullException(nameof(target));
            _description = description;
            _isAvailable = isAvailable ?? throw new ArgumentNullException(nameof(isAvailable));
            _deliver = deliver ?? throw new ArgumentNullException(nameof(deliver));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _time = time ?? throw new ArgumentNullException(nameof(time));
        }

        /// <summary>The delay before the attempt that follows failed attempt number <paramref name="attempts"/>.</summary>
        public static TimeSpan Backoff(int attempts) =>
            attempts <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempts - 1), MaxBackoff.TotalSeconds));

        /// <summary>Wakes the dispatcher: a message was added or the target may have become available.</summary>
        public void Signal()
        {
            try
            {
                _signal.Release();
            }
            catch (SemaphoreFullException)
            {
                // Already signalled.
            }
        }

        /// <summary>The target announced itself: retry its waiting messages now instead of after their backoff.</summary>
        public void TargetCameOnline()
        {
            _store.ResetBackoff(_target, Now);
            Signal();
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TimeSpan wait;
                try
                {
                    wait = await StepAsync(cancellationToken);
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception x)
                {
                    _logger.LogError(x, "The outbox dispatcher for {Target} failed; it continues in {Seconds} s", _target, ErrorWait.TotalSeconds);
                    wait = ErrorWait;
                }
                try
                {
                    await _signal.WaitAsync(wait, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        /// <summary>Does everything that is due now and returns how long to wait unless signalled.</summary>
        internal async Task<TimeSpan> StepAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                var now = Now;
                Expire(now);
                Cleanup(now);
                var earliestExpiry = _store.EarliestExpiry(_target);
                if (earliestExpiry == null)
                {
                    _waitingLogged = false;
                    return MaxWait;
                }
                if (!_isAvailable())
                {
                    if (!_waitingLogged)
                    {
                        _waitingLogged = true;
                        _logger.LogInformation("{Description}s for {Target} wait until it is available, each until its time-to-live passes",
                            Capitalize(_description), _target);
                    }
                    return Until(earliestExpiry.Value, now);
                }
                _waitingLogged = false;
                var next = _store.NextPending(_target);
                if (next == null)
                    return MaxWait;
                if (next.NextAttemptUtc > now)
                    return Until(Math.Min(earliestExpiry.Value, next.NextAttemptUtc), now);
                await DeliverAsync(next, cancellationToken);
            }
        }

        private async Task DeliverAsync(OutboxMessage message, CancellationToken cancellationToken)
        {
            DeliveryResult result;
            try
            {
                result = await _deliver(message, cancellationToken);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                // Stopping: the message stays pending and is delivered after the restart.
                throw;
            }
            catch (Exception x)
            {
                result = DeliveryResult.Retry(x.GetBaseException().Message);
            }

            var now = Now;
            switch (result.Outcome)
            {
                case DeliveryOutcome.Delivered:
                    _store.Complete(message.Id, OutboxStatus.Done, now, result.Detail);
                    if (message.Attempts > 0)
                        _logger.LogInformation("{Description} {MessageId} for {Subject} delivered to {Target} after {Attempts} failed attempts",
                            Capitalize(_description), message.Id, message.Subject, _target, message.Attempts);
                    break;
                case DeliveryOutcome.Failed:
                    _store.Complete(message.Id, OutboxStatus.Failed, now, error: result.Detail);
                    _logger.LogError("{Description} {MessageId} for {Subject} was rejected by {Target} and is not retried: {Error}",
                        Capitalize(_description), message.Id, message.Subject, _target, result.Detail);
                    break;
                default:
                    var attempts = message.Attempts + 1;
                    var delay = Backoff(attempts);
                    _store.ScheduleRetry(message.Id, attempts, now + (long)delay.TotalMilliseconds, now, result.Detail);
                    _logger.LogWarning("{Description} {MessageId} for {Subject} could not be delivered to {Target} (attempt {Attempts}); retrying in {Delay} s: {Error}",
                        Capitalize(_description), message.Id, message.Subject, _target, attempts, delay.TotalSeconds, result.Detail);
                    break;
            }
        }

        // Expiries are logged at most once a minute, so a long outage doesn't log every message.
        private void Expire(long now)
        {
            var expired = _store.ExpireDue(_target, now);
            if (expired.Count > 0)
            {
                _unloggedExpired += expired.Count;
                _lastExpired = expired[^1];
            }
            if (_unloggedExpired == 0 || now < _nextExpiryLog)
                return;
            _logger.LogWarning("{Count} {Description}(s) for {Target} expired undelivered; the last one, {MessageId} for {Subject}, after {Attempts} attempts. Last error: {Error}",
                _unloggedExpired, _description, _target, _lastExpired.Id, _lastExpired.Subject, _lastExpired.Attempts, _lastExpired.LastError ?? "none");
            _unloggedExpired = 0;
            _nextExpiryLog = now + (long)ExpiryLogInterval.TotalMilliseconds;
        }

        private void Cleanup(long now)
        {
            if (now < _nextCleanup)
                return;
            _nextCleanup = now + (long)CleanupInterval.TotalMilliseconds;
            var deleted = _store.DeleteFinishedBefore(now - (long)OutboxStore.Retention.TotalMilliseconds);
            if (deleted > 0)
                _logger.LogDebug("Deleted {Count} outbox rows that were final for more than {Hours} hours", deleted, OutboxStore.Retention.TotalHours);
        }

        // Wakes up at the given time, or after at most a minute (expiry logging and cleanup are periodic).
        // Rows that expire on the step that wakes up are logged then.
        private static TimeSpan Until(long wakeAt, long now)
        {
            var wait = TimeSpan.FromMilliseconds(Math.Max(1, wakeAt - now));
            return wait < MaxWait ? wait : MaxWait;
        }

        private long Now => _time.GetUtcNow().ToUnixTimeMilliseconds();

        private static string Capitalize(string text) =>
            string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text[1..];
    }
}
