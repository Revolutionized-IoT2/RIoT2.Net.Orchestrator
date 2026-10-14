using RIoT2.Core.Configuration;

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

        /// <summary>Unique id; also the MQTT client id.</summary>
        public string Id { get; set; }

        /// <summary>The URL nodes use to reach this orchestrator; sent as <c>ConfigurationCommand.apiBaseUrl</c>.</summary>
        public string Url { get; set; }

        public MqttOptions Mqtt { get; set; } = new();

        /// <summary>
        /// How long a workflow trigger waits in the outbox for delivery before it expires (design 7.1).
        /// <c>RIOT2_OUTBOX_WORKFLOW_TTL</c> in seconds, 1–86400; default 300.
        /// </summary>
        public TimeSpan WorkflowTriggerTtl { get; set; } = TimeSpan.FromSeconds(300);

        /// <summary>
        /// Reads and validates the settings, and throws <see cref="ConfigurationValidationException"/>
        /// listing every problem (logged as critical) when any is missing or invalid.
        /// </summary>
        public static OrchestratorOptions Load(IConfiguration configuration, ILogger logger = null)
        {
            var settings = new SettingsReader(configuration);
            var options = new OrchestratorOptions
            {
                Id = settings.Required(IdVariable),
                Url = settings.RequiredHttpUrl(UrlVariable),
                Mqtt = MqttOptions.Read(settings),
                WorkflowTriggerTtl = TimeSpan.FromSeconds(settings.Integer(WorkflowTriggerTtlVariable, 300, 1, 86400))
            };
            settings.ThrowIfInvalid("orchestrator", logger);
            return options;
        }
    }
}
