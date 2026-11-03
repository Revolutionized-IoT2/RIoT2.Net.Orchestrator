using System.Text.Json.Nodes;
using RIoT2.Elsa.Workflows.Management;

namespace RIoT2.Net.Orchestrator.Services.Workflows
{
    /// <summary>The values of <c>RIOT2_WORKFLOW_MODE</c> (ADR 0016).</summary>
    public static class WorkflowModes
    {
        /// <summary>Elsa runs in the orchestrator's process, with its database in <c>StoredObjects/elsa.sqlite.db</c>.</summary>
        public const string Embedded = "embedded";

        /// <summary>Elsa runs on a workflow host (the riot2-elsa container), reached over gRPC and HTTP.</summary>
        public const string Remote = "remote";
    }

    /// <summary>The workflow engine's state for the designer: where it runs and whether it is reachable.</summary>
    public sealed record WorkflowEngineStatus(string Mode, bool Available, string Engine, string? Host);

    /// <summary>
    /// The workflow management the designer uses (ADR 0016), in either mode: the embedded engine's
    /// <see cref="WorkflowManagementService"/> in process, or the remote workflow host's <c>/riot/workflows</c> API.
    /// </summary>
    public interface IWorkflowEngine
    {
        Task<WorkflowEngineStatus> GetStatusAsync(CancellationToken cancellationToken = default);
        Task<IReadOnlyList<WorkflowSummary>> ListAsync(CancellationToken cancellationToken = default);
        Task<WorkflowDetails?> GetAsync(string definitionId, int? version, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<WorkflowVersion>> ListVersionsAsync(string definitionId, CancellationToken cancellationToken = default);
        Task<WorkflowSaveResult> SaveAsync(string? definitionId, JsonObject model, bool publish, CancellationToken cancellationToken = default);
        Task<WorkflowSaveResult> ImportAsync(JsonObject model, bool publish, CancellationToken cancellationToken = default);
        Task<WorkflowSaveResult?> PublishAsync(string definitionId, CancellationToken cancellationToken = default);
        Task<bool> RetractAsync(string definitionId, CancellationToken cancellationToken = default);
        Task<bool> DeleteAsync(string definitionId, CancellationToken cancellationToken = default);
        Task<WorkflowExport?> ExportAsync(string definitionId, CancellationToken cancellationToken = default);
        Task<WorkflowRunsPage> ListRunsAsync(string? definitionId, string? status, int page, int pageSize, CancellationToken cancellationToken = default);
        Task<WorkflowRunDetails?> GetRunAsync(string instanceId, CancellationToken cancellationToken = default);
        Task<bool> CancelRunAsync(string instanceId, CancellationToken cancellationToken = default);
        Task<JsonObject> GetCatalogAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>The remote workflow host is offline or failed; the API answers 503.</summary>
    public sealed class WorkflowEngineUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
}
