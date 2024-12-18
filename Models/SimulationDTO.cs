using RIoT2.Core.Models;

namespace RIoT2.Net.Orchestrator.Models
{
    public class SimulationDTO
    {
        public string Id { get; set; }
        public object Data { get; set; }

        public ValueModel GetAsModel() 
        {
            return new ValueModel(Data);
        }
    }
}