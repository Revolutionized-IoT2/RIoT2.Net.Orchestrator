using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace RIoT2.Net.Orchestrator.Services
{
    internal sealed class MqttHealthCheck : IHealthCheck
    {
        private readonly OrchestratorMqttService _mqtt;

        public MqttHealthCheck(OrchestratorMqttService mqtt)
        {
            ArgumentNullException.ThrowIfNull(mqtt);
            _mqtt = mqtt;
        }

        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_mqtt.IsConnected
                ? HealthCheckResult.Healthy("MQTT broker is connected.")
                : HealthCheckResult.Unhealthy("MQTT broker is disconnected."));
        }
    }
}
