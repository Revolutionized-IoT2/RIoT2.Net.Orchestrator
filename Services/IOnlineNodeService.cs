using RIoT2.Core.Models;
using RIoT2.Net.Orchestrator.Models;

namespace RIoT2.Net.Orchestrator.Services
{
    public interface IOnlineNodeService
    {
        void Add(OnlineNode node);
        void Remove(string id);
        IEnumerable<OnlineNode> OnlineNodes { get; }

        /// <summary>Records the configuration status a node published (design 7.2); <c>null</c> forgets it.</summary>
        void SetStatus(string id, NodeStatusMessage? status);
        NodeStatusMessage? GetStatus(string id);

        Task<List<Node>> GetNodesAsync();

        /// <summary>Every configured node's sync state (design 7.2 phase 6), without asking the nodes anything.</summary>
        List<NodeSync> GetSyncStates();
        /// <summary>The online connectors (design 7.3 phase 3), each with its <c>/health</c> answer when it announced a URL.</summary>
        Task<List<ConnectorInfo>> GetConnectorsAsync(CancellationToken cancellationToken = default);
        Task<List<DeviceStatus>?> LoadDeviceStatusFromNodeAsync(string nodeId);
        Task<List<DeviceConfiguration>?> LoadDeviceConfigurationTemplateAsync(string id);
        Task<Dictionary<string, List<DeviceConfiguration>?>> LoadDeviceConfigurationTemplatesAsync();
    }
}
