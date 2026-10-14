using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Options;
using RIoT2.Core;
using RIoT2.Core.Models;
using RIoT2.Net.Orchestrator.Grpc;
using RIoT2.Net.Orchestrator.Services.Outbox;

namespace RIoT2.Net.Orchestrator.Services.Automation
{
    /// <summary>
    /// Elsa as the automation engine (ADR 0003, ADR 0008). Every report becomes a workflow trigger in the
    /// outbox (design 7.1). The dispatcher delivers the triggers in order over gRPC
    /// (<c>riot.RIoTTriggerService/Trigger</c>) to the online workflow node, with the row id as
    /// <c>message_id</c>, which Elsa deduplicates on. Triggers wait while no workflow node is online,
    /// are retried with backoff after a transient failure, and expire after
    /// <see cref="OrchestratorOptions.WorkflowTriggerTtl"/>.
    /// </summary>
    internal sealed class ElsaAutomationProvider : BackgroundService, IAutomationProvider
    {
        public const string Target = OutboxKind.Workflow;

        private readonly OutboxStore _store;
        private readonly IOnlineNodeService _nodes;
        private readonly ILogger<ElsaAutomationProvider> _logger;
        private readonly TimeProvider _time;
        private readonly TimeSpan _ttl;
        private readonly WorkflowTriggerClient _client = new();
        private readonly OutboxDispatcher _dispatcher;

        public ElsaAutomationProvider(OutboxStore store, IOnlineNodeService nodes, IOptions<OrchestratorOptions> options,
            ILogger<ElsaAutomationProvider> logger, TimeProvider time)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _nodes = nodes ?? throw new ArgumentNullException(nameof(nodes));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _time = time ?? throw new ArgumentNullException(nameof(time));
            _ttl = options.Value.WorkflowTriggerTtl;
            _dispatcher = new OutboxDispatcher(store, Target, "workflow trigger", () => FindEndpoint() != null, DeliverAsync, logger, time);
        }

        public Task ReportReceivedAsync(Report report, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(report);
            var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
            var request = new TriggerRequest { Id = report.Id ?? "", Data = report.ToJson() };
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
            _dispatcher.Signal();
            return Task.CompletedTask;
        }

        public void NodeOnlineChanged(string nodeId, NodeOnlineMessage message)
        {
            if (message?.IsOnline == true && message.NodeType == NodeType.Workflow)
                _dispatcher.TargetCameOnline();
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken) => _dispatcher.RunAsync(stoppingToken);

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            await base.StopAsync(cancellationToken);
            await _client.DisposeAsync();
            var pending = _store.GetHealth(_time.GetUtcNow().ToUnixTimeMilliseconds()).Pending;
            if (pending > 0)
                _logger.LogInformation("{Count} outbox messages are pending; they are delivered after the restart unless their time-to-live has passed", pending);
        }

        private string FindEndpoint()
        {
            var workflowNode = _nodes.OnlineNodes.FirstOrDefault(x => x.OnlineNodeSettings?.IsOnline == true && x.OnlineNodeSettings.NodeType == NodeType.Workflow);
            if (workflowNode == null)
                return null;
            var settings = workflowNode.OnlineNodeSettings;
            var url = string.IsNullOrWhiteSpace(settings.GrpcBaseUrl) ? settings.NodeBaseUrl : settings.GrpcBaseUrl;
            return string.IsNullOrWhiteSpace(url) ? null : url;
        }

        private async Task<DeliveryResult> DeliverAsync(OutboxMessage message, CancellationToken cancellationToken)
        {
            var url = FindEndpoint();
            if (url == null)
                return DeliveryResult.Retry("No workflow node is online.");
            var request = JsonParser.Default.Parse<TriggerRequest>(message.Payload);
            request.MessageId = message.Id;
            // The call never outlives the trigger: past its time-to-live it must not reach Elsa, and
            // Elsa doesn't start a request whose deadline has passed.
            var expires = DateTimeOffset.FromUnixTimeMilliseconds(message.ExpiresUtc).UtcDateTime;
            if (expires <= _time.GetUtcNow().UtcDateTime)
                return DeliveryResult.Retry("The time-to-live passed before the trigger was sent.");
            try
            {
                var response = await _client.TriggerAsync(url, request, cancellationToken, expires);
                return response.Success
                    ? DeliveryResult.Delivered()
                    : DeliveryResult.Failed("The workflow engine did not accept the trigger.");
            }
            catch (RpcException x) when (x.StatusCode == StatusCode.InvalidArgument)
            {
                return DeliveryResult.Failed($"{x.StatusCode}: {x.Status.Detail}");
            }
            catch (RpcException x) when (!cancellationToken.IsCancellationRequested)
            {
                return DeliveryResult.Retry($"{x.StatusCode}: {x.Status.Detail}");
            }
        }
    }
}
