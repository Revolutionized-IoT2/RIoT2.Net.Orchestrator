using Microsoft.Extensions.Options;
using RIoT2.Core.Interfaces.Services;
using RIoT2.Core.Models;
using RIoT2.Core.Services;
using RIoT2.Net.Orchestrator.CustomJsonSettings;
using RIoT2.Net.Orchestrator.Services;
using RIoT2.Net.Orchestrator.Services.Automation;
using RIoT2.Net.Orchestrator.Services.Outbox;
using RIoT2.Net.Orchestrator.Services.Matter;
using RIoT2.Net.Orchestrator.Services.Persistence;
using System.Text.Json;
using Serilog;

// Workflow nodes (e.g. Elsa) are reached over plain HTTP; allow cleartext HTTP/2 so Grpc.Net.Client can call them.
AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

// The build-time OpenAPI generator (GetDocument.Insider) runs this entry point and starts the host
// without the runtime environment, so the background services and startup checks are skipped.
var generatingOpenApiDocument = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";

var builder = WebApplication.CreateBuilder(args);

var serilog = new LoggerConfiguration()
   .WriteTo.Console()
   .WriteTo.File("Logs/RIoT2.log", rollingInterval: RollingInterval.Day, outputTemplate: "{Timestamp:dd.MM.yyyy HH:mm:ss.fff} [{Level}] {Message:lj}{NewLine}{Exception}", shared: true)
   .CreateLogger();

ILoggerFactory logger = LoggerFactory.Create(log =>
{
    log.AddSerilog(serilog);
});

Microsoft.Extensions.Logging.ILogger orchestratorLogger = logger.CreateLogger("RIoT2.Net.Orchestrator");

// Every RIOT2_* setting is read and validated here, before anything uses it; an invalid setting
// stops the start with one message that lists every problem.
var orchestratorOptions = generatingOpenApiDocument
    ? new OrchestratorOptions()
    : OrchestratorOptions.Load(builder.Configuration, orchestratorLogger);
builder.Services.AddSingleton(Options.Create(orchestratorOptions));

// Add services to the container.
builder.Services.AddControllers()
    .AddJsonOptions(o => OrchestratorJsonOptions.Configure(o.JsonSerializerOptions, JsonNamingPolicy.CamelCase))
    //header json-naming-policy: pascal, This uses "original formatting"
    .AddJsonOptions("pascal", o => OrchestratorJsonOptions.Configure(o.JsonSerializerOptions, null))
    //header json-naming-policy: lower
    .AddJsonOptions("lower", o => OrchestratorJsonOptions.Configure(o.JsonSerializerOptions, new LowerCaseNamingPolicy()));

// The OpenAPI document is generated at build time into openapi/ (the REST route contract, plan M7
// step 2); it isn't served at runtime.
builder.Services.AddOpenApi();

builder.Services.AddSingleton<Microsoft.Extensions.Logging.ILogger>(orchestratorLogger);
builder.Services.AddSingleton<IObjectStore, FileObjectStore>();
builder.Services.AddSingleton<IOrchestratorConfigurationService, OrchestratorConfigurationService>();
builder.Services.AddSingleton<IOnlineNodeService, OnlineNodeService>();
builder.Services.AddSingleton<IStoredObjectService, StoredObjectService>();
builder.Services.AddSingleton<IMessageStateService, MessageStateService>();
builder.Services.AddSingleton(TimeProvider.System);
// Durable delivery (design 7.1): workflow triggers wait in StoredObjects/outbox.db until Elsa takes them.
builder.Services.AddSingleton(s => OutboxStore.Open(s.GetRequiredService<IWebHostEnvironment>()));
builder.Services.AddSingleton<ElsaAutomationProvider>();
builder.Services.AddSingleton<IAutomationProvider>(s => s.GetRequiredService<ElsaAutomationProvider>());
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

if (!generatingOpenApiDocument)
{
    //Start Mqtt Background service...
    builder.Services.AddHostedService<MqttBackgroundService>();

    // Delivers the outbox's workflow triggers. Registered after MQTT, so it stops first, and the reports
    // MQTT still drains while stopping are stored for the next start.
    builder.Services.AddHostedService(s => s.GetRequiredService<ElsaAutomationProvider>());

    //Start the Matter bridge with the application (it does nothing when disabled in its configuration)...
    builder.Services.AddHostedService<MatterBackgroundService>();
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
    var orchestratorConfiguration = app.Services.GetRequiredService<IOrchestratorConfigurationService>().OrchestratorConfiguration;
    orchestratorLogger.LogInformation($"Services initialized. {orchestratorConfiguration.Manifest?.Name} - {orchestratorConfiguration.Manifest?.Version}");

    IHostApplicationLifetime lifetime = app.Lifetime;
    lifetime.ApplicationStarted.Register(() =>
    {
        foreach (var variable in app.Services.GetService<IStoredObjectService>().GetAll<Variable>())
            app.Services.GetService<IMessageStateService>().SetState(variable.CreateReport());
    });
}

// Configure the HTTP request pipeline.

//app.UseHttpsRedirection();

app.UseAuthorization();

app.UseCors();

app.MapHealthChecks("/health").AllowAnonymous();
app.MapControllers();

app.Run();