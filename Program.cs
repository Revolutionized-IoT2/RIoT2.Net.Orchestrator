using Microsoft.Extensions.Options;
using RIoT2.Core.Models;
using RIoT2.Net.Orchestrator.Controllers;
using RIoT2.Net.Orchestrator.CustomJsonSettings;
using RIoT2.Net.Orchestrator.Services;
using RIoT2.Net.Orchestrator.Services.Automation;
using RIoT2.Net.Orchestrator.Services.Backup;
using RIoT2.Net.Orchestrator.Services.Commands;
using RIoT2.Net.Orchestrator.Services.Outbox;
using RIoT2.Net.Orchestrator.Services.Matter;
using RIoT2.Net.Orchestrator.Services.Persistence;
using RIoT2.Net.Orchestrator.Services.Templates;
using RIoT2.Core.Diagnostics;
using RIoT2.Core.Hosting;
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
if (!generatingOpenApiDocument)
    builder.AddRIoT2Telemetry("orchestrator", builder.Configuration[OrchestratorOptions.IdVariable], MeterNames.Orchestrator);

// Every RIOT2_* setting is read and validated here, before anything uses it; an invalid setting
// stops the start with one message that lists every problem.
var orchestratorOptions = generatingOpenApiDocument
    ? new OrchestratorOptions()
    : OrchestratorOptions.Load(builder.Configuration, orchestratorLogger);
builder.Services.AddSingleton(Options.Create(orchestratorOptions));

// Add services to the container.
// Nullable annotations must not change request validation (plan M8 step 9).
builder.Services.AddControllers(o => o.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true)
    .AddJsonOptions(o => OrchestratorJsonOptions.Configure(o.JsonSerializerOptions, JsonNamingPolicy.CamelCase))
    // Deprecated (NamingPolicyHeaderDeprecation): the json-naming-policy header's "pascal" (the C# names)
    // and "lower" variants.
    .AddJsonOptions("pascal", o => OrchestratorJsonOptions.Configure(o.JsonSerializerOptions, null))
    .AddJsonOptions("lower", o => OrchestratorJsonOptions.Configure(o.JsonSerializerOptions, new LowerCaseNamingPolicy()));

// The OpenAPI document is generated at build time into openapi/ (the REST route contract, plan M7
// step 2); it isn't served at runtime.
builder.Services.AddOpenApi(o => o.AddOperationTransformer(BinaryRequestBodyAttribute.Apply));

builder.Services.AddSingleton<Microsoft.Extensions.Logging.ILogger>(orchestratorLogger);
builder.Services.AddSingleton<FileObjectStore>();
builder.Services.AddSingleton<IObjectStore>(s => s.GetRequiredService<FileObjectStore>());
builder.Services.AddSingleton<IObjectStoreArchive>(s => s.GetRequiredService<FileObjectStore>());
builder.Services.AddSingleton<OrchestratorConfigurationService>();
builder.Services.AddSingleton<IOrchestratorConfigurationService>(s => s.GetRequiredService<OrchestratorConfigurationService>());
builder.Services.AddSingleton<IOnlineNodeService, OnlineNodeService>();
builder.Services.AddSingleton<StoredObjectService>();
builder.Services.AddSingleton<IStoredObjectStore>(s => s.GetRequiredService<StoredObjectService>());
builder.Services.AddSingleton<IMessageStateService, MessageStateService>();
builder.Services.AddSingleton<ITemplateCatalog, TemplateCatalog>();
builder.Services.AddSingleton(TimeProvider.System);
// Durable delivery (design 7.1): workflow triggers wait in StoredObjects/outbox.db until Elsa takes them.
builder.Services.AddSingleton(s => OutboxStore.Open(s.GetRequiredService<IWebHostEnvironment>()));
builder.Services.AddSingleton<ElsaAutomationProvider>();
builder.Services.AddSingleton<IAutomationProvider>(s => s.GetRequiredService<ElsaAutomationProvider>());
// Commands go through the same outbox, one dispatcher per node, and nodes answer with results.
builder.Services.AddSingleton<CommandOutbox>();
builder.Services.AddSingleton<ICommandOutbox>(s => s.GetRequiredService<CommandOutbox>());
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
    builder.Services.AddHostedService(s => s.GetRequiredService<ElsaAutomationProvider>());

    // Delivers the outbox's commands; also registered after MQTT, so it stops before MQTT does.
    builder.Services.AddHostedService(s => s.GetRequiredService<CommandOutbox>());

    //Start the Matter bridge with the application (it does nothing when disabled in its configuration)...
    builder.Services.AddHostedService<MatterBackgroundService>();

    // Writes a backup at every RIOT2_BACKUP_SCHEDULE time; does nothing when it isn't set.
    builder.Services.AddHostedService<BackupScheduler>();
}

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(
        policy =>
        {
            policy.AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader();
        });
});

var app = builder.Build();

if (!generatingOpenApiDocument)
{
    // The rule engine's files, which this version doesn't read, go to StoredObjects/.archive/.
    LegacyStoredObjects.Archive(app.Services.GetRequiredService<IObjectStoreArchive>(), orchestratorLogger);

    var orchestratorConfiguration = app.Services.GetRequiredService<IOrchestratorConfigurationService>().OrchestratorConfiguration;
    orchestratorLogger.LogInformation($"Services initialized. {orchestratorConfiguration.Manifest?.Name} - {orchestratorConfiguration.Manifest?.Version}");

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

app.UseAuthorization();

app.UseCors();

app.UseMiddleware<NamingPolicyHeaderDeprecation>();

app.MapHealthChecks("/health").AllowAnonymous();
app.MapControllers();

app.Run();