using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RIoT2.Net.Orchestrator.Services.Health;

namespace RIoT2.Net.Orchestrator.Services.Mcp.Tools
{
    /// <summary>The <see cref="McpToolGroups.Health"/> tools: the nodes and the system health, as the UI's Nodes and System pages show them.</summary>
    [McpServerToolType]
    public sealed class HealthTools(IOnlineNodeService nodes, SystemHealthService health, McpJson json)
    {
        [McpServerTool(Name = "list_nodes", Title = "List nodes", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
        [McpToolGroup(McpToolGroups.Health)]
        [Description("Lists the configured nodes: whether each is online, whether it runs its saved configuration (syncState), and the state " +
            "and last error of each of its devices. Asks each online node for its devices' states.")]
        public async Task<CallToolResult> ListNodesAsync() => json.Result(await nodes.GetNodesAsync());

        [McpServerTool(Name = "get_system_health", Title = "System health", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
        [McpToolGroup(McpToolGroups.Health)]
        [Description("The orchestrator's health checks (MQTT, outbox), the outbox, and every node with its presence history since the " +
            "orchestrator started, its sync state, and each device's state and last error.")]
        public async Task<CallToolResult> GetSystemHealthAsync(CancellationToken cancellationToken) => json.Result(await health.GetAsync(cancellationToken));
    }
}
