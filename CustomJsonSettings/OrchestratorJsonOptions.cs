using System.Text.Json;

namespace RIoT2.Net.Orchestrator.CustomJsonSettings
{
    /// <summary>
    /// The System.Text.Json settings of the controller responses that don't go through
    /// <c>Json.Serialize</c>. Shared with the golden-file tests in RIoT2.Tests.
    /// </summary>
    public static class OrchestratorJsonOptions
    {
        public static void Configure(JsonSerializerOptions options, JsonNamingPolicy naming)
        {
            ArgumentNullException.ThrowIfNull(options);
            options.PropertyNamingPolicy = naming;
            options.DictionaryKeyPolicy = naming;
            options.PropertyNameCaseInsensitive = true;
            options.WriteIndented = true;
            options.Converters.Add(new JObjectConverter());
        }
    }
}
