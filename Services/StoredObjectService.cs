using RIoT2.Core;
using RIoT2.Core.Interfaces.Services;
using RIoT2.Core.Utils;
using RIoT2.Net.Orchestrator.Services.Persistence;

// Stored configuration keeps Newtonsoft's type-name handling until typed persistence (I26).
#pragma warning disable CS0618

namespace RIoT2.Net.Orchestrator.Services
{
    public class StoredObjectService : IStoredObjectService
    {
        private ILogger _logger;
        private Dictionary<string, List<dynamic>> _objects;
        private readonly IObjectStore _store;

        // Guards all access to _objects and the associated persistence calls.
        // Compound check-then-act sequences (save/delete/load) require a single lock
        // to remain atomic.
        private readonly object _sync = new object();

        public StoredObjectService(IObjectStore store, ILogger<StoredObjectService> logger)
        {
            _store = store;
            _objects = new Dictionary<string, List<dynamic>>();
            _logger = logger;
        }

        public event StoredObjectEventHandler StoredObjectEvent;

        public void Delete<T>(string id, bool persistent = true)
        {
            lock (_sync)
            {
                delete<T>(id, persistent);
            }
            StoredObjectEvent?.Invoke(typeof(T), id, OperationType.Deleted);
        }

        public IEnumerable<T> GetAll<T>()
        {
            lock (_sync)
            {
                var t = getTypeString(typeof(T));
                if (!_objects.ContainsKey(t))
                {
                    load<T>();
                    if (!_objects.ContainsKey(t))
                        return [];
                }

                var objs = _objects[t];
                if (objs == null)
                    return [];

                // Materialize a snapshot inside the lock so callers can enumerate safely.
                return objs.OfType<T>().ToList();
            }
        }

        public string Save<T>(T obj, bool persistent = true, bool autoTypeNameHandling = false, bool includeNulls = false)
        {
            ArgumentNullException.ThrowIfNull(obj);
            OperationType op = OperationType.NoChange;
            string id;
            var hasNoChange = false;

            lock (_sync)
            {
                id = Guid.NewGuid().ToString();

                var t = getTypeString(typeof(T));
                if (!_objects.ContainsKey(t))
                    load<T>();

                if (String.IsNullOrEmpty((obj as dynamic).Id))
                    (obj as dynamic).Id = id;
                else
                    id = (obj as dynamic).Id;

                var json = Json.SerializeAutoTypeNameHandling(obj, autoTypeNameHandling, includeNulls);

                //Check if current exists -> if does and no change, do nothing
                var objs = _objects[t];
                var currentObject = objs.FirstOrDefault(x => x.Id == id);
                if (currentObject != null)
                {
                    var currentJson = Json.SerializeAutoTypeNameHandling(currentObject, autoTypeNameHandling, includeNulls);
                    if (currentJson == json)
                    {
                        _logger.LogInformation("No change in existing object. Object not Saved.");
                        hasNoChange = true;
                    }
                }

                if (!hasNoChange)
                {
                    if (persistent)
                    {
                        try
                        {
                            _store.Write(t, id, json);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Could not save {Type} with id {Id}", t, id);
                            return null;
                        }
                    }

                    op = currentObject == null ? OperationType.Created : OperationType.Updated;
                    objs.RemoveAll(x => x.Id == id);
                    objs.Add(obj);
                }
            }

            StoredObjectEvent?.Invoke(typeof(T), obj, op);
            return id;
        }

        public T Get<T>()
        {
            return GetAll<T>().FirstOrDefault();
        }

        public void DeleteAll<T>()
        {
            lock (_sync)
            {
                delete<T>();
            }
            StoredObjectEvent?.Invoke(typeof(T), null, OperationType.Deleted);
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
        /// Raises <see cref="StoredObjectEvent"/> after <see cref="Replace"/>: <c>Deleted</c> for each of
        /// <paramref name="previousIds"/> that is gone, then <c>Updated</c> for every object of
        /// <typeparamref name="T"/> now stored.
        /// </summary>
        public void AnnounceReplaced<T>(IEnumerable<string> previousIds)
        {
            ArgumentNullException.ThrowIfNull(previousIds);
            var current = GetAll<T>().ToList();
            var currentIds = current.Select(x => (string)(x as dynamic).Id).ToHashSet(StringComparer.Ordinal);
            foreach (var id in previousIds.Where(id => !currentIds.Contains(id)).Distinct(StringComparer.Ordinal).ToList())
                StoredObjectEvent?.Invoke(typeof(T), id, OperationType.Deleted);
            foreach (var obj in current)
                StoredObjectEvent?.Invoke(typeof(T), obj, OperationType.Updated);
        }

        private void delete<T>(string id = "", bool persistent = true)
        {
            var t = getTypeString(typeof(T));
            if (!_objects.ContainsKey(t))
                load<T>();

            var objs = _objects[t];

            if (persistent)
            {
                if (id == "")
                    _store.DeleteAll(t);
                else
                    _store.Delete(t, id);
            }

            if (id == "") //delete everything of type T
            {
                _objects.Remove(t);
            }
            else //just delete the one with the id
            {
                var objToDelete = objs.FirstOrDefault(x => x.Id == id);
                if (objToDelete != null)
                    objs.Remove(objToDelete);
            }

        }

        private void load<T>()
        {
            var t = getTypeString(typeof(T));
            var objList = new List<dynamic>();

            try
            {
                foreach (var json in _store.ReadAll(t))
                    objList.Add(Json.DeserializeAutoTypeNameHandling<T>(json));

                if (_objects.ContainsKey(t))
                    _objects[t] = objList;
                else
                    _objects.Add(t, objList);
            }
            catch (Exception x)
            {
                _logger.LogError(x, "Could not load objects of type {Type}", t);
                throw;
            }
        }

        private string getTypeString(Type type)
        {
            return type.FullName.Split('.').Last();
        }
    }
}
