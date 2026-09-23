using RIoT2.Core;
using RIoT2.Core.Interfaces.Services;
using RIoT2.Core.Utils;
using RIoT2.Net.Orchestrator.Services.Persistence;

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
            OperationType op;
            string id;

            lock (_sync)
            {
                id = Guid.NewGuid().ToString();

                var t = getTypeString(typeof(T));
                if (String.IsNullOrEmpty((obj as dynamic).Id))
                    (obj as dynamic).Id = id;
                else
                    id = (obj as dynamic).Id;

                var json = Json.SerializeAutoTypeNameHandling(obj, autoTypeNameHandling, includeNulls);

                //Check if current exists -> if does and no change, do nothing
                _objects.TryGetValue(t, out var objs);
                if (objs != null)
                {
                    var currentObject = objs.FirstOrDefault(x => x.Id == id);
                    if (currentObject != null)
                    {
                        var currentJson = Json.SerializeAutoTypeNameHandling(currentObject, autoTypeNameHandling, includeNulls);
                        if (currentJson == json)
                        {
                            _logger.LogInformation("No change in existing object. Object not Saved.");
                            StoredObjectEvent?.Invoke(typeof(T), obj, OperationType.NoChange);
                            return id;
                        }
                    }
                }

                if (persistent)
                {
                    // Overwrite semantics: remove any existing durable copy first.
                    _store.Delete(t, id);

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

                op = OperationType.Created;
                if (_objects.ContainsKey(t))
                {
                    _objects[t].RemoveAll(x => x.Id == id);
                    _objects[t].Add(obj);
                    op = OperationType.Updated;
                }
                else
                    _objects.Add(t, new List<dynamic>() { obj });
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
            delete<T>();
            StoredObjectEvent?.Invoke(typeof(T), null, OperationType.Deleted);
        }

        private void delete<T>(string id = "", bool persistent = true)
        {
            var t = getTypeString(typeof(T));
            if (!_objects.ContainsKey(t))
                return;

            var objs = _objects[t];
            if (objs == null)
                return;

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

            if (persistent)
            {
                if (id == "") //delete everything of type T
                    _store.DeleteAll(t);
                else
                    _store.Delete(t, id);
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
                _logger.LogError("Could not load objects {Message}", x.Message);
            }
        }

        private string getTypeString(Type type)
        {
            return type.FullName.Split('.').Last();
        }
    }
}
