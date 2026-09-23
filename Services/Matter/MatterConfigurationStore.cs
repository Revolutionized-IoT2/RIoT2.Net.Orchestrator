using RIoT2.Core.Utils;
using RIoT2.Net.Orchestrator.Services.Persistence;

namespace RIoT2.Net.Orchestrator.Services.Matter
{
    /// <summary>
    /// Loads and saves the two persisted Matter documents through the orchestrator's
    /// <see cref="IObjectStore"/>: the operator-editable <see cref="MatterConfiguration"/> and the
    /// machine-owned <see cref="MatterBridgeState"/>. Each document is a single object under its own
    /// type folder, so a read is simply the first entry of that folder.
    /// </summary>
    public class MatterConfigurationStore
    {
        private const string ConfigurationType = "MatterConfiguration";
        private const string StateType = "MatterBridgeState";
        private const string SingletonId = "matter";

        private readonly IObjectStore _store;
        private readonly ILogger<MatterConfigurationStore> _logger;

        public MatterConfigurationStore(IObjectStore store, ILogger<MatterConfigurationStore> logger)
        {
            ArgumentNullException.ThrowIfNull(store);
            _store = store;
            _logger = logger;
        }

        /// <summary>Reads the stored configuration, or a default (disabled) one when nothing is stored yet.</summary>
        public MatterConfiguration LoadConfiguration() => Read<MatterConfiguration>(ConfigurationType) ?? new MatterConfiguration();

        public void SaveConfiguration(MatterConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            _store.Write(ConfigurationType, SingletonId, Json.Serialize(configuration));
        }

        /// <summary>Reads the stored bridge state, or an empty one when the bridge has never run.</summary>
        public MatterBridgeState LoadState() => Read<MatterBridgeState>(StateType) ?? new MatterBridgeState();

        public void SaveState(MatterBridgeState state)
        {
            ArgumentNullException.ThrowIfNull(state);
            _store.Write(StateType, SingletonId, Json.Serialize(state));
        }

        /// <summary>Drops the provisioning bundle and endpoint map so the next start pairs the bridge afresh.</summary>
        public void DeleteState() => _store.Delete(StateType, SingletonId);

        private T Read<T>(string typeName) where T : class
        {
            try
            {
                var json = _store.ReadAll(typeName).FirstOrDefault();
                return string.IsNullOrWhiteSpace(json) ? null : Json.Deserialize<T>(json);
            }
            catch (Exception x)
            {
                // A corrupt document must not stop the orchestrator from starting; fall back to defaults.
                _logger.LogError(x, "Could not read stored {TypeName}; falling back to defaults", typeName);
                return null;
            }
        }
    }
}
