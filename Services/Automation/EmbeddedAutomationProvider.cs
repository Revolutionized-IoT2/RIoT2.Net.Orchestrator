using System.Text.Json;
using Microsoft.Extensions.Options;
using RIoT2.Core.Models;
using RIoT2.Elsa.Workflows.Services;
using RIoT2.Net.Orchestrator.Services.Outbox;

namespace RIoT2.Net.Orchestrator.Services.Automation
{
    /// <summary>
    /// The embedded workflow mode (RIOT2_WORKFLOW_MODE=embedded, ADR 0016): the dispatcher hands the outbox's triggers to
    /// the in-process engine (<see cref="RIoTTriggers"/>), with the row id as message id, so a redelivery after a crash
    /// isn't run twice. Delivery starts once the host has started, after Elsa's database is ready.
    /// </summary>
    internal sealed class EmbeddedAutomationProvider : OutboxAutomationProvider
    {
        private readonly IServiceProvider _services;
        private volatile bool _started;

        public EmbeddedAutomationProvider(OutboxStore store, IOptions<OrchestratorOptions> options, IServiceProvider services,
            IHostApplicationLifetime lifetime, ILogger<EmbeddedAutomationProvider> logger, TimeProvider time)
            : base(store, (options ?? throw new ArgumentNullException(nameof(options))).Value.WorkflowTriggerTtl, logger, time)
        {
            _services = services ?? throw new ArgumentNullException(nameof(services));
            ArgumentNullException.ThrowIfNull(lifetime);
            lifetime.ApplicationStarted.Register(() =>
            {
                _started = true;
                Dispatcher.TargetCameOnline();
            });
        }

        // The engine is in this process; nodes coming and going don't matter.
        public override void NodeOnlineChanged(string nodeId, NodeOnlineMessage message)
        {
        }

        protected override bool IsEngineAvailable() => _started;

        protected override async Task<DeliveryResult> DeliverAsync(OutboxMessage message, CancellationToken cancellationToken)
        {
            var request = ReadTrigger(message);
            if (DateTimeOffset.FromUnixTimeMilliseconds(message.ExpiresUtc) <= Time.GetUtcNow())
                return DeliveryResult.Retry("The time-to-live passed before the trigger was sent.");
            object? data = null;
            if (!string.IsNullOrWhiteSpace(request.Data))
            {
                try
                {
                    data = JsonSerializer.Deserialize<JsonElement>(request.Data);
                }
                catch (JsonException x)
                {
                    return DeliveryResult.Failed($"Trigger data must be valid JSON: {x.Message}");
                }
            }

            try
            {
                await _services.GetRequiredService<RIoTTriggers>().TriggerOnceAsync(request.MessageId, request.Id, data, "inprocess", cancellationToken);
                return DeliveryResult.Delivered();
            }
            catch (Exception x) when (x is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                return DeliveryResult.Retry($"The workflow engine failed: {x.Message}");
            }
        }
    }
}
