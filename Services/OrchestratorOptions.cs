using RIoT2.Core.Configuration;
using RIoT2.Net.Orchestrator.Services.Workflows;

namespace RIoT2.Net.Orchestrator.Services
{
    /// <summary>
    /// The orchestrator's settings. Every <c>RIOT2_*</c> name it reads is mapped here (plan M4).
    /// </summary>
    public sealed class OrchestratorOptions
    {
        public const string IdVariable = "RIOT2_ORCHESTRATOR_ID";
        public const string UrlVariable = "RIOT2_ORCHESTRATOR_URL";
        public const string WorkflowTriggerTtlVariable = "RIOT2_OUTBOX_WORKFLOW_TTL";
        public const string CommandTtlVariable = "RIOT2_OUTBOX_COMMAND_TTL";
        public const string BackupScheduleVariable = "RIOT2_BACKUP_SCHEDULE";
        public const string BackupRetentionVariable = "RIOT2_BACKUP_RETENTION";
        public const string WorkflowModeVariable = "RIOT2_WORKFLOW_MODE";

        /// <summary>
        /// Where workflows run (ADR 0016): <see cref="WorkflowModes.Embedded"/> in this process, or
        /// <see cref="WorkflowModes.Remote"/> on a workflow host (the riot2-elsa container). <c>RIOT2_WORKFLOW_MODE</c>;
        /// default <c>embedded</c>.
        /// </summary>
        public string WorkflowMode { get; set; } = WorkflowModes.Embedded;

        /// <summary>
        /// In the embedded mode, where <c>RIoT Schedule</c> calculates sunrise and sunset: <c>RIOT2_LATITUDE</c> and
        /// <c>RIOT2_LONGITUDE</c>, both or neither, as the workflow host reads them. <c>null</c>: sun-based schedules don't fire.
        /// </summary>
        public RIoT2.Elsa.Workflows.Automation.GeoLocation? Location { get; set; }

        /// <summary>Unique id; also the MQTT client id.</summary>
        public string Id { get; set; }

        /// <summary>The URL nodes use to reach this orchestrator; sent as <c>ConfigurationCommand.apiBaseUrl</c>.</summary>
        public string Url { get; set; }

        public MqttOptions Mqtt { get; set; } = new();

        /// <summary><c>RIOT2_LOG_FORMAT</c> and <c>RIOT2_LOG_LEVEL</c>, applied by <c>AddRIoT2Logging()</c> (design 7.4).</summary>
        public LoggingOptions Logging { get; set; } = new();

        /// <summary><c>OTEL_EXPORTER_OTLP_ENDPOINT</c> and <c>RIOT2_TRACING</c>, applied by <c>AddRIoT2Telemetry()</c> (design 7.4).</summary>
        public TelemetryOptions Telemetry { get; set; } = new();

        /// <summary>
        /// <c>RIOT2_SECURITY_MODE</c>, <c>RIOT2_SECURITY_FEATURES</c>, <c>RIOT2_API_KEY</c> and
        /// <c>RIOT2_CORS_ORIGINS</c> (design 7.5); this version accepts the mode <c>off</c> only.
        /// </summary>
        public SecurityOptions Security { get; set; } = new();

        /// <summary>
        /// How long a workflow trigger waits in the outbox for delivery before it expires (design 7.1).
        /// <c>RIOT2_OUTBOX_WORKFLOW_TTL</c> in seconds, 1–86400; default 300.
        /// </summary>
        public TimeSpan WorkflowTriggerTtl { get; set; } = TimeSpan.FromSeconds(300);

        /// <summary>
        /// How long a command waits in the outbox for its node before it expires, unless the request
        /// sets <c>ttlSeconds</c> (design 7.1). <c>RIOT2_OUTBOX_COMMAND_TTL</c> in seconds, 1–86400; default 30.
        /// </summary>
        public TimeSpan CommandTtl { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// When to write a configuration backup to <c>Backups/</c> (design 7.4): a Quartz cron expression
        /// with seconds, in the container's time zone. <c>RIOT2_BACKUP_SCHEDULE</c>; unset or empty turns
        /// scheduled backups off.
        /// </summary>
        public string? BackupSchedule { get; set; }

        /// <summary>
        /// How many backups <c>Backups/</c> keeps; the oldest are deleted. <c>RIOT2_BACKUP_RETENTION</c>,
        /// 1–365; default 7.
        /// </summary>
        public int BackupRetention { get; set; } = 7;

        /// <summary>
        /// Reads and validates the settings, and throws <see cref="ConfigurationValidationException"/>
        /// listing every problem (logged as critical) when any is missing or invalid.
        /// </summary>
        public static OrchestratorOptions Load(IConfiguration configuration, ILogger? logger = null)
        {
            var settings = new SettingsReader(configuration);
            var options = new OrchestratorOptions
            {
                Id = settings.Required(IdVariable),
                Url = settings.RequiredHttpUrl(UrlVariable),
                Mqtt = MqttOptions.Read(settings),
                Logging = LoggingOptions.Read(settings),
                Telemetry = TelemetryOptions.Read(settings),
                Security = SecurityOptions.Read(settings),
                WorkflowTriggerTtl = TimeSpan.FromSeconds(settings.Integer(WorkflowTriggerTtlVariable, 300, 1, 86400)),
                CommandTtl = TimeSpan.FromSeconds(settings.Integer(CommandTtlVariable, 30, 1, 86400)),
                BackupSchedule = settings.Optional(BackupScheduleVariable)?.Trim(),
                BackupRetention = settings.Integer(BackupRetentionVariable, 7, 1, 365),
                WorkflowMode = (settings.Optional(WorkflowModeVariable)?.Trim().ToLowerInvariant()) is { Length: > 0 } mode ? mode : WorkflowModes.Embedded,
                Location = RIoT2.Elsa.Workflows.Automation.GeoLocation.Read(name => settings.Optional(name), settings.AddError)
            };
            if (options.WorkflowMode is not (WorkflowModes.Embedded or WorkflowModes.Remote))
            {
                settings.AddError($"{WorkflowModeVariable} must be {WorkflowModes.Embedded} or {WorkflowModes.Remote}");
                options.WorkflowMode = WorkflowModes.Embedded;
            }
            if (!string.IsNullOrEmpty(options.BackupSchedule) && !Backup.CronSchedule.IsValid(options.BackupSchedule))
            {
                settings.AddError($"{BackupScheduleVariable} must be a Quartz cron expression with seconds, " +
                    "for example \"0 0 3 * * ?\" for 03:00 every day");
                options.BackupSchedule = null;
            }
            settings.ThrowIfInvalid("orchestrator", logger);
            return options;
        }
    }
}
