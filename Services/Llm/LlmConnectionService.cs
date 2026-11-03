using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using RIoT2.Net.Orchestrator.Services.Persistence;

namespace RIoT2.Net.Orchestrator.Services.Llm
{
    /// <summary>
    /// The language model connections (ADR 0016): validated, unique by name, with the API key encrypted at rest and
    /// readable only here.
    /// </summary>
    public sealed partial class LlmConnectionService(IStoredObjectStore store, IDataProtectionProvider protection)
    {
        public const int MaxNameLength = 100;
        public const int MaxOutputTokensLimit = 32000;

        private readonly IDataProtector _protector = protection.CreateProtector("RIoT2.Llm.ApiKey");
        private readonly Lock _gate = new();

        public IReadOnlyList<LlmConnectionInfo> List() =>
            store.GetAll<LlmConnection>().OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Select(LlmConnectionInfo.From).ToList();

        public LlmConnection? Get(string id) => store.Get<LlmConnection>(id);

        /// <summary>The connection a Run LLM step names, without regard to case.</summary>
        public LlmConnection? FindByName(string name) =>
            store.GetAll<LlmConnection>().FirstOrDefault(x => string.Equals(x.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

        /// <summary>Creates (null <paramref name="id"/>) or changes a connection; returns it, or the problems.</summary>
        public (LlmConnectionInfo? Connection, IReadOnlyList<string> Errors) Save(string? id, LlmConnectionSave request)
        {
            ArgumentNullException.ThrowIfNull(request);
            lock (_gate)
            {
                LlmConnection? existing = null;
                if (id != null)
                {
                    existing = store.Get<LlmConnection>(id);
                    if (existing == null)
                        return (null, [$"No connection {id}."]);
                }

                var errors = Validate(request).ToList();
                var name = request.Name?.Trim() ?? "";
                if (store.GetAll<LlmConnection>().Any(x => x.Id != existing?.Id && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                    errors.Add($"A connection named '{name}' already exists.");
                if (errors.Count > 0)
                    return (null, errors);

                var connection = new LlmConnection
                {
                    Id = existing?.Id ?? "",
                    Name = name,
                    Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
                    Provider = request.Provider,
                    Endpoint = string.IsNullOrWhiteSpace(request.Endpoint) ? null : request.Endpoint.Trim().TrimEnd('/'),
                    Model = request.Model.Trim(),
                    ApiVersion = string.IsNullOrWhiteSpace(request.ApiVersion) ? null : request.ApiVersion.Trim(),
                    ProtectedApiKey = request.ApiKey switch
                    {
                        null => existing?.ProtectedApiKey,
                        "" => null,
                        var key => _protector.Protect(key.Trim())
                    },
                    Temperature = request.Temperature,
                    MaxOutputTokens = request.MaxOutputTokens,
                    Enabled = request.Enabled
                };
                var saved = store.Save(connection);
                return saved == null ? (null, ["The connection could not be stored."]) : (LlmConnectionInfo.From(connection), []);
            }
        }

        public bool Delete(string id)
        {
            lock (_gate)
            {
                if (store.Get<LlmConnection>(id) == null)
                    return false;
                store.Delete<LlmConnection>(id);
                return true;
            }
        }

        /// <summary>The connection's API key; null without one.</summary>
        /// <exception cref="CryptographicException">
        /// The key was encrypted with keys this orchestrator doesn't have (a backup restored elsewhere): enter it again.
        /// </exception>
        public string? ReadApiKey(LlmConnection connection)
        {
            ArgumentNullException.ThrowIfNull(connection);
            return string.IsNullOrEmpty(connection.ProtectedApiKey) ? null : _protector.Unprotect(connection.ProtectedApiKey);
        }

        public static IEnumerable<string> Validate(LlmConnectionSave request)
        {
            var name = request.Name?.Trim() ?? "";
            if (name.Length is 0 or > MaxNameLength || !NamePattern().IsMatch(name))
                yield return $"The name must be 1-{MaxNameLength} letters, digits, spaces, dots, dashes or underscores.";
            if (!LlmProviders.All.Contains(request.Provider))
                yield return $"The provider must be one of {string.Join(", ", LlmProviders.All)}.";
            if (string.IsNullOrWhiteSpace(request.Model) || request.Model.Trim().Length > 200)
                yield return "A model (or Azure deployment) of at most 200 characters is required.";
            if (!string.IsNullOrWhiteSpace(request.Endpoint))
            {
                if (!Uri.TryCreate(request.Endpoint.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                    || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.UserInfo))
                    yield return "The endpoint must be an absolute http or https URL without a query or credentials.";
            }
            else if (request.Provider is LlmProviders.AzureOpenAI or LlmProviders.OpenAICompatible)
            {
                yield return $"{request.Provider} needs an endpoint.";
            }
            if (request.ApiVersion is { Length: > 40 })
                yield return "The API version is too long.";
            if (request.ApiKey is { Length: > 1000 })
                yield return "The API key is too long.";
            if (request.Temperature is < 0 or > 2)
                yield return "The temperature must be 0-2.";
            if (request.MaxOutputTokens is < 1 or > MaxOutputTokensLimit)
                yield return $"The answer limit must be 1-{MaxOutputTokensLimit} tokens.";
        }

        [GeneratedRegex(@"^[A-Za-z0-9 _.\-]+$")]
        private static partial Regex NamePattern();
    }
}
