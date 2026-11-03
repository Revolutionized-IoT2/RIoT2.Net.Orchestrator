using RIoT2.Core.Interfaces;

namespace RIoT2.Net.Orchestrator.Services.Llm
{
    /// <summary>The model protocols a connection speaks (ADR 0016).</summary>
    public static class LlmProviders
    {
        /// <summary>OpenAI's chat completions; the endpoint defaults to https://api.openai.com/v1.</summary>
        public const string OpenAI = "OpenAI";

        /// <summary>An Azure OpenAI deployment: the endpoint is the resource, the model the deployment name.</summary>
        public const string AzureOpenAI = "AzureOpenAI";

        /// <summary>Anthropic's messages API; the endpoint defaults to https://api.anthropic.com.</summary>
        public const string Anthropic = "Anthropic";

        /// <summary>Any OpenAI-compatible chat completions server, such as Ollama (http://ollama:11434/v1); the key is optional.</summary>
        public const string OpenAICompatible = "OpenAICompatible";

        public static readonly IReadOnlyList<string> All = [OpenAI, AzureOpenAI, Anthropic, OpenAICompatible];

        /// <summary>The Azure OpenAI API version when the connection sets none.</summary>
        public const string DefaultAzureApiVersion = "2024-10-21";
    }

    /// <summary>
    /// A language model connection the Run LLM step uses by name (ADR 0016), stored as
    /// <c>StoredObjects/LlmConnection/&lt;Id&gt;.json</c>. The API key is encrypted with the orchestrator's data
    /// protection keys (<c>StoredObjects/.keys</c>, not in backups) and never leaves the orchestrator.
    /// </summary>
    public sealed class LlmConnection : IStoredObject
    {
        public string Id { get; set; } = "";

        /// <summary>The name workflows use; unique without regard to case.</summary>
        public string Name { get; set; } = "";

        public string? Description { get; set; }

        /// <summary>One of <see cref="LlmProviders"/>.</summary>
        public string Provider { get; set; } = LlmProviders.OpenAI;

        /// <summary>The base URL; empty for the provider's default (OpenAI, Anthropic).</summary>
        public string? Endpoint { get; set; }

        /// <summary>The model, or the Azure OpenAI deployment.</summary>
        public string Model { get; set; } = "";

        /// <summary>The Azure OpenAI API version; empty for <see cref="LlmProviders.DefaultAzureApiVersion"/>.</summary>
        public string? ApiVersion { get; set; }

        /// <summary>The API key, encrypted; null without one.</summary>
        public string? ProtectedApiKey { get; set; }

        /// <summary>Sampling temperature, 0-2; null for the model's default.</summary>
        public double? Temperature { get; set; }

        /// <summary>Most tokens per answer; null for the default (1024).</summary>
        public int? MaxOutputTokens { get; set; }

        public bool Enabled { get; set; } = true;
    }

    /// <summary>A connection as the API shows it: never the key, only whether there is one.</summary>
    public sealed record LlmConnectionInfo(string Id, string Name, string? Description, string Provider, string? Endpoint, string Model,
        string? ApiVersion, bool HasApiKey, double? Temperature, int? MaxOutputTokens, bool Enabled)
    {
        public static LlmConnectionInfo From(LlmConnection x) => new(x.Id, x.Name, x.Description, x.Provider, x.Endpoint, x.Model,
            x.ApiVersion, !string.IsNullOrEmpty(x.ProtectedApiKey), x.Temperature, x.MaxOutputTokens, x.Enabled);
    }

    /// <summary>A new or changed connection. <see cref="ApiKey"/>: null keeps the stored key, empty removes it.</summary>
    public sealed record LlmConnectionSave(string Name, string? Description, string Provider, string? Endpoint, string Model,
        string? ApiVersion, string? ApiKey, double? Temperature, int? MaxOutputTokens, bool Enabled = true);
}
