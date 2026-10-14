using RIoT2.Core.Models;

namespace RIoT2.Net.Orchestrator.Services.Automation
{
    /// <summary>
    /// The automation engine as the orchestrator sees it (ADR 0008). The orchestrator reports domain
    /// events here and never calls an engine client itself. Elsa is the only implementation
    /// (<see cref="ElsaAutomationProvider"/>, ADR 0003); this is not a plugin point for a second engine.
    /// </summary>
    internal interface IAutomationProvider
    {
        /// <summary>
        /// A report matched its template and updated the state. The provider stores it durably before
        /// the returned task completes; delivery to the engine happens later.
        /// </summary>
        Task ReportReceivedAsync(Report report, CancellationToken cancellationToken);

        /// <summary>A node announced itself online, or offline.</summary>
        void NodeOnlineChanged(string nodeId, NodeOnlineMessage message);
    }
}
