using System.Security.Cryptography;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using RIoT2.Core;
using RIoT2.Core.Interfaces.Services;
using RIoT2.Core.Models;
using RIoT2.Core.Models.Matter;
using RIoT2.Matter.Clusters;
using RIoT2.Matter.ControlBridge;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;
using RIoT2.Matter.SecureChannel.Pase;
using RIoT2.Net.Orchestrator.Models;
using MatterNetworkInterface = RIoT2.Matter.Clusters.NetworkInterface;
using SystemNetworkInterface = System.Net.NetworkInformation.NetworkInterface;

namespace RIoT2.Net.Orchestrator.Services.Matter
{
    /// <summary>
    /// Hosts the RIoT Control Bridge and keeps its bridged endpoints in step with the orchestrator's node
    /// configuration, device state and node online state.
    /// </summary>
    /// <remarks>
    /// One instance serves both <see cref="IMatterBridgeService"/> and <see cref="IMatterReportSink"/>.
    /// It deliberately resolves <see cref="IOrchestratorMqttService"/> from the service provider rather
    /// than taking it in the constructor: the MQTT service depends on the report sink, and two singletons
    /// cannot construct each other.
    /// </remarks>
    internal sealed class MatterBridgeService : IMatterBridgeService
    {
        /// <summary>The Control Bridge's own endpoint; the aggregator must not share it.</summary>
        private static readonly EndpointId ControlEndpoint = new(1);

        /// <summary>The aggregator endpoint every bridged device hangs off.</summary>
        private static readonly EndpointId AggregatorEndpoint = new(2);

        private const ushort CommissioningWindowSeconds = 900;
        private const string FabricFileName = "fabrics.json";

        private readonly MatterConfigurationStore _store;
        private readonly IOrchestratorConfigurationService _configurationService;
        private readonly IMessageStateService _messageState;
        private readonly IOnlineNodeService _onlineNodes;
        private readonly IServiceProvider _services;
        private readonly IWebHostEnvironment _environment;
        private readonly ILogger<MatterBridgeService> _logger;

        // Serialises start/stop/refresh: all of them add or remove bridged endpoints, and the UI can call
        // them while the hosted service is still starting the bridge.
        private readonly SemaphoreSlim _gate = new(1, 1);

        // Copied on read so OnReport can walk the adapters without holding the gate on the MQTT thread.
        private volatile RiotBridgedDeviceAdapter[] _adapters = [];

        private MatterConfiguration _configuration;
        private MatterBridgeState _state;
        private ControlBridgeService _bridge;
        private FileFabricPersistence _fabricPersistence;
        private string _error;

        public MatterBridgeService(
            MatterConfigurationStore store,
            IOrchestratorConfigurationService configurationService,
            IMessageStateService messageState,
            IOnlineNodeService onlineNodes,
            IServiceProvider services,
            IWebHostEnvironment environment,
            ILogger<MatterBridgeService> logger)
        {
            ArgumentNullException.ThrowIfNull(store);
            ArgumentNullException.ThrowIfNull(configurationService);
            ArgumentNullException.ThrowIfNull(messageState);
            ArgumentNullException.ThrowIfNull(onlineNodes);
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(environment);

            _store = store;
            _configurationService = configurationService;
            _messageState = messageState;
            _onlineNodes = onlineNodes;
            _services = services;
            _environment = environment;
            _logger = logger;

            _configuration = _store.LoadConfiguration();
            _state = _store.LoadState();
        }

        /// <inheritdoc />
        public bool IsRunning => _bridge is not null;

        /// <inheritdoc />
        public MatterConfiguration Configuration => _configuration.Copy();

        /// <inheritdoc />
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                await StartCoreAsync(cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <inheritdoc />
        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                await StopCoreAsync();
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <inheritdoc />
        public async Task SaveConfigurationAsync(MatterConfiguration configuration, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            await _gate.WaitAsync(cancellationToken);
            try
            {
                // The fabric store key is never taken from the caller: changing it silently orphans every
                // commissioned fabric, so it stays whatever was generated on the first run.
                configuration.FabricStoreKey = _configuration.FabricStoreKey;

                _store.SaveConfiguration(configuration);
                _configuration = configuration;

                // Every configured value is baked into the composed node, so a running bridge is rebuilt.
                await StopCoreAsync();
                await StartCoreAsync(cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <inheritdoc />
        public async Task RefreshDevicesAsync(CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (_bridge is null)
                    return;

                await RemoveDevicesAsync(cancellationToken);
                await AddDevicesAsync(cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <inheritdoc />
        public void OpenCommissioningWindow()
        {
            var bridge = _bridge ?? throw new InvalidOperationException("The Matter bridge is not running.");

            // A basic window re-opens with the device's factory verifier, so the printed QR and manual
            // codes keep working; NoFabric marks it as opened by the device itself rather than by an admin.
            var status = bridge.Device.Commissioning.AdministratorCommissioning.OpenBasicWindow(
                CommissioningWindowSeconds, FabricIndex.NoFabric, adminVendor: null);

            _logger?.LogInformation("Matter commissioning window requested: {Status}", status);
        }

        /// <inheritdoc />
        public async Task ResetAsync(CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                await StopCoreAsync();

                // Drop both halves of the identity: the fabrics a controller commissioned, and the
                // provisioning bundle behind the onboarding codes. The endpoint map goes with them, since
                // a fresh pairing has no device references to keep stable.
                var fabricFile = Path.Combine(ResolveCredentialsDirectory(), FabricFileName);
                if (File.Exists(fabricFile))
                {
                    File.Delete(fabricFile);
                }

                _store.DeleteState();
                _state = new MatterBridgeState();

                await StartCoreAsync(cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <inheritdoc />
        public MatterStatus GetStatus()
        {
            var configuration = _configuration;
            var bridge = _bridge;

            var status = new MatterStatus
            {
                Enabled = configuration.Enabled,
                Running = bridge is not null,
                Error = _error,
                NodeLabel = configuration.NodeLabel,
                VendorId = configuration.VendorId,
                ProductId = configuration.ProductId,
                Discriminator = configuration.Discriminator
            };

            if (bridge is null)
                return status;

            status.QrCode = bridge.Onboarding.QrCode;
            status.ManualCode = bridge.Onboarding.ManualCode;
            status.FormattedManualCode = bridge.Onboarding.FormattedManualCode;
            status.CommissioningWindow = bridge.Device.Commissioning.AdministratorCommissioning.Status.ToString();

            foreach (var fabric in bridge.Device.Commissioning.Manager.Fabrics)
            {
                status.Fabrics.Add(new MatterFabricStatus
                {
                    FabricIndex = fabric.FabricIndex.Value,
                    Label = fabric.Label,
                    VendorId = fabric.VendorId.Value,
                    NodeId = fabric.NodeId.Value.ToString("X16")
                });
            }

            foreach (var adapter in _adapters)
            {
                if (adapter.Device is null)
                    continue;

                status.Endpoints.Add(new MatterEndpointStatus
                {
                    EndpointId = adapter.Device.EndpointId.Value,
                    TemplateId = adapter.Template.Id,
                    Name = adapter.Template.Name,
                    DeviceType = adapter.Template.DeviceType.ToString(),
                    NodeId = adapter.NodeId,
                    DeviceId = adapter.DeviceId,
                    Reachable = adapter.Device.Reachable
                });
            }

            return status;
        }

        /// <inheritdoc />
        public void OnReport(Report report)
        {
            // Called from the MQTT consumer for every report the orchestrator handles, so it must never
            // throw and must not block: each adapter ignores reports that are not bound to it.
            foreach (var adapter in _adapters)
            {
                try
                {
                    adapter.OnReport(report);
                }
                catch (Exception x)
                {
                    _logger?.LogError(x, "Matter endpoint {Endpoint} could not handle report {ReportId}",
                        adapter.Template.Id, report?.Id);
                }
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await _gate.WaitAsync();
            try
            {
                await StopCoreAsync();
            }
            finally
            {
                _gate.Release();
                _gate.Dispose();
            }
        }

        private async Task StartCoreAsync(CancellationToken cancellationToken)
        {
            if (_bridge is not null)
                return;

            _error = null;
            _configuration = _store.LoadConfiguration();

            if (!_configuration.Enabled)
            {
                _logger?.LogInformation("The Matter bridge is disabled; not starting");
                return;
            }

            try
            {
                var credentialsDirectory = ResolveCredentialsDirectory();
                Directory.CreateDirectory(credentialsDirectory);

                // Generated once and then left alone: it seals the persisted fabric table, so replacing it
                // would make every commissioned fabric unreadable.
                if (string.IsNullOrEmpty(_configuration.FabricStoreKey))
                {
                    _configuration.FabricStoreKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                    _store.SaveConfiguration(_configuration);
                }

                var settings = new ControlBridgeSettings
                {
                    Information = new DeviceInformation
                    {
                        VendorId = new VendorId(_configuration.VendorId),
                        ProductId = _configuration.ProductId,
                        VendorName = "RIoT2",
                        ProductName = "RIoT2 Control Bridge",
                        SoftwareVersion = 1,
                        SoftwareVersionString = "1.0.0",
                        SerialNumber = _configurationService.OrchestratorConfiguration?.Id
                    },
                    Attestation = MatterAttestationProvider.Create(_configuration, credentialsDirectory, _logger),
                    NetworkInterfaces = EnumerateNetworkInterfaces(),
                    Discriminator = _configuration.Discriminator,
                    NodeLabel = _configuration.NodeLabel,
                    ControlEndpoint = ControlEndpoint,
                    AggregatorEndpoint = AggregatorEndpoint,
                    CommissioningWindowSeconds = CommissioningWindowSeconds,
                    Provisioning = ResolveProvisioning()
                };

                var bridge = ControlBridgeService.Create(settings);

                // Attached before the host starts, so restored fabrics are in place when the host decides
                // whether to open a commissioning window for an uncommissioned node.
                _fabricPersistence = FileFabricPersistence.Attach(
                    bridge.Device.Commissioning.Manager,
                    Path.Combine(credentialsDirectory, FabricFileName),
                    _configuration.FabricStoreKey);

                await bridge.StartAsync(cancellationToken);
                _bridge = bridge;

                _logger?.LogInformation(
                    "Matter bridge started; pairing code {ManualCode}, discriminator 0x{Discriminator:X4}, {FabricCount} fabric(s)",
                    bridge.Onboarding.FormattedManualCode, _configuration.Discriminator, bridge.Device.Commissioning.Manager.Fabrics.Count);

                await AddDevicesAsync(cancellationToken);
            }
            catch (Exception x)
            {
                _error = x.Message;
                _logger?.LogError(x, "The Matter bridge could not be started");
                await StopCoreAsync();
            }
        }

        private async Task StopCoreAsync()
        {
            await RemoveDevicesAsync(CancellationToken.None);

            if (_bridge is not null)
            {
                try
                {
                    await _bridge.DisposeAsync();
                }
                catch (Exception x)
                {
                    _logger?.LogError(x, "The Matter bridge could not be stopped cleanly");
                }
                _bridge = null;
            }

            // Disposed after the bridge so a fabric change during shutdown is still persisted.
            _fabricPersistence?.Dispose();
            _fabricPersistence = null;
        }

        private async Task AddDevicesAsync(CancellationToken cancellationToken)
        {
            var bridge = _bridge;
            if (bridge is null)
                return;

            var online = _onlineNodes.OnlineNodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
            var adapters = new List<RiotBridgedDeviceAdapter>();
            var mapChanged = false;

            foreach (var node in _configurationService.NodeConfigurations ?? [])
            {
                foreach (var device in node.DeviceConfigurations ?? [])
                {
                    foreach (var template in device.MatterEndpoints ?? [])
                    {
                        if (string.IsNullOrEmpty(template?.Id))
                            continue;

                        try
                        {
                            var adapter = await AddDeviceAsync(bridge, node.Id, device, template, online, cancellationToken);
                            adapters.Add(adapter);

                            var key = EndpointKey(device.Id, template.Id);
                            var assigned = adapter.Device.EndpointId.Value;
                            if (!_state.EndpointMap.TryGetValue(key, out var known) || known != assigned)
                            {
                                _state.EndpointMap[key] = assigned;
                                mapChanged = true;
                            }
                        }
                        catch (Exception x)
                        {
                            // One bad declaration must not cost the whole bridge its other endpoints.
                            _logger?.LogError(x, "Matter endpoint {Endpoint} of device {DeviceId} could not be bridged",
                                template.Id, device.Id);
                        }
                    }
                }
            }

            _adapters = [.. adapters];

            if (mapChanged)
            {
                _store.SaveState(_state);
            }

            _logger?.LogInformation("Matter bridge exposes {Count} bridged endpoint(s)", adapters.Count);
        }

        private async Task<RiotBridgedDeviceAdapter> AddDeviceAsync(
            ControlBridgeService bridge,
            string nodeId,
            DeviceConfiguration device,
            MatterEndpointTemplate template,
            HashSet<string> onlineNodes,
            CancellationToken cancellationToken)
        {
            var reachable = !string.IsNullOrEmpty(nodeId) && onlineNodes.Contains(nodeId);
            var key = EndpointKey(device.Id, template.Id);

            var adapter = new RiotBridgedDeviceAdapter(
                template,
                nodeId,
                device.Id,
                () => _messageState.Reports,
                SendCommandAsync,
                _logger);

            var definition = new BridgedDeviceDefinition
            {
                DeviceType = MatterEndpointComposer.Resolve(template.DeviceType),
                ComposeApplicationClusters = MatterEndpointComposer.CreateComposer(template.DeviceType),
                NodeLabel = string.IsNullOrEmpty(template.Name) ? device.Name : template.Name,
                Reachable = reachable,
                Information = new BridgedDeviceInformation
                {
                    VendorName = string.IsNullOrEmpty(template.VendorName) ? "RIoT2" : template.VendorName,
                    ProductName = string.IsNullOrEmpty(template.ProductName) ? device.Name : template.ProductName,
                    UniqueId = UniqueId(key)
                }
            };

            // A known endpoint id is offered back to the aggregator so a controller keeps pointing at the
            // same device across restarts; an unknown one is allocated sequentially.
            EndpointId? preferred = _state.EndpointMap.TryGetValue(key, out var mapped) ? new EndpointId(mapped) : null;

            await bridge.AddBridgedDeviceAsync(definition, adapter, preferred, cancellationToken);
            return adapter;
        }

        private async Task RemoveDevicesAsync(CancellationToken cancellationToken)
        {
            var bridge = _bridge;
            var adapters = _adapters;
            _adapters = [];

            if (bridge is null)
                return;

            foreach (var adapter in adapters)
            {
                if (adapter.Device is null)
                    continue;

                try
                {
                    await bridge.RemoveBridgedDeviceAsync(adapter.Device, cancellationToken);
                }
                catch (Exception x)
                {
                    _logger?.LogError(x, "Matter endpoint {Endpoint} could not be removed", adapter.Template.Id);
                }
            }
        }

        /// <summary>
        /// Sends the RIoT command a controller-driven attribute change produced, reusing the orchestrator's
        /// own output path so node resolution, command state and MQTT publishing behave exactly as for a rule.
        /// </summary>
        private Task SendCommandAsync(MatterCommandBinding binding, ValueModel value)
        {
            var mqtt = _services.GetService<IOrchestratorMqttService>();
            if (mqtt is null)
            {
                _logger?.LogWarning("The MQTT service is unavailable; command {CommandId} was dropped", binding.CommandTemplateId);
                return Task.CompletedTask;
            }

            return mqtt.ExecuteCommand(new Command
            {
                Id = binding.CommandTemplateId,
                Value = value
            });
        }

        /// <summary>
        /// Restores the provisioning bundle behind the onboarding codes, or mints and persists a new one.
        /// </summary>
        /// <remarks>
        /// The verifier is re-derived rather than stored: it is a deterministic function of the passcode
        /// and the PBKDF parameters. Re-provisioning instead of restoring would change the passcode and
        /// invalidate any QR code the user has already been shown.
        /// </remarks>
        private PaseProvisioning ResolveProvisioning()
        {
            if (_state.Passcode != 0 && _state.PbkdfIterations != 0 && !string.IsNullOrEmpty(_state.PbkdfSalt))
            {
                try
                {
                    var restored = new SetupPasscode(_state.Passcode);
                    var parameters = new PbkdfParameters(_state.PbkdfIterations, Convert.FromBase64String(_state.PbkdfSalt));
                    return new PaseProvisioning(restored, parameters, PaseVerifierGenerator.GenerateVerifier(restored, parameters));
                }
                catch (Exception x)
                {
                    _logger?.LogError(x, "The stored Matter provisioning bundle is unusable; provisioning a new one");
                }
            }

            var provisioning = PaseVerifierGenerator.Provision();
            _state.Passcode = provisioning.Passcode.Value;
            _state.PbkdfIterations = provisioning.Parameters.Iterations;
            _state.PbkdfSalt = Convert.ToBase64String(provisioning.Parameters.Salt);
            _store.SaveState(_state);

            return provisioning;
        }

        private string ResolveCredentialsDirectory()
        {
            var directory = string.IsNullOrWhiteSpace(_configuration.CredentialsDirectory)
                ? "MatterCredentials"
                : _configuration.CredentialsDirectory;

            return Path.IsPathRooted(directory) ? directory : Path.Combine(_environment.ContentRootPath, directory);
        }

        private static string EndpointKey(string deviceId, string templateId) => $"{deviceId}:{templateId}";

        /// <summary>
        /// Derives the Bridged Device Basic Information UniqueId, which the specification limits to 32
        /// characters, from the endpoint key.
        /// </summary>
        private static string UniqueId(string key)
        {
            if (key.Length <= 32)
                return key;

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            return Convert.ToHexString(hash)[..32];
        }

        /// <summary>
        /// Reports the host's operational interfaces to General Diagnostics. Matter is IPv6-centric, so an
        /// interface without an IPv6 address is of no use to a commissioner and is left out.
        /// </summary>
        private IReadOnlyList<MatterNetworkInterface> EnumerateNetworkInterfaces()
        {
            var interfaces = new List<MatterNetworkInterface>();

            foreach (var nic in SystemNetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                var addresses = nic.GetIPProperties().UnicastAddresses;
                var ipv6 = addresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6)
                    .Select(a => a.Address.GetAddressBytes())
                    .ToList();

                if (ipv6.Count == 0)
                    continue;

                var ipv4 = addresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.Address.GetAddressBytes())
                    .ToList();

                interfaces.Add(new MatterNetworkInterface
                {
                    // The cluster limits the name to 32 characters; a Windows adapter name easily exceeds that.
                    Name = nic.Name.Length > 32 ? nic.Name[..32] : nic.Name,
                    IsOperational = true,
                    Type = MapInterfaceType(nic.NetworkInterfaceType),
                    HardwareAddress = nic.GetPhysicalAddress().GetAddressBytes(),
                    IPv4Addresses = ipv4,
                    IPv6Addresses = ipv6
                });
            }

            if (interfaces.Count == 0)
            {
                _logger?.LogWarning(
                    "No operational IPv6 interface was found. Matter requires IPv6; a commissioner will not discover the bridge.");
            }

            return interfaces;
        }

        private static InterfaceType MapInterfaceType(NetworkInterfaceType type) => type switch
        {
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
                or NetworkInterfaceType.FastEthernetFx => InterfaceType.Ethernet,
            NetworkInterfaceType.Wireless80211 => InterfaceType.WiFi,
            NetworkInterfaceType.Wman or NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => InterfaceType.Cellular,
            _ => InterfaceType.Unspecified
        };
    }
}
