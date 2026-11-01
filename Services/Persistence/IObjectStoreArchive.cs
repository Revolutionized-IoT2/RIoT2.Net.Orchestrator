namespace RIoT2.Net.Orchestrator.Services.Persistence
{
    /// <summary>A stored object file, by its path relative to the store root (<c>&lt;Type&gt;/&lt;Id&gt;.json</c>).</summary>
    public sealed record StoredFile(string Path, byte[] Content);

    /// <summary>
    /// Copies the whole object store out and back in, for backup and restore (design 7.4). Only the
    /// object files are included: other files in the store root, such as the outbox database, are
    /// left alone.
    /// </summary>
    public interface IObjectStoreArchive
    {
        /// <summary>Every stored object file, byte for byte, ordered by path.</summary>
        IReadOnlyList<StoredFile> Export();

        /// <summary>
        /// Replaces every stored object with <paramref name="files"/>: types that aren't in them are
        /// removed. Either all files are replaced or, on a failure, the previous ones are put back.
        /// </summary>
        void ReplaceAll(IReadOnlyList<StoredFile> files);

        /// <summary>
        /// Moves every file of <paramref name="typeName"/> to the hidden <c>.archive/&lt;typeName&gt;/</c>
        /// folder in the store root, which is not loaded, backed up or replaced by a restore. A file
        /// already archived under the same name is kept, and the new one gets a numbered name.
        /// </summary>
        /// <returns>The number of files moved.</returns>
        int Archive(string typeName);
    }
}
