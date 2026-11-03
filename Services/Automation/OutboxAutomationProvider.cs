using Google.Protobuf;
using RIoT2.Core.Models;
using RIoT2.Net.Orchestrator.Grpc;
using RIoT2.Net.Orchestrator.Services.Outbox;

namespace RIoT2.Net.Orchestrator.Services.Automation
{
    /// <summary>
    /// What both workflow modes share (ADR 0003, ADR 0008, ADR 0016): every report becomes a workflow trigger in the
    /// outbox (design 7.1) before <see cref="ReportReceivedAsync"/> returns, in the gRPC request format, and one dispatcher
    /// delivers the triggers in order with the row id as <c>message_id</c>, which the engine deduplicates on. Triggers wait
    /// while the engine is unavailable, are retried with backoff after a transient failure, and expire after
    /// <see cref="OrchestratorOptions.WorkflowTriggerTtl"/>. Both modes write the same rows, so pending triggers survive a
    /// change of <c>RIOT2_WORKFLOW_MODE</c>.
    /// </summary>
    internal abstract class OutboxAutomationProvider : BackgroundService, IAutomationProvider
    {
        public const string Target = OutboxKind.Workflow;

        private readonly OutboxStore _store;
        private readonly ILogger _logger;
        private readonly TimeProvider _time;
        private readonly TimeSpan _ttl;

        protected OutboxAutomationProvider(OutboxStore store, TimeSpan ttl, ILogger logger, TimeProvider time)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _time = time ?? throw new ArgumentNullException(nameof(time));
            _ttl = ttl;
            Dispatcher = new OutboxDispatcher(store, Target, "workflow trigger", IsEngineAvailable, DeliverAsync, logger, time);
        }

        protected OutboxDispatcher Dispatcher { get; }

        protected TimeProvider Time => _time;

        public Task ReportReceivedAsync(Report report, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(report);
            var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
            // ToJson returns null only when serialization throws; the protobuf setter rejects it as before.
            var request = new TriggerRequest { Id = report.Id ?? "", Data = report.ToJson()! };
            _store.Add(new OutboxMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                Kind = OutboxKind.Workflow,
                Target = Target,
                Subject = report.Id,
                Payload = JsonFormatter.Default.Format(request),
                CreatedUtc = now,
                ExpiresUtc = now + (long)_ttl.TotalMilliseconds,
                NextAttemptUtc = now,
                UpdatedUtc = now
            });
            Dispatcher.Signal();
            return Task.CompletedTask;
        }

        public abstract void NodeOnlineChanged(string nodeId, NodeOnlineMessage message);

        /// <summary>Whether triggers can be delivered now; the dispatcher waits while they can't.</summary>
        protected abstract bool IsEngineAvailable();

        /// <summary>Delivers one trigger, with the outbox row id as its message id.</summary>
        protected abstract Task<DeliveryResult> DeliverAsync(OutboxMessage message, CancellationToken cancellationToken);

        protected override Task ExecuteAsync(CancellationToken stoppingToken) => Dispatcher.RunAsync(stoppingToken);

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            await base.StopAsync(cancellationToken);
            await OnStoppedAsync();
            var pending = _store.GetHealth(_time.GetUtcNow().ToUnixTimeMilliseconds()).Pending;
            if (pending > 0)
                _logger.LogInformation("{Count} outbox messages are pending; they are delivered after the restart unless their time-to-live has passed", pending);
        }

        protected virtual Task OnStoppedAsync() => Task.CompletedTask;

        /// <summary>The trigger as it was stored.</summary>
        protected static TriggerRequest ReadTrigger(OutboxMessage message)
        {
            var request = JsonParser.Default.Parse<TriggerRequest>(message.Payload);
            request.MessageId = message.Id;
            return request;
        }
    }
}
