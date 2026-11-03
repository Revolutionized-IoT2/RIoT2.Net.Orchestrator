using RIoT2.Core.Configuration;
using RIoT2.Core.Security;

namespace RIoT2.Net.Orchestrator.Services.Mcp
{
    /// <summary>The values of <c>RIOT2_MCP_AUTH</c> (ADR 0018): how the remote MCP listener authenticates.</summary>
    public static class McpAuthProfiles
    {
        /// <summary>No remote listener; only the local <c>/mcp</c> on the API port.</summary>
        public const string None = "none";

        /// <summary>
        /// Cloudflare Access (Managed OAuth) in front of a Cloudflare Tunnel: Access runs the OAuth flow with the MCP client and
        /// sends its signed JWT in <c>Cf-Access-Jwt-Assertion</c>, which the listener validates.
        /// </summary>
        public const string CloudflareAccess = "cloudflare-access";

        /// <summary>A bearer JWT from any OAuth 2.1 / OpenID Connect issuer; the listener serves the protected resource metadata (RFC 9728).</summary>
        public const string Oidc = "oidc";

        public static readonly IReadOnlyList<string> All = [None, CloudflareAccess, Oidc];
    }

    /// <summary>
    /// The MCP server's deployment settings (ADR 0018). The tool policy (groups, allowed commands, rate) is the stored
    /// <see cref="McpConfiguration"/>, changed on the UI's AI access page; these settings decide what the UI can't loosen.
    /// </summary>
    public sealed class McpOptions
    {
        public const string EnabledVariable = "RIOT2_MCP_ENABLED";
        public const string AuthVariable = "RIOT2_MCP_AUTH";
        public const string RemotePortVariable = "RIOT2_MCP_REMOTE_PORT";
        public const string PublicUrlVariable = "RIOT2_MCP_PUBLIC_URL";
        public const string AuthorityVariable = "RIOT2_MCP_AUTH_AUTHORITY";
        public const string AudienceVariable = "RIOT2_MCP_AUTH_AUDIENCE";
        public const string TeamDomainVariable = "RIOT2_MCP_CF_TEAM_DOMAIN";
        public const string RemoteMaxRoleVariable = "RIOT2_MCP_REMOTE_MAX_ROLE";
        public const string AllowedSubjectsVariable = "RIOT2_MCP_ALLOWED_SUBJECTS";

        public const int DefaultRemotePort = 8090;

        /// <summary>Maps <c>/mcp</c> on the API port. <c>RIOT2_MCP_ENABLED</c>; default <c>false</c>.</summary>
        public bool Enabled { get; set; }

        /// <summary>One of <see cref="McpAuthProfiles"/>. <c>RIOT2_MCP_AUTH</c>; default <c>none</c> (no remote listener).</summary>
        public string Auth { get; set; } = McpAuthProfiles.None;

        /// <summary>The remote listener's port, which serves only <c>/mcp</c> and its metadata. <c>RIOT2_MCP_REMOTE_PORT</c>; default 8090.</summary>
        public int RemotePort { get; set; } = DefaultRemotePort;

        /// <summary>The public https URL of the remote <c>/mcp</c>, as MCP clients use it (the OAuth resource). <c>RIOT2_MCP_PUBLIC_URL</c>.</summary>
        public string? PublicUrl { get; set; }

        /// <summary><c>oidc</c>: the token issuer (OpenID Connect discovery). <c>RIOT2_MCP_AUTH_AUTHORITY</c>.</summary>
        public string? Authority { get; set; }

        /// <summary>The token audience: the Cloudflare Access application's AUD tag, or the API's audience. <c>RIOT2_MCP_AUTH_AUDIENCE</c>.</summary>
        public string? Audience { get; set; }

        /// <summary><c>cloudflare-access</c>: the team domain, such as <c>myteam.cloudflareaccess.com</c>. <c>RIOT2_MCP_CF_TEAM_DOMAIN</c>.</summary>
        public string? TeamDomain { get; set; }

        /// <summary>
        /// The highest role a remote caller gets; a token's <c>riot2.*</c> scopes or roles can only lower it.
        /// <c>RIOT2_MCP_REMOTE_MAX_ROLE</c>: <c>viewer</c>, <c>operator</c> (default) or <c>admin</c>.
        /// </summary>
        public string RemoteMaxRole { get; set; } = SecurityPolicies.Operator;

        /// <summary>When not empty, only these e-mail addresses or subjects may use the remote listener. <c>RIOT2_MCP_ALLOWED_SUBJECTS</c>, comma-separated.</summary>
        public IReadOnlyList<string> AllowedSubjects { get; set; } = [];

        /// <summary>Whether the remote listener runs.</summary>
        public bool RemoteEnabled => Enabled && Auth != McpAuthProfiles.None;

        /// <summary>The Cloudflare Access issuer, <c>https://&lt;team&gt;.cloudflareaccess.com</c>.</summary>
        public string? CloudflareIssuer => string.IsNullOrEmpty(TeamDomain) ? null : $"https://{TeamDomain}";

        public static McpOptions Read(SettingsReader settings, Microsoft.Extensions.Configuration.IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(settings);
            var options = new McpOptions
            {
                Enabled = settings.Flag(EnabledVariable),
                Auth = settings.Optional(AuthVariable)?.Trim().ToLowerInvariant() is { Length: > 0 } auth ? auth : McpAuthProfiles.None,
                RemotePort = settings.Integer(RemotePortVariable, DefaultRemotePort, 1, 65535),
                PublicUrl = Trimmed(settings.Optional(PublicUrlVariable)),
                Authority = Trimmed(settings.Optional(AuthorityVariable))?.TrimEnd('/'),
                Audience = Trimmed(settings.Optional(AudienceVariable)),
                TeamDomain = TeamDomainHost(Trimmed(settings.Optional(TeamDomainVariable))),
                AllowedSubjects = (settings.Optional(AllowedSubjectsVariable) ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            };

            var role = settings.Optional(RemoteMaxRoleVariable)?.Trim();
            if (!string.IsNullOrEmpty(role))
            {
                var policy = new[] { SecurityPolicies.Viewer, SecurityPolicies.Operator, SecurityPolicies.Admin }
                    .FirstOrDefault(x => string.Equals(x, role, StringComparison.OrdinalIgnoreCase));
                if (policy == null)
                    settings.AddError($"{RemoteMaxRoleVariable} must be viewer, operator or admin");
                else
                    options.RemoteMaxRole = policy;
            }

            if (!McpAuthProfiles.All.Contains(options.Auth))
            {
                settings.AddError($"{AuthVariable} must be {string.Join(", ", McpAuthProfiles.All)}");
                options.Auth = McpAuthProfiles.None;
            }
            if (options.Auth == McpAuthProfiles.None)
                return options;

            // The remote listener is never anonymous: every value its validation needs is required.
            if (!options.Enabled)
                settings.AddError($"{AuthVariable} needs {EnabledVariable}=true");
            if (!IsHttpsUrl(options.PublicUrl))
                settings.AddError($"{PublicUrlVariable} must be the public https URL of the MCP endpoint, for example https://riot.example.com/mcp");
            if (string.IsNullOrEmpty(options.Audience))
                settings.AddError($"{AudienceVariable} is required with {AuthVariable}={options.Auth}" +
                    (options.Auth == McpAuthProfiles.CloudflareAccess ? " (the Access application's AUD tag)" : ""));
            if (options.Auth == McpAuthProfiles.CloudflareAccess && string.IsNullOrEmpty(options.TeamDomain))
                settings.AddError($"{TeamDomainVariable} is required with {AuthVariable}={McpAuthProfiles.CloudflareAccess}, for example myteam.cloudflareaccess.com");
            if (options.Auth == McpAuthProfiles.Oidc && !IsHttpsUrl(options.Authority))
                settings.AddError($"{AuthorityVariable} must be the token issuer's https URL with {AuthVariable}={McpAuthProfiles.Oidc}");
            if (ApiPorts(configuration).Contains(options.RemotePort))
                settings.AddError($"{RemotePortVariable} ({options.RemotePort}) must not be a port the API listens on");
            return options;
        }

        /// <summary>The ports in <c>ASPNETCORE_URLS</c> and <c>ASPNETCORE_HTTP_PORTS</c>/<c>HTTPS_PORTS</c>.</summary>
        public static IReadOnlyList<int> ApiPorts(Microsoft.Extensions.Configuration.IConfiguration? configuration)
        {
            if (configuration == null)
                return [];
            var ports = new List<int>();
            foreach (var url in Split(configuration["urls"]))
                if (Uri.TryCreate(url.Replace("://*", "://localhost").Replace("://+", "://localhost"), UriKind.Absolute, out var uri))
                    ports.Add(uri.Port);
            foreach (var port in Split(configuration["http_ports"]).Concat(Split(configuration["https_ports"])))
                if (int.TryParse(port, out var number))
                    ports.Add(number);
            return ports;
        }

        private static IEnumerable<string> Split(string? value) =>
            (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        // Accepts "myteam.cloudflareaccess.com" or its https URL.
        private static string? TeamDomainHost(string? value)
        {
            if (value == null)
                return null;
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                return uri.Host;
            return Uri.CheckHostName(value) == UriHostNameType.Dns ? value.ToLowerInvariant() : "";
        }

        private static bool IsHttpsUrl(string? value) =>
            Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.UserInfo);
    }
}
