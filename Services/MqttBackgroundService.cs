using RIoT2.Core.Interfaces.Services;

namespace RIoT2.Net.Orchestrator.Services
{
    internal class MqttBackgroundService : IHostedService, IDisposable
    {
        private IOrchestratorMqttService _mqttService;
        public MqttBackgroundService(IOrchestratorMqttService mqttService)
        {
            _mqttService = mqttService;
        }

        public void Dispose()
        {
            _mqttService.Dispose();
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await _mqttService.Start();
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await _mqttService.Stop();
        }
    }
}
