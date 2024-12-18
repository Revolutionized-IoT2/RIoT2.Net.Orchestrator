using System.Text.Json;

namespace RIoT2.Net.Orchestrator.CustomJsonSettings
{
    public class LowerCaseNamingPolicy : JsonNamingPolicy
    {
        public override string ConvertName(string name) =>
            name.ToLower();
    }
}
