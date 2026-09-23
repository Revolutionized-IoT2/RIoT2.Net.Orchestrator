using RIoT2.Net.Orchestrator.Models;

namespace RIoT2.Net.Orchestrator.Services.Matter
{
    /// <summary>
    /// Hosts the RIoT Control Bridge: one commissionable Matter node whose aggregator exposes every RIoT
    /// device that declared Matter endpoints, so a controller such as Google Home can see and drive them.
    /// </summary>
    public interface IMatterBridgeService : IMatterReportSink, IAsyncDisposable
    {
        /// <summary>Whether the bridge is currently running.</summary>
        bool IsRunning { get; }

        /// <summary>A copy of the current configuration; mutating it does not affect the running bridge.</summary>
        MatterConfiguration Configuration { get; }

        /// <summary>
        /// Starts the bridge and composes a bridged endpoint per declared Matter endpoint. Does nothing
        /// when the bridge is disabled or already running.
        /// </summary>
        Task StartAsync(CancellationToken cancellationToken = default);

        /// <summary>Stops the bridge, leaving the commissioned fabrics and pinned endpoint ids persisted.</summary>
        Task StopAsync(CancellationToken cancellationToken = default);

        /// <summary>Returns the bridge's state for the UI, including the onboarding codes.</summary>
        MatterStatus GetStatus();

        /// <summary>
        /// Saves the configuration and restarts the bridge if that changed anything the running node has
        /// baked in (identity, discriminator, attestation or the enabled flag).
        /// </summary>
        Task SaveConfigurationAsync(MatterConfiguration configuration, CancellationToken cancellationToken = default);

        /// <summary>Re-reads the node configurations and adds, removes or refreshes bridged endpoints to match.</summary>
        Task RefreshDevicesAsync(CancellationToken cancellationToken = default);

        /// <summary>Re-opens the basic commissioning window so the bridge can be added to another fabric.</summary>
        void OpenCommissioningWindow();

        /// <summary>
        /// Decommissions the bridge: stops it, drops every fabric and the provisioning bundle, and starts
        /// it again with fresh onboarding codes.
        /// </summary>
        Task ResetAsync(CancellationToken cancellationToken = default);
    }
}
