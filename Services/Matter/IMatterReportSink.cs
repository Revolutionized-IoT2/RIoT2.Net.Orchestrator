using RIoT2.Core.Models;

namespace RIoT2.Net.Orchestrator.Services.Matter
{
    /// <summary>
    /// The narrow seam <see cref="OrchestratorMqttService"/> hands device reports to the Matter bridge
    /// through.
    /// </summary>
    /// <remarks>
    /// Kept separate from <see cref="IMatterBridgeService"/> to break a singleton construction cycle: the
    /// MQTT service depends on this sink, while the bridge needs the MQTT service to send commands and so
    /// resolves it lazily. Both interfaces are served by the same instance.
    /// </remarks>
    public interface IMatterReportSink
    {
        /// <summary>Applies a device report to every bridged endpoint bound to it. Never throws.</summary>
        void OnReport(Report report);
    }
}
