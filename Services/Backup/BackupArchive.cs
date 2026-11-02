using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using RIoT2.Net.Orchestrator.Services.Persistence;

namespace RIoT2.Net.Orchestrator.Services.Backup
{
    /// <summary>
    /// Writes and validates the configuration backup zip (design 7.4): <c>backup-manifest.json</c>,
    /// <c>StoredObjects/&lt;Type&gt;/&lt;Id&gt;.json</c> and <c>MatterCredentials/…</c>. Reading checks
    /// everything a restore depends on before anything is changed.
    /// </summary>
    public static class BackupArchive
    {
        public const string StoredObjectsFolder = "StoredObjects";
        public const string MatterCredentialsFolder = "MatterCredentials";

        /// <summary>The largest archive the API accepts.</summary>
        public const long MaxArchiveBytes = 64L * 1024 * 1024;

        /// <summary>The largest total of the unpacked entries, against zip bombs.</summary>
        public const long MaxContentBytes = 256L * 1024 * 1024;

        public const int MaxFiles = 10_000;
        private const int MaxManifestBytes = 4 * 1024 * 1024;

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

        /// <summary>Writes the archive; fills in <see cref="BackupManifest.Files"/> from <paramref name="files"/>.</summary>
        public static byte[] Create(BackupManifest manifest, IReadOnlyList<BackupFile> files)
        {
            ArgumentNullException.ThrowIfNull(manifest);
            ArgumentNullException.ThrowIfNull(files);
            foreach (var file in files)
            {
                var problem = CheckPath(file.Path);
                if (problem != null)
                    throw new ArgumentException(problem, nameof(files));
            }

            manifest.Files = files
                .Select(f => new BackupManifestFile { Path = f.Path, Size = f.Content.LongLength, Sha256 = Hash(f.Content) })
                .ToList();

            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                Add(zip, BackupManifest.FileName, JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions), manifest.CreatedAt);
                foreach (var file in files)
                    Add(zip, file.Path, file.Content, manifest.CreatedAt);
            }
            return buffer.ToArray();
        }

        /// <summary>
        /// Reads and validates an archive. Throws <see cref="BackupValidationException"/> with every
        /// problem when it isn't a complete, unchanged RIoT2 configuration backup this version can read.
        /// </summary>
        public static BackupContent Read(byte[] archive)
        {
            ArgumentNullException.ThrowIfNull(archive);
            try
            {
                using var zip = new ZipArchive(new MemoryStream(archive, writable: false), ZipArchiveMode.Read);
                return Read(zip);
            }
            catch (InvalidDataException)
            {
                throw Invalid("The file is not a zip archive, or it is damaged.");
            }
        }

        private static BackupContent Read(ZipArchive zip)
        {
            if (zip.Entries.Count > MaxFiles + 1)
                throw Invalid($"The archive has more than {MaxFiles} files.");

            var manifestEntry = zip.GetEntry(BackupManifest.FileName)
                ?? throw Invalid($"The archive has no {BackupManifest.FileName}, so it is not a RIoT2 configuration backup.");

            BackupManifest? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<BackupManifest>(ReadEntry(manifestEntry, MaxManifestBytes), JsonOptions);
            }
            catch (JsonException)
            {
                throw Invalid($"{BackupManifest.FileName} is not valid JSON.");
            }

            if (manifest?.Format != BackupManifest.FormatName)
                throw Invalid($"{BackupManifest.FileName} is not a RIoT2 configuration backup manifest.");
            if (manifest.FormatVersion < 1 || manifest.FormatVersion > BackupManifest.CurrentFormatVersion)
                throw Invalid($"The backup has format version {manifest.FormatVersion}; this orchestrator reads " +
                    $"up to {BackupManifest.CurrentFormatVersion}. Upgrade the orchestrator first.");

            var errors = new List<string>();
            var expected = new Dictionary<string, BackupManifestFile>(StringComparer.Ordinal);
            foreach (var listed in manifest.Files ?? [])
            {
                if (string.IsNullOrEmpty(listed?.Path) || string.IsNullOrEmpty(listed.Sha256))
                    errors.Add("The manifest lists a file without a path or hash.");
                else if (!expected.TryAdd(listed.Path, listed))
                    errors.Add($"The manifest lists {listed.Path} twice.");
            }

            var files = new List<BackupFile>();
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName == BackupManifest.FileName || (entry.FullName.EndsWith('/') && entry.Length == 0))
                    continue;

                var problem = CheckPath(entry.FullName);
                if (problem != null)
                {
                    errors.Add(problem);
                    continue;
                }
                if (!expected.Remove(entry.FullName, out var listed))
                {
                    errors.Add($"{entry.FullName} is not listed in the manifest.");
                    continue;
                }

                byte[] content;
                try
                {
                    content = ReadEntry(entry, MaxContentBytes - total);
                }
                catch (InvalidDataException)
                {
                    errors.Add($"{entry.FullName} is damaged, or the archive unpacks to more than {MaxContentBytes / (1024 * 1024)} MB.");
                    continue;
                }
                total += content.LongLength;

                if (content.LongLength != listed.Size || !string.Equals(Hash(content), listed.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"{entry.FullName} doesn't match the manifest; the archive is damaged or was changed.");
                    continue;
                }
                if (entry.FullName.StartsWith(StoredObjectsFolder + "/", StringComparison.Ordinal) && !IsJson(content))
                {
                    errors.Add($"{entry.FullName} is not valid JSON.");
                    continue;
                }
                files.Add(new BackupFile(entry.FullName, content));
            }

            foreach (var missing in expected.Keys)
                errors.Add($"{missing} is listed in the manifest but missing from the archive.");

            if (errors.Count > 0)
                throw new BackupValidationException(errors);
            return new BackupContent(manifest, files);
        }

        /// <summary>
        /// Why <paramref name="path"/> can't be an entry, or <c>null</c>: only
        /// <c>StoredObjects/&lt;Type&gt;/&lt;Id&gt;.json</c> and files under <c>MatterCredentials/</c>, with
        /// portable names, no <c>..</c> and nothing hidden. The outbox database is never part of a backup.
        /// </summary>
        public static string? CheckPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return "An entry has no name.";

            var segments = path.Split('/');
            if (segments.Any(s => !FileObjectStore.IsSafeName(s) || s.StartsWith('.')))
                return $"{path} is not a safe path.";

            var valid = segments[0] switch
            {
                StoredObjectsFolder => segments.Length == 3 && segments[2].Length > 5 && segments[2].EndsWith(".json", StringComparison.Ordinal),
                MatterCredentialsFolder => segments.Length >= 2,
                _ => false
            };
            return valid ? null : $"{path} is not part of a configuration backup.";
        }

        public static string Hash(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

        private static void Add(ZipArchive zip, string path, byte[] content, DateTimeOffset time)
        {
            var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
            // Zip times can't be before 1980.
            entry.LastWriteTime = time.Year < 1980 ? new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero) : time;
            using var stream = entry.Open();
            stream.Write(content);
        }

        private static byte[] ReadEntry(ZipArchiveEntry entry, long limit)
        {
            if (entry.Length > limit)
                throw new InvalidDataException("The entry is too large.");

            // The declared length can lie, so the copy is limited as well.
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + read > limit)
                    throw new InvalidDataException("The entry is too large.");
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }

        private static bool IsJson(byte[] content)
        {
            ReadOnlyMemory<byte> json = content;
            if (json.Span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
                json = json[3..];
            try
            {
                using var document = JsonDocument.Parse(json);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static BackupValidationException Invalid(string error) => new([error]);
    }
}
