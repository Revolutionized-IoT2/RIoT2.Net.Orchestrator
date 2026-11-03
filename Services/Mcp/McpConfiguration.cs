using RIoT2.Core.Interfaces;
using RIoT2.Core.Security;
using RIoT2.Net.Orchestrator.Services.Persistence;

namespace RIoT2.Net.Orchestrator.Services.Mcp
{
    /// <summary>The MCP tool groups (ADR 0018); each group's tools need its <see cref="RoleOf"/> role.</summary>
    public static class McpToolGroups
    {
        /// <summary>Values: list and read reports, commands and variables.</summary>
        public const string Read = "read";

        /// <summary>Nodes, their devices' states and the system health.</summary>
        public const string Health = "health";

        /// <summary>Send commands from <see cref="McpConfiguration.AllowedCommands"/>.</summary>
        public const string Commands = "commands";

        /// <summary>Set variable values.</summary>
        public const string Variables = "variables";

        /// <summary>Workflow definitions and runs, read only.</summary>
        public const string Workflows = "workflows";

        /// <summary>Cancel workflow runs.</summary>
        public const string WorkflowControl = "workflowControl";

        /// <summary>Configuration writes: variables, node configurations and workflows. Off by default.</summary>
        public const string Admin = "admin";

        public static readonly IReadOnlyList<string> All = [Read, Health, Commands, Variables, Workflows, WorkflowControl, Admin];

        /// <summary>The groups a new installation has: reading only.</summary>
        public static readonly IReadOnlyList<string> Defaults = [Read, Health, Workflows];

        /// <summary>The <see cref="SecurityPolicies"/> role a group's tools need.</summary>
        public static string RoleOf(string group) => group switch
        {
            Read or Health or Workflows => SecurityPolicies.Viewer,
            Commands or Variables or WorkflowControl => SecurityPolicies.Operator,
            Admin => SecurityPolicies.Admin,
            _ => throw new ArgumentOutOfRangeException(nameof(group), group, "Not an MCP tool group.")
        };
    }

    /// <summary>
    /// The MCP tool policy (ADR 0018), stored as <c>StoredObjects/McpConfiguration/default.json</c> and changed on the UI's
    /// AI access page. No MCP tool can change it.
    /// </summary>
    public sealed class McpConfiguration : IStoredObject
    {
        public const string DefaultId = "default";
        public const int DefaultWritesPerMinute = 10;
        public const int MaxWritesPerMinute = 600;
        public const int MaxAllowedCommands = 1000;

        public string Id { get; set; } = DefaultId;

        /// <summary>Whether the local <c>/mcp</c> on the API port answers.</summary>
        public bool LocalEnabled { get; set; } = true;

        /// <summary>Whether the remote listener answers: a switch that cuts internet access off without a restart.</summary>
        public bool RemoteEnabled { get; set; } = true;

        /// <summary>The enabled <see cref="McpToolGroups"/>.</summary>
        public List<string> Groups { get; set; } = [.. McpToolGroups.Defaults];

        /// <summary>The command template ids <c>send_command</c> may send; empty allows none.</summary>
        public List<string> AllowedCommands { get; set; } = [];

        /// <summary>The most commands, variable changes and configuration writes per minute, across every MCP client.</summary>
        public int WritesPerMinute { get; set; } = DefaultWritesPerMinute;
    }

    /// <summary>A new tool policy from the UI.</summary>
    public sealed record McpConfigurationSave(bool LocalEnabled, bool RemoteEnabled, List<string>? Groups, List<string>? AllowedCommands, int WritesPerMinute);

    /// <summary>Reads and validates the stored <see cref="McpConfiguration"/>; the defaults when none is stored.</summary>
    public sealed class McpConfigurationService(IStoredObjectStore store)
    {
        public McpConfiguration Get() => store.Get<McpConfiguration>(McpConfiguration.DefaultId) ?? new McpConfiguration();

        public (McpConfiguration? Configuration, IReadOnlyList<string> Errors) Save(McpConfigurationSave request)
        {
            ArgumentNullException.ThrowIfNull(request);
            var errors = new List<string>();
            var groups = (request.Groups ?? []).Select(x => x?.Trim() ?? "").Distinct(StringComparer.Ordinal).ToList();
            foreach (var group in groups.Where(x => !McpToolGroups.All.Contains(x)))
                errors.Add($"'{group}' is not a tool group; the groups are {string.Join(", ", McpToolGroups.All)}.");
            var commands = (request.AllowedCommands ?? []).Select(x => x?.Trim() ?? "").Distinct(StringComparer.Ordinal).ToList();
            if (commands.Any(x => x.Length is 0 or > 200))
                errors.Add("Every allowed command must be a command id of 1-200 characters.");
            if (commands.Count > McpConfiguration.MaxAllowedCommands)
                errors.Add($"At most {McpConfiguration.MaxAllowedCommands} commands can be allowed.");
            if (request.WritesPerMinute is < 1 or > McpConfiguration.MaxWritesPerMinute)
                errors.Add($"The writes per minute must be 1-{McpConfiguration.MaxWritesPerMinute}.");
            if (errors.Count > 0)
                return (null, errors);

            var configuration = new McpConfiguration
            {
                LocalEnabled = request.LocalEnabled,
                RemoteEnabled = request.RemoteEnabled,
                Groups = McpToolGroups.All.Where(groups.Contains).ToList(),
                AllowedCommands = commands,
                WritesPerMinute = request.WritesPerMinute
            };
            return store.Save(configuration) == null ? (null, ["The MCP configuration could not be stored."]) : (configuration, []);
        }
    }
}
