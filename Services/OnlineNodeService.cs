using System.Collections.Concurrent;
using RIoT2.Core;
using RIoT2.Core.Interfaces.Services;
using RIoT2.Core.Models;
using RIoT2.Core.Utils;
using RIoT2.Net.Orchestrator.Models;

namespace RIoT2.Net.Orchestrator.Services
{
    public class OnlineNodeService : IOnlineNodeService
    {
        private readonly ConcurrentDictionary<string, OnlineNode> _onlineNodes;
        // Kept while a node is offline: the status is retained, and still describes the node.
        private readonly ConcurrentDictionary<string, NodeStatusMessage> _statuses = new();
        private readonly IOrchestratorConfigurationService _configuration;
        private ILogger<OnlineNodeService> _logger;

        public OnlineNodeService(IOrchestratorConfigurationService configuration, ILogger<OnlineNodeService> logger)
        {
            _configuration = configuration;
            _logger = logger;
            _onlineNodes = new ConcurrentDictionary<string, OnlineNode>();
        }

        public IEnumerable<OnlineNode> OnlineNodes { get { return _onlineNodes.Values; } }

        public void Add(OnlineNode node)
        {
            _onlineNodes.AddOrUpdate(
                node.Id,
                node,
                (_, existing) =>
                {
                    existing.OnlineNodeSettings = node.OnlineNodeSettings;
                    return existing;
                });
        }

        public void Remove(string id)
        {
            _onlineNodes.TryRemove(id, out _);
        }

        public void SetStatus(string id, NodeStatusMessage status)
        {
            if (status == null)
                _statuses.TryRemove(id, out _);
            else
                _statuses[id] = status;
        }

        public NodeStatusMessage GetStatus(string id) => _statuses.TryGetValue(id, out var status) ? status : null;

        internal static string SyncState(NodeDeviceConfiguration configuration, OnlineNode online, NodeStatusMessage status)
        {
            var settings = online?.OnlineNodeSettings;
            if (settings != null && !settings.HasCapability(Contract.Capabilities.DesiredState))
                return NodeSyncState.Legacy;
            if (status == null)
                return NodeSyncState.Pending;
            if (status.State == NodeConfigurationState.Failed)
                return NodeSyncState.Failed;
            return status.State == NodeConfigurationState.Applied && status.AppliedHash != null && status.AppliedHash == configuration.Hash
                ? NodeSyncState.InSync
                : NodeSyncState.Pending;
        }

        public async Task<List<Node>> GetNodesAsync()
        {
            // Issue all per-node status requests concurrently, then aggregate.
            var tasks = _configuration.NodeConfigurations.Select(async conf =>
            {
                _onlineNodes.TryGetValue(conf.Id, out var onlineNode);
                var status = GetStatus(conf.Id);
                var deviceStatuses = await LoadDeviceStatusFromNodeAsync(conf.Id);

                return new Node()
                {
                    Id = conf.Id,
                    Name = conf.Name,
                    IsOnline = onlineNode?.OnlineNodeSettings?.IsOnline == true,
                    DeviceStatuses = deviceStatuses,
                    Manifest = onlineNode?.OnlineNodeSettings.Manifest,
                    PluginManifest = onlineNode?.OnlineNodeSettings.PluginManifest,
                    DesiredRevision = conf.Revision,
                    AppliedRevision = status?.AppliedRevision,
                    SyncState = SyncState(conf, onlineNode, status),
                    LastError = status?.Error
                };
            });

            var nodes = await Task.WhenAll(tasks);
            return nodes.ToList();
        }

        public async Task<List<DeviceConfiguration>> LoadDeviceConfigurationTemplateAsync(string id) 
        {
            if (!_onlineNodes.TryGetValue(id, out var onlineNode))
                return null;

            try 
            {
                var response = await Web.GetAsync(onlineNode.OnlineNodeSettings.NodeBaseUrl + Constants.ApiConfigurationTemplateUrl);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var deviceConfiguration = Json.Deserialize<List<DeviceConfiguration>>(json);
                    return deviceConfiguration;
                }
            }
            catch(Exception x) 
            {
                _logger.LogError(x, "Could not load configuration template for {Id}", id);
            }
            return null;
        }

        public async Task<List<DeviceStatus>> LoadDeviceStatusFromNodeAsync(string nodeId)
        {
            if (!_onlineNodes.TryGetValue(nodeId, out var onlineNode))
                return null;

            try
            {
                var response = await Web.GetAsync(onlineNode.OnlineNodeSettings.NodeBaseUrl + Constants.ApiDeviceStateUrl);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var listOfDeviceStatus = Json.Deserialize<List<DeviceStatus>>(json);
                    return listOfDeviceStatus;
                }
            }
            catch (Exception x)
            {
                _logger.LogError(x, "Could not load Device status data for node: {NodeId}", nodeId);
            }
            return null;
        }

        public async Task<Dictionary<string, List<DeviceConfiguration>>> LoadDeviceConfigurationTemplatesAsync()
        {
            var results = new Dictionary<string, List<DeviceConfiguration>>();

            Dictionary<string, Task<List<DeviceConfiguration>>> requests = new Dictionary<string, Task<List<DeviceConfiguration>>>();
            foreach (var onlineNode in _onlineNodes.Values) 
            {
                if(onlineNode.OnlineNodeSettings.NodeType == NodeType.Device)
                    requests.Add(onlineNode.Id, LoadDeviceConfigurationTemplateAsync(onlineNode.Id));
            }

            if(requests.Count == 0)
                return results;

            await Task.WhenAll(requests.Values.ToArray());

            foreach (var key in requests.Keys)
                results.Add(key, requests[key].Result);

            return results;
        }
    }
}
