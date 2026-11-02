using RIoT2.Core.Models;

namespace RIoT2.Net.Orchestrator.Models
{
    public class Node
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public bool IsOnline { get; set; }
        public List<DeviceStatus>? DeviceStatuses { get; set; }
        public PackageManifest? Manifest { get; set; }
        public PackageManifest? PluginManifest { get; set; }

        /// <summary>The revision of the saved configuration (design 7.2).</summary>
        public int? DesiredRevision { get; set; }

        /// <summary>The revision the node reports as applied; <c>null</c> when unknown.</summary>
        public int? AppliedRevision { get; set; }

        /// <summary>A <see cref="NodeSyncState"/> value.</summary>
        public string SyncState { get; set; }

        /// <summary>The error of the node's last failed configuration, if any.</summary>
        public string? LastError { get; set; }

        /// <summary>The state the node last reported (<c>applying</c>, <c>applied</c>, <c>failed</c>, <c>pendingRestart</c>); <c>null</c> when none.</summary>
        public string? ConfigurationState { get; set; }

        /// <summary>Configured devices that aren't running, as the node reported them; <c>null</c> when none.</summary>
        public List<FailedDevice>? FailedDevices { get; set; }
    }
}
