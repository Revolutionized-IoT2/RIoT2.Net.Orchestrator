using RIoT2.Core.Models;
using System.Collections.Generic;
using System.Linq;

namespace RIoT2.Net.Orchestrator.Services
{
    /// <summary>
    /// Default <see cref="IMessageStateService"/> implementation that keeps the latest report and
    /// command states in memory and maintains a bounded history per report.
    /// </summary>
    public class MessageStateService : IMessageStateService
    {
        private List<Command> _commands;
        private List<Report> _reports;
        private static readonly int _maxHistory = 25;
        Dictionary<string, List<Report>> _history;
        private readonly object _sync = new object();

        /// <summary>
        /// Initializes a new instance of the <see cref="MessageStateService"/> class with empty state and history.
        /// </summary>
        public MessageStateService() 
        {
            Reset(true);
        }

        /// <inheritdoc/>
        public IEnumerable<Command> Commands
        {
            get
            {
                lock (_sync)
                    return _commands.Select(c => new Command { Id = c.Id, Value = c.Value?.Copy() }).ToList();
            }
        }

        /// <inheritdoc/>
        public IEnumerable<Report> Reports
        {
            get
            {
                lock (_sync)
                    return _reports.Select(CopyReport).ToList();
            }
        }

        /// <inheritdoc/>
        public IEnumerable<Report> GetHistory(string reportId, int? count = null)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));
            lock (_sync)
            {
                if (!_history.TryGetValue(reportId, out var history))
                    return null;
                return history.OrderByDescending(x => x.TimeStamp).Take(count ?? _maxHistory).Select(CopyReport).ToList();
            }
        }

        private void addToHistory(Report report)
        {
            if (!_history.ContainsKey(report.Id))
                _history.Add(report.Id, new List<Report>());

            _history[report.Id].Add(report);

            if (_history[report.Id].Count > _maxHistory) 
                _history[report.Id].RemoveAt(0);

        }

        /// <inheritdoc/>
        public void SetState(Report report, bool maintainHistory = false)
        {
            if (report == null)
                throw new ArgumentNullException(nameof(report));
            lock (_sync)
            {
                var snapshot = CopyReport(report);
                _reports.RemoveAll(x => x.Id == report.Id);
                _reports.Add(snapshot);
                if (maintainHistory)
                    addToHistory(snapshot);
            }
        }

        /// <inheritdoc/>
        public void SetState(Command command)
        {
            if (command == null)
                throw new ArgumentNullException(nameof(command));
            lock (_sync)
            {
                _commands.RemoveAll(x => x.Id == command.Id);
                _commands.Add(new Command { Id = command.Id, Value = command.Value?.Copy() });
            }
        }

        /// <inheritdoc/>
        public void Reset(bool includeState = false)
        {
            lock (_sync)
            {
                if (includeState)
                {
                    _commands = new List<Command>();
                    _reports = new List<Report>();
                }
                _history = new Dictionary<string, List<Report>>();
            }
        }

        private static Report CopyReport(Report report) => new Report
        {
            Id = report.Id, Filter = report.Filter, TimeStamp = report.TimeStamp, Value = report.Value?.Copy()
        };
    }
}