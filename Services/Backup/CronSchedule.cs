using Quartz;

namespace RIoT2.Net.Orchestrator.Services.Backup
{
    /// <summary>
    /// <c>RIOT2_BACKUP_SCHEDULE</c>: a Quartz cron expression with seconds (<c>0 0 3 * * ?</c> is 03:00
    /// every day), evaluated in the local time zone, which is the container's <c>TZ</c>.
    /// </summary>
    public static class CronSchedule
    {
        public static bool IsValid(string expression) =>
            !string.IsNullOrWhiteSpace(expression) && CronExpression.IsValidExpression(expression.Trim());

        /// <summary>The first time after <paramref name="after"/>, or <c>null</c> when it never fires again.</summary>
        public static DateTimeOffset? Next(string expression, DateTimeOffset after, TimeZoneInfo? timeZone = null)
        {
            var cron = new CronExpression(expression.Trim()) { TimeZone = timeZone ?? TimeZoneInfo.Local };
            return cron.GetNextValidTimeAfter(after);
        }
    }
}
