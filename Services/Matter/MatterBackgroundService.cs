namespace RIoT2.Net.Orchestrator.Services.Matter
{
    /// <summary>
    /// Starts and stops the Matter bridge with the application lifetime, mirroring
    /// <see cref="MqttBackgroundService"/>.
    /// </summary>
    /// <remarks>
    /// A failure to start is swallowed by the bridge itself and surfaced through
    /// <see cref="IMatterBridgeService.GetStatus"/>, so a misconfigured bridge (missing attestation files,
    /// no IPv6 interface) never stops the orchestrator from serving the rest of the system.
    /// </remarks>
    internal class MatterBackgroundService : IHostedService
    {
        private readonly IMatterBridgeService _bridge;

        public MatterBackgroundService(IMatterBridgeService bridge)
        {
            ArgumentNullException.ThrowIfNull(bridge);
            _bridge = bridge;
        }

        public Task StartAsync(CancellationToken cancellationToken) => _bridge.StartAsync(cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken) => _bridge.StopAsync(cancellationToken);
    }
}
