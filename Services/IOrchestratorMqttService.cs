using RIoT2.Core.Models;
using System.Threading.Tasks;

namespace RIoT2.Net.Orchestrator.Services
{
    /// <summary>
    /// Provides MQTT connectivity and messaging for the orchestrator.
    /// </summary>
    public interface IOrchestratorMqttService
    {
        /// <summary>
        /// Connects the orchestrator to the MQTT broker and begins processing messages.
        /// </summary>
        Task Start();

        /// <summary>
        /// Disconnects the orchestrator from the MQTT broker.
        /// </summary>
        Task Stop();

        /// <summary>
        /// Releases the resources used by the service.
        /// </summary>
        void Dispose();

        /// <summary>
        /// Publishes a command to the specified topic.
        /// </summary>
        /// <param name="topic">The topic to publish to.</param>
        /// <param name="command">The command to publish.</param>
        Task SendCommand(string topic, Command command);

        /// <summary>
        /// Publishes a report.
        /// </summary>
        /// <param name="report">The report to publish.</param>
        Task SendReport(Report report);

        /// <summary>
        /// Requests the configuration for the component with the specified identifier.
        /// </summary>
        /// <param name="id">The identifier of the component to configure.</param>
        Task SendConfigurationCommand(string id);

        /// <summary>
        /// Resolves the target node, publishes the command, and records its state.
        /// </summary>
        /// <param name="command">The command to execute.</param>
        /// <returns>False if the command identifier is missing or cannot be resolved to a node.</returns>
        Task<bool> ExecuteCommand(Command command);
    }
}