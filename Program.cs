using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.DataProtection;
using Elsa.Extensions;
using RIoT2.Core.Models;
using RIoT2.Elsa.Workflows;
using RIoT2.Elsa.Workflows.Llm;
using RIoT2.Elsa.Workflows.Services;
using RIoT2.Net.Orchestrator.Services.Llm;
using RIoT2.Net.Orchestrator.Controllers;
using RIoT2.Net.Orchestrator.CustomJsonSettings;
using RIoT2.Net.Orchestrator.Services;
using RIoT2.Net.Orchestrator.Services.Automation;
using RIoT2.Net.Orchestrator.Services.Workflows;
using RIoT2.Net.Orchestrator.Services.Backup;
using RIoT2.Net.Orchestrator.Services.Commands;
using RIoT2.Net.Orchestrator.Services.Forecasts;
using RIoT2.Net.Orchestrator.Services.Outbox;
using RIoT2.Net.Orchestrator.Services.Matter;
using RIoT2.Net.Orchestrator.Services.Mcp;
using RIoT2.Net.Orchestrator.Services.Persistence;
using RIoT2.Net.Orchestrator.Services.Plans;
using RIoT2.Net.Orchestrator.Services.Templates;
using RIoT2.Core.Diagnostics;
using RIoT2.Core.Hosting;
using RIoT2.Core.Security;
using System.Text.Json;

// Workflow nodes (e.g. Elsa) are reached over plain HTTP; allow cleartext HTTP/2 so Grpc.Net.Client can call them.
AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

// The build-time OpenAPI generator (GetDocument.Insider) runs this entry point and starts the host
// without the runtime environment, so the background services and startup checks are skipped.
var generatingOpenApiDocument = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";

var builder = WebApplication.CreateBuilder(args);

// Console and Logs/RIoT2.log, text or JSON (RIOT2_LOG_FORMAT, RIOT2_LOG_LEVEL; design 7.4). Loggers
// from this factory forward to the host's once it exists, so they share providers and log scopes.
ILoggerFactory logger = builder.AddRIoT2Logging("orchestrator");

Microsoft.Extensions.Logging.ILogger orchestratorLogger = logger.CreateLogger("RIoT2.Net.Orchestrator");

// Metrics (and traces with RIOT2_TRACING=true) over OTLP, only when OTEL_EXPORTER_OTLP_ENDPOINT is set (design 7.4).
// The embedded workflow engine adds Elsa's meter (riot2.workflow.*).
if (!generatingOpenApiDocument)
    builder.AddRIoT2Telemetry("orchestrator", builder.Configuration[OrchestratorOptions.IdVariable],
        string.Equals(builder.Configuration[OrchestratorOptions.WorkflowModeVariable]?.Trim(), WorkflowModes.Remote, StringComparison.OrdinalIgnoreCase)
            ? [MeterNames.Orchestrator]
            : [MeterNames.Orchestrator, MeterNames.Elsa]);

// Every RIOT2_* setting is read and validated here, before anything uses it; an invalid setting
// stops the start with one message that lists every problem.
var orchestratorOptions = generatingOpenApiDocument
    ? new OrchestratorOptions()
    : OrchestratorOptions.Load(builder.Configuration, orchestratorLogger);
builder.Services.AddSingleton(Options.Create(orchestratorOptions));

// Add services to the container.
// Nullable annotations must not change request validation (plan M8 step 9).
builder.Services.AddControllers(o => o.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true)
    .AddJsonOptions(o => OrchestratorJsonOptions.Configure(o.JsonSerializerOptions, JsonNamingPolicy.CamelCase));

// The OpenAPI document is generated at build time into openapi/ (the REST route contract, plan M7
// step 2); it isn't served at runtime.
builder.Services.AddOpenApi(o => o.AddOperationTransformer(BinaryRequestBodyAttribute.Apply));

builder.Services.AddSingleton<Microsoft.Extensions.Logging.ILogger>(orchestratorLogger);
builder.Services.AddSingleton<FileObjectStore>();
builder.Services.AddSingleton<IObjectStore>(s => s.GetRequiredService<FileObjectStore>());
builder.Services.AddSingleton<IObjectStoreArchive>(s => s.GetRequiredService<FileObjectStore>());
builder.Services.AddSingleton<OrchestratorConfigurationService>();
builder.Services.AddSingleton<IOrchestratorConfigurationService>(s => s.GetRequiredService<OrchestratorConfigurationService>());
// The health page (I46): presence changes and device errors since the start, and the report.
builder.Services.AddSingleton<RIoT2.Net.Orchestrator.Services.Health.NodeHistory>();
builder.Services.AddSingleton<RIoT2.Net.Orchestrator.Services.Health.SystemHealthService>();
builder.Services.AddSingleton<IOnlineNodeService, OnlineNodeService>();
builder.Services.AddSingleton<StoredObjectService>();
builder.Services.AddSingleton<IStoredObjectStore>(s => s.GetRequiredService<StoredObjectService>());
builder.Services.AddSingleton<IMessageStateService, MessageStateService>();
builder.Services.AddSingleton<ITemplateCatalog, TemplateCatalog>();
builder.Services.AddSingleton(TimeProvider.System);
// Durable delivery (design 7.1): workflow triggers wait in StoredObjects/outbox.db until Elsa takes them.
builder.Services.AddSingleton(s => OutboxStore.Open(s.GetRequiredService<IWebHostEnvironment>()));
// Where workflows run (RIOT2_WORKFLOW_MODE, ADR 0016): in this process, with Elsa's database in
// StoredObjects/elsa.sqlite.db, or on the remote workflow host over gRPC and HTTP. The designer's API is the same.
var embeddedWorkflows = orchestratorOptions.WorkflowMode == WorkflowModes.Embedded;
if (embeddedWorkflows)
{
    if (!generatingOpenApiDocument)
    {
        builder.Services.AddRIoT2Workflows(new RIoT2WorkflowsOptions
        {
            ConnectionString = $"Data Source={Path.Combine(builder.Environment.ContentRootPath, "StoredObjects", "elsa.sqlite.db")};Cache=Shared;",
            Location = orchestratorOptions.Location
        });
    }
    builder.Services.AddSingleton<IRIoTDataService, OrchestratorRIoTDataService>();
    builder.Services.AddSingleton<IWorkflowEngine, EmbeddedWorkflowEngine>();
    builder.Services.AddSingleton<EmbeddedAutomationProvider>();
    builder.Services.AddSingleton<OutboxAutomationProvider>(s => s.GetRequiredService<EmbeddedAutomationProvider>());
}
else
{
    builder.Services.AddSingleton<IWorkflowEngine, RemoteWorkflowEngine>();
    builder.Services.AddSingleton<ElsaAutomationProvider>();
    builder.Services.AddSingleton<OutboxAutomationProvider>(s => s.GetRequiredService<ElsaAutomationProvider>());
}
builder.Services.AddSingleton<IAutomationProvider>(s => s.GetRequiredService<OutboxAutomationProvider>());
// Run LLM steps (ADR 0016): the connections (API keys encrypted with keys in StoredObjects/.keys, which backups leave
// out), the model adapters, the tools, the usage records (StoredObjects/llm-usage.db), and the runner both modes use.
builder.Services.AddDataProtection()
    .SetApplicationName("RIoT2.Orchestrator")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "StoredObjects", ".keys")));
builder.Services.AddSingleton<LlmConnectionService>();
builder.Services.AddSingleton<ILanguageModels, HttpLanguageModels>();
builder.Services.AddSingleton<LlmToolbox>();
builder.Services.AddSingleton(s => LlmUsageStore.Open(s.GetRequiredService<IWebHostEnvironment>(), s.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<LlmService>();
builder.Services.AddSingleton<ILlmRunner>(s => s.GetRequiredService<LlmService>());
// The MCP server (ADR 0018): /mcp on this port when RIOT2_MCP_ENABLED, and the remote listener on its own port with
// RIOT2_MCP_AUTH. The AI access page's services are always registered.
builder.AddRIoT2Mcp(orchestratorOptions.Mcp);
// Commands go through the same outbox, one dispatcher per node, and nodes answer with results.
builder.Services.AddSingleton<CommandOutbox>();
builder.Services.AddSingleton<ICommandOutbox>(s => s.GetRequiredService<CommandOutbox>());
// Forecasts and command plans (ADR 0019): the latest series of every forecast report (StoredObjects/forecasts.db), and the
// plans of commands' future values (StoredObjects/command-plans.db), sent at their times through the command outbox.
builder.Services.AddSingleton(s => ForecastStore.Open(s.GetRequiredService<IWebHostEnvironment>(), s.GetRequiredService<TimeProvider>(), s.GetRequiredService<ITemplateCatalog>()));
builder.Services.AddSingleton<IForecastStore>(s => s.GetRequiredService<ForecastStore>());
builder.Services.AddSingleton(s => CommandPlanStore.Open(s.GetRequiredService<IWebHostEnvironment>()));
builder.Services.AddSingleton<CommandPlanService>();
builder.Services.AddSingleton<ICommandPlanService>(s => s.GetRequiredService<CommandPlanService>());
builder.Services.AddSingleton<OrchestratorMqttService>();
builder.Services.AddSingleton<IOrchestratorMqttService>(s => s.GetRequiredService<OrchestratorMqttService>());
builder.Services.AddHealthChecks()
    .AddCheck<MqttHealthCheck>("mqtt")
    .AddCheck<OutboxHealthCheck>("outbox");

// Matter Control Bridge. One instance serves both interfaces: the bridge the UI drives, and the
// report sink OrchestratorMqttService pushes device reports into.
builder.Services.AddSingleton<MatterConfigurationStore>();
builder.Services.AddSingleton<IMatterBridgeService, MatterBridgeService>();
builder.Services.AddSingleton<IMatterReportSink>(s => s.GetRequiredService<IMatterBridgeService>());

// Configuration backup and restore (design 7.4); scheduled backups go to Backups/.
builder.Services.AddSingleton<BackupService>();

if (!generatingOpenApiDocument)
{
    //Start Mqtt Background service...
    builder.Services.AddHostedService<MqttBackgroundService>();

    // Delivers the outbox's workflow triggers. Registered after MQTT, so it stops first, and the reports
    // MQTT still drains while stopping are stored for the next start.
    builder.Services.AddHostedService(s => s.GetRequiredService<OutboxAutomationProvider>());

    // Delivers the outbox's commands; also registered after MQTT, so it stops before MQTT does.
    builder.Services.AddHostedService(s => s.GetRequiredService<CommandOutbox>());

    // Sends the command plans' steps at their times (ADR 0019); through the command outbox, so after it.
    builder.Services.AddHostedService(s => s.GetRequiredService<CommandPlanService>());

    //Start the Matter bridge with the application (it does nothing when disabled in its configuration)...
    builder.Services.AddHostedService<MatterBackgroundService>();

    // Writes a backup at every RIOT2_BACKUP_SCHEDULE time; does nothing when it isn't set.
    builder.Services.AddHostedService<BackupScheduler>();
}

// The optional security mode's seams (design 7.5, phase S0): every controller action has a Viewer,
// Operator or Admin policy, which the LocalTrust scheme passes while RIOT2_SECURITY_MODE is off (the
// only mode this version accepts); CORS allows any origin, and every outbound URL is allowed.
builder.Services.AddRIoT2Security(orchestratorOptions.Security);

// Calls to nodes send X-RIoT2-Key when RIOT2_API_KEY is set; every outbound client passes the policy.
builder.Services.AddHttpClient(OnlineNodeService.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(60))
    .AddHttpMessageHandler(() => new ApiKeyHandler(orchestratorOptions.Security.ApiKey))
    .AddHttpMessageHandler(services => new OutboundUrlPolicyHandler(services.GetRequiredService<IOutboundUrlPolicy>(), OutboundPurposes.Node));
builder.Services.AddHttpClient(PluginsController.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(60))
    .AddHttpMessageHandler(services => new OutboundUrlPolicyHandler(services.GetRequiredService<IOutboundUrlPolicy>(), OutboundPurposes.Plugin));
// The remote workflow host's management API (RIOT2_WORKFLOW_MODE=remote).
builder.Services.AddHttpClient(RemoteWorkflowEngine.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(60))
    .AddHttpMessageHandler(() => new ApiKeyHandler(orchestratorOptions.Security.ApiKey))
    .AddHttpMessageHandler(services => new OutboundUrlPolicyHandler(services.GetRequiredService<IOutboundUrlPolicy>(), OutboundPurposes.Workflow));
// Language model providers: never a redirect, never the RIoT2 API key; each Run LLM step bounds its calls.
builder.Services.AddHttpClient(HttpLanguageModels.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
    .AddHttpMessageHandler(services => new OutboundUrlPolicyHandler(services.GetRequiredService<IOutboundUrlPolicy>(), HttpLanguageModels.OutboundPurpose));

var app = builder.Build();

if (!generatingOpenApiDocument)
{
    // The rule engine's files, which this version doesn't read, go to StoredObjects/.archive/.
    LegacyStoredObjects.Archive(app.Services.GetRequiredService<IObjectStoreArchive>(), orchestratorLogger);

    var orchestratorConfiguration = app.Services.GetRequiredService<IOrchestratorConfigurationService>().OrchestratorConfiguration;
    orchestratorLogger.LogInformation($"Services initialized. {orchestratorConfiguration.Manifest?.Name} - {orchestratorConfiguration.Manifest?.Version}");
    var mcp = orchestratorOptions.Mcp;
    if (mcp.Enabled)
        orchestratorLogger.LogInformation("MCP server at {Route}; remote listener {Remote}", McpServerSetup.Route,
            mcp.RemoteEnabled ? $"on port {mcp.RemotePort} ({mcp.Auth}, {mcp.PublicUrl}, at most {mcp.RemoteMaxRole})" : "off");

    // The gauges read the outbox and the node lists until the host stops.
    OrchestratorMetrics.Observe(app.Services.GetRequiredService<OutboxStore>(), app.Services.GetRequiredService<IOnlineNodeService>(),
        app.Services.GetRequiredService<IOrchestratorConfigurationService>(), app.Services.GetRequiredService<TimeProvider>());
    app.Lifetime.ApplicationStopping.Register(() => OrchestratorMetrics.Observe(null, null, null, null));

    IHostApplicationLifetime lifetime = app.Lifetime;
    lifetime.ApplicationStarted.Register(() =>
    {
        foreach (var variable in app.Services.GetService<IStoredObjectStore>()!.GetAll<Variable>())
            app.Services.GetService<IMessageStateService>()!.SetState(variable.CreateReport());
    });
}

// Configure the HTTP request pipeline.

//app.UseHttpsRedirection();

// The remote MCP listener's port serves only /mcp and its metadata; this gate runs before routing.
app.UseRIoT2McpGate(orchestratorOptions.Mcp);

app.UseRIoT2Security();
if (orchestratorOptions.Mcp.RemoteEnabled)
    app.UseRateLimiter();

app.MapHealthChecks("/health").AllowAnonymous();
app.MapRIoT2SecurityInfo();
app.MapControllers();
app.MapRIoT2Mcp(orchestratorOptions.Mcp);
// The embedded engine's HTTP endpoint activities, below /workflows.
if (embeddedWorkflows && !generatingOpenApiDocument)
    app.UseWorkflows();

app.Run();