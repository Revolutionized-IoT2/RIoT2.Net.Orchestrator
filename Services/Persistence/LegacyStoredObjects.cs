namespace RIoT2.Net.Orchestrator.Services.Persistence
{
    /// <summary>
    /// Stored types that this version no longer reads. Their files are moved to
    /// <c>StoredObjects/.archive/</c> at startup and after a restore, so they are kept for reference
    /// but not loaded or backed up (plan M2 step 4).
    /// </summary>
    public static class LegacyStoredObjects
    {
        /// <summary>
        /// The rule engine's rules, replaced by Elsa workflows. Their files hold Newtonsoft type names
        /// (<c>$type</c>) and <c>ExpandoObject</c>s that typed persistence doesn't read.
        /// </summary>
        public static IReadOnlyList<string> Types { get; } = ["Rule"];

        /// <summary>Archives the files of every legacy type; logs a warning for each type it moves.</summary>
        /// <returns>The number of files moved.</returns>
        public static int Archive(IObjectStoreArchive store, ILogger logger)
        {
            ArgumentNullException.ThrowIfNull(store);
            ArgumentNullException.ThrowIfNull(logger);
            var total = 0;
            foreach (var type in Types)
            {
                try
                {
                    var moved = store.Archive(type);
                    if (moved > 0)
                        logger.LogWarning("Moved {Count} legacy {Type} files to StoredObjects/{Folder}/{Type}; this version doesn't use them",
                            moved, type, FileObjectStore.ArchiveFolder, type);
                    total += moved;
                }
                catch (Exception x)
                {
                    logger.LogError(x, "Could not archive the legacy {Type} files; they stay where they are and are not loaded", type);
                }
            }
            return total;
        }
    }
}
