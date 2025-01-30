using RIoT2.Core.Models;

namespace RIoT2.Net.Orchestrator.Models
{
    public class VariableDTO
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public bool IsPersistant { get; set; }
        public Core.ValueType Type { get; set; }
        public object Value { get; set; }

        public static VariableDTO ToVariableDTO(Variable variable) 
        {
            return new VariableDTO() 
            {
                Id = variable.Id,
                Name = variable.Name,
                Description = variable.Description,
                IsPersistant = variable.IsPersistant,
                Value = variable.Value,
                Type = variable.Type
            };
        }

        public Variable ToVariable() 
        {
            return new Variable() 
            {
                Id = Id,
                Name = Name,
                Description = Description,
                IsPersistant = IsPersistant,
                Value = Value
            };
        }
    }
}
