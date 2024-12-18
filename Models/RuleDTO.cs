using RIoT2.Core;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Models;
using System.Text.Json;

namespace RIoT2.Net.Orchestrator.Models
{
    public class RuleDTO : Rule
    {
        new public List<object> RuleItems { get; set; }

        internal Rule ToRule() 
        {
            return new Rule()
            {
                Id = this.Id,
                Description = this.Description,
                IsActive = this.IsActive,
                Name = this.Name,
                RuleItems = convertToItems(this.RuleItems),
                Tags = this.Tags,
                DataModel = this.DataModel
            };
        }

        private List<IRuleItem> convertToItems(List<dynamic> items) 
        {
            var itemsList = new List<IRuleItem>();
            JsonSerializerOptions options = new JsonSerializerOptions()
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            foreach (var item in items)
            {
                RuleType ruleType = (RuleType)item.GetProperty("ruleType").GetInt32();
      

                switch (ruleType) 
                {
                    case RuleType.Trigger: itemsList.Add(JsonSerializer.Deserialize<RuleTrigger>(item, options)); break;
                    case RuleType.Condition: itemsList.Add(JsonSerializer.Deserialize<RuleCondition>(item, options)); break;
                    case RuleType.Function: itemsList.Add(JsonSerializer.Deserialize<RuleFunction>(item, options)); break;
                    case RuleType.Output: itemsList.Add(JsonSerializer.Deserialize<RuleOutput>(item, options)); break;
                }
            }

            return itemsList;
        }
    }
}
