using System.Security.Cryptography;

namespace RIoT2.Net.Orchestrator.Services.Matter
{
    /// <summary>
    /// The Matter bridge's credential files: the content-root-relative credentials directory, the
    /// generated fabric store key and the persisted fabric table.
    /// </summary>
    internal sealed class MatterCredentials
    {
        /// <summary>The fabric table's file name in the credentials directory.</summary>
        public const string FabricFileName = "fabrics.json";

        /// <summary>The credentials directory when none is configured.</summary>
        public const string DefaultDirectory = "MatterCredentials";

        private readonly string _contentRoot;
        private readonly MatterConfigurationStore _store;

        public MatterCredentials(string contentRoot, MatterConfigurationStore store)
        {
            ArgumentException.ThrowIfNullOrEmpty(contentRoot);
            ArgumentNullException.ThrowIfNull(store);
            _contentRoot = contentRoot;
            _store = store;
        }

        /// <summary>The full path of the configured credentials directory.</summary>
        /// <exception cref="ArgumentException">The directory is absolute or escapes the content root.</exception>
        public string ResolveDirectory(string configuredDirectory) => ResolveDirectory(_contentRoot, configuredDirectory);

        /// <summary>The full path of the configuration's fabric table.</summary>
        public string FabricFile(MatterConfiguration configuration) =>
            Path.Combine(ResolveDirectory(configuration.CredentialsDirectory), FabricFileName);

        /// <summary>
        /// Creates the credentials directory and, on the first run, generates and saves the fabric store
        /// key. Returns the directory.
        /// </summary>
        public string Prepare(MatterConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            var directory = ResolveDirectory(configuration.CredentialsDirectory);
            Directory.CreateDirectory(directory);

            // Generated once and then left alone: it seals the persisted fabric table, so replacing it
            // would make every commissioned fabric unreadable.
            if (string.IsNullOrEmpty(configuration.FabricStoreKey))
            {
                configuration.FabricStoreKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                _store.SaveConfiguration(configuration);
            }

            return directory;
        }

        /// <summary>Deletes the persisted fabric table, forgetting every commissioned fabric.</summary>
        public void DeleteFabrics(MatterConfiguration configuration)
        {
            var fabricFile = FabricFile(configuration);
            if (File.Exists(fabricFile))
                File.Delete(fabricFile);
        }

        /// <summary>
        /// The full path of the configured credentials directory, which must be relative to and stay
        /// under <paramref name="contentRoot"/>; empty means <see cref="DefaultDirectory"/>.
        /// </summary>
        /// <exception cref="ArgumentException">The directory is absolute or escapes the content root.</exception>
        public static string ResolveDirectory(string contentRoot, string configuredDirectory)
        {
            var directory = string.IsNullOrWhiteSpace(configuredDirectory)
                ? DefaultDirectory
                : configuredDirectory;

            if (Path.IsPathRooted(directory))
                throw new ArgumentException("Matter credentials directory must be relative to the orchestrator content root.", nameof(configuredDirectory));

            contentRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(contentRoot));
            var fullPath = Path.GetFullPath(Path.Combine(contentRoot, directory));
            var normalizedRoot = contentRoot.EndsWith(Path.DirectorySeparatorChar)
                ? contentRoot
                : contentRoot + Path.DirectorySeparatorChar;

            if (!fullPath.Equals(contentRoot, StringComparison.OrdinalIgnoreCase) &&
                !fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Matter credentials directory must stay under the orchestrator content root.", nameof(configuredDirectory));
            }

            return fullPath;
        }
    }
}
