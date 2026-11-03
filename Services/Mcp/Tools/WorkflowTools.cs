using System.ComponentModel;
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
