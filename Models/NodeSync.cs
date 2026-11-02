using RIoT2.Core.Models;

namespace RIoT2.Net.Orchestrator.Models
{
    /// <summary>
    /// A node's configuration sync as <c>GET /api/v2/nodes/sync</c> reports it (design 7.2 phase 6): what
    /// <c>GET /api/nodes</c> says about the configuration, without asking the nodes for their device states.
    /// </summary>
    public class NodeSync
    {
        public string Id { get; set; } = string.Empty;
        public string? Name { get; set; }
        public bool IsOnline { get; set; }

        /// <summary>The installed plugin package the node advertises (.NET nodes).</summary>
        public PackageManifest? PluginManifest { get; set; }

        /// <summary>The revision of the saved configuration.</summary>
        public int? DesiredRevision { get; set; }

        /// <summary>The revision the node reports as applied; <c>null</c> when unknown.</summary>
        public int? AppliedRevision { get; set; }

        /// <summary>A <see cref="NodeSyncState"/> value.</summary>
        public string SyncState { get; set; } = NodeSyncState.Pending;

        /// <summary>
        /// The state the node last reported (<c>applying</c>, <c>applied</c>, <c>failed</c> or <c>pendingRestart</c>),
        /// or <c>null</c> when it reported none.
        /// </summary>
        public string? ConfigurationState { get; set; }

        /// <summary>The error of the node's last failed configuration, if any.</summary>
        public string? LastError { get; set; }

        /// <summary>Configured devices that aren't running, as the node reported them; <c>null</c> when none.</summary>
        public List<FailedDevice>? FailedDevices { get; set; }
    }
}
