using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc.Filters;

namespace RIoT2.Net.Orchestrator.Controllers
{
    /// <summary>
    /// Marks the <c>GET</c> route of an action that changes state as a deprecated alias of its
    /// <c>/api/v2</c> route (backlog item 7). A request through the alias still works, gets the headers
    /// <c>Deprecation: true</c> and <c>Link: &lt;successor&gt;; rel="successor-version"</c>, and is logged as a
    /// warning once per route. The aliases are removed in the release after the UI stops using them.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class DeprecatedGetAliasAttribute(string successor) : ActionFilterAttribute
    {
        private static readonly ConcurrentDictionary<string, bool> _logged = new();

        /// <summary>The route to use instead, e.g. <c>DELETE /api/v2/nodes/{id}</c>.</summary>
        public string Successor { get; } = successor;

        public override void OnActionExecuting(ActionExecutingContext context)
        {
            var http = context.HttpContext;
            if (!HttpMethods.IsGet(http.Request.Method))
                return;
            var successorPath = Successor[(Successor.IndexOf(' ') + 1)..];
            http.Response.Headers["Deprecation"] = "true";
            http.Response.Headers.Link = $"<{successorPath}>; rel=\"successor-version\"";
            var route = context.ActionDescriptor.AttributeRouteInfo?.Template ?? http.Request.Path.Value;
            if (_logged.TryAdd(route, true))
                http.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("RIoT2.Net.Orchestrator.DeprecatedRoutes")
                    .LogWarning("GET /{Route} changes state and is deprecated; use {Successor}. It is removed in a later release",
                        route, Successor);
        }
    }
}
