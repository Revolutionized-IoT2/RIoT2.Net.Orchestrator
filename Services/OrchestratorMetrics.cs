using System.Diagnostics.Metrics;
using RIoT2.Core;
using RIoT2.Core.Diagnostics;
using RIoT2.Core.Models;
using RIoT2.Net.Orchestrator.Models;
using RIoT2.Net.Orchestrator.Services.Outbox;

namespace RIoT2.Net.Orchestrator.Services
{
    /// <summary>
    /// The orchestrator's instruments (meter <see cref="MeterNames.Orchestrator"/>, design 7.4 phase 4).
    /// Counters are recorded where things happen; the gauges read the outbox and the node lists that
    /// <see cref="Observe"/> registers. Nothing is measured while no listener is attached.
    /// </summary>
    public static class OrchestratorMetrics
    {
        public const string ReportsReceivedName = "riot2.reports.received";
        public const string ReportsUnknownTemplateName = "riot2.reports.unknown_template";
        public const string WorkflowDeliveriesName = "riot2.workflow.deliveries";
        public const string OutboxPendingName = "riot2.outbox.pending";
        public const string OutboxOldestAgeName = "riot2.outbox.oldest_age_seconds";
        public const string CommandsName = "riot2.commands";
        public const string CommandLatencyName = "riot2.command.latency";
        public const string NodesOnlineName = "riot2.nodes.online";
        public const string NodesOutOfSyncName = "riot2.nodes.out_of_sync";

        private static readonly Meter Meter = new(MeterNames.Orchestrator);
        private static volatile Sources _sources;

        /// <summary>Reports with a known template, by <c>node</c>.</summary>
        public static readonly Counter<long> ReportsReceived = Meter.CreateCounter<long>(ReportsReceivedName, "{report}",
            "Reports received for a configured report template, by node.");

        /// <summary>Reports whose id no configured template has, by <c>node</c>; they are dropped.</summary>
        public static readonly Counter<long> ReportsUnknownTemplate = Meter.CreateCounter<long>(ReportsUnknownTemplateName, "{report}",
            "Reports dropped because no configured report template has their id, by node.");

        /// <summary>Workflow trigger delivery attempts and expiries, by <c>outcome</c>: <c>delivered</c>, <c>failed</c>, <c>retry</c> or <c>expired</c>.</summary>
        public static readonly Counter<long> WorkflowDeliveries = Meter.CreateCounter<long>(WorkflowDeliveriesName, "{delivery}",
            "Workflow trigger deliveries, by outcome: delivered, failed, retry or expired.");

        /// <summary>Final command deliveries, by <c>outcome</c>: the delivery status (<c>executed</c>, <c>expired</c>, …).</summary>
        public static readonly Counter<long> Commands = Meter.CreateCounter<long>(CommandsName, "{command}",
            "Commands whose delivery became final, by outcome (the delivery status).");

        /// <summary>From queueing a command to its final status, by <c>outcome</c>.</summary>
        public static readonly Histogram<double> CommandLatency = Meter.CreateHistogram<double>(CommandLatencyName, "s",
            "Time from queueing a command to its final status, by outcome.");

        static OrchestratorMetrics()
        {
            Meter.CreateObservableGauge(OutboxPendingName, () => Observe(s => s.Pending()), "{message}",
                "Outbox messages waiting for delivery, by kind (workflow or command).");
            Meter.CreateObservableGauge(OutboxOldestAgeName, () => Observe(s => s.OldestAge()), "s",
                "Age of the oldest pending outbox message, by kind; 0 when none is pending.");
            Meter.CreateObservableGauge(NodesOnlineName, () => Observe(s => s.Online()), "{node}",
                "Nodes that announced themselves online, by type.");
            Meter.CreateObservableGauge(NodesOutOfSyncName, () => Observe(s => s.OutOfSync()), "{node}",
                "Online configured nodes that don't run their configuration, by state (pending or failed).");
        }

        /// <summary>Lets the gauges read <paramref name="store"/> and the node lists; <c>null</c> stops them.</summary>
        public static void Observe(OutboxStore store, IOnlineNodeService nodes, IOrchestratorConfigurationService configuration, TimeProvider time) =>
            _sources = store == null ? null : new Sources(store, nodes, configuration, time ?? TimeProvider.System);

        /// <summary>Records a delivery step of the outbox: workflow outcomes, and final commands with their latency.</summary>
        internal static void Delivery(OutboxMessage message, string status, long now)
        {
            if (message == null)
                return;
            if (message.Kind == OutboxKind.Workflow)
                WorkflowDeliveries.Add(1, Tag("outcome", status));
            else if (message.Kind == OutboxKind.Command && status != Retry)
            {
                Commands.Add(1, Tag("outcome", status));
                CommandLatency.Record(Math.Max(0, now - message.CreatedUtc) / 1000.0, Tag("outcome", status));
            }
        }

        /// <summary>The <c>outcome</c> of an attempt that will be retried; counted for workflow triggers only.</summary>
        public const string Retry = "retry";

        internal static KeyValuePair<string, object> Tag(string name, object value) => new(name, value);

        private static IEnumerable<Measurement<T>> Observe<T>(Func<Sources, IEnumerable<Measurement<T>>> observe) where T : struct
        {
            var sources = _sources;
            if (sources == null)
                return [];
            try
            {
                return observe(sources).ToList();
            }
            catch (ObjectDisposedException)
            {
                // The host is stopping.
                return [];
            }
        }

        private sealed record Sources(OutboxStore Store, IOnlineNodeService Nodes, IOrchestratorConfigurationService Configuration, TimeProvider Time)
        {
            private static readonly string[] Kinds = [OutboxKind.Workflow, OutboxKind.Command];

            public IEnumerable<Measurement<long>> Pending()
            {
                var pending = Store.GetPending(Time.GetUtcNow().ToUnixTimeMilliseconds());
                return Kinds.Select(kind => new Measurement<long>(pending.TryGetValue(kind, out var p) ? p.Count : 0, Tag("kind", kind)));
            }

            public IEnumerable<Measurement<double>> OldestAge()
            {
                var pending = Store.GetPending(Time.GetUtcNow().ToUnixTimeMilliseconds());
                return Kinds.Select(kind => new Measurement<double>(pending.TryGetValue(kind, out var p) ? p.OldestAgeSeconds : 0, Tag("kind", kind)));
            }

            public IEnumerable<Measurement<int>> Online() =>
                (Nodes?.OnlineNodes ?? [])
                    .Where(node => node?.OnlineNodeSettings?.IsOnline == true)
                    .GroupBy(node => TypeName(node.OnlineNodeSettings.NodeType))
                    .Select(group => new Measurement<int>(group.Count(), Tag("type", group.Key)));

            public IEnumerable<Measurement<int>> OutOfSync()
            {
                var online = (Nodes?.OnlineNodes ?? []).Where(n => n?.OnlineNodeSettings?.IsOnline == true)
                    .GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First());
                var states = (Configuration?.NodeConfigurations ?? [])
                    .Where(configuration => online.ContainsKey(configuration.Id))
                    .Select(configuration => OnlineNodeService.SyncState(configuration, online[configuration.Id], Nodes.GetStatus(configuration.Id)))
                    .ToList();
                return new[] { NodeSyncState.Pending, NodeSyncState.Failed }
                    .Select(state => new Measurement<int>(states.Count(s => s == state), Tag("state", state)));
            }

            private static string TypeName(NodeType type)
            {
                var name = type.ToString();
                return char.ToLowerInvariant(name[0]) + name[1..];
            }
        }
    }
}
