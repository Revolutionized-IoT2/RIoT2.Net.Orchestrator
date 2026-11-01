using System.Security.Cryptography;
using System.Text;
using RIoT2.Core.Interfaces.Services;
using RIoT2.Core.Models;
using RIoT2.Core.Models.Matter;
using RIoT2.Matter.Clusters;
using RIoT2.Matter.ControlBridge;
using RIoT2.Matter.DataModel;
using RIoT2.Net.Orchestrator.Models;

namespace RIoT2.Net.Orchestrator.Services.Matter
{
    /// <summary>
    /// The bridged endpoints the aggregator exposes: composes one per Matter endpoint template in the
    /// node configuration, keeps their endpoint ids stable, routes reports to them and mirrors node
    /// presence into their reachability.
    /// </summary>
    /// <remarks>
    /// <see cref="ReconcileAsync"/> and <see cref="RemoveAllAsync"/> must not run concurrently; the
    /// bridge service serialises them. <see cref="RouteReport"/> and <see cref="SetReachable"/> may run
    /// on any thread at any time.
    /// </remarks>
    internal sealed class MatterBridgedEndpoints
    {
        private readonly IOrchestratorConfigurationService _configurationService;
        private readonly IMessageStateService _messageState;
        private readonly IOnlineNodeService _onlineNodes;
        private readonly MatterConfigurationStore _store;
        private readonly Func<MatterCommandBinding, ValueModel, Task> _sendCommand;
        private readonly ILogger _logger;

        // Copied on read so RouteReport can walk the adapters without a lock on the MQTT thread.
        private volatile RiotBridgedDeviceAdapter[] _adapters = [];
        private readonly object _presenceGate = new();
        private readonly Dictionary<string, string> _adapterSignatures = new(StringComparer.Ordinal);

        public MatterBridgedEndpoints(
            IOrchestratorConfigurationService configurationService,
            IMessageStateService messageState,
            IOnlineNodeService onlineNodes,
            MatterConfigurationStore store,
            Func<MatterCommandBinding, ValueModel, Task> sendCommand,
            ILogger logger)
        {
            _configurationService = configurationService;
            _messageState = messageState;
            _onlineNodes = onlineNodes;
            _store = store;
            _sendCommand = sendCommand;
            _logger = logger;
        }

        /// <summary>The current adapters; a snapshot that later changes don't affect.</summary>
        public IReadOnlyList<RiotBridgedDeviceAdapter> Adapters => _adapters;

        /// <summary>
        /// Brings the aggregator in line with the node configuration: removes endpoints whose
        /// declaration is gone or changed, adds the missing ones and leaves the unchanged ones alone,
        /// so a controller keeps its subscriptions. New endpoint ids go into the endpoint map of
        /// <paramref name="state"/>, which is saved when it changes.
        /// </summary>
        /// <exception cref="InvalidOperationException">Two devices declare the same endpoint key.</exception>
        public async Task ReconcileAsync(AggregatorEndpoint aggregator, MatterBridgeState state, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(aggregator);
            ArgumentNullException.ThrowIfNull(state);

            var online = OnlineNodeIds();
            var desired = DesiredEndpoints();
            var adapters = _adapters.ToList();
            var mapChanged = false;
            try
            {
                foreach (var adapter in adapters.ToArray())
                {
                    var key = EndpointKey(adapter.DeviceId, adapter.Template.Id);
                    if (desired.TryGetValue(key, out var definition) &&
                        _adapterSignatures.TryGetValue(key, out var signature) && signature == definition.Signature)
                        continue;
                    try
                    {
                        await aggregator.RemoveBridgedDeviceAsync(adapter.Device, cancellationToken);
                        adapters.Remove(adapter);
                        _adapterSignatures.Remove(key);
                    }
                    catch (Exception x) when (x is not OperationCanceledException)
                    {
                        _logger?.LogError(x, "Matter endpoint {Endpoint} could not be removed during reconciliation", adapter.Template.Id);
                    }
                }

                foreach (var (key, definition) in desired)
                {
                    if (adapters.Any(a => EndpointKey(a.DeviceId, a.Template.Id) == key))
                        continue;
                    try
                    {
                        var adapter = await AddAsync(aggregator, state, definition.NodeId, definition.Device, definition.Template, online, cancellationToken);
                        adapters.Add(adapter);
                        _adapterSignatures[key] = definition.Signature;
                        var assigned = adapter.Device.EndpointId.Value;
                        if (!state.EndpointMap.TryGetValue(key, out var known) || known != assigned)
                        {
                            state.EndpointMap[key] = assigned;
                            mapChanged = true;
                        }
                    }
                    catch (Exception x) when (x is not OperationCanceledException)
                    {
                        _logger?.LogError(x, "Matter endpoint {Endpoint} of device {DeviceId} could not be bridged",
                            definition.Template.Id, definition.Device.Id);
                    }
                }
            }
            finally
            {
                lock (_presenceGate)
                {
                    _adapters = [.. adapters];
                    var latestOnline = OnlineNodeIds();
                    foreach (var adapter in _adapters)
                        adapter.SetReachable(latestOnline.Contains(adapter.NodeId));
                }
                if (mapChanged)
                    _store.SaveState(state);
            }

            _logger?.LogInformation("Matter bridge exposes {Count} bridged endpoint(s)", adapters.Count);
        }

        /// <summary>
        /// Removes every bridged endpoint from <paramref name="aggregator"/>, or only forgets them when
        /// the bridge is already gone.
        /// </summary>
        public async Task RemoveAllAsync(AggregatorEndpoint aggregator, CancellationToken cancellationToken = default)
        {
            var adapters = _adapters;
            _adapters = [];
            _adapterSignatures.Clear();

            if (aggregator is null)
                return;

            foreach (var adapter in adapters)
            {
                if (adapter.Device is null)
                    continue;

                try
                {
                    await aggregator.RemoveBridgedDeviceAsync(adapter.Device, cancellationToken);
                }
                catch (Exception x)
                {
                    _logger?.LogError(x, "Matter endpoint {Endpoint} could not be removed", adapter.Template.Id);
                }
            }
        }

        /// <summary>
        /// Hands a report to every endpoint; each ignores reports that are not bound to it. Never throws.
        /// </summary>
        public void RouteReport(Report report)
        {
            foreach (var adapter in _adapters)
            {
                try
                {
                    adapter.OnReport(report);
                }
                catch (Exception x)
                {
                    _logger?.LogError(x, "Matter endpoint {Endpoint} could not handle report {ReportId}",
                        adapter.Template.Id, report?.Id);
                }
            }
        }

        /// <summary>Marks the endpoints of a node reachable or unreachable. Never throws.</summary>
        public void SetReachable(string nodeId, bool isOnline)
        {
            lock (_presenceGate)
                foreach (var adapter in _adapters.Where(a => a.NodeId == nodeId))
                {
                    try { adapter.SetReachable(isOnline); }
                    catch (Exception x)
                    {
                        _logger?.LogError(x, "Matter endpoint {Endpoint} could not update reachability", adapter.Template.Id);
                    }
                }
        }

        /// <summary>The status of every composed endpoint.</summary>
        public IEnumerable<MatterEndpointStatus> GetStatus() =>
            _adapters.Where(a => a.Device is not null).Select(a => new MatterEndpointStatus
            {
                EndpointId = a.Device.EndpointId.Value,
                TemplateId = a.Template.Id,
                Name = a.Template.Name,
                DeviceType = a.Template.DeviceType.ToString(),
                NodeId = a.NodeId,
                DeviceId = a.DeviceId,
                Reachable = a.Device.Reachable
            });

        private HashSet<string> OnlineNodeIds() =>
            _onlineNodes.OnlineNodes.Where(n => n.OnlineNodeSettings?.IsOnline == true)
                .Select(n => n.Id).ToHashSet(StringComparer.Ordinal);

        // The signature covers everything baked into a composed endpoint, so any change re-creates it.
        private Dictionary<string, (string NodeId, DeviceConfiguration Device, MatterEndpointTemplate Template, string Signature)> DesiredEndpoints()
        {
            var desired = new Dictionary<string, (string NodeId, DeviceConfiguration Device, MatterEndpointTemplate Template, string Signature)>(StringComparer.Ordinal);
            foreach (var node in _configurationService.NodeConfigurations ?? [])
                foreach (var device in node.DeviceConfigurations ?? [])
                    foreach (var template in device.MatterEndpoints ?? [])
                    {
                        if (string.IsNullOrEmpty(template?.Id))
                            continue;
                        var key = EndpointKey(device.Id, template.Id);
                        var signature = Core.Utils.Json.SerializeIgnoreNulls(new { NodeId = node.Id, device.Name, Template = template });
                        if (!desired.TryAdd(key, (node.Id, device, template, signature)))
                            throw new InvalidOperationException($"Duplicate Matter endpoint declaration: {key}.");
                    }
            return desired;
        }

        private async Task<RiotBridgedDeviceAdapter> AddAsync(
            AggregatorEndpoint aggregator,
            MatterBridgeState state,
            string nodeId,
            DeviceConfiguration device,
            MatterEndpointTemplate template,
            HashSet<string> onlineNodes,
            CancellationToken cancellationToken)
        {
            var reachable = !string.IsNullOrEmpty(nodeId) && onlineNodes.Contains(nodeId);
            var key = EndpointKey(device.Id, template.Id);

            var adapter = new RiotBridgedDeviceAdapter(
                template,
                nodeId,
                device.Id,
                () => _messageState.Reports,
                _sendCommand,
                _logger);

            var definition = new BridgedDeviceDefinition
            {
                DeviceType = MatterEndpointComposer.Resolve(template.DeviceType),
                ComposeApplicationClusters = MatterEndpointComposer.CreateComposer(template.DeviceType),
                NodeLabel = string.IsNullOrEmpty(template.Name) ? device.Name : template.Name,
                Reachable = reachable,
                Information = new BridgedDeviceInformation
                {
                    VendorName = string.IsNullOrEmpty(template.VendorName) ? "RIoT2" : template.VendorName,
                    ProductName = string.IsNullOrEmpty(template.ProductName) ? device.Name : template.ProductName,
                    UniqueId = UniqueId(key)
                }
            };

            // A known endpoint id is offered back to the aggregator so a controller keeps pointing at the
            // same device across restarts; an unknown one is allocated sequentially.
            EndpointId? preferred = state.EndpointMap.TryGetValue(key, out var mapped) ? new EndpointId(mapped) : null;

            await aggregator.AddBridgedDeviceAsync(definition, adapter, preferred, cancellationToken);
            return adapter;
        }

        internal static string EndpointKey(string deviceId, string templateId) => $"{deviceId}:{templateId}";

        /// <summary>
        /// Derives the Bridged Device Basic Information UniqueId, which the specification limits to 32
        /// characters, from the endpoint key.
        /// </summary>
        internal static string UniqueId(string key)
        {
            if (key.Length <= 32)
                return key;

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            return Convert.ToHexString(hash)[..32];
        }
    }
}
