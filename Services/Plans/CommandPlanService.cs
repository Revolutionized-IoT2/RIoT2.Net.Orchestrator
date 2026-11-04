using System.Text.Json;
using RIoT2.Net.Orchestrator.Services.Commands;
using RIoT2.Net.Orchestrator.Services.Tools;

namespace RIoT2.Net.Orchestrator.Services.Plans
{
    /// <summary>The answer to a new plan: the plan, or why it was refused.</summary>
    public sealed record CommandPlanResult(CommandPlan? Plan, string? Error, bool UnknownCommand = false)
    {
        public bool Succeeded => Plan != null;
    }

    /// <summary>Command plans (ADR 0019): schedules of a command's future values that the orchestrator carries out.</summary>
    public interface ICommandPlanService
    {
        /// <summary>
        /// Checks and starts a plan; it replaces the command's active plan. <paramref name="defaultSource"/> is used when the
        /// request names no source.
        /// </summary>
        CommandPlanResult Submit(CommandPlanRequest request, string? defaultSource = null);

        CommandPlan? Get(string id);

        /// <summary>Newest first, at most <paramref name="limit"/> (1-500).</summary>
        IReadOnlyList<CommandPlan> List(string? commandId = null, bool activeOnly = false, int limit = 100);

        /// <summary>Ends the plan; <c>null</c> when there is none with the id.</summary>
        CommandPlan? Cancel(string id);

        /// <summary>Ends the command's active plan; <c>null</c> when it has none.</summary>
        CommandPlan? CancelActive(string commandId);
    }

    /// <summary>
    /// Carries out the command plans: at each step's time it sends the step's value through the command outbox, and follows
    /// the delivery to its final status. When several steps are due at once (after a restart, or steps in the past) only the
    /// latest is sent; the others are <c>skipped</c>, or with <see cref="CommandPlanCatchUp.None"/> a step more than
    /// <see cref="LateGrace"/> late is <c>missed</c>. Plans are kept in <see cref="CommandPlanStore"/>.
    /// </summary>
    public sealed class CommandPlanService : BackgroundService, ICommandPlanService
    {
        public const int MaxSteps = 200;
        public const int MaxTextLength = 200;
        public static readonly TimeSpan Horizon = TimeSpan.FromDays(7);
        public static readonly TimeSpan PastLimit = TimeSpan.FromHours(24);
        public static readonly TimeSpan MinSpacing = TimeSpan.FromMinutes(1);
        public static readonly TimeSpan LateGrace = TimeSpan.FromMinutes(2);
        public static readonly TimeSpan MinTtl = TimeSpan.FromSeconds(30);
        public static readonly TimeSpan MaxTtl = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan DeliveryPoll = TimeSpan.FromSeconds(1);

        private readonly CommandPlanStore _store;
        private readonly IOrchestratorConfigurationService _configuration;
        private readonly ICommandOutbox _commands;
        private readonly TimeProvider _time;
        private readonly ILogger<CommandPlanService> _logger;
        private readonly Lock _gate = new();
        private readonly Dictionary<string, CommandPlan> _live = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _wake = new(0, 1);
        private long _nextPrune;

        public CommandPlanService(CommandPlanStore store, IOrchestratorConfigurationService configuration, ICommandOutbox commands, TimeProvider time,
            ILogger<CommandPlanService> logger)
        {
            _store = store;
            _configuration = configuration;
            _commands = commands;
            _time = time;
            _logger = logger;
            lock (_gate)
                foreach (var plan in _store.Live())
                    _live[plan.Id] = plan;
        }

        public CommandPlanResult Submit(CommandPlanRequest request, string? defaultSource = null)
        {
            ArgumentNullException.ThrowIfNull(request);
            var now = _time.GetUtcNow();
            var commandId = request.CommandId?.Trim();
            if (string.IsNullOrEmpty(commandId))
                return new(null, "A plan needs a commandId.");
            var catchUp = string.IsNullOrWhiteSpace(request.CatchUp) ? CommandPlanCatchUp.Latest : request.CatchUp.Trim();
            if (!CommandPlanCatchUp.All.Contains(catchUp))
                return new(null, $"catchUp must be {string.Join(" or ", CommandPlanCatchUp.All)}.");
            if (request.Name?.Length > MaxTextLength || request.Source?.Length > MaxTextLength || defaultSource?.Length > MaxTextLength)
                return new(null, $"The name and source are at most {MaxTextLength} characters.");
            var requested = request.Steps ?? [];
            if (requested.Count is 0 or > MaxSteps)
                return new(null, $"A plan has 1-{MaxSteps} steps.");
            for (var i = 0; i < requested.Count; i++)
            {
                if (requested[i] == null || requested[i].At == null)
                    return new(null, $"Step {i + 1} needs a time (at).");
                if (requested[i].Value == null)
                    return new(null, $"Step {i + 1} needs a value.");
                var at = requested[i].At!.Value;
                if (at < now - PastLimit)
                    return new(null, $"Step {i + 1} is more than {PastLimit.TotalHours:0} hours in the past.");
                if (at > now + Horizon)
                    return new(null, $"Step {i + 1} is more than {Horizon.TotalDays:0} days ahead.");
            }
            var steps = requested.OrderBy(x => x.At).Select(x => new CommandPlanStep { At = x.At!.Value.ToUniversalTime(), Value = x.Value!.DeepClone() }).ToList();
            for (var i = 1; i < steps.Count; i++)
            {
                if (steps[i].At - steps[i - 1].At < MinSpacing)
                    return new(null, $"Steps must be at least {MinSpacing.TotalMinutes:0} minute apart; two are at {steps[i - 1].At:O} and {steps[i].At:O}.");
            }
            if (string.IsNullOrEmpty(_configuration.FindNodeId(commandId)))
                return new(null, $"No node has the command '{commandId}'.", UnknownCommand: true);

            var plan = new CommandPlan
            {
                Id = Guid.NewGuid().ToString("N"),
                CommandId = commandId,
                Name = string.IsNullOrWhiteSpace(request.Name) ? null : request.Name.Trim(),
                Source = string.IsNullOrWhiteSpace(request.Source) ? defaultSource : request.Source.Trim(),
                CatchUp = catchUp,
                Status = CommandPlanStatus.Active,
                CreatedAt = now,
                UpdatedAt = now,
                Steps = steps
            };
            lock (_gate)
            {
                foreach (var active in _live.Values.Where(x => x.CommandId == commandId && x.Status == CommandPlanStatus.Active).ToList())
                {
                    End(active, CommandPlanStatus.Replaced, now);
                    _logger.LogInformation("Command plan {Plan} for {Command} was replaced by {NewPlan}", active.Id, commandId, plan.Id);
                }
                _live[plan.Id] = plan;
                _store.Save(plan);
                _logger.LogInformation("Command plan {Plan} for {Command}: {Steps} steps from {Source}", plan.Id, commandId, steps.Count, plan.Source);
                Process(now);
                Wake();
                return new(Clone(plan), null);
            }
        }

        public CommandPlan? Get(string id)
        {
            lock (_gate)
                return _live.TryGetValue(id, out var plan) ? Clone(plan) : _store.Get(id);
        }

        public IReadOnlyList<CommandPlan> List(string? commandId = null, bool activeOnly = false, int limit = 100)
        {
            lock (_gate)
                return _store.List(string.IsNullOrWhiteSpace(commandId) ? null : commandId.Trim(), activeOnly, Math.Clamp(limit, 1, 500));
        }

        public CommandPlan? Cancel(string id)
        {
            lock (_gate)
            {
                var plan = _live.TryGetValue(id, out var live) ? live : _store.Get(id);
                if (plan == null)
                    return null;
                if (plan.Status == CommandPlanStatus.Active)
                {
                    End(plan, CommandPlanStatus.Cancelled, _time.GetUtcNow());
                    _logger.LogInformation("Command plan {Plan} for {Command} was cancelled", plan.Id, plan.CommandId);
                }
                return Clone(plan);
            }
        }

        public CommandPlan? CancelActive(string commandId)
        {
            lock (_gate)
            {
                var plan = _live.Values.FirstOrDefault(x => x.CommandId == commandId && x.Status == CommandPlanStatus.Active);
                return plan == null ? null : Cancel(plan.Id);
            }
        }

        /// <summary>Sends the due steps and follows the deliveries; returns when it should run next.</summary>
        internal DateTimeOffset Process(DateTimeOffset now)
        {
            lock (_gate)
            {
                foreach (var plan in _live.Values.ToList())
                {
                    var changed = Follow(plan);
                    if (plan.Status == CommandPlanStatus.Active)
                        changed |= SendDue(plan, now);
                    if (plan.Status == CommandPlanStatus.Active && !plan.Steps.Any(x => x.IsOpen))
                    {
                        plan.Status = CommandPlanStatus.Completed;
                        changed = true;
                    }
                    if (changed)
                    {
                        plan.UpdatedAt = now;
                        _store.Save(plan);
                    }
                    if (plan.Status != CommandPlanStatus.Active && !plan.Steps.Any(x => x.Status == CommandPlanStepStatus.Delivering))
                        _live.Remove(plan.Id);
                }
                if (now.ToUnixTimeMilliseconds() >= _nextPrune)
                {
                    _nextPrune = now.AddHours(1).ToUnixTimeMilliseconds();
                    _store.Prune(now);
                }

                if (_live.Values.Any(x => x.Steps.Any(s => s.Status == CommandPlanStepStatus.Delivering)))
                    return now + DeliveryPoll;
                var next = _live.Values.Select(x => x.Next).Where(x => x != null).Select(x => x!.Value).DefaultIfEmpty(now + IdleInterval).Min();
                return next < now + IdleInterval ? next : now + IdleInterval;
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                DateTimeOffset next;
                try
                {
                    next = Process(_time.GetUtcNow());
                }
                catch (Exception x) when (x is not OperationCanceledException)
                {
                    _logger.LogError(x, "Command plans could not be processed");
                    next = _time.GetUtcNow() + IdleInterval;
                }
                var delay = next - _time.GetUtcNow();
                if (delay < TimeSpan.FromMilliseconds(100))
                    delay = TimeSpan.FromMilliseconds(100);
                try
                {
                    await _wake.WaitAsync(delay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        public override void Dispose()
        {
            _wake.Dispose();
            base.Dispose();
        }

        private void Wake()
        {
            if (_wake.CurrentCount == 0)
            {
                try
                {
                    _wake.Release();
                }
                catch (SemaphoreFullException)
                {
                }
            }
        }

        private bool SendDue(CommandPlan plan, DateTimeOffset now)
        {
            var due = plan.Steps.Where(x => x.Status == CommandPlanStepStatus.Pending && x.At <= now).ToList();
            if (due.Count == 0)
                return false;
            var latest = due[^1];
            foreach (var step in due.Take(due.Count - 1))
                step.Status = CommandPlanStepStatus.Skipped;
            if (plan.CatchUp == CommandPlanCatchUp.None && now - latest.At > LateGrace)
            {
                latest.Status = CommandPlanStepStatus.Missed;
                return true;
            }

            var nodeId = _configuration.FindNodeId(plan.CommandId);
            if (string.IsNullOrEmpty(nodeId))
            {
                latest.Status = CommandPlanStepStatus.UnknownCommand;
                latest.Error = $"No node has the command '{plan.CommandId}'.";
                return true;
            }
            var following = plan.Steps.Where(x => x.Status == CommandPlanStepStatus.Pending && x.At > now).Select(x => (DateTimeOffset?)x.At).Min();
            var ttl = following == null ? MaxTtl : following.Value - now;
            ttl = ttl < MinTtl ? MinTtl : ttl > MaxTtl ? MaxTtl : ttl;
            var delivery = _commands.Enqueue(RiotTools.CreateCommand(plan.CommandId, latest.Value), nodeId, ttl);
            latest.Status = CommandPlanStepStatus.Delivering;
            latest.CorrelationId = delivery.CorrelationId;
            latest.SentAt = now;
            _logger.LogInformation("Command plan {Plan} sent {Command} ({CorrelationId})", plan.Id, plan.CommandId, delivery.CorrelationId);
            return true;
        }

        private bool Follow(CommandPlan plan)
        {
            var changed = false;
            foreach (var step in plan.Steps.Where(x => x.Status == CommandPlanStepStatus.Delivering))
            {
                var delivery = step.CorrelationId == null ? null : _commands.Get(step.CorrelationId);
                if (delivery != null && !delivery.IsFinal)
                    continue;
                step.Status = delivery?.Status switch
                {
                    CommandDeliveryStatus.Executed or CommandDeliveryStatus.Duplicate => CommandPlanStepStatus.Executed,
                    CommandDeliveryStatus.Failed => CommandPlanStepStatus.Failed,
                    CommandDeliveryStatus.Expired => CommandPlanStepStatus.Expired,
                    CommandDeliveryStatus.UnknownCommand => CommandPlanStepStatus.UnknownCommand,
                    _ => CommandPlanStepStatus.Sent
                };
                step.Error = delivery?.Error;
                changed = true;
            }
            return changed;
        }

        private void End(CommandPlan plan, string status, DateTimeOffset now)
        {
            plan.Status = status;
            foreach (var step in plan.Steps.Where(x => x.Status == CommandPlanStepStatus.Pending))
                step.Status = CommandPlanStepStatus.Cancelled;
            plan.UpdatedAt = now;
            _store.Save(plan);
            if (!plan.Steps.Any(x => x.Status == CommandPlanStepStatus.Delivering))
                _live.Remove(plan.Id);
        }

        private static CommandPlan Clone(CommandPlan plan) =>
            JsonSerializer.Deserialize<CommandPlan>(JsonSerializer.Serialize(plan, CommandPlanStore.Json), CommandPlanStore.Json)!;
    }
}
