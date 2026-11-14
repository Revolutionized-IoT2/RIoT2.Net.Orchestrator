using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RIoT2.Elsa.Workflows.Management;
using RIoT2.Net.Orchestrator.Services.Workflows;

namespace RIoT2.Net.Orchestrator.Services.Mcp.Tools
{
    /// <summary>
    /// The <see cref="McpToolGroups.Workflows"/> tools (workflow definitions and runs, read only) and the
    /// <see cref="McpToolGroups.WorkflowControl"/> tool <c>cancel_workflow_run</c>, through the same <see cref="IWorkflowEngine"/> as
    /// <c>/api/workflows</c>, in either workflow mode.
    /// </summary>
    [McpServerToolType]
    public sealed class WorkflowTools(IWorkflowEngine engine, McpJson json)
    {
        [McpServerTool(Name = "list_workflows", Title = "List workflows", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
        [McpToolGroup(McpToolGroups.Workflows)]
        [Description("Lists the automation workflows with their ids, names, versions and whether they are published (running).")]
        public Task<CallToolResult> ListWorkflowsAsync(CancellationToken cancellationToken) =>
            Engine(async () => json.Result(await engine.ListAsync(cancellationToken)));

        [McpServerTool(Name = "get_workflow", Title = "Get a workflow", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
        [McpToolGroup(McpToolGroups.Workflows)]
        [Description("Gets a workflow's definition (its activities and connections) by its definition id: the latest version, or the given one.")]
        public Task<CallToolResult> GetWorkflowAsync(
            [Description("The workflow's definition id from list_workflows.")] string definitionId,
            [Description("A version; omit for the latest.")] int? version = null,
            CancellationToken cancellationToken = default) =>
            Engine(async () => await engine.GetAsync(Required(definitionId), version, cancellationToken) is { } workflow
                ? json.Result(workflow)
                : McpJson.Error($"There is no workflow '{definitionId}'."));

        [McpServerTool(Name = "list_workflow_runs", Title = "List workflow runs", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
        [McpToolGroup(McpToolGroups.Workflows)]
        [Description("Lists workflow runs, newest first, with their status and times: of one workflow or all, and of one status or all.")]
        public Task<CallToolResult> ListWorkflowRunsAsync(
            [Description("Only this workflow's runs; omit for all.")] string? definitionId = null,
            [Description("Only runs with this status, such as Running, Finished, Faulted or Cancelled; omit for all.")] string? status = null,
            [Description("The page, from 0.")] int page = 0,
            [Description("Runs per page, 1-100; default 20.")] int pageSize = 20,
            CancellationToken cancellationToken = default)
        {
            if (page < 0 || pageSize < 1 || pageSize > Math.Min(100, WorkflowManagementService.MaxPageSize))
                return Task.FromResult(McpJson.Error($"page must be 0 or more and pageSize 1-{Math.Min(100, WorkflowManagementService.MaxPageSize)}."));
            return Engine(async () => json.Result(await engine.ListRunsAsync(Optional(definitionId), Optional(status), page, pageSize, cancellationToken)));
        }

        [McpServerTool(Name = "get_workflow_run", Title = "Get a workflow run", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
        [McpToolGroup(McpToolGroups.Workflows)]
        [Description("Gets one workflow run: its status, the activities it ran and any fault.")]
        public Task<CallToolResult> GetWorkflowRunAsync(
            [Description("The run's instance id from list_workflow_runs.")] string instanceId,
            CancellationToken cancellationToken = default) =>
            Engine(async () => await engine.GetRunAsync(Required(instanceId), cancellationToken) is { } run
                ? json.Result(run)
                : McpJson.Error($"There is no workflow run '{instanceId}'."));

        [McpServerTool(Name = "list_workflow_activities", Title = "List workflow activities", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
        [McpToolGroup(McpToolGroups.Workflows)]
        [Description("Lists the activity types a workflow can use (triggers such as RIoT2.RIoTTrigger, and actions), with their categories and " +
            "what they do. get_workflow_activity gives one's inputs, outcomes and an example for the definition model.")]
        public Task<CallToolResult> ListWorkflowActivitiesAsync(
            [Description("Only this category, such as RIoT2.")] string? category = null,
            [Description("Trigger or Action; omit for both.")] string? kind = null,
            [Description("Only types whose type, name or description contains this text (any case).")] string? query = null,
            CancellationToken cancellationToken = default) =>
            Engine(async () =>
            {
                var activities = (await engine.GetCatalogAsync(cancellationToken))["activities"] as JsonArray ?? [];
                var list = activities.OfType<JsonObject>()
                    .Where(a => Matches(a["category"], category) && Matches(a["kind"], kind)
                        && (string.IsNullOrWhiteSpace(query) || new[] { "type", "displayName", "description" }.Any(k => Contains(a[k], query))))
                    .Select(a => (JsonNode)new JsonObject
                    {
                        ["type"] = a["type"]?.DeepClone(),
                        ["displayName"] = a["displayName"]?.DeepClone(),
                        ["category"] = a["category"]?.DeepClone(),
                        ["kind"] = a["kind"]?.DeepClone(),
                        ["description"] = a["description"]?.DeepClone()
                    });
                return McpJson.Text(new JsonArray([.. list]).ToJsonString());
            });

        [McpServerTool(Name = "get_workflow_activity", Title = "Get a workflow activity", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
        [McpToolGroup(McpToolGroups.Workflows)]
        [Description("Gets an activity type: its inputs (with types, defaults and options), outputs and outcomes, and an example of the activity " +
            "and a connection as they go in a workflow definition model's root.activities and root.connections.")]
        public Task<CallToolResult> GetWorkflowActivityAsync(
            [Description("The activity type from list_workflow_activities, such as RIoT2.RIoTCompare.")] string type,
            CancellationToken cancellationToken = default) =>
            Engine(async () =>
            {
                var activities = (await engine.GetCatalogAsync(cancellationToken))["activities"] as JsonArray ?? [];
                var activity = activities.OfType<JsonObject>().FirstOrDefault(a => string.Equals((string?)a["type"], type?.Trim(), StringComparison.OrdinalIgnoreCase));
                if (activity == null)
                    return McpJson.Error($"There is no activity type '{type}'; list_workflow_activities lists them.");
                var result = (JsonObject)activity.DeepClone();
                result["example"] = ActivityExample(activity);
                result["howTo"] = ActivityHelp;
                return McpJson.Text(result.ToJsonString());
            });

        internal const string ActivityHelp =
            "Add the example to root.activities (root is an Elsa.Flowchart) with a unique id, and connect activities in root.connections from a " +
            "source outcome port to the target's In port. Wrapped inputs are { typeName, expression: { type, value } }; the other inputs take " +
            "their value directly. A RIoT2.RIoTTemplateItem input's value is JSON text: {\"Id\":\"<id from list_values>\",\"Name\":\"<name>\"," +
            "\"TemplateType\":2} with TemplateType 1 for a command, 2 for a report and 3 for a variable. Triggers start a workflow; get_workflow " +
            "shows a complete definition model to copy.";

        /// <summary>An activity of <paramref name="activity"/>'s type as the definition model has it, with each input's default.</summary>
        internal static JsonObject ActivityExample(JsonObject activity)
        {
            var id = Camel((string?)activity["displayName"] ?? "activity");
            var example = new JsonObject
            {
                ["id"] = id,
                ["type"] = activity["type"]?.DeepClone(),
                ["version"] = activity["version"]?.DeepClone() ?? 1,
                ["name"] = id,
                ["customProperties"] = (string?)activity["kind"] == "Trigger" ? new JsonObject { ["canStartWorkflow"] = true } : new JsonObject(),
                ["metadata"] = new JsonObject { ["designer"] = new JsonObject { ["position"] = new JsonObject { ["x"] = 0, ["y"] = 0 } } }
            };
            foreach (var input in (activity["inputs"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var key = (string?)input["key"];
                if (string.IsNullOrEmpty(key) || example.ContainsKey(key))
                    continue;
                var value = input["defaultValue"]?.DeepClone();
                example[key] = input["isWrapped"]?.GetValueKind() == JsonValueKind.False ? value : new JsonObject
                {
                    ["typeName"] = input["typeName"]?.DeepClone(),
                    ["expression"] = new JsonObject { ["type"] = input["defaultSyntax"]?.DeepClone() ?? "Literal", ["value"] = value }
                };
            }
            var outcome = (activity["outcomes"] as JsonArray)?.FirstOrDefault()?.DeepClone() ?? "Done";
            return new JsonObject
            {
                ["activity"] = example,
                ["connection"] = new JsonObject
                {
                    ["source"] = new JsonObject { ["activity"] = id, ["port"] = outcome },
                    ["target"] = new JsonObject { ["activity"] = "<next activity id>", ["port"] = "In" },
                    ["vertices"] = new JsonArray()
                }
            };
        }

        private static string Camel(string text)
        {
            var words = text.Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries);
            var joined = string.Concat(words.Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
            return joined.Length == 0 ? "activity" : char.ToLowerInvariant(joined[0]) + joined[1..];
        }

        private static bool Matches(JsonNode? value, string? wanted) =>
            string.IsNullOrWhiteSpace(wanted) || string.Equals(value?.GetValueKind() == JsonValueKind.String ? (string?)value : null, wanted.Trim(), StringComparison.OrdinalIgnoreCase);

        private static bool Contains(JsonNode? value, string text) =>
            value?.GetValueKind() == JsonValueKind.String && ((string?)value ?? "").Contains(text.Trim(), StringComparison.OrdinalIgnoreCase);

        [McpServerTool(Name = "cancel_workflow_run", Title = "Cancel a workflow run", ReadOnly = false, Idempotent = true, Destructive = true, OpenWorld = false)]
        [McpToolGroup(McpToolGroups.WorkflowControl, Write = true)]
        [Description("Cancels a running or suspended workflow run.")]
        public Task<CallToolResult> CancelWorkflowRunAsync(
            [Description("The run's instance id from list_workflow_runs.")] string instanceId,
            CancellationToken cancellationToken = default) =>
            Engine(async () => await engine.CancelRunAsync(Required(instanceId), cancellationToken)
                ? McpJson.Text($$"""{"instanceId":{{System.Text.Json.JsonSerializer.Serialize(instanceId)}},"cancelled":true}""")
                : McpJson.Error($"There is no workflow run '{instanceId}' to cancel."));

        /// <summary>Runs a call to the engine; an unavailable engine or an invalid argument is the tool's error.</summary>
        internal static async Task<CallToolResult> Engine(Func<Task<CallToolResult>> call)
        {
            try
            {
                return await call();
            }
            catch (WorkflowEngineUnavailableException x)
            {
                return McpJson.Error($"The workflow engine is unavailable: {x.Message}");
            }
            catch (ArgumentException x)
            {
                return McpJson.Error(x.Message);
            }
        }

        internal static string Required(string? id)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 200)
                throw new ArgumentException("An id of 1-200 characters is required.");
            return id.Trim();
        }

        private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
