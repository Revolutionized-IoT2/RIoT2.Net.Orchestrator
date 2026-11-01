using Microsoft.Extensions.Options;

namespace RIoT2.Net.Orchestrator.Services.Backup
{
    /// <summary>Writes a backup at every <c>RIOT2_BACKUP_SCHEDULE</c> time (design 7.4).</summary>
    internal sealed class BackupScheduler : BackgroundService
    {
        // Task.Delay can't wait longer than about 49 days, so long waits are taken in steps.
        private static readonly TimeSpan MaxStep = TimeSpan.FromDays(1);

        private readonly BackupService _backups;
        private readonly OrchestratorOptions _options;
        private readonly TimeProvider _time;
        private readonly ILogger<BackupScheduler> _logger;

        public BackupScheduler(BackupService backups, IOptions<OrchestratorOptions> options, TimeProvider time, ILogger<BackupScheduler> logger)
        {
            _backups = backups;
            _options = options.Value;
            _time = time;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var schedule = _options.BackupSchedule;
            if (string.IsNullOrEmpty(schedule))
            {
                _logger.LogInformation("Scheduled backups are off; set {Variable} to turn them on", OrchestratorOptions.BackupScheduleVariable);
                return;
            }

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    var next = CronSchedule.Next(schedule, _time.GetUtcNow());
                    _backups.NextRunAt = next;
                    if (next == null)
                    {
                        _logger.LogWarning("The backup schedule {Schedule} has no future time; no more scheduled backups", schedule);
                        return;
                    }
                    _logger.LogInformation("Next scheduled backup at {Next:o}; {Retention} backups are kept", next, _options.BackupRetention);

                    for (var wait = next.Value - _time.GetUtcNow(); wait > TimeSpan.Zero; wait = next.Value - _time.GetUtcNow())
                        await Task.Delay(wait < MaxStep ? wait : MaxStep, _time, stoppingToken);

                    try
                    {
                        await _backups.WriteScheduledAsync(stoppingToken);
                    }
                    catch (Exception x) when (x is not OperationCanceledException)
                    {
                        _logger.LogError(x, "The scheduled backup failed");
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Stopping.
            }
        }
    }
}
