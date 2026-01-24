using RIoT2.Core.Interfaces;

namespace RIoT2.Net.Orchestrator.Models
{
    public class VariableTemplate : INodeTemplate, ITemplate
    {
        public string NodeId { get; set; }
        public string Node { get; set; }
        public string DeviceId { get; set; }
        public string Device { get; set; }
        public string Id { get; set; }
        public string Name { get; set; }
        public string Address { get; set; }
        public object Model { get; set; }
        public Core.ValueType Type { get; set; }
    }
}
