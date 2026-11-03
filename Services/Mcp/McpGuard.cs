using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RIoT2.Core.Utils;

namespace RIoT2.Net.Orchestrator.Services.Mcp
{
    /// <summary>Puts an MCP tool in a <see cref="McpToolGroups"/> group; <see cref="Write"/> tools count against the writes per minute.</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class McpToolGroupAttribute(string group) : Attribute
    {
        public string Group { get; } = group;
        public bool Write { get; init; }
    }

    /// <summary>An MCP tool's name, group, required role and whether it writes.</summary>
    public sealed record McpToolInfo(string Name, string Group, string Role, bool Write);

    /// <summary>Every MCP tool (ADR 0018), from the tool classes' attributes.</summary>
    public static class McpToolCatalog
    {
        public static readonly IReadOnlyList<Type> ToolTypes =
            [typeof(Tools.ValueTools), typeof(Tools.HealthTools), typeof(Tools.CommandTools), typeof(Tools.VariableTools),
             typeof(Tools.WorkflowTools), typeof(Tools.AdminTools)];

        public static readonly IReadOnlyDictionary<string, McpToolInfo> Tools = Build();

        public static McpToolInfo? Find(string? name) => name != null && Tools.TryGetValue(name, out var tool) ? tool : null;

        private static Dictionary<string, McpToolInfo> Build()
        {
            var tools = new Dictionary<string, McpToolInfo>(StringComparer.Ordinal);
            foreach (var method in ToolTypes.SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)))
            {
                var tool = method.GetCustomAttribute<McpServerToolAttribute>();
                if (tool == null)
                    continue;
                var group = method.GetCustomAttribute<McpToolGroupAttribute>()
                    ?? throw new InvalidOperationException($"The MCP tool {method.DeclaringType?.Name}.{method.Name} has no tool group.");
                var name = tool.Name ?? throw new InvalidOperationException($"The MCP tool {method.DeclaringType?.Name}.{method.Name} has no name.");
                tools.Add(name, new McpToolInfo(name, group.Group, McpToolGroups.RoleOf(group.Group), group.Write));
            }
            return tools;
        }
    }

    /// <summary>
    /// The MCP request filters that apply <see cref="McpConfiguration"/> and the caller's role to every list and call (ADR 0018):
    /// tools a caller may not use are not listed and answer with an error; writes are limited per minute; every call is logged,
    /// counted and kept in <see cref="McpActivity"/>.
    /// </summary>
    public static class McpGuard
    {
        /// <summary>Whether <paramref name="caller"/> may use <paramref name="tool"/>; the reason when not.</summary>
        public static string? Deny(McpToolInfo tool, McpCaller caller, McpConfiguration configuration)
        {
            if (caller.Listener == McpListeners.Remote ? !configuration.RemoteEnabled : !configuration.LocalEnabled)
                return $"MCP access over the {caller.Listener} listener is turned off on the AI access page.";
            if (!configuration.Groups.Contains(tool.Group))
                return $"The '{tool.Group}' tools are turned off on the AI access page.";
            if (!caller.Has(tool.Role))
                return $"The tool '{tool.Name}' needs the {tool.Role} role.";
            if (tool.Group == McpToolGroups.Commands && configuration.AllowedCommands.Count == 0)
                return "No commands are allowed on the AI access page.";
            return null;
        }

        public static McpRequestHandler<ListToolsRequestParams, ListToolsResult> ListTools(McpRequestHandler<ListToolsRequestParams, ListToolsResult> next) =>
            async (context, cancellationToken) =>
            {
                var result = await next(context, cancellationToken);
                var caller = McpCaller.From(context.User);
                var configuration = Services(context).GetRequiredService<McpConfigurationService>().Get();
                result.Tools = result.Tools
                    .Where(t => McpToolCatalog.Find(t.Name) is { } tool && Deny(tool, caller, configuration) == null)
                    .ToList();
                return result;
            };

        public static McpRequestHandler<CallToolRequestParams, CallToolResult> CallTool(McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
            async (context, cancellationToken) =>
            {
                var tool = McpToolCatalog.Find(context.Params?.Name);
                if (tool == null)
                    return await next(context, cancellationToken);

                var services = Services(context);
                var caller = McpCaller.From(context.User);
                var configuration = services.GetRequiredService<McpConfigurationService>().Get();
                var time = services.GetRequiredService<TimeProvider>();
                var started = time.GetUtcNow();
                var target = Target(context.Params?.Arguments);
                var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("RIoT2.Mcp");
                using var scope = logger.BeginIdScope("McpTool", tool.Name);

                var denied = Deny(tool, caller, configuration);
                if (denied == null && tool.Group == McpToolGroups.Commands && (target == null || !configuration.AllowedCommands.Contains(target)))
                    denied = $"The command '{target}' is not allowed. Use list_values with kind 'command' to see the allowed ones.";
                if (denied != null)
                    return Record(services, logger, caller, tool, target, started, McpOutcomes.Denied, denied, Error(denied));
                if (tool.Write && !services.GetRequiredService<McpWriteLimiter>().TryAcquire(configuration.WritesPerMinute))
                {
                    var limited = $"The limit of {configuration.WritesPerMinute} writes per minute is reached; try again later.";
                    return Record(services, logger, caller, tool, target, started, McpOutcomes.Limited, limited, Error(limited));
                }

                try
                {
                    var result = await next(context, cancellationToken);
                    var error = result.IsError == true ? string.Join(" ", result.Content.OfType<TextContentBlock>().Select(x => x.Text)) : null;
                    return Record(services, logger, caller, tool, target, started, result.IsError == true ? McpOutcomes.Error : McpOutcomes.Ok, error, result);
                }
                catch (Exception x) when (x is not OperationCanceledException)
                {
                    Record(services, logger, caller, tool, target, started, McpOutcomes.Failed, x.Message, null);
                    throw;
                }
            };

        /// <summary>A tool result with <paramref name="message"/> as its error.</summary>
        public static CallToolResult Error(string message) => new() { IsError = true, Content = [new TextContentBlock { Text = message }] };

        private static IServiceProvider Services(MessageContext context) =>
            context.Services ?? throw new InvalidOperationException("The MCP request has no services.");

        private static CallToolResult Record(IServiceProvider services, ILogger logger, McpCaller caller, McpToolInfo tool, string? target,
            DateTimeOffset started, string outcome, string? error, CallToolResult? result)
        {
            var duration = (long)(services.GetRequiredService<TimeProvider>().GetUtcNow() - started).TotalMilliseconds;
            services.GetRequiredService<McpActivity>().Add(new McpActivityEntry(started, caller.Listener, caller.User, tool.Name, target, outcome,
                duration, Shorten(error)));
            OrchestratorMetrics.McpToolCalls.Add(1, OrchestratorMetrics.Tag("tool", tool.Name), OrchestratorMetrics.Tag("listener", caller.Listener),
                OrchestratorMetrics.Tag("outcome", outcome));
            var level = outcome is McpOutcomes.Ok or McpOutcomes.Error ? (tool.Write ? LogLevel.Information : LogLevel.Debug) : LogLevel.Warning;
            logger.Log(level, "MCP tool {Tool} on {Target} by {User} ({Listener}): {Outcome} in {DurationMs} ms. {Error}",
                tool.Name, target ?? "-", caller.User, caller.Listener, outcome, duration, error);
            return result ?? Error(error ?? outcome);
        }

        // The id the call is about, for the activity log.
        private static string? Target(IDictionary<string, JsonElement>? arguments)
        {
            if (arguments == null)
                return null;
            foreach (var name in new[] { "id", "instanceId", "definitionId", "nodeId" })
                if (arguments.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String)
                    return Shorten(value.GetString(), 200);
            return null;
        }

        private static string? Shorten(string? text, int max = 500) => text == null || text.Length <= max ? text : text[..max] + "…";
    }
}
