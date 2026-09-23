namespace RIoT2.Net.Orchestrator.Services.Persistence
{
    public class FileObjectStore : IObjectStore
    {
        private readonly string _rootFolder;
        private readonly ILogger<FileObjectStore> _logger;

        public FileObjectStore(IWebHostEnvironment env, ILogger<FileObjectStore> logger)
        {
            _rootFolder = Path.Combine(env.ContentRootPath, "StoredObjects");
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
            var fullFileName = Path.Combine(directory.FullName, id + ".json");
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
            var fullFileName = Path.Combine(_rootFolder, typeName, id + ".json");
            var fileInfo = new FileInfo(fullFileName);
            if (fileInfo.Exists)
                fileInfo.Delete();
        }

        public void DeleteAll(string typeName)
        {
            var directory = new DirectoryInfo(Path.Combine(_rootFolder, typeName));
            if (directory.Exists)
                directory.Delete(true);
        }

        private DirectoryInfo EnsureTypeDirectory(string typeName)
        {
            var directory = new DirectoryInfo(Path.Combine(_rootFolder, typeName));
            if (!directory.Exists)
            {
                directory.Create();
                _logger.LogWarning("Stored objects folder {Folder} did not exist. Directory created.", directory.FullName);
            }
            return directory;
        }
    }
}