using RIoT2.Core;
using RIoT2.Core.Interfaces.Services;
using RIoT2.Core.Models;
using RIoT2.Core.Models.Matter;
using RIoT2.Matter.Clusters;
using RIoT2.Matter.ControlBridge;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;
using RIoT2.Net.Orchestrator.Models;

namespace RIoT2.Net.Orchestrator.Services.Matter
{
    /// <summary>
    /// Hosts the RIoT Control Bridge and keeps its bridged endpoints in step with the orchestrator's node
    /// configuration, device state and node online state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The service owns the bridge's lifecycle and its persisted configuration and state; the work is
    /// done by collaborators: <see cref="MatterCredentials"/> (credentials directory, fabric store key
    /// and fabric table), <see cref="MatterProvisioning"/> (the onboarding bundle),
    /// <see cref="MatterHostNetwork"/> (General Diagnostics interfaces) and
    /// <see cref="MatterBridgedEndpoints"/> (endpoint composition, report routing and presence).
    /// </para>
    /// <para>
    /// One instance serves both <see cref="IMatterBridgeService"/> and <see cref="IMatterReportSink"/>.
    /// It deliberately resolves <see cref="IOrchestratorMqttService"/> from the service provider rather
    /// than taking it in the constructor: the MQTT service depends on the report sink, and two singletons
    /// cannot construct each other.
    /// </para>
    /// </remarks>
    internal sealed class MatterBridgeService : IMatterBridgeService
    {
        /// <summary>The Control Bridge's own endpoint; the aggregator must not share it.</summary>
        private static readonly EndpointId ControlEndpoint = new(1);

        /// <summary>The aggregator endpoint every bridged device hangs off.</summary>
        private static readonly EndpointId AggregatorEndpoint = new(2);

        private const ushort CommissioningWindowSeconds = 900;

        private readonly MatterConfigurationStore _store;
        private readonly IOrchestratorConfigurationService _configurationService;
        private readonly IServiceProvider _services;
        private readonly ILogger<MatterBridgeService> _logger;
        private readonly MatterCredentials _credentials;
        private readonly MatterBridgedEndpoints _endpoints;

        // Serialises start/stop/refresh: all of them add or remove bridged endpoints, and the UI can call
        // them while the hosted service is still starting the bridge.
        private readonly SemaphoreSlim _gate = new(1, 1);
        private int _disposed;

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
            _services = services;
            _logger = logger;
            _credentials = new MatterCredentials(environment.ContentRootPath, store);
            _endpoints = new MatterBridgedEndpoints(configurationService, messageState, onlineNodes, store, SendCommandAsync, logger);

            _configuration = _store.LoadConfiguration();
            _state = _store.LoadState();
        }

        /// <inheritdoc />
        public bool IsRunning => _bridge is not null;

        /// <inheritdoc />
        public MatterConfiguration Configuration => _configuration.Copy();

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken = default) =>
            WithGateAsync(() => StartCoreAsync(cancellationToken), cancellationToken);

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken = default) =>
            WithGateAsync(StopCoreAsync, cancellationToken);

        /// <inheritdoc />
        public async Task SaveConfigurationAsync(MatterConfiguration configuration, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            // Validate before persisting: this value controls where the bridge writes generated
            // credentials and deletes its fabric store during reset.
            _credentials.ResolveDirectory(configuration.CredentialsDirectory);

            await WithGateAsync(async () =>
            {
                // The fabric store key is never taken from the caller: changing it silently orphans every
                // commissioned fabric, so it stays whatever was generated on the first run.
                configuration.FabricStoreKey = _configuration.FabricStoreKey;

                _store.SaveConfiguration(configuration);
                _configuration = configuration;

                // Every configured value is baked into the composed node, so a running bridge is rebuilt.
                await StopCoreAsync();
                await StartCoreAsync(cancellationToken);
            }, cancellationToken);
        }

        /// <inheritdoc />
        public Task RefreshDevicesAsync(CancellationToken cancellationToken = default) =>
            WithGateAsync(() => AddDevicesAsync(cancellationToken), cancellationToken);

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
        public async Task RunStoppedAsync(Func<Task> action, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(action);

            await WithGateAsync(async () =>
            {
                await StopCoreAsync();
                try
                {
                    await action();
                }
                finally
                {
                    // The action may have replaced both documents and the fabric file.
                    _configuration = _store.LoadConfiguration();
                    _state = _store.LoadState();
                    await StartCoreAsync(CancellationToken.None);
                }
            }, cancellationToken);
        }

        /// <inheritdoc />
        public Task ResetAsync(CancellationToken cancellationToken = default) =>
            WithGateAsync(async () =>
            {
                await StopCoreAsync();

                // Drop both halves of the identity: the fabrics a controller commissioned, and the
                // provisioning bundle behind the onboarding codes. The endpoint map goes with them, since
                // a fresh pairing has no device references to keep stable.
                _credentials.DeleteFabrics(_configuration);
                _store.DeleteState();
                _state = new MatterBridgeState();

                await StartCoreAsync(cancellationToken);
            }, cancellationToken);

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

            status.Endpoints.AddRange(_endpoints.GetStatus());
            return status;
        }

        /// <inheritdoc />
        /// <remarks>
        /// Called from the MQTT consumer for every report the orchestrator handles, so it never throws
        /// and does not block.
        /// </remarks>
        public void OnReport(Report report) => _endpoints.RouteReport(report);

        public Task OnConfigurationChangedAsync() => RefreshDevicesAsync();

        public void OnNodeOnlineChanged(string nodeId, bool isOnline) => _endpoints.SetReachable(nodeId, isOnline);

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            // The container disposes this instance once per registration (IMatterBridgeService and
            // IMatterReportSink), so a second call must do nothing.
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

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

        /// <summary>Reconciles the bridged endpoints of <paramref name="aggregator"/> with the configuration.</summary>
        internal Task ReconcileDevicesAsync(RIoT2.Matter.ControlBridge.AggregatorEndpoint aggregator, CancellationToken cancellationToken = default) =>
            _endpoints.ReconcileAsync(aggregator, _state, cancellationToken);

        private async Task WithGateAsync(Func<Task> action, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                await action();
            }
            finally
            {
                _gate.Release();
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
                var credentialsDirectory = _credentials.Prepare(_configuration);

                if (MatterProvisioning.Resolve(_state, _logger, out var provisioning))
                    _store.SaveState(_state);

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
                    NetworkInterfaces = MatterHostNetwork.EnumerateInterfaces(_logger),
                    Discriminator = _configuration.Discriminator,
                    NodeLabel = _configuration.NodeLabel,
                    ControlEndpoint = ControlEndpoint,
                    AggregatorEndpoint = AggregatorEndpoint,
                    CommissioningWindowSeconds = CommissioningWindowSeconds,
                    Provisioning = provisioning
                };

                var bridge = ControlBridgeService.Create(settings);

                // Attached before the host starts, so restored fabrics are in place when the host decides
                // whether to open a commissioning window for an uncommissioned node.
                _fabricPersistence = FileFabricPersistence.Attach(
                    bridge.Device.Commissioning.Manager,
                    _credentials.FabricFile(_configuration),
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
            await _endpoints.RemoveAllAsync(_bridge?.Aggregator, CancellationToken.None);

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

        private Task AddDevicesAsync(CancellationToken cancellationToken) =>
            _bridge?.Aggregator is { } aggregator ? ReconcileDevicesAsync(aggregator, cancellationToken) : Task.CompletedTask;

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
    }
}
