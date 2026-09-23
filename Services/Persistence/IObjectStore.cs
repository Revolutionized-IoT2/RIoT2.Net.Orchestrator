namespace RIoT2.Net.Orchestrator.Services.Persistence
{
    /// <summary>
    /// Abstraction over durable storage of serialized objects.
    /// Implementations are responsible only for persisting/retrieving raw JSON
    /// keyed by a logical type name and object id. No in-memory caching,
    /// serialization policy or change tracking belongs here.
    /// </summary>
    public interface IObjectStore
    {
        /// <summary>Reads all persisted JSON payloads for the given type.</summary>
        IEnumerable<string> ReadAll(string typeName);

        /// <summary>
        /// Atomically creates or replaces an object's JSON. A failed write must preserve the previous payload.
        /// </summary>
        void Write(string typeName, string id, string json);

        /// <summary>Deletes a single object by id.</summary>
        void Delete(string typeName, string id);

        /// <summary>Deletes every object of the given type.</summary>
        void DeleteAll(string typeName);
    }
}