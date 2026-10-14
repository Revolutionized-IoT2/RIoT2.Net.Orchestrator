namespace RIoT2.Net.Orchestrator.Models
{
    /// <summary>Whether a node runs its saved configuration (design 7.2), as <c>GET /api/nodes</c> reports it.</summary>
    public static class NodeSyncState
    {
        /// <summary>The node reports the saved configuration as applied.</summary>
        public const string InSync = "inSync";

        /// <summary>The node is applying it, runs another one, or hasn't reported yet.</summary>
        public const string Pending = "pending";

        /// <summary>The node couldn't fetch or apply its configuration and runs the previous one.</summary>
        public const string Failed = "failed";

        /// <summary>The node doesn't report its configuration (firmware, or a .NET node before 1.5.0).</summary>
        public const string Legacy = "legacy";
    }
}
