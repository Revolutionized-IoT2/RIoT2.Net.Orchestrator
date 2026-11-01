namespace RIoT2.Net.Orchestrator.Services.Persistence
{
    public class FileObjectStore : IObjectStore, IObjectStoreArchive
    {
        private const string ObjectExtension = ".json";

        /// <summary>The hidden folder in the store root that <see cref="Archive"/> moves files to.</summary>
        public const string ArchiveFolder = ".archive";

        private readonly string _rootFolder;
        private readonly ILogger<FileObjectStore> _logger;

        public FileObjectStore(IWebHostEnvironment env, ILogger<FileObjectStore> logger)
        {
            _rootFolder = Path.GetFullPath(Path.Combine(env.ContentRootPath, "StoredObjects"));
            _logger = logger;
        }

        public IEnumerable<string> ReadAll(string typeName)
        {
            var directory = EnsureTypeDirectory(typeName);

            foreach (var file in directory.EnumerateFiles("*.json"))
            {
                string json;
                using (var fileStream = file.OpenRead())
                using (var reader = new StreamReader(fileStream))
                {
                    json = reader.ReadToEnd();
                }
                yield return json;
            }
        }

        public void Write(string typeName, string id, string json)
        {
            var directory = EnsureTypeDirectory(typeName);
            var fullFileName = GetObjectPath(directory, id);
            var temporaryFileName = Path.Combine(directory.FullName, $".{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(temporaryFileName, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using var writer = new StreamWriter(stream, leaveOpen: true);
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                // Keep the old file intact until the complete replacement is on the same filesystem.
                File.Move(temporaryFileName, fullFileName, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryFileName))
                    File.Delete(temporaryFileName);
            }
        }

        public void Delete(string typeName, string id)
        {
            var directory = EnsureTypeDirectory(typeName);
            var fullFileName = GetObjectPath(directory, id);
            var fileInfo = new FileInfo(fullFileName);
            if (fileInfo.Exists)
                fileInfo.Delete();
        }

        public void DeleteAll(string typeName)
        {
            var directory = new DirectoryInfo(GetTypeDirectoryPath(typeName));
            if (directory.Exists)
                directory.Delete(true);
        }

        public IReadOnlyList<StoredFile> Export()
        {
            var root = new DirectoryInfo(_rootFolder);
            if (!root.Exists)
                return [];

            // Hidden folders and files are a restore in progress or a write's temporary file.
            var files = new List<StoredFile>();
            foreach (var directory in root.EnumerateDirectories()
                .Where(d => IsSafeName(d.Name) && !d.Name.StartsWith('.'))
                .OrderBy(d => d.Name, StringComparer.Ordinal))
            {
                foreach (var file in directory.EnumerateFiles("*" + ObjectExtension)
                    .Where(f => IsObjectFileName(f.Name))
                    .OrderBy(f => f.Name, StringComparer.Ordinal))
                {
                    files.Add(new StoredFile($"{directory.Name}/{file.Name}", File.ReadAllBytes(file.FullName)));
                }
            }
            return files;
        }

        public void ReplaceAll(IReadOnlyList<StoredFile> files)
        {
            ArgumentNullException.ThrowIfNull(files);
            var entries = files.Select(file => (File: file, Parts: SplitObjectPath(file?.Path))).ToList();

            var root = Directory.CreateDirectory(_rootFolder);
            var token = Guid.NewGuid().ToString("N");
            // Both folders are inside the root, so the moves below stay on one filesystem (a volume).
            var staging = Directory.CreateDirectory(Path.Combine(root.FullName, $".restore-{token}"));
            var previous = Path.Combine(root.FullName, $".previous-{token}");
            var movedAway = new List<string>();
            var movedIn = new List<string>();
            var restored = false;
            try
            {
                foreach (var (file, (type, name)) in entries)
                {
                    var directory = Directory.CreateDirectory(Path.Combine(staging.FullName, type));
                    File.WriteAllBytes(Path.Combine(directory.FullName, name), file.Content ?? []);
                }

                Directory.CreateDirectory(previous);
                foreach (var existing in root.EnumerateDirectories().Where(d => !d.Name.StartsWith('.')).ToList())
                {
                    existing.MoveTo(Path.Combine(previous, existing.Name));
                    movedAway.Add(existing.Name);
                }
                foreach (var staged in staging.EnumerateDirectories().ToList())
                {
                    staged.MoveTo(Path.Combine(root.FullName, staged.Name));
                    movedIn.Add(staged.Name);
                }
                restored = true;
            }
            catch (Exception x)
            {
                _logger.LogError(x, "Could not replace the stored objects; putting the previous ones back");
                PutBack(root.FullName, previous, movedAway, movedIn);
                throw;
            }
            finally
            {
                TryDelete(staging.FullName);
                if (restored)
                    TryDelete(previous);
            }
        }

        public int Archive(string typeName)
        {
            var directory = new DirectoryInfo(GetTypeDirectoryPath(typeName));
            if (!directory.Exists)
                return 0;

            var archive = Directory.CreateDirectory(Path.Combine(_rootFolder, ArchiveFolder, typeName));
            var moved = 0;
            foreach (var file in directory.EnumerateFiles().ToList())
            {
                var target = Path.Combine(archive.FullName, file.Name);
                for (var i = 1; File.Exists(target); i++)
                    target = Path.Combine(archive.FullName, $"{Path.GetFileNameWithoutExtension(file.Name)}-{i}{file.Extension}");
                file.MoveTo(target);
                moved++;
            }
            directory.Delete(true);
            return moved;
        }

        private void PutBack(string root, string previous, List<string> movedAway, List<string> movedIn)
        {
            try
            {
                foreach (var name in movedIn)
                    Directory.Delete(Path.Combine(root, name), true);
                foreach (var name in movedAway)
                    Directory.Move(Path.Combine(previous, name), Path.Combine(root, name));
                TryDelete(previous);
            }
            catch (Exception x)
            {
                // The previous objects stay in the hidden folder for a manual repair.
                _logger.LogCritical(x, "Could not put the previous stored objects back; they are in {Folder}", previous);
            }
        }

        private void TryDelete(string directory)
        {
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
            catch (Exception x)
            {
                _logger.LogWarning(x, "Could not delete the temporary folder {Folder}", directory);
            }
        }

        private static (string Type, string Name) SplitObjectPath(string path)
        {
            var parts = path?.Split('/');
            if (parts is not { Length: 2 } || !IsSafeName(parts[0]) || parts[0].StartsWith('.') || !IsObjectFileName(parts[1]))
                throw new ArgumentException($"'{path}' is not a stored object path (<Type>/<Id>.json).", nameof(path));
            return (parts[0], parts[1]);
        }

        private static bool IsObjectFileName(string name) =>
            IsSafeName(name) && !name.StartsWith('.') &&
            name.Length > ObjectExtension.Length && name.EndsWith(ObjectExtension, StringComparison.Ordinal);

        private DirectoryInfo EnsureTypeDirectory(string typeName)
        {
            var directory = new DirectoryInfo(GetTypeDirectoryPath(typeName));
            if (!directory.Exists)
            {
                directory.Create();
                _logger.LogWarning("Stored objects folder {Folder} did not exist. Directory created.", directory.FullName);
            }
            return directory;
        }

        private string GetTypeDirectoryPath(string typeName)
        {
            ValidateSafeName(typeName, nameof(typeName));
            var path = Path.GetFullPath(Path.Combine(_rootFolder, typeName));
            EnsureUnderRoot(path, _rootFolder, "Stored object type path escapes the storage root.");
            return path;
        }

        private static string GetObjectPath(DirectoryInfo directory, string id)
        {
            ValidateSafeName(id, nameof(id));
            var path = Path.GetFullPath(Path.Combine(directory.FullName, id + ".json"));
            EnsureUnderRoot(path, directory.FullName, "Stored object id path escapes its type directory.");
            return path;
        }

        private static void ValidateSafeName(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("A non-empty storage name is required.", parameterName);

            if (!IsSafeName(value))
                throw new ArgumentException($"Storage name '{value}' contains invalid path characters.", parameterName);
        }

        /// <summary>Whether <paramref name="value"/> is a single, portable file or folder name.</summary>
        internal static bool IsSafeName(string value) =>
            !string.IsNullOrWhiteSpace(value) &&
            value is not ("." or "..") &&
            value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
            // Both separators on every OS: on Linux '\' is a valid file name character, but a
            // name with it would not be portable to a Windows store or a Windows developer box.
            value.IndexOfAny(['/', '\\']) < 0;

        private static void EnsureUnderRoot(string path, string root, string message)
        {
            var normalizedRoot = Path.GetFullPath(root);
            if (!normalizedRoot.EndsWith(Path.DirectorySeparatorChar))
                normalizedRoot += Path.DirectorySeparatorChar;

            if (!path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(message);
        }
    }
}