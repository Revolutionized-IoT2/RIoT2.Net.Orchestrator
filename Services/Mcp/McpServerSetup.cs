using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Protocol;
using RIoT2.Core.Security;
using RIoT2.Net.Orchestrator.Services.Mcp.Tools;
using RIoT2.Net.Orchestrator.Services.Tools;

namespace RIoT2.Net.Orchestrator.Services.Mcp
{
    /// <summary>
    /// The MCP server (ADR 0018): <c>/mcp</c> on the API port with the security mode's policies, and, with <c>RIOT2_MCP_AUTH</c>, a
    /// remote listener on its own port that serves only <c>/mcp</c> and its OAuth metadata and always requires a valid token.
    /// </summary>
    public static class McpServerSetup
    {
        public const string Route = "/mcp";
        public const string RemoteScheme = "RIoT2Mcp";
        public const string RemotePolicy = "RIoT2McpRemote";
        public const string RateLimitPolicy = "RIoT2McpRemote";
        public const string ResourceMetadataPath = "/.well-known/oauth-protected-resource";
        public const string CloudflareHeader = "Cf-Access-Jwt-Assertion";
        public const string AuthHttpClientName = "riot2-mcp-auth";
        public const string AuthOutboundPurpose = "mcp-auth";
        public const int RemoteRequestsPerMinute = 120;

        // The OAuth scopes that lower a remote token's role.
        public const string ScopePrefix = "riot2.";
        public const string ReadScope = ScopePrefix + "read";
        public const string OperateScope = ScopePrefix + "operate";
        public const string AdminScope = ScopePrefix + "admin";

        // Internal paths the remote port's requests are moved to; the API port answers 404 for them.
        internal const string InternalPrefix = "/_riot2-mcp-remote";
        internal const string RemoteRoute = InternalPrefix + "/mcp";
        internal const string RemoteMetadataRoute = InternalPrefix + "/metadata";

        public const string Instructions =
            "RIoT2 is a home automation system. Use list_values to find reports (measurements and states), commands and variables by name, " +
            "read_value or read_values for current values, and send_command or set_variable to act; list_nodes and get_system_health show " +
            "whether nodes and devices work. Commands and changes act on a real home: confirm what the user asked for before sending them.";

        /// <summary>The services the AI access page uses, always; the MCP server and the remote listener when <paramref name="options"/> enable them.</summary>
        public static void AddRIoT2Mcp(this WebApplicationBuilder builder, McpOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            var services = builder.Services;
            services.AddSingleton<McpConfigurationService>();
            services.AddSingleton<McpActivity>();
            services.AddSingleton<McpWriteLimiter>();
            services.AddSingleton<RiotTools>();
            services.AddSingleton<McpJson>();
            if (!options.Enabled)
                return;

            services.AddMcpServer(server =>
                {
                    server.ServerInfo = new Implementation { Name = "riot2", Title = "RIoT2", Version = "0.0.0" };
                    server.ServerInstructions = Instructions;
                })
                // Stateless: every request stands alone, so the server works behind proxies and tunnels without session affinity.
                .WithHttpTransport(transport => transport.Stateless = true)
                .WithTools((IEnumerable<Type>)McpToolCatalog.ToolTypes)
                .WithRequestFilters(filters =>
                {
                    filters.AddListToolsFilter(McpGuard.ListTools);
                    filters.AddCallToolFilter(McpGuard.CallTool);
                });
            // The version the image was built as (Data/Manifest.json).
            services.AddOptions<ModelContextProtocol.Server.McpServerOptions>().Configure<IOrchestratorConfigurationService>((server, configuration) =>
            {
                if (server.ServerInfo != null && configuration.OrchestratorConfiguration.Manifest?.Version is { Length: > 0 } version)
                    server.ServerInfo.Version = version;
            });

            if (options.RemoteEnabled)
                AddRemoteListener(builder, options);
        }

        /// <summary>Maps <c>/mcp</c> and the remote listener's routes. Call before <c>UseRIoT2Security</c>: the remote port's gate must run before routing.</summary>
        public static void UseRIoT2McpGate(this WebApplication app, McpOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            if (!options.RemoteEnabled)
                return;
            var publicPath = new Uri(options.PublicUrl!).AbsolutePath.TrimEnd('/');
            app.Use(async (context, next) =>
            {
                var path = context.Request.Path;
                if (context.Connection.LocalPort == options.RemotePort)
                {
                    // The remote port serves the MCP endpoint and its metadata, nothing else.
                    if (path.Equals(Route, StringComparison.OrdinalIgnoreCase)
                        || (publicPath.Length > 0 && path.Equals(publicPath, StringComparison.OrdinalIgnoreCase)))
                        context.Request.Path = RemoteRoute;
                    else if (path.StartsWithSegments(ResourceMetadataPath, StringComparison.OrdinalIgnoreCase))
                        context.Request.Path = RemoteMetadataRoute;
                    else
                    {
                        context.Response.StatusCode = StatusCodes.Status404NotFound;
                        return;
                    }
                }
                else if (path.StartsWithSegments(InternalPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                await next();
            });
            // Routing runs after the gate, so the moved paths find their endpoints.
            app.UseRouting();
        }

        /// <summary>Maps the local <c>/mcp</c> (Viewer; tools check their own roles) and the remote listener's endpoints.</summary>
        public static void MapRIoT2Mcp(this WebApplication app, McpOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            if (!options.Enabled)
                return;
            app.MapMcp(Route).RequireAuthorization(SecurityPolicies.Viewer);
            if (!options.RemoteEnabled)
                return;
            app.MapMcp(RemoteRoute).RequireAuthorization(RemotePolicy).RequireRateLimiting(RateLimitPolicy);
            app.MapGet(RemoteMetadataRoute, () => Results.Json(ResourceMetadata(options), MetadataJson))
                .AllowAnonymous()
                .ExcludeFromDescription();
        }

        private static readonly System.Text.Json.JsonSerializerOptions MetadataJson =
            new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

        /// <summary>The OAuth protected resource metadata (RFC 9728) of the remote <c>/mcp</c>.</summary>
        public static ProtectedResourceMetadata ResourceMetadata(McpOptions options) => new()
        {
            Resource = options.PublicUrl!,
            AuthorizationServers = options.Auth == McpAuthProfiles.CloudflareAccess ? [options.CloudflareIssuer!] : [options.Authority!],
            BearerMethodsSupported = ["header"],
            ScopesSupported = [ReadScope, OperateScope, AdminScope],
            ResourceName = "RIoT2"
        };

        /// <summary>Where the metadata is, as RFC 9728 places it for the public URL: the well-known path, then the resource's path.</summary>
        public static string ResourceMetadataUrl(McpOptions options)
        {
            var url = new Uri(options.PublicUrl!);
            var path = url.AbsolutePath.TrimEnd('/');
            return $"{url.GetLeftPart(UriPartial.Authority)}{ResourceMetadataPath}{path}";
        }

        /// <summary>
        /// The role of a validated remote token: <see cref="McpOptions.RemoteMaxRole"/>, lowered to the highest RIoT2 role in the token's
        /// <c>scope</c>/<c>scp</c> (<c>riot2.read</c>, <c>riot2.operate</c>, <c>riot2.admin</c>) or <c>roles</c>/<c>role</c> claims when it has any.
        /// </summary>
        public static string RemoteRole(ClaimsPrincipal principal, string maxRole)
        {
            var granted = new List<string>();
            foreach (var claim in principal.Claims.Where(c => c.Type is "scope" or "scp"))
                foreach (var scope in claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    granted.Add(scope switch
                    {
                        ReadScope => SecurityPolicies.Viewer,
                        OperateScope => SecurityPolicies.Operator,
                        AdminScope => SecurityPolicies.Admin,
                        _ => ""
                    });
            foreach (var claim in principal.Claims.Where(c => c.Type is "roles" or "role" or ClaimTypes.Role))
                granted.Add(new[] { SecurityPolicies.Viewer, SecurityPolicies.Operator, SecurityPolicies.Admin }
                    .FirstOrDefault(x => string.Equals(x, claim.Value, StringComparison.OrdinalIgnoreCase)) ?? "");
            var highest = Roles.Highest(granted);
            return highest == null ? maxRole : Roles.Lowest(highest, maxRole);
        }

        /// <summary>Whether <paramref name="principal"/>'s e-mail, subject or name is in <paramref name="allowed"/>; always when it is empty.</summary>
        public static bool IsAllowedSubject(ClaimsPrincipal principal, IReadOnlyList<string> allowed) =>
            allowed.Count == 0 || principal.Claims
                .Where(c => c.Type is "email" or "sub" or "preferred_username" or "upn" or "common_name" or ClaimTypes.Email or ClaimTypes.NameIdentifier)
                .Any(c => allowed.Contains(c.Value, StringComparer.OrdinalIgnoreCase));

        /// <summary>The URLs the host listens on (<c>ASPNETCORE_URLS</c>, <c>HTTP_PORTS</c>, <c>HTTPS_PORTS</c>, or the default) and the remote port.</summary>
        public static IReadOnlyList<string> ListenUrls(IConfiguration configuration, int remotePort)
        {
            static IEnumerable<string> Split(string? value) => (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var urls = Split(configuration["urls"]).ToList();
            if (urls.Count == 0)
                urls.AddRange(Split(configuration["http_ports"]).Select(p => $"http://*:{p}").Concat(Split(configuration["https_ports"]).Select(p => $"https://*:{p}")));
            if (urls.Count == 0)
                urls.Add("http://localhost:5000");
            urls.Add($"http://*:{remotePort}");
            return urls;
        }

        private static void AddRemoteListener(WebApplicationBuilder builder, McpOptions options)
        {
            var services = builder.Services;
            // The remote port joins the API's URLs; explicit Kestrel endpoints would replace them.
            builder.WebHost.UseUrls([.. ListenUrls(builder.Configuration, options.RemotePort)]);

            services.AddHttpClient(AuthHttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30))
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
                .AddHttpMessageHandler(s => new OutboundUrlPolicyHandler(s.GetRequiredService<IOutboundUrlPolicy>(), AuthOutboundPurpose));

            services.AddAuthentication().AddJwtBearer(RemoteScheme, jwt =>
            {
                jwt.MapInboundClaims = false;
                jwt.RequireHttpsMetadata = true;
                jwt.SaveToken = false;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    RequireSignedTokens = true,
                    RequireExpirationTime = true,
                    ClockSkew = TimeSpan.FromMinutes(1),
                    NameClaimType = "email",
                    RoleClaimType = ClaimTypes.Role
                };
                if (options.Auth == McpAuthProfiles.CloudflareAccess)
                {
                    jwt.TokenValidationParameters.ValidIssuer = options.CloudflareIssuer;
                    jwt.TokenValidationParameters.ValidAudience = options.Audience;
                }
                else
                {
                    jwt.Authority = options.Authority;
                    // The audience the issuer puts in tokens for this API, or the resource itself (RFC 8707).
                    jwt.TokenValidationParameters.ValidAudiences = [options.Audience!, options.PublicUrl!];
                }
                jwt.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        if (options.Auth == McpAuthProfiles.CloudflareAccess)
                        {
                            // Cloudflare Access sends its JWT in a header of its own; a bearer token from the client is not used.
                            var assertion = context.Request.Headers[CloudflareHeader].ToString();
                            context.Token = string.IsNullOrEmpty(assertion) ? null : assertion;
                            if (context.Token == null)
                                context.NoResult();
                        }
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = context =>
                    {
                        var principal = context.Principal!;
                        if (!IsAllowedSubject(principal, options.AllowedSubjects))
                        {
                            OrchestratorMetrics.McpAuthFailures.Add(1, OrchestratorMetrics.Tag("reason", "subject"));
                            context.Fail("The token's subject is not in RIOT2_MCP_ALLOWED_SUBJECTS.");
                            return Task.CompletedTask;
                        }
                        var identity = new ClaimsIdentity(principal.Claims.Where(c => c.Type != ClaimTypes.Role && c.Type != McpClaims.Listener),
                            RemoteScheme, "email", ClaimTypes.Role);
                        identity.AddClaim(new Claim(ClaimTypes.Role, RemoteRole(principal, options.RemoteMaxRole)));
                        identity.AddClaim(new Claim(McpClaims.Listener, McpListeners.Remote));
                        context.Principal = new ClaimsPrincipal(identity);
                        return Task.CompletedTask;
                    },
                    OnAuthenticationFailed = context =>
                    {
                        OrchestratorMetrics.McpAuthFailures.Add(1, OrchestratorMetrics.Tag("reason", "token"));
                        context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("RIoT2.Mcp")
                            .LogWarning("Remote MCP request with an invalid token: {Error}", context.Exception.Message);
                        return Task.CompletedTask;
                    },
                    OnChallenge = context =>
                    {
                        context.HandleResponse();
                        if (context.AuthenticateFailure == null)
                            OrchestratorMetrics.McpAuthFailures.Add(1, OrchestratorMetrics.Tag("reason", "token"));
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        var error = context.AuthenticateFailure == null ? "" : ", error=\"invalid_token\"";
                        context.Response.Headers.WWWAuthenticate = options.Auth == McpAuthProfiles.Oidc
                            ? $"Bearer resource_metadata=\"{ResourceMetadataUrl(options)}\"{error}"
                            : $"Bearer realm=\"RIoT2\"{error}";
                        return Task.CompletedTask;
                    }
                };
            });

            // The keys come through the outbound policy's client; Cloudflare Access publishes only a key set, without discovery.
            services.AddOptions<JwtBearerOptions>(RemoteScheme).Configure<IHttpClientFactory>((jwt, clients) =>
            {
                jwt.Backchannel = clients.CreateClient(AuthHttpClientName);
                if (options.Auth == McpAuthProfiles.CloudflareAccess)
                    jwt.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                        $"{options.CloudflareIssuer}/cdn-cgi/access/certs",
                        new KeySetRetriever(options.CloudflareIssuer!),
                        new HttpDocumentRetriever(jwt.Backchannel) { RequireHttps = true });
            });

            services.AddAuthorization(authorization => authorization.AddPolicy(RemotePolicy, policy => policy
                .AddAuthenticationSchemes(RemoteScheme)
                .RequireAuthenticatedUser()
                .RequireClaim(McpClaims.Listener, McpListeners.Remote)));

            services.AddRateLimiter(limiter =>
            {
                limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                limiter.AddPolicy(RateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                    McpCaller.From(context.User).User,
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = RemoteRequestsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            });
        }

        /// <summary>Reads a JSON Web Key Set into a configuration with <paramref name="issuer"/>, for issuers without OpenID Connect discovery.</summary>
        internal sealed class KeySetRetriever(string issuer) : IConfigurationRetriever<OpenIdConnectConfiguration>
        {
            public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(string address, IDocumentRetriever retriever, CancellationToken cancel)
            {
                var keys = new JsonWebKeySet(await retriever.GetDocumentAsync(address, cancel));
                var configuration = new OpenIdConnectConfiguration { Issuer = issuer, JwksUri = address, JsonWebKeySet = keys };
                foreach (var key in keys.GetSigningKeys())
                    configuration.SigningKeys.Add(key);
                return configuration;
            }
        }
    }
}
