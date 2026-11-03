using RIoT2.Core;
using RIoT2.Core.Models;

namespace RIoT2.Net.Orchestrator.Services.Health
{
    /// <summary>
    /// What the orchestrator has seen of each node since it started (I46): presence changes and the last
    /// error of every device. Kept in memory and bounded; dashboards (a new id per browser tab) aren't kept.
    /// </summary>
    public sealed class NodeHistory
    {
        /// <summary>Presence changes kept per node, newest last.</summary>
        public const int EventsPerNode = 20;

        /// <summary>Nodes kept; the one seen longest ago is dropped first.</summary>
        public const int MaxNodes = 256;

        /// <summary>Device errors kept; the oldest is dropped first.</summary>
        public const int MaxDeviceErrors = 1024;

        private readonly TimeProvider _time;
        private readonly object _gate = new();
        private readonly Dictionary<string, NodeEntry> _nodes = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Node, string Device), DeviceError> _deviceErrors = new();

        public NodeHistory(TimeProvider time)
        {
            _time = time;
            Started = time.GetUtcNow();
        }

        /// <summary>When the history starts: the orchestrator's start.</summary>
        public DateTimeOffset Started { get; }

        /// <summary>Records a presence message. Only a change of state adds an event.</summary>
        public void Presence(string id, NodeOnlineMessage message)
        {
            if (string.IsNullOrEmpty(id) || message == null || message.NodeType == NodeType.Dashboard)
                return;

            lock (_gate)
            {
                if (!_nodes.TryGetValue(id, out var entry))
                {
                    if (!message.IsOnline)
                        return;
                    EvictNodeIfFull();
                    entry = new NodeEntry(id);
                    _nodes[id] = entry;
                }
                if (message.IsOnline)
                {
                    if (!string.IsNullOrEmpty(message.Name))
                        entry.Name = message.Name;
                    entry.NodeType = message.NodeType;
                    entry.Version = message.Manifest?.Version ?? message.PluginManifest?.Version ?? entry.Version;
                }
                Change(entry, message.IsOnline);
            }
        }

        /// <summary>Records that a node went offline (its last will or an offline message).</summary>
        public void Offline(string id)
        {
            if (string.IsNullOrEmpty(id))
                return;
            lock (_gate)
            {
                if (_nodes.TryGetValue(id, out var entry))
                    Change(entry, false);
            }
        }

        /// <summary>Records the devices a node reports as failed or in the error state.</summary>
        public void DeviceErrors(string nodeId, IEnumerable<(string? Id, string? Name, string? Message)>? devices)
        {
            if (string.IsNullOrEmpty(nodeId) || devices == null)
                return;

            var now = _time.GetUtcNow();
            lock (_gate)
            {
                foreach (var (id, name, message) in devices)
                {
                    if (string.IsNullOrEmpty(id))
                        continue;
                    var key = (nodeId, id);
                    var text = string.IsNullOrWhiteSpace(message) ? "Error" : message!;
                    _deviceErrors.TryGetValue(key, out var existing);
                    // The same error reported again keeps the time it was first seen.
                    if (existing != null && existing.Message == text)
                    {
                        if (!string.IsNullOrEmpty(name))
                            existing.Name = name;
                        continue;
                    }
                    if (existing == null && _deviceErrors.Count >= MaxDeviceErrors)
                        _deviceErrors.Remove(_deviceErrors.MinBy(e => e.Value.At).Key);
                    _deviceErrors[key] = new DeviceError(nodeId, id, string.IsNullOrEmpty(name) ? existing?.Name : name, text, now);
                }
            }
        }

        /// <summary>A copy of the history of one node, or <c>null</c> when it wasn't seen.</summary>
        public NodeHistorySnapshot? Get(string id)
        {
            lock (_gate)
                return _nodes.TryGetValue(id, out var entry) ? entry.Snapshot() : null;
        }

        /// <summary>Copies of every node's history.</summary>
        public IReadOnlyList<NodeHistorySnapshot> Nodes()
        {
            lock (_gate)
                return _nodes.Values.Select(n => n.Snapshot()).ToList();
        }

        /// <summary>The last error of each device of a node, newest first.</summary>
        public IReadOnlyList<DeviceError> DeviceErrorsOf(string nodeId)
        {
            lock (_gate)
                return _deviceErrors.Values.Where(e => e.NodeId == nodeId).OrderByDescending(e => e.At).Select(e => e.Copy()).ToList();
        }

        private void Change(NodeEntry entry, bool online)
        {
            var now = _time.GetUtcNow();
            entry.LastSeen = now;
            if (entry.Events.Count > 0 && entry.Events[^1].IsOnline == online)
                return;
            entry.Events.Add(new PresenceEvent(now, online));
            if (entry.Events.Count > EventsPerNode)
                entry.Events.RemoveAt(0);
        }

        private void EvictNodeIfFull()
        {
            if (_nodes.Count < MaxNodes)
                return;
            var oldest = _nodes.Values.MinBy(n => n.LastSeen)!;
            _nodes.Remove(oldest.Id);
        }

        private sealed class NodeEntry(string id)
        {
            public string Id { get; } = id;
            public string? Name { get; set; }
            public NodeType NodeType { get; set; }
            public string? Version { get; set; }
            public DateTimeOffset LastSeen { get; set; }
            public List<PresenceEvent> Events { get; } = [];

            public NodeHistorySnapshot Snapshot() => new(Id, Name, NodeType, Version, [.. Events]);
        }
    }

    /// <summary>A node going online or offline.</summary>
    public sealed record PresenceEvent(DateTimeOffset At, bool IsOnline);

    /// <summary>A node's history: its last presence changes, oldest first.</summary>
    public sealed record NodeHistorySnapshot(string Id, string? Name, NodeType NodeType, string? Version, IReadOnlyList<PresenceEvent> Events)
    {
        /// <summary>When the node's current state began, or <c>null</c> when no change was seen.</summary>
        public DateTimeOffset? Since => Events.Count == 0 ? null : Events[^1].At;
    }

    /// <summary>The last error a device reported, and when it was first seen.</summary>
    public sealed class DeviceError(string nodeId, string deviceId, string? name, string message, DateTimeOffset at)
    {
        public string NodeId { get; } = nodeId;
        public string DeviceId { get; } = deviceId;
        public string? Name { get; set; } = name;
        public string Message { get; } = message;
        public DateTimeOffset At { get; } = at;

        internal DeviceError Copy() => new(NodeId, DeviceId, Name, Message, At);
    }
}
