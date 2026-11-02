using RIoT2.Core.Models;
using System.Collections.Generic;

namespace RIoT2.Net.Orchestrator.Services
{
    /// <summary>
    /// This service maintain the current state of each IO
    /// Maintains history of the suitable IO's
    /// </summary>
    public interface IMessageStateService
    {
        /// <summary>
        /// Updates the stored state from the specified report.
        /// </summary>
        /// <param name="report">The report whose value becomes the current state.</param>
        /// <param name="maintainHistory">If <c>true</c>, the report is also appended to the history.</param>
        void SetState(Report report, bool maintainHistory = false);

        /// <summary>
        /// Updates the stored state from the specified command.
        /// </summary>
        /// <param name="command">The command whose value becomes the current state.</param>
        void SetState(Command command);

        /// <summary>
        /// Gets the current command states.
        /// </summary>
        IEnumerable<Command> Commands { get; }

        /// <summary>
        /// Gets the current report states.
        /// </summary>
        IEnumerable<Report> Reports { get; }

        /// <summary>
        /// Gets the recorded history for the specified report.
        /// </summary>
        /// <param name="reportId">The identifier of the report whose history to retrieve.</param>
        /// <param name="count">The maximum number of history entries to return, or <c>null</c> for all.</param>
        /// <returns>The historical reports, most recent first.</returns>
        IEnumerable<Report>? GetHistory(string reportId, int? count = null);

        /// <summary>
        /// Clears the stored history and, optionally, the current state.
        /// </summary>
        /// <param name="includeState">If <c>true</c>, the current state is cleared in addition to the history.</param>
        void Reset(bool includeState = false);
    }
}