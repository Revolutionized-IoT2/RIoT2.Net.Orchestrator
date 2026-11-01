namespace RIoT2.Net.Orchestrator.CustomJsonSettings
{
    /// <summary>
    /// The <c>json-naming-policy</c> request header (<c>pascal</c> or <c>lower</c>) selects other
    /// property names for the controllers' JSON. Nothing in RIoT2 sends it; it is deprecated since
    /// Orchestrator 1.11.0 and a later release removes it (plan M2 step 5). A response to a request
    /// with the header carries <c>Deprecation: true</c>, and the first request for each value is logged
    /// as a warning (later ones at debug level).
    /// </summary>
    public sealed class NamingPolicyHeaderDeprecation
    {
        public const string HeaderName = "json-naming-policy";

        // One flag per known value and one for anything else, so a client can't grow this.
        private static readonly string[] Policies = ["pascal", "lower"];
        private readonly int[] _logged = new int[Policies.Length + 1];
        private readonly RequestDelegate _next;
        private readonly ILogger<NamingPolicyHeaderDeprecation> _logger;

        public NamingPolicyHeaderDeprecation(RequestDelegate next, ILogger<NamingPolicyHeaderDeprecation> logger)
        {
            _next = next;
            _logger = logger;
        }

        public Task InvokeAsync(HttpContext context)
        {
            var value = context.Request.Headers[HeaderName].ToString();
            if (!string.IsNullOrEmpty(value))
            {
                context.Response.Headers["Deprecation"] = "true";
                var index = Array.IndexOf(Policies, value);
                var level = Interlocked.Exchange(ref _logged[index < 0 ? Policies.Length : index], 1) == 0 ? LogLevel.Warning : LogLevel.Debug;
                _logger.Log(level, "{Method} {Path} sent the deprecated {Header} header ({Policy}); a later orchestrator release removes it and always answers in camelCase",
                    context.Request.Method, context.Request.Path, HeaderName, index < 0 ? "unknown value" : value);
            }
            return _next(context);
        }
    }
}
