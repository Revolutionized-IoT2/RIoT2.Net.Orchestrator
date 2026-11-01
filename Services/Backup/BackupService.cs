using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using RIoT2.Core.Interfaces.Services;
using RIoT2.Core.Models;
using RIoT2.Core.Utils;
using RIoT2.Net.Orchestrator.Services.Matter;
using RIoT2.Net.Orchestrator.Services.Persistence;

namespace RIoT2.Net.Orchestrator.Services.Backup
{
    /// <summary>
    /// Configuration backup and restore (design 7.4): a zip of <c>StoredObjects/</c> (without the
    /// outbox) and the Matter credentials, made without downtime, and a restore that replaces both and
    /// reloads every cache. Scheduled backups and the safety copy taken before each restore are kept
    /// in <c>Backups/</c>.
    /// </summary>
    public sealed partial class BackupService
    {
        public const string FolderName = "Backups";

        // Folders a restored credentials directory must not be, or be inside: clearing it would delete them.
        private static readonly string[] ReservedFolders = [BackupArchive.StoredObjectsFolder, FolderName, "Logs", "Data"];

        private readonly StoredObjectService _storedObjects;
        private readonly OrchestratorConfigurationService _configuration;
        private readonly IObjectStoreArchive _archive;
        private readonly MatterConfigurationStore _matterStore;
        private readonly IMatterBridgeService _matter;
        private readonly string _contentRoot;
        private readonly OrchestratorOptions _options;
        private readonly TimeProvider _time;
        private readonly ILogger<BackupService> _logger;
        private readonly SemaphoreSlim _gate = new(1, 1);

        public BackupService(StoredObjectService storedObjects, OrchestratorConfigurationService configuration,
            IObjectStoreArchive archive, MatterConfigurationStore matterStore, IMatterBridgeService matter,
            IWebHostEnvironment environment, IOptions<OrchestratorOptions> options, TimeProvider time, ILogger<BackupService> logger)
        {
            _storedObjects = storedObjects;
            _configuration = configuration;
            _archive = archive;
            _matterStore = matterStore;
            _matter = matter;
            _contentRoot = Path.GetFullPath(environment.ContentRootPath);
            _options = options.Value;
            _time = time;
            _logger = logger;
        }

        /// <summary><c>Backups/</c> under the content root (<c>/app/Backups</c> in the image).</summary>
        public string Folder => Path.Combine(_contentRoot, FolderName);

        /// <summary>Set by <see cref="BackupScheduler"/>.</summary>
        public DateTimeOffset? NextRunAt { get; set; }

        public BackupRun LastRun { get; private set; }

        /// <summary>Makes a backup of the current configuration; nothing is stopped.</summary>
        public async Task<(string Name, byte[] Content)> CreateAsync(CancellationToken cancellationToken = default)
        {
            await EnterAsync(cancellationToken);
            try
            {
                return CreateCore(BackupManifest.Manual);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Writes a scheduled backup to <c>Backups/</c> and deletes the oldest beyond the retention.</summary>
        public async Task<string> WriteScheduledAsync(CancellationToken cancellationToken = default)
        {
            await EnterAsync(cancellationToken);
            try
            {
                var (name, content) = CreateCore(BackupManifest.Scheduled);
                name = Save(name, content);
                LastRun = new BackupRun { At = _time.GetUtcNow(), Name = name };
                _logger.LogInformation("Scheduled backup {Name} written ({Bytes} bytes)", name, content.Length);
                return name;
            }
            catch (Exception x) when (x is not OperationCanceledException)
            {
                LastRun = new BackupRun { At = _time.GetUtcNow(), Error = x.Message };
                throw;
            }
            finally
            {
                _gate.Release();
            }
        }

        public BackupSummary GetSummary() => new()
        {
            Schedule = _options.BackupSchedule,
            Retention = _options.BackupRetention,
            NextRunAt = NextRunAt,
            LastRun = LastRun,
            Backups = ListFiles().Select(f => new BackupInfo { Name = f.Name, SizeBytes = f.Length, CreatedAt = f.LastWriteTimeUtc }).ToList()
        };

        /// <summary>The full path of a backup in <c>Backups/</c>, or <c>null</c> when there is no such backup.</summary>
        public string Find(string name)
        {
            if (string.IsNullOrEmpty(name) || !BackupName().IsMatch(name))
                return null;
            var path = Path.Combine(Folder, name);
            return File.Exists(path) ? path : null;
        }

        /// <summary>
        /// Validates <paramref name="archive"/>, saves the current configuration to <c>Backups/</c>, then
        /// replaces the stored objects and Matter credentials while writes wait, and reloads every cache.
        /// Nodes get a configuration command, so they fetch the restored configuration.
        /// </summary>
        /// <exception cref="BackupValidationException">The archive can't be restored; nothing was changed.</exception>
        /// <exception cref="BackupBusyException">Another backup or restore is running.</exception>
        public async Task<RestoreResult> RestoreAsync(byte[] archive, CancellationToken cancellationToken = default)
        {
            var backup = BackupArchive.Read(archive);
            var storedFiles = backup.Files
                .Where(f => f.Path.StartsWith(BackupArchive.StoredObjectsFolder + "/", StringComparison.Ordinal))
                .Select(f => new StoredFile(f.Path[(BackupArchive.StoredObjectsFolder.Length + 1)..], f.Content))
                .ToList();
            var credentialFiles = backup.Files
                .Where(f => f.Path.StartsWith(BackupArchive.MatterCredentialsFolder + "/", StringComparison.Ordinal))
                .Select(f => new BackupFile(f.Path[(BackupArchive.MatterCredentialsFolder.Length + 1)..], f.Content))
                .ToList();
            var errors = CheckRestorable(storedFiles, _contentRoot);
            if (errors.Count > 0)
                throw new BackupValidationException(errors);

            await EnterAsync(cancellationToken);
            try
            {
                // A copy of what is replaced, so a wrong restore can be undone.
                var (safetyName, safety) = CreateCore(BackupManifest.PreRestore);
                safetyName = Save(safetyName, safety);

                var previousNodes = Ids(_storedObjects.GetAll<NodeDeviceConfiguration>());
                var previousVariables = Ids(_storedObjects.GetAll<Variable>());
                var previousDashboards = Ids(_storedObjects.GetAll<DashboardConfiguration>());

                // Not cancellable from here on: a half-done restore is worse than a late answer.
                await _matter.RunStoppedAsync(() =>
                {
                    _storedObjects.Replace(() => _archive.ReplaceAll(storedFiles));
                    ReplaceCredentials(credentialFiles);
                    _configuration.Reload();
                    return Task.CompletedTask;
                }, CancellationToken.None);

                // Listeners send each node a configuration command, publish the variables, refresh the
                // Matter endpoints and clear the status of nodes the backup doesn't have.
                _storedObjects.AnnounceReplaced<NodeDeviceConfiguration>(previousNodes);
                _storedObjects.AnnounceReplaced<Variable>(previousVariables);
                _storedObjects.AnnounceReplaced<DashboardConfiguration>(previousDashboards);

                var result = new RestoreResult
                {
                    CreatedAt = backup.Manifest.CreatedAt,
                    Kind = backup.Manifest.Kind,
                    OrchestratorId = backup.Manifest.OrchestratorId,
                    Versions = backup.Manifest.Versions ?? [],
                    Files = backup.Files.Count,
                    Nodes = _configuration.NodeConfigurations.Count,
                    Variables = _storedObjects.GetAll<Variable>().Count(),
                    MatterCredentials = credentialFiles.Count,
                    SafetyBackup = safetyName
                };
                _logger.LogWarning(
                    "Configuration restored from a {Kind} backup of {CreatedAt:o} by {OrchestratorId}: {Nodes} nodes, {Variables} variables, " +
                    "{Credentials} Matter files. The replaced configuration is in {SafetyBackup}",
                    result.Kind, result.CreatedAt, result.OrchestratorId, result.Nodes, result.Variables, result.MatterCredentials, safetyName);
                return result;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Problems that would stop a restored file from loading: every known stored type must
        /// deserialize, and a restored Matter credentials directory must be a folder of its own.
        /// </summary>
        internal static List<string> CheckRestorable(IEnumerable<StoredFile> files, string contentRoot)
        {
            var errors = new List<string>();
            foreach (var file in files)
            {
                var type = file.Path[..file.Path.IndexOf('/')];
                var path = $"{BackupArchive.StoredObjectsFolder}/{file.Path}";
                try
                {
                    var json = Text(file.Content);
                    object loaded = type switch
                    {
                        nameof(NodeDeviceConfiguration) => Json.DeserializeAutoTypeNameHandling<NodeDeviceConfiguration>(json),
                        nameof(DashboardConfiguration) => Json.DeserializeAutoTypeNameHandling<DashboardConfiguration>(json),
                        nameof(Variable) => Json.DeserializeAutoTypeNameHandling<Variable>(json),
                        nameof(MatterBridgeState) => Json.Deserialize<MatterBridgeState>(json),
                        nameof(MatterConfiguration) => Json.Deserialize<MatterConfiguration>(json),
                        // Types this version doesn't use are restored as they are.
                        _ => json
                    };
                    if (loaded == null)
                        errors.Add($"{path} is empty.");
                    else if (loaded is MatterConfiguration matter)
                        CheckCredentialsDirectory(matter.CredentialsDirectory, contentRoot, path, errors);
                }
                catch (Exception x)
                {
                    errors.Add($"{path} can't be read as a {type}: {x.Message}");
                }
            }
            return errors;
        }

        private static void CheckCredentialsDirectory(string configured, string contentRoot, string path, List<string> errors)
        {
            try
            {
                var directory = MatterBridgeService.ResolveCredentialsDirectory(contentRoot, configured);
                var relative = Path.GetRelativePath(contentRoot, directory);
                var first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                if (relative == "." || ReservedFolders.Contains(first, StringComparer.OrdinalIgnoreCase))
                    errors.Add($"{path} puts the Matter credentials in '{configured}', which is not a folder of its own.");
            }
            catch (ArgumentException x)
            {
                errors.Add($"{path}: {x.Message}");
            }
        }

        /// <summary>The backups beyond the newest <paramref name="keep"/>.</summary>
        internal static IEnumerable<FileInfo> Expired(IEnumerable<FileInfo> backups, int keep) =>
            backups.OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name, StringComparer.Ordinal).Skip(keep);

        private (string Name, byte[] Content) CreateCore(string kind)
        {
            var now = _time.GetUtcNow();
            var files = _storedObjects.Exclusive(() => _archive.Export())
                .Select(f => new BackupFile($"{BackupArchive.StoredObjectsFolder}/{f.Path}", f.Content))
                .ToList();
            files.AddRange(ReadCredentials());

            var manifest = new BackupManifest
            {
                CreatedAt = now,
                Kind = kind,
                OrchestratorId = _configuration.OrchestratorConfiguration?.Id,
                Versions = Versions()
            };
            var name = $"riot2-{(kind == BackupManifest.PreRestore ? "pre-restore" : "backup")}-{now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}.zip";
            return (name, BackupArchive.Create(manifest, files));
        }

        private List<BackupFile> ReadCredentials()
        {
            var directory = new DirectoryInfo(CredentialsDirectory());
            if (!directory.Exists)
                return [];

            var files = new List<BackupFile>();
            foreach (var file in directory.EnumerateFiles("*", SearchOption.AllDirectories).OrderBy(f => f.FullName, StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(directory.FullName, file.FullName).Replace(Path.DirectorySeparatorChar, '/');
                var path = $"{BackupArchive.MatterCredentialsFolder}/{relative}";
                // Hidden and temporary files are writes in progress.
                if (file.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || BackupArchive.CheckPath(path) != null)
                {
                    _logger.LogDebug("Matter credentials file {Path} is not backed up", relative);
                    continue;
                }
                files.Add(new BackupFile(path, File.ReadAllBytes(file.FullName)));
            }
            return files;
        }

        // Mirrors the backup: credentials the backup doesn't have, such as the fabrics of a bridge
        // paired after it was made, are deleted.
        private void ReplaceCredentials(IReadOnlyList<BackupFile> files)
        {
            var directory = Directory.CreateDirectory(CredentialsDirectory());
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if (entry is DirectoryInfo folder)
                    folder.Delete(true);
                else
                    entry.Delete();
            }
            foreach (var file in files)
            {
                var path = Path.GetFullPath(Path.Combine(directory.FullName, file.Path));
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, file.Content);
            }
        }

        private string CredentialsDirectory() =>
            MatterBridgeService.ResolveCredentialsDirectory(_contentRoot, _matterStore.LoadConfiguration().CredentialsDirectory);

        private string Save(string name, byte[] content)
        {
            var folder = Directory.CreateDirectory(Folder);
            var path = Path.Combine(folder.FullName, name);
            for (var i = 1; File.Exists(path); i++)
                path = Path.Combine(folder.FullName, Path.GetFileNameWithoutExtension(name) + $"-{i}.zip");

            var temporary = Path.Combine(folder.FullName, $".{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(temporary, content);
                File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }

            foreach (var expired in Expired(ListFiles(), _options.BackupRetention).ToList())
            {
                try
                {
                    expired.Delete();
                    _logger.LogInformation("Deleted backup {Name}; {Retention} are kept", expired.Name, _options.BackupRetention);
                }
                catch (Exception x)
                {
                    _logger.LogWarning(x, "Could not delete the old backup {Name}", expired.Name);
                }
            }
            return Path.GetFileName(path);
        }

        private List<FileInfo> ListFiles()
        {
            var folder = new DirectoryInfo(Folder);
            return folder.Exists
                ? folder.EnumerateFiles("riot2-*.zip").Where(f => BackupName().IsMatch(f.Name))
                    .OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name, StringComparer.Ordinal).ToList()
                : [];
        }

        private async Task EnterAsync(CancellationToken cancellationToken)
        {
            // A backup takes well under a second; a wait this long means something is stuck.
            if (!await _gate.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken))
                throw new BackupBusyException();
        }

        private Dictionary<string, string> Versions() => new()
        {
            ["orchestrator"] = _configuration.OrchestratorConfiguration?.Manifest?.Version
                ?? typeof(BackupService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            ["core"] = typeof(IStoredObjectService).Assembly.GetName().Version?.ToString(3)
        };

        private static List<string> Ids<T>(IEnumerable<T> objects) => objects.Select(o => (string)(o as dynamic).Id).ToList();

        // As the store reads it: a byte order mark is not part of the JSON.
        private static string Text(byte[] content)
        {
            using var reader = new StreamReader(new MemoryStream(content), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }

        [GeneratedRegex(@"^riot2-(backup|pre-restore)-\d{8}T\d{6}Z(-\d+)?\.zip$")]
        private static partial Regex BackupName();
    }
}
