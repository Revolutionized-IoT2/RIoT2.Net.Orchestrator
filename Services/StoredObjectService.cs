using RIoT2.Core;
using RIoT2.Core.Interfaces.Services;
using RIoT2.Core.Utils;

namespace RIoT2.Net.Orchestrator.Services
{
    public class StoredObjectService : IStoredObjectService
    {
        private ILogger _logger;
        private Dictionary<string, List<dynamic>>  _objects;
        private string _storedObjectsFolder;

        public StoredObjectService(ILogger<StoredObjectService> logger, IWebHostEnvironment env) 
        {
            _storedObjectsFolder = Path.Combine(env.ContentRootPath, "StoredObjects");
            _objects = new Dictionary<string, List<dynamic>>();
            _logger = logger;
        }

        public event StoredObjectEventHandler StoredObjectEvent;

        public void Delete<T>(string id, bool persistent = true)
        {
            delete<T>(id, persistent);
            StoredObjectEvent?.Invoke(typeof(T), id, OperationType.Deleted);
        }

        public IEnumerable<T> GetAll<T>()
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

            //TODO uncomment if needed
            //StoredObjectEvent?.Invoke(typeof(T), objs, OperationType.Read);
            return objs.OfType<T>();
        }

        public string Save<T>(T obj, bool persistent = true)
        {
            var id = Guid.NewGuid().ToString();
            var t = getTypeString(typeof(T));
            if ((obj as dynamic).Id == null)
                (obj as dynamic).Id = id;
            else
                id = (obj as dynamic).Id;
            var json = Json.SerializeAutoTypeNameHandling(obj);
            var fullFileName = Path.Combine(_storedObjectsFolder, t, id + ".json");
            DirectoryInfo directory = new DirectoryInfo(Path.Combine(_storedObjectsFolder, t));

            if (persistent)
            {
                if (!directory.Exists)
                {
                    directory.Create();
                    _logger.LogWarning($"Saving stored objects. Folder {directory.FullName} did not exist. Directory Created.");
                }
                else 
                {
                    delete<T>(id, persistent);
                }

                try
                {
                    using (var f = File.Create(fullFileName))
                    {
                        using (StreamWriter r = new StreamWriter(f))
                        {
                            r.Write(json);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Could not save {t} with id {id}", id);
                    return null;
                }
            }

            var op = OperationType.Created;
            if (_objects.ContainsKey(t)) 
            {
                _objects[t].Add(obj);
                op = OperationType.Updated;
            }
            else
                _objects.Add(t, new List<dynamic>() { obj });

            StoredObjectEvent?.Invoke(typeof(T), obj, op);
            return id;
        }

        private void delete<T>(string id, bool persistent = true)
        {
            var t = getTypeString(typeof(T));
            if (!_objects.ContainsKey(t))
                return;

            var objs = _objects[t];
            if (objs == null)
                return;

            var objToDelete = objs.FirstOrDefault(x => x.Id == id);
            if (objToDelete != null)
                objs.Remove(objToDelete);

            if (persistent)
            {
                var fullFileName = Path.Combine(_storedObjectsFolder, t, id + ".json");

                FileInfo ruleFileInfo = new FileInfo(fullFileName);
                if (ruleFileInfo.Exists)
                    ruleFileInfo.Delete();
            }
        }

        private void load<T>() 
        {
            var t = getTypeString(typeof(T));
            var objList = new List<dynamic>();

            DirectoryInfo directory = new DirectoryInfo(Path.Combine(_storedObjectsFolder, t));
            if (!directory.Exists)
            {
                directory.Create();
                _logger.LogWarning($"Loading stored objects. Folder {directory.FullName} did not exist. Directory Created.");
            }

            try
            {
                foreach (var file in directory.EnumerateFiles("*.json"))
                {
                    using (var fileStream = file.OpenRead())
                    {
                        using (StreamReader reader = new StreamReader(fileStream))
                        {
                            string json = reader.ReadToEnd();
                            objList.Add(Json.DeserializeAutoTypeNameHandling<T>(json));
                        }
                    }
                }

                if (_objects.ContainsKey(t))
                    _objects[t] = objList;
                else
                    _objects.Add(t, objList);
            }
            catch (Exception x)
            {
                _logger.LogError("Could not load objects {x.Message}", x.Message);
            }
        }

        private string getTypeString(Type type) 
        {
            return type.FullName.Split('.').Last();
        }
    }
}
