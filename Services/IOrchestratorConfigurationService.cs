using RIoT2.Core.Models;
using System.Collections.Generic;

namespace RIoT2.Net.Orchestrator.Services
{
    /// <summary>
    /// Manages the orchestrator, node, and dashboard configurations.
    /// </summary>
    public interface IOrchestratorConfigurationService
    {
        /// <summary>
        /// Gets the orchestrator configuration.
        /// </summary>
        OrchestratorConfiguration OrchestratorConfiguration { get; }

        /// <summary>
        /// Gets the configurations of all known nodes and their devices.
        /// </summary>
        List<NodeDeviceConfiguration> NodeConfigurations { get; }

        /// <summary>
        /// Gets the dashboard configuration.
        /// </summary>
        DashboardConfiguration? DashboardConfiguration { get; }

        /// <summary>
        /// Persists the specified dashboard configuration.
        /// </summary>
        /// <param name="dashboard">The dashboard configuration to save.</param>
        /// <returns>The identifier of the saved dashboard configuration.</returns>
        string? SaveDashboardConfiguration(DashboardConfiguration dashboard);

        /// <summary>
        /// Persists a dashboard configuration from its JSON representation.
        /// </summary>
        /// <param name="json">The JSON payload representing the dashboard configuration.</param>
        /// <returns>The identifier of the saved dashboard configuration.</returns>
        string? SaveDashboardConfiguration(string json);

        /// <summary>
        /// Finds the identifier of the node that owns the specified report or command identifier.
        /// </summary>
        /// <param name="id">The report or command identifier to resolve.</param>
        /// <returns>The owning node identifier, or <c>null</c> if none is found.</returns>
        string? FindNodeId(string id);

        /// <summary>
        /// Gets the command templates aggregated from all node configurations.
        /// </summary>
        /// <returns>The available command templates.</returns>
        IEnumerable<CommandTemplate> GetCommandTemplates();

        /// <summary>
        /// Gets the report templates aggregated from all node configurations.
        /// </summary>
        /// <returns>The available report templates.</returns>
        IEnumerable<ReportTemplate> GetReportTemplates();

        /// <summary>
        /// Persists the specified node device configuration.
        /// </summary>
        /// <param name="configuration">The node device configuration to save.</param>
        /// <returns>The identifier of the saved node configuration.</returns>
        string? SaveNodeConfiguration(NodeDeviceConfiguration configuration);

        /// <summary>
        /// Persists a node device configuration from its JSON representation.
        /// </summary>
        /// <param name="json">The JSON payload representing the node device configuration.</param>
        /// <returns>The identifier of the saved node configuration.</returns>
        string? SaveNodeConfiguration(string json);

        /// <summary>
        /// Deletes the configuration for the specified node.
        /// </summary>
        /// <param name="nodeId">The identifier of the node whose configuration should be deleted.</param>
        void DeleteNodeConfiguration(string nodeId);
    }
}