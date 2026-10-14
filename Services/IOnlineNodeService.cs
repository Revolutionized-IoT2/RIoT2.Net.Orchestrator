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
        void SetStatus(string id, NodeStatusMessage status);
        NodeStatusMessage GetStatus(string id);

        Task<List<Node>> GetNodesAsync();
        Task<List<DeviceStatus>> LoadDeviceStatusFromNodeAsync(string nodeId);
        Task<List<DeviceConfiguration>> LoadDeviceConfigurationTemplateAsync(string id);
        Task<Dictionary<string, List<DeviceConfiguration>>> LoadDeviceConfigurationTemplatesAsync();
    }
}
