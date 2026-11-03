using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RIoT2.Core.Models;
using RIoT2.Net.Orchestrator.Services.Persistence;
using RIoT2.Net.Orchestrator.Services.Tools;
using RIoT2.Net.Orchestrator.Services.Workflows;
using CoreJson = RIoT2.Core.Utils.Json;

namespace RIoT2.Net.Orchestrator.Services.Mcp.Tools
{
    /// <summary>
    /// The <see cref="McpToolGroups.Admin"/> tools, off by default: node configurations (device parameters always redacted),
    /// variable definitions and workflows. There is no delete tool, and none for security, backups, language model
    /// connections, Matter or the MCP settings.
    /// </summary>
    [McpServerToolType]
    public sealed class AdminTools(IOrchestratorConfigurationService configuration, IStoredObjectStore store, IWorkflowEngine engine, McpJson json)
    {
        /// <summary>What every device parameter value reads as; saving it keeps the stored value.</summary>
        public const string Redacted = "[redacted]";
        public const int MaxNameLength = 100;
        public const int MaxDescriptionLength = 1000;

        [McpServerTool(Name = "get_node_configuration", Title = "Get a node configuration", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
        [McpToolGroup(McpToolGroups.Admin)]
        [Description("Gets a node's saved configuration: its devices with their report and command templates. Every device parameter value " +
            "reads as \"[redacted]\"; send it back unchanged to keep the stored value.")]
        public CallToolResult GetNodeConfiguration([Description("The node's id from list_nodes.")] string nodeId)
        {
            var node = configuration.NodeConfigurations.FirstOrDefault(x => x.Id == nodeId);
            if (node == null)
                return McpJson.Error($"There is no node configuration '{nodeId}'.");
            return McpJson.Text(Redact(JsonNode.Parse(CoreJson.Serialize(node))!).ToJsonString());
        }

        [McpServerTool(Name = "save_node_configuration", Title = "Save a node configuration", ReadOnly = false, Idempotent = true, Destructive = true, OpenWorld = false)]
        [McpToolGroup(McpToolGroups.Admin, Write = true)]
        [Description("Saves a node configuration, as get_node_configuration returns it, and sends it to the node, which restarts the changed " +
            "devices. Device parameters that read \"[redacted]\" keep their stored values.")]
        public CallToolResult SaveNodeConfiguration(
            [Description("The whole node configuration object, with its id.")] JsonElement nodeConfiguration)
        {
            if (McpJson.Argument(nodeConfiguration) is not JsonObject node || string.IsNullOrWhiteSpace((string?)node["id"]))
                return McpJson.Error("The node configuration must be an object with an id.");
            var stored = configuration.NodeConfigurations.FirstOrDefault(x => x.Id == (string?)node["id"]);
            var error = Restore(node, stored);
            if (error != null)
                return McpJson.Error(error);
            try
            {
                var id = configuration.SaveNodeConfiguration(node.ToJsonString());
                return string.IsNullOrEmpty(id)
                    ? McpJson.Error("The node configuration could not be saved.")
                    : McpJson.Text(new JsonObject { ["id"] = id, ["saved"] = true }.ToJsonString());
            }
            catch (Exception x) when (x is JsonException or ArgumentException or InvalidOperationException or FormatException)
            {
                return McpJson.Error($"The node configuration is not valid: {x.Message}");
            }
        }

        [McpServerTool(Name = "save_variable", Title = "Save a variable", ReadOnly = false, Idempotent = false, Destructive = true, OpenWorld = false)]
        [McpToolGroup(McpToolGroups.Admin, Write = true)]
        [Description("Creates a variable (omit id) or changes a variable's name, description and value.")]
        public CallToolResult SaveVariable(
            [Description("The name.")] string name,
            [Description("The value: a boolean, number, text or object; its type becomes the variable's type.")] JsonElement value,
            [Description("The variable's id from list_values; omit to create a variable.")] string? id = null,
            [Description("A description.")] string? description = null,
            [Description("Whether the value survives a restart.")] bool isPersistent = false)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaxNameLength)
                return McpJson.Error($"The name must be 1-{MaxNameLength} characters.");
            if (description is { Length: > MaxDescriptionLength })
                return McpJson.Error($"The description must be at most {MaxDescriptionLength} characters.");
            Variable? existing = null;
            if (!string.IsNullOrWhiteSpace(id) && (existing = store.Get<Variable>(id)) == null)
                return McpJson.Error($"There is no variable '{id}'; omit the id to create one.");
            var (converted, error) = VariableValues.Convert(McpJson.Argument(value), null);
            if (error != null)
                return McpJson.Error(error);

            var variable = new Variable
            {
                Id = existing?.Id ?? "",
                Name = name.Trim(),
                Description = description?.Trim() ?? "",
                IsPersistant = isPersistent,
                Value = converted
            };
            var saved = store.Save(variable);
            return saved == null
                ? McpJson.Error("The variable could not be stored.")
                : McpJson.Text(new JsonObject { ["id"] = saved, ["name"] = variable.Name, ["type"] = variable.Type.ToString(), ["value"] = RiotTools.Value(variable.Value) }.ToJsonString());
        }

        [McpServerTool(Name = "save_workflow", Title = "Save a workflow", ReadOnly = false, Idempotent = false, Destructive = true, OpenWorld = false)]
        [McpToolGroup(McpToolGroups.Admin, Write = true)]
        [Description("Creates a workflow (omit definitionId) or saves a new draft of one from its definition model, as get_workflow returns " +
            "it, and publishes it when asked. A published workflow runs on its triggers.")]
        public Task<CallToolResult> SaveWorkflowAsync(
            [Description("The workflow's definition model.")] JsonElement model,
            [Description("The workflow's definition id; omit to create a workflow.")] string? definitionId = null,
            [Description("Publish the saved version, so it runs.")] bool publish = false,
            CancellationToken cancellationToken = default)
        {
            if (McpJson.Argument(model) is not JsonObject definition)
                return Task.FromResult(McpJson.Error("The model must be a workflow definition object."));
            return WorkflowTools.Engine(async () => Saved(await engine.SaveAsync(
                string.IsNullOrWhiteSpace(definitionId) ? null : WorkflowTools.Required(definitionId), definition, publish, cancellationToken)));
        }

        [McpServerTool(Name = "publish_workflow", Title = "Publish a workflow", ReadOnly = false, Idempotent = true, Destructive = true, OpenWorld = false)]
        [McpToolGroup(McpToolGroups.Admin, Write = true)]
        [Description("Publishes a workflow's latest version, so it runs on its triggers.")]
        public Task<CallToolResult> PublishWorkflowAsync(
            [Description("The workflow's definition id from list_workflows.")] string definitionId,
            CancellationToken cancellationToken = default) =>
            WorkflowTools.Engine(async () => await engine.PublishAsync(WorkflowTools.Required(definitionId), cancellationToken) is { } result
                ? Saved(result)
                : McpJson.Error($"There is no workflow '{definitionId}'."));

        [McpServerTool(Name = "retract_workflow", Title = "Retract a workflow", ReadOnly = false, Idempotent = true, Destructive = true, OpenWorld = false)]
        [McpToolGroup(McpToolGroups.Admin, Write = true)]
        [Description("Unpublishes a workflow, so its triggers stop.")]
        public Task<CallToolResult> RetractWorkflowAsync(
            [Description("The workflow's definition id from list_workflows.")] string definitionId,
            CancellationToken cancellationToken = default) =>
            WorkflowTools.Engine(async () => await engine.RetractAsync(WorkflowTools.Required(definitionId), cancellationToken)
                ? McpJson.Text(new JsonObject { ["definitionId"] = definitionId, ["retracted"] = true }.ToJsonString())
                : McpJson.Error($"There is no workflow '{definitionId}'."));

        /// <summary><paramref name="node"/> with every device parameter value replaced by <see cref="Redacted"/>.</summary>
        public static JsonNode Redact(JsonNode node)
        {
            foreach (var device in Devices(node))
                if (device["deviceParameters"] is JsonObject parameters)
                    foreach (var key in parameters.Select(x => x.Key).ToList())
                        parameters[key] = Redacted;
            return node;
        }

        /// <summary>
        /// Puts the stored values back for the device parameters that read <see cref="Redacted"/>; the problem when a redacted parameter
        /// has no stored value (a new device or parameter).
        /// </summary>
        public static string? Restore(JsonNode node, NodeDeviceConfiguration? stored)
        {
            foreach (var device in Devices(node))
            {
                if (device["deviceParameters"] is not JsonObject parameters)
                    continue;
                var deviceId = (string?)device["id"];
                var storedParameters = stored?.DeviceConfigurations?.FirstOrDefault(x => x.Id == deviceId)?.DeviceParameters;
                foreach (var (key, value) in parameters.ToList())
                {
                    if (value is not JsonValue v || v.GetValueKind() != JsonValueKind.String || v.GetValue<string>() != Redacted)
                        continue;
                    if (storedParameters == null || !storedParameters.TryGetValue(key, out var original))
                        return $"The parameter '{key}' of device '{deviceId}' reads \"{Redacted}\" but has no stored value; give its value.";
                    parameters[key] = original;
                }
            }
            return null;
        }

        private static IEnumerable<JsonObject> Devices(JsonNode node) =>
            (node["deviceConfigurations"] as JsonArray ?? []).OfType<JsonObject>();

        private CallToolResult Saved(Elsa.Workflows.Management.WorkflowSaveResult result)
        {
            var text = json.ToNode(result)?.ToJsonString() ?? "null";
            return result.Succeeded ? McpJson.Text(text) : new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = text }] };
        }
    }
}
