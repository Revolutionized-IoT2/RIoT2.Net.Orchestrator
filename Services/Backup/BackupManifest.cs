namespace RIoT2.Net.Orchestrator.Services.Backup
{
    /// <summary>
    /// <c>backup-manifest.json</c>, the first entry of a configuration backup (design 7.4). It lists
    /// every other entry with its size and SHA-256, so a restore can reject a damaged or changed archive.
    /// </summary>
    public sealed class BackupManifest
    {
        public const string FileName = "backup-manifest.json";
        public const string FormatName = "riot2-configuration-backup";
        public const int CurrentFormatVersion = 1;

        public const string Manual = "manual";
        public const string Scheduled = "scheduled";
        public const string PreRestore = "pre-restore";

        public string Format { get; set; } = FormatName;
        public int FormatVersion { get; set; } = CurrentFormatVersion;
        public DateTimeOffset CreatedAt { get; set; }

        /// <summary><c>manual</c> (the API), <c>scheduled</c> or <c>pre-restore</c> (taken before a restore).</summary>
        public string Kind { get; set; }

        public string OrchestratorId { get; set; }

        /// <summary><c>orchestrator</c> and <c>core</c>: the versions that wrote the backup.</summary>
        public Dictionary<string, string> Versions { get; set; } = [];

        public List<BackupManifestFile> Files { get; set; } = [];
    }

    public sealed class BackupManifestFile
    {
        /// <summary><c>StoredObjects/&lt;Type&gt;/&lt;Id&gt;.json</c> or <c>MatterCredentials/&lt;file&gt;</c>.</summary>
        public string Path { get; set; }
        public long Size { get; set; }

        /// <summary>Lowercase hexadecimal SHA-256 of the entry's content.</summary>
        public string Sha256 { get; set; }
    }

    /// <summary>An entry of a configuration backup.</summary>
    public sealed record BackupFile(string Path, byte[] Content);

    /// <summary>A validated configuration backup.</summary>
    public sealed record BackupContent(BackupManifest Manifest, IReadOnlyList<BackupFile> Files);

    /// <summary>A backup that can't be restored, with every reason.</summary>
    public sealed class BackupValidationException : Exception
    {
        public BackupValidationException(IReadOnlyList<string> errors)
            : base("The backup can't be restored: " + string.Join(" ", errors))
        {
            Errors = errors;
        }

        public IReadOnlyList<string> Errors { get; }
    }

    /// <summary>A backup or restore is already running.</summary>
    public sealed class BackupBusyException : Exception
    {
        public BackupBusyException() : base("Another backup or restore is running; try again in a moment.") { }
    }
}
