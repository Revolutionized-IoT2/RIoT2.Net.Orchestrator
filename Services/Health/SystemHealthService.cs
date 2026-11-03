using System.Reflection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RIoT2.Core;
using RIoT2.Core.Models;
using RIoT2.Net.Orchestrator.Models;
using RIoT2.Net.Orchestrator.Services.Outbox;

namespace RIoT2.Net.Orchestrator.Services.Health
{
    /// <summary>
    /// Builds <c>GET /api/v2/system/health</c> (I46): the orchestrator's health checks, the outbox, and every
    /// node the orchestrator knows (configured or seen online) with its presence history, sync state and
    /// the state and last error of its devices.
    /// </summary>
    public sealed class SystemHealthService
    {
        /// <summary>How long the report waits for one node's device states; a slower node is reported without them.</summary>
        public static readonly TimeSpan DeviceStatusTimeout = TimeSpan.FromSeconds(5);

        private static readonly string? Version = typeof(SystemHealthService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0];

        private readonly IOrchestratorConfigurationService _configuration;
        private readonly IOnlineNodeService _nodes;
        private readonly NodeHistory _history;
        private readonly OutboxStore _outbox;
        private readonly HealthCheckService _checks;
        private readonly TimeProvider _time;

        public SystemHealthService(IOrchestratorConfigurationService configuration, IOnlineNodeService nodes, NodeHistory history,
            OutboxStore outbox, HealthCheckService checks, TimeProvider time)
        {
            _configuration = configuration;
            _nodes = nodes;
            _history = history;
            _outbox = outbox;
            _checks = checks;
            _time = time;
        }

        public async Task<SystemHealth> GetAsync(CancellationToken cancellationToken)
        {
            var report = await _checks.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
            var nodes = await GetNodesAsync(cancellationToken).ConfigureAwait(false);
            var now = _time.GetUtcNow();

            return new SystemHealth
            {
                GeneratedAt = now,
                Orchestrator = new OrchestratorHealth
                {
                    Version = Version,
                    Started = _history.Started,
                    Status = report.Status.ToString(),
                    Checks = report.Entries.Select(e => new HealthCheckEntry
                    {
                        Name = e.Key,
                        Status = e.Value.Status.ToString(),
                        Description = e.Value.Description
                    }).ToList()
                },
                Outbox = _outbox.GetSummary(now.ToUnixTimeMilliseconds()),
                Nodes = nodes
            };
        }

        private async Task<List<NodeHealth>> GetNodesAsync(CancellationToken cancellationToken)
        {
            var configured = _configuration.NodeConfigurations.ToDictionary(c => c.Id, StringComparer.Ordinal);
            var syncs = _nodes.GetSyncStates().ToDictionary(s => s.Id, StringComparer.Ordinal);
            var online = _nodes.OnlineNodes
                .Where(n => n.OnlineNodeSettings?.IsOnline == true && n.OnlineNodeSettings.NodeType != NodeType.Dashboard)
                .GroupBy(n => n.Id, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().OnlineNodeSettings, StringComparer.Ordinal);
            var history = _history.Nodes().ToDictionary(h => h.Id, StringComparer.Ordinal);

            var ids = configured.Keys.Concat(online.Keys).Concat(history.Keys).Distinct(StringComparer.Ordinal);
            var nodes = await Task.WhenAll(ids.Select(id =>
            {
                configured.TryGetValue(id, out var configuration);
                syncs.TryGetValue(id, out var sync);
                online.TryGetValue(id, out var presence);
                history.TryGetValue(id, out var seen);
                return BuildNodeAsync(id, configuration, sync, presence, seen, cancellationToken);
            })).ConfigureAwait(false);

            return nodes
                .OrderBy(n => n.NodeType)
                .ThenBy(n => string.IsNullOrEmpty(n.Name) ? n.Id : n.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(n => n.Id, StringComparer.Ordinal)
                .ToList();
        }

        private async Task<NodeHealth> BuildNodeAsync(string id, NodeDeviceConfiguration? configuration, NodeSync? sync,
            NodeOnlineMessage? presence, NodeHistorySnapshot? seen, CancellationToken cancellationToken)
        {
            var nodeType = presence?.NodeType ?? seen?.NodeType ?? NodeType.Device;
            var node = new NodeHealth
            {
                Id = id,
                Name = configuration?.Name ?? (string.IsNullOrEmpty(presence?.Name) ? seen?.Name : presence.Name),
                NodeType = nodeType,
                Configured = configuration != null,
                IsOnline = presence != null,
                Since = seen?.Since,
                Version = presence?.Manifest?.Version ?? seen?.Version,
                PluginVersion = presence?.PluginManifest?.Version ?? sync?.PluginManifest?.Version,
                SyncState = sync?.SyncState,
                ConfigurationState = sync?.ConfigurationState,
                LastError = sync?.LastError,
                History = seen?.Events.ToList() ?? []
            };

            if (configuration == null || nodeType != NodeType.Device)
                return node;

            List<DeviceStatus>? statuses = null;
            if (presence != null)
            {
                try
                {
                    statuses = await _nodes.LoadDeviceStatusFromNodeAsync(id).WaitAsync(DeviceStatusTimeout, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // Reported as unavailable below.
                }
            }
            node.DevicesUnavailable = statuses == null;
            node.Devices = Devices(id, configuration, statuses, sync);
            return node;
        }

        // The configured devices, with the live state when the node answered and the last error the
        // orchestrator saw for each (from the node's status or from an earlier device state).
        private List<DeviceHealth> Devices(string nodeId, NodeDeviceConfiguration configuration, List<DeviceStatus>? statuses, NodeSync? sync)
        {
            var errors = _history.DeviceErrorsOf(nodeId).ToDictionary(e => e.DeviceId, StringComparer.Ordinal);
            var failed = sync?.FailedDevices?.Where(f => f.Id != null).ToDictionary(f => f.Id!, StringComparer.Ordinal)
                ?? new Dictionary<string, FailedDevice>(StringComparer.Ordinal);
            var live = statuses?.Where(s => s.Id != null).GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal)
                ?? new Dictionary<string, DeviceStatus>(StringComparer.Ordinal);

            var devices = (configuration.DeviceConfigurations ?? []).Select(d => (d.Id, Name: (string?)d.Name))
                .Concat(live.Values.Select(s => (s.Id, Name: (string?)s.Name)))
                .Concat(errors.Values.Select(e => (Id: e.DeviceId, e.Name)))
                .Where(d => !string.IsNullOrEmpty(d.Id))
                .GroupBy(d => d.Id, StringComparer.Ordinal)
                .Select(g =>
                {
                    var id = g.Key;
                    live.TryGetValue(id, out var status);
                    errors.TryGetValue(id, out var error);
                    failed.TryGetValue(id, out var failure);
                    return new DeviceHealth
                    {
                        Id = id,
                        Name = g.Select(d => d.Name).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? error?.Name,
                        State = status?.State,
                        Message = string.IsNullOrEmpty(status?.Message) ? failure?.Message : status!.Message,
                        LastError = error?.Message,
                        LastErrorAt = error?.At
                    };
                });
            return devices.ToList();
        }
    }

    /// <summary>The body of <c>GET /api/v2/system/health</c>.</summary>
    public sealed class SystemHealth
    {
        public DateTimeOffset GeneratedAt { get; init; }
        public OrchestratorHealth Orchestrator { get; init; } = new();
        public OutboxSummary Outbox { get; init; } = null!;
        public List<NodeHealth> Nodes { get; init; } = [];
    }

    /// <summary>The orchestrator itself: its version, start and the results of its <c>/health</c> checks.</summary>
    public sealed class OrchestratorHealth
    {
        public string? Version { get; init; }

        /// <summary>When the orchestrator started; the node histories begin here.</summary>
        public DateTimeOffset Started { get; init; }

        /// <summary><c>Healthy</c>, <c>Degraded</c> or <c>Unhealthy</c>: the worst check.</summary>
        public string Status { get; init; } = "Healthy";

        /// <summary>Each health check (<c>mqtt</c>, <c>outbox</c>).</summary>
        public List<HealthCheckEntry> Checks { get; init; } = [];
    }

    public sealed class HealthCheckEntry
    {
        public string Name { get; init; } = "";
        public string Status { get; init; } = "";
        public string? Description { get; init; }
    }

    /// <summary>A node, connector or workflow engine as the health page shows it.</summary>
    public sealed class NodeHealth
    {
        public string Id { get; init; } = "";
        public string? Name { get; init; }
        public NodeType NodeType { get; init; }

        /// <summary>Whether the orchestrator has a saved configuration for it.</summary>
        public bool Configured { get; init; }
        public bool IsOnline { get; init; }

        /// <summary>When its current online or offline state began; <c>null</c> when it hasn't changed since the orchestrator started.</summary>
        public DateTimeOffset? Since { get; init; }

        /// <summary>The node's own version (its presence manifest).</summary>
        public string? Version { get; init; }

        /// <summary>The installed plugin package's version (.NET nodes).</summary>
        public string? PluginVersion { get; init; }

        /// <summary>Configured nodes: a <see cref="Models.NodeSyncState"/> value.</summary>
        public string? SyncState { get; init; }
        public string? ConfigurationState { get; init; }

        /// <summary>The error of the node's last failed configuration.</summary>
        public string? LastError { get; init; }

        /// <summary>Presence changes since the orchestrator started, oldest first (at most <see cref="NodeHistory.EventsPerNode"/>).</summary>
        public List<PresenceEvent> History { get; init; } = [];

        /// <summary>Configured device nodes: every device. <c>null</c> for other nodes.</summary>
        public List<DeviceHealth>? Devices { get; set; }

        /// <summary>The node didn't report its device states (offline, or no answer within 5 s).</summary>
        public bool DevicesUnavailable { get; set; }
    }

    public sealed class DeviceHealth
    {
        public string Id { get; init; } = "";
        public string? Name { get; init; }

        /// <summary>The device's state now; <c>null</c> when the node didn't answer.</summary>
        public DeviceState? State { get; init; }

        /// <summary>The device's state message now, or why the node couldn't start it.</summary>
        public string? Message { get; init; }

        /// <summary>The last error the orchestrator saw for the device since it started, and when it was first seen.</summary>
        public string? LastError { get; init; }
        public DateTimeOffset? LastErrorAt { get; init; }
    }
}
