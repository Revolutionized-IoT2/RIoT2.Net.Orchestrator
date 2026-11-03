using System.Text.Json.Nodes;
using RIoT2.Elsa.Workflows.Management;

namespace RIoT2.Net.Orchestrator.Services.Workflows
{
    /// <summary>
    /// The embedded engine (RIOT2_WORKFLOW_MODE=embedded, ADR 0016): every call runs the workflow management service in a
    /// scope of its own, in this process.
    /// </summary>
    internal sealed class EmbeddedWorkflowEngine(IServiceScopeFactory scopes) : IWorkflowEngine
    {
        public Task<WorkflowEngineStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkflowEngineStatus(WorkflowModes.Embedded, true, WorkflowCatalogBuilder.Engine, null));

        public Task<IReadOnlyList<WorkflowSummary>> ListAsync(CancellationToken cancellationToken = default) =>
            RunAsync(x => x.ListAsync(cancellationToken));

        public Task<WorkflowDetails?> GetAsync(string definitionId, int? version, CancellationToken cancellationToken = default) =>
            RunAsync(x => x.GetAsync(definitionId, version, cancellationToken));

        public Task<IReadOnlyList<WorkflowVersion>> ListVersionsAsync(string definitionId, CancellationToken cancellationToken = default) =>
            RunAsync(x => x.ListVersionsAsync(definitionId, cancellationToken));

        public Task<WorkflowSaveResult> SaveAsync(string? definitionId, JsonObject model, bool publish, CancellationToken cancellationToken = default) =>
            RunAsync(x => x.SaveAsync(definitionId, model, publish, cancellationToken));

        public Task<WorkflowSaveResult> ImportAsync(JsonObject model, bool publish, CancellationToken cancellationToken = default) =>
            RunAsync(x => x.ImportAsync(model, publish, cancellationToken));

        public Task<WorkflowSaveResult?> PublishAsync(string definitionId, CancellationToken cancellationToken = default) =>
            RunAsync(x => x.PublishAsync(definitionId, cancellationToken));

        public Task<bool> RetractAsync(string definitionId, CancellationToken cancellationToken = default) =>
            RunAsync(x => x.RetractAsync(definitionId, cancellationToken));

        public Task<bool> DeleteAsync(string definitionId, CancellationToken cancellationToken = default) =>
            RunAsync(x => x.DeleteAsync(definitionId, cancellationToken));

        public Task<WorkflowExport?> ExportAsync(string definitionId, CancellationToken cancellationToken = default) =>
            RunAsync(x => x.ExportAsync(definitionId, cancellationToken));

        public Task<WorkflowRunsPage> ListRunsAsync(string? definitionId, string? status, int page, int pageSize, CancellationToken cancellationToken = default) =>
            RunAsync(x => x.ListRunsAsync(definitionId, status, page, pageSize, cancellationToken));

        public Task<WorkflowRunDetails?> GetRunAsync(string instanceId, CancellationToken cancellationToken = default) =>
            RunAsync(x => x.GetRunAsync(instanceId, cancellationToken));

        public Task<bool> CancelRunAsync(string instanceId, CancellationToken cancellationToken = default) =>
            RunAsync(x => x.CancelRunAsync(instanceId, cancellationToken));

        public Task<JsonObject> GetCatalogAsync(CancellationToken cancellationToken = default) =>
            RunAsync(x => Task.FromResult(x.GetCatalog()));

        private async Task<T> RunAsync<T>(Func<WorkflowManagementService, Task<T>> call)
        {
            await using var scope = scopes.CreateAsyncScope();
            return await call(scope.ServiceProvider.GetRequiredService<WorkflowManagementService>());
        }
    }
}
