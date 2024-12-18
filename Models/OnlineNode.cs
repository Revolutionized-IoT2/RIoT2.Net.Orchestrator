using RIoT2.Core.Models;

namespace RIoT2.Net.Orchestrator.Models
{
    public class OnlineNode
    {
        public string Id { get; set; }
        public NodeOnlineMessage OnlineNodeSettings { get; set; }
    }
}
