using RIoT2.Core.Models;
using RIoT2.Net.Orchestrator.Models;

namespace RIoT2.Net.Orchestrator.Services
{
    public interface IOnlineNodeService
    {
        void Add(OnlineNode node);
        void Remove(string id);
        IEnumerable<OnlineNode> OnlineNodes { get; }

        Task<List<DeviceConfiguration>> LoadDeviceConfigurationTemplateAsync(string id);
        Task<Dictionary<string, List<DeviceConfiguration>>> LoadDeviceConfigurationTemplatesAsync();
    }
}
