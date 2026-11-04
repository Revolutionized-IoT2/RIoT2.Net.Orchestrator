using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RIoT2.Net.Orchestrator.Services.Commands;
using RIoT2.Net.Orchestrator.Services.Tools;

namespace RIoT2.Net.Orchestrator.Services.Mcp.Tools
{
    /// <summary>
    /// The <see cref="McpToolGroups.Commands"/> tool: <c>send_command</c>, only for <see cref="McpConfiguration.AllowedCommands"/>, through the
    /// command outbox like every command.
    /// </summary>
    [McpServerToolType]
    public sealed class CommandTools(RiotTools tools, McpConfigurationService configuration)
    {
        public const int DefaultWaitSeconds = 10;

        [McpServerTool(Name = "send_command", Title = "Send a command", ReadOnly = false, Idempotent = false, Destructive = true, OpenWorld = true)]
        [McpToolGroup(McpToolGroups.Commands, Write = true)]
        [Description("Sends a command to a device (switch a light, set a thermostat, ...) and waits for the device's result. Only commands " +
            "list_values marks 'allowed' can be sent. The status is executed when the device ran it; sent, expired or failed otherwise.")]
        public async Task<CallToolResult> SendCommandAsync(
            [Description("The command's id from list_values.")] string id,
            [Description("The command's value: a boolean, number, text or object, as its type in list_values expects.")] JsonElement value,
            [Description("How long to wait for the device's result, 1-30 seconds; default 10.")] int waitSeconds = DefaultWaitSeconds,
            CancellationToken cancellationToken = default)
        {
            var allowed = configuration.Get().AllowedCommands;
            if (string.IsNullOrWhiteSpace(id) || !allowed.Contains(id))
                return McpJson.Error($"The command '{id}' is not allowed. Use list_values with kind 'command' to see the allowed ones.");
            if (waitSeconds is < 1 or > 30)
                return McpJson.Error("waitSeconds must be 1-30.");

            JsonNode? node = McpJson.Argument(value);
            var delivery = await tools.SendAsync(id, node, TimeSpan.FromSeconds(waitSeconds), cancellationToken);
            if (delivery == null)
                return McpJson.Error($"No node has the command '{id}'.");
            var result = new JsonObject
            {
                ["id"] = id,
                ["correlationId"] = delivery.CorrelationId,
                ["status"] = delivery.Status,
                ["error"] = delivery.Error
            };
            return delivery.Status is CommandDeliveryStatus.Failed or CommandDeliveryStatus.UnknownCommand or CommandDeliveryStatus.Expired
                ? new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = result.ToJsonString() }] }
                : McpJson.Text(result.ToJsonString());
        }

        [McpServerTool(Name = "plan_command", Title = "Plan a command", ReadOnly = false, Idempotent = false, Destructive = true, OpenWorld = true)]
        [McpToolGroup(McpToolGroups.Commands, Write = true)]
        [Description("Plans a command's values at future times, such as a heat pump's temperatures for the cheapest hours: the orchestrator " +
            "sends each step's value at its time. The plan replaces the command's earlier plan. Only commands list_values marks 'allowed'. " +
            "1-200 steps, at least a minute apart, within 7 days.")]
        public CallToolResult PlanCommand(
            [Description("The command's id from list_values.")] string id,
            [Description("The steps: a list of { \"at\": an ISO 8601 time with a time zone, \"value\": the command's value from then on }.")] JsonElement steps,
            [Description("A short name for the plan.")] string? name = null)
        {
            var allowed = configuration.Get().AllowedCommands;
            if (string.IsNullOrWhiteSpace(id) || !allowed.Contains(id))
                return McpJson.Error($"The command '{id}' is not allowed. Use list_values with kind 'command' to see the allowed ones.");
            if (name is { Length: > 200 })
                return McpJson.Error("The name is longer than 200 characters.");
            var result = tools.PlanCommand(id, McpJson.Argument(steps), name, "MCP");
            return result.ContainsKey("error") ? McpJson.Error((string?)result["error"] ?? "") : McpJson.Text(result.ToJsonString());
        }
    }
}
