using RIoT2.Core.Models;

namespace RIoT2.Net.Orchestrator.Models
{
    public class Node
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public bool IsOnline { get; set; }
        public List<DeviceStatus> DeviceStatuses { get; set; }
        public PackageManifest Manifest { get; set; }
        public PackageManifest PluginManifest { get; set; }

        /// <summary>The revision of the saved configuration (design 7.2).</summary>
        public int? DesiredRevision { get; set; }

        /// <summary>The revision the node reports as applied; <c>null</c> when unknown.</summary>
        public int? AppliedRevision { get; set; }

        /// <summary>A <see cref="NodeSyncState"/> value.</summary>
        public string SyncState { get; set; }

        /// <summary>The error of the node's last failed configuration, if any.</summary>
        public string LastError { get; set; }
    }
}
