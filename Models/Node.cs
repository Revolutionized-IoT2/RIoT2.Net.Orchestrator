using RIoT2.Core.Models;

namespace RIoT2.Net.Orchestrator.Models
{
    public class Node
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public bool IsOnline { get; set; }
        public List<DeviceStatus> DeviceStatuses { get; set; }
    }
}
