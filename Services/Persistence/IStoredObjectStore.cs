using RIoT2.Core;
using RIoT2.Core.Interfaces;

namespace RIoT2.Net.Orchestrator.Services.Persistence
{
    /// <summary>
    /// The orchestrator's configuration objects (node configurations, variables, the dashboard), each
    /// type cached in memory and stored as <c>StoredObjects/&lt;Type&gt;/&lt;Id&gt;.json</c> (typed
    /// persistence, plan M2 step 4). Replaces RIoT2.Core's <c>IStoredObjectService</c>.
    /// </summary>
    public interface IStoredObjectStore
    {
        /// <summary>
        /// Raised after every save and delete, outside the store's lock, in the order the changes
        /// were made. A save whose content didn't change is raised with <see cref="OperationType.NoChange"/>.
        /// </summary>
        event Action<StoredObjectChange>? Changed;

        /// <summary>A snapshot of every stored <typeparamref name="T"/>; loaded from the store on first use.</summary>
        IReadOnlyList<T> GetAll<T>() where T : class, IStoredObject;

        /// <summary>The stored <typeparamref name="T"/> with <paramref name="id"/>, or <c>null</c>.</summary>
        T? Get<T>(string id) where T : class, IStoredObject;

        /// <summary>
        /// Saves <paramref name="obj"/>, giving it a new GUID when its id is empty, and returns the id;
        /// <c>null</c> when the file could not be written (the cache then keeps the previous object).
        /// </summary>
        /// <param name="persistent">When <c>false</c>, the object is only kept in memory.</param>
        string? Save<T>(T obj, bool persistent = true) where T : class, IStoredObject;

        /// <summary>Deletes the <typeparamref name="T"/> with <paramref name="id"/>.</summary>
        /// <param name="persistent">When <c>false</c>, the object is only removed from memory.</param>
        void Delete<T>(string id, bool persistent = true) where T : class, IStoredObject;

        /// <summary>Deletes every stored <typeparamref name="T"/>.</summary>
        void DeleteAll<T>() where T : class, IStoredObject;
    }

    /// <summary>A change to the stored objects, raised by <see cref="IStoredObjectStore.Changed"/>.</summary>
    /// <param name="Type">The stored type.</param>
    /// <param name="Id">The object's id; <c>null</c> when every object of <paramref name="Type"/> was deleted.</param>
    /// <param name="Object">
    /// The saved object, or the deleted one when it was in memory; <c>null</c> for a delete of everything.
    /// </param>
    /// <param name="Operation"><c>Created</c>, <c>Updated</c>, <c>NoChange</c> or <c>Deleted</c>.</param>
    public sealed record StoredObjectChange(Type Type, string? Id, IStoredObject? Object, OperationType Operation)
    {
        /// <summary>Whether the change is to a <typeparamref name="T"/>.</summary>
        public bool Is<T>() where T : IStoredObject => Type == typeof(T);
    }
}
