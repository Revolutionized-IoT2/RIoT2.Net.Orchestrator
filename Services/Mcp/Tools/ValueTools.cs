using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RIoT2.Net.Orchestrator.Services.Tools;

namespace RIoT2.Net.Orchestrator.Services.Mcp.Tools
{
    /// <summary>The <see cref="McpToolGroups.Read"/> tools: the reports, commands and variables, and their values.</summary>
    [McpServerToolType]
    public sealed class ValueTools(RiotTools tools, McpConfigurationService configuration)
    {
        public const int MaxIds = 50;

        [McpServerTool(Name = "list_values", Title = "List values", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
        [McpToolGroup(McpToolGroups.Read)]
        [Description("Lists the home's reports (device measurements and states), commands (things devices can do) and variables, " +
            "with their ids, names, nodes, devices and value types. Commands carry 'allowed': whether send_command may send them. " +
            "Use the ids with read_value, read_values, send_command and set_variable.")]
        public CallToolResult ListValues(
            [Description("Only this kind: report, command or variable. Omit for all.")] string? kind = null,
            [Description("Only items whose name, id, node or device contains this text (any case).")] string? query = null)
        {
            if (!string.IsNullOrEmpty(kind) && !RiotTools.Kinds.Contains(kind))
                return McpJson.Error($"The kind must be {string.Join(", ", RiotTools.Kinds)}, or omitted.");
            if (query is { Length: > 200 })
                return McpJson.Error("The query is longer than 200 characters.");
            var allowed = configuration.Get().AllowedCommands.ToHashSet(StringComparer.Ordinal);
            var items = tools.List(kind, query);
            foreach (var item in items.OfType<JsonObject>().Where(x => (string?)x["kind"] == RiotTools.CommandKind))
                item["allowed"] = allowed.Contains((string?)item["id"] ?? "");
            return McpJson.Text(items.ToJsonString());
        }

        [McpServerTool(Name = "read_value", Title = "Read a value", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
        [McpToolGroup(McpToolGroups.Read)]
        [Description("Reads the current value of one report, command or variable by its id, with its time stamp for reports.")]
        public CallToolResult ReadValue([Description("The id from list_values.")] string id)
        {
            var value = tools.Read(id);
            return value.ContainsKey("error") ? McpJson.Error((string?)value["error"] ?? "") : McpJson.Text(value.ToJsonString());
        }

        [McpServerTool(Name = "read_values", Title = "Read values", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
        [McpToolGroup(McpToolGroups.Read)]
        [Description("Reads the current values of up to 50 reports, commands or variables at once; an unknown id gets an 'error'.")]
        public CallToolResult ReadValues([Description("The ids from list_values.")] string[] ids)
        {
            if (ids == null || ids.Length is 0 or > MaxIds)
                return McpJson.Error($"Give 1-{MaxIds} ids.");
            var values = new JsonArray();
            foreach (var id in ids.Distinct(StringComparer.Ordinal))
            {
                var value = tools.Read(id);
                value.TryAdd("id", id);
                values.Add(value);
            }
            return McpJson.Text(values.ToJsonString());
        }

        [McpServerTool(Name = "read_forecast", Title = "Read a forecast", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
        [McpToolGroup(McpToolGroups.Read)]
        [Description("Reads a forecast report (list_values marks them 'forecast'): a weather forecast or electricity prices ahead. Gives the " +
            "points { start, end, value } of a window with its min, max, time-weighted average, sum and the value now; with rank, the lowest " +
            "and highest points (for example the cheapest hours). 'complete' tells whether the forecast covers the whole window.")]
        public CallToolResult ReadForecast(
            [Description("The report's id from list_values.")] string id,
            [Description("today or tomorrow (local days), next (the next hours, the default), range (from until to) or all.")] string? window = null,
            [Description("With next: 1-384 hours; default 24.")] int? hours = null,
            [Description("With range: the start, ISO 8601.")] string? from = null,
            [Description("With range: the end, ISO 8601.")] string? to = null,
            [Description("How many lowest and highest points to list, 0-100.")] int? rank = null)
        {
            var value = tools.ReadForecast(id, window, hours, from, to, rank);
            return value.ContainsKey("error") ? McpJson.Error((string?)value["error"] ?? "") : McpJson.Text(value.ToJsonString());
        }

        [McpServerTool(Name = "list_command_plans", Title = "List command plans", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
        [McpToolGroup(McpToolGroups.Read)]
        [Description("Lists the command plans, newest first: schedules of a command's future values that the orchestrator sends at their " +
            "times, with each step's time, value and status.")]
        public CallToolResult ListCommandPlans(
            [Description("Only this command's plans.")] string? commandId = null,
            [Description("Only the active plans; default true.")] bool activeOnly = true)
        {
            if (commandId is { Length: > 200 })
                return McpJson.Error("The commandId is longer than 200 characters.");
            return McpJson.Text(tools.ListPlans(commandId, activeOnly).ToJsonString());
        }
    }
}
