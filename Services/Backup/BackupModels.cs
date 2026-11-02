namespace RIoT2.Net.Orchestrator.Services.Backup
{
    /// <summary><c>GET /api/v2/backups</c>: the schedule and the backups in <c>Backups/</c>.</summary>
    public sealed class BackupSummary
    {
        /// <summary><c>RIOT2_BACKUP_SCHEDULE</c>, or <c>null</c> when scheduled backups are off.</summary>
        public string? Schedule { get; set; }
        public int Retention { get; set; }
        public DateTimeOffset? NextRunAt { get; set; }
        public BackupRun? LastRun { get; set; }

        /// <summary>Newest first.</summary>
        public List<BackupInfo> Backups { get; set; } = [];
    }

    public sealed class BackupInfo
    {
        public string Name { get; set; }
        public long SizeBytes { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }

    /// <summary>The last scheduled backup: the file it wrote, or why it failed.</summary>
    public sealed class BackupRun
    {
        public DateTimeOffset At { get; set; }
        public string? Name { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>The answer to a restore.</summary>
    public sealed class RestoreResult
    {
        /// <summary>When the restored backup was made, its kind, source orchestrator and versions.</summary>
        public DateTimeOffset CreatedAt { get; set; }
        public string Kind { get; set; }
        public string? OrchestratorId { get; set; }
        public Dictionary<string, string?> Versions { get; set; } = [];

        public int Files { get; set; }
        public int Nodes { get; set; }
        public int Variables { get; set; }
        public int MatterCredentials { get; set; }

        /// <summary>The backup of the replaced configuration, in <c>Backups/</c>, to undo the restore.</summary>
        public string SafetyBackup { get; set; }
    }
}
