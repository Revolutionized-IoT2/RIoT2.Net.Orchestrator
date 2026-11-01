using RIoT2.Core;
using RIoT2.Core.Interfaces;
using RIoT2.Net.Orchestrator.Services.Persistence;

namespace RIoT2.Net.Orchestrator.Services
{
    /// <inheritdoc cref="IStoredObjectStore" />
    /// <remarks>The files are written with <see cref="StoredObjectJson"/>.</remarks>
    public class StoredObjectService : IStoredObjectStore
    {
        private readonly ILogger _logger;
        private readonly IObjectStore _store;

        // The cached objects by type name; each list holds objects of that type only.
        private readonly Dictionary<string, List<IStoredObject>> _objects = new(StringComparer.Ordinal);

        // Guards _objects and the persistence calls, so a check-then-act sequence (save, delete, load)
        // is atomic.
        private readonly object _sync = new();

        public StoredObjectService(IObjectStore store, ILogger<StoredObjectService> logger)
        {
            ArgumentNullException.ThrowIfNull(store);
            _store = store;
            _logger = logger;
        }

        public event Action<StoredObjectChange> Changed;

        public IReadOnlyList<T> GetAll<T>() where T : class, IStoredObject
        {
            lock (_sync)
            {
                // A snapshot, so callers can enumerate it while the store changes.
                return Objects<T>().Cast<T>().ToList();
            }
        }

        public T Get<T>(string id) where T : class, IStoredObject
        {
            lock (_sync)
            {
                return (T)Objects<T>().FirstOrDefault(x => x.Id == id);
            }
        }

        public string Save<T>(T obj, bool persistent = true) where T : class, IStoredObject
        {
            ArgumentNullException.ThrowIfNull(obj);
            OperationType operation;

            lock (_sync)
            {
                var objects = Objects<T>();
                if (string.IsNullOrEmpty(obj.Id))
                    obj.Id = Guid.NewGuid().ToString();

                var json = StoredObjectJson.Serialize(obj);
                var current = objects.FirstOrDefault(x => x.Id == obj.Id);
                if (current != null && StoredObjectJson.Serialize((T)current) == json)
                {
                    _logger.LogInformation("No change in {Type} {Id}; not saved", TypeName<T>(), obj.Id);
                    operation = OperationType.NoChange;
                }
                else
                {
                    if (persistent)
                    {
                        try
                        {
                            _store.Write(TypeName<T>(), obj.Id, json);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Could not save {Type} with id {Id}", TypeName<T>(), obj.Id);
                            return null;
                        }
                    }

                    operation = current == null ? OperationType.Created : OperationType.Updated;
                    objects.RemoveAll(x => x.Id == obj.Id);
                    objects.Add(obj);
                }
            }

            Changed?.Invoke(new StoredObjectChange(typeof(T), obj.Id, obj, operation));
            return obj.Id;
        }

        public void Delete<T>(string id, bool persistent = true) where T : class, IStoredObject
        {
            ArgumentException.ThrowIfNullOrEmpty(id);
            IStoredObject deleted;
            lock (_sync)
            {
                var objects = Objects<T>();
                if (persistent)
                    _store.Delete(TypeName<T>(), id);

                deleted = objects.FirstOrDefault(x => x.Id == id);
                if (deleted != null)
                    objects.Remove(deleted);
            }
            Changed?.Invoke(new StoredObjectChange(typeof(T), id, deleted, OperationType.Deleted));
        }

        public void DeleteAll<T>() where T : class, IStoredObject
        {
            lock (_sync)
            {
                _store.DeleteAll(TypeName<T>());
                _objects[TypeName<T>()] = [];
            }
            Changed?.Invoke(new StoredObjectChange(typeof(T), null, null, OperationType.Deleted));
        }

        /// <summary>Runs <paramref name="action"/> while no object can be saved or deleted.</summary>
        public TResult Exclusive<TResult>(Func<TResult> action)
        {
            ArgumentNullException.ThrowIfNull(action);
            lock (_sync)
            {
                return action();
            }
        }

        /// <summary>
        /// Runs <paramref name="replace"/>, which changes the stored files, while no object can be saved
        /// or deleted, then drops every cached object, so the next read loads the new files. Call
        /// <see cref="AnnounceReplaced{T}"/> afterwards for the types whose listeners must hear of it.
        /// </summary>
        public void Replace(Action replace)
        {
            ArgumentNullException.ThrowIfNull(replace);
            lock (_sync)
            {
                try
                {
                    replace();
                }
                finally
                {
                    _objects.Clear();
                }
            }
        }

        /// <summary>
        /// Raises <see cref="Changed"/> after <see cref="Replace"/>: <c>Deleted</c> for each of
        /// <paramref name="previousIds"/> that is gone, then <c>Updated</c> for every object of
        /// <typeparamref name="T"/> now stored.
        /// </summary>
        public void AnnounceReplaced<T>(IEnumerable<string> previousIds) where T : class, IStoredObject
        {
            ArgumentNullException.ThrowIfNull(previousIds);
            var current = GetAll<T>();
            var currentIds = current.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var id in previousIds.Where(id => !currentIds.Contains(id)).Distinct(StringComparer.Ordinal).ToList())
                Changed?.Invoke(new StoredObjectChange(typeof(T), id, null, OperationType.Deleted));
            foreach (var obj in current)
                Changed?.Invoke(new StoredObjectChange(typeof(T), obj.Id, obj, OperationType.Updated));
        }

        // The cached objects of T, loaded from the store on first use. Call inside the lock.
        private List<IStoredObject> Objects<T>() where T : class, IStoredObject
        {
            var name = TypeName<T>();
            if (_objects.TryGetValue(name, out var objects))
                return objects;

            objects = [];
            try
            {
                foreach (var json in _store.ReadAll(name))
                {
                    var obj = StoredObjectJson.Deserialize<T>(json);
                    if (obj == null)
                        _logger.LogWarning("Skipped an empty {Type} file", name);
                    else
                        objects.Add(obj);
                }
            }
            catch (Exception x)
            {
                // Nothing is cached, so a save can't replace objects that failed to load.
                _logger.LogError(x, "Could not load objects of type {Type}", name);
                throw;
            }

            _objects[name] = objects;
            return objects;
        }

        private static string TypeName<T>() => typeof(T).Name;
    }
}
