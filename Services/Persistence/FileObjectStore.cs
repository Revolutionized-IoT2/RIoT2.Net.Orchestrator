namespace RIoT2.Net.Orchestrator.Services.Persistence
{
    public class FileObjectStore : IObjectStore
    {
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

            if (value is "." or ".." ||
                value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                value.Contains(Path.DirectorySeparatorChar) ||
                value.Contains(Path.AltDirectorySeparatorChar))
            {
                throw new ArgumentException($"Storage name '{value}' contains invalid path characters.", parameterName);
            }
        }

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