using Grpc.Core;
using Microsoft.Extensions.Options;
using RIoT2.Core;
using RIoT2.Core.Models;
using RIoT2.Core.Security;
using RIoT2.Net.Orchestrator.Services.Outbox;

namespace RIoT2.Net.Orchestrator.Services.Automation
{
    /// <summary>
    /// The remote workflow mode (RIOT2_WORKFLOW_MODE=remote; ADR 0003, ADR 0008, ADR 0016): the dispatcher delivers the
    /// outbox's triggers over gRPC (<c>riot.RIoTTriggerService/Trigger</c>) to the online workflow host, with the row id as
    /// <c>message_id</c>, which Elsa deduplicates on. Triggers wait while no workflow host is online.
    /// </summary>
    internal sealed class ElsaAutomationProvider : OutboxAutomationProvider
    {
        private readonly IOnlineNodeService _nodes;
        private readonly WorkflowTriggerClient _client;

        public ElsaAutomationProvider(OutboxStore store, IOnlineNodeService nodes, IOptions<OrchestratorOptions> options,
            ILogger<ElsaAutomationProvider> logger, TimeProvider time, IOutboundUrlPolicy? outboundUrlPolicy = null)
            : base(store, (options ?? throw new ArgumentNullException(nameof(options))).Value.WorkflowTriggerTtl, logger, time)
        {
            _nodes = nodes ?? throw new ArgumentNullException(nameof(nodes));
            _client = new WorkflowTriggerClient(options.Value.Security.ApiKey, outboundUrlPolicy);
        }

        public override void NodeOnlineChanged(string nodeId, NodeOnlineMessage message)
        {
            if (message?.IsOnline == true && message.NodeType == NodeType.Workflow)
                Dispatcher.TargetCameOnline();
        }

        protected override bool IsEngineAvailable() => FindEndpoint() != null;

        protected override Task OnStoppedAsync() => _client.DisposeAsync().AsTask();

        private string? FindEndpoint()
        {
            var workflowNode = _nodes.OnlineNodes.FirstOrDefault(x => x.OnlineNodeSettings?.IsOnline == true && x.OnlineNodeSettings.NodeType == NodeType.Workflow);
            if (workflowNode == null)
                return null;
            var settings = workflowNode.OnlineNodeSettings;
            var url = string.IsNullOrWhiteSpace(settings.GrpcBaseUrl) ? settings.NodeBaseUrl : settings.GrpcBaseUrl;
            return string.IsNullOrWhiteSpace(url) ? null : url;
        }

        protected override async Task<DeliveryResult> DeliverAsync(OutboxMessage message, CancellationToken cancellationToken)
        {
            var url = FindEndpoint();
            if (url == null)
                return DeliveryResult.Retry("No workflow node is online.");
            var request = ReadTrigger(message);
            // The call never outlives the trigger: past its time-to-live it must not reach Elsa, and
            // Elsa doesn't start a request whose deadline has passed.
            var expires = DateTimeOffset.FromUnixTimeMilliseconds(message.ExpiresUtc).UtcDateTime;
            if (expires <= Time.GetUtcNow().UtcDateTime)
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
