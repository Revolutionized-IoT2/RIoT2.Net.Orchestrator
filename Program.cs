using RIoT2.Common.Services;
using RIoT2.Core.Interfaces.Services;
using RIoT2.Core.Models;
using RIoT2.Core.Services;
using RIoT2.Net.Orchestrator.CustomJsonSettings;
using RIoT2.Net.Orchestrator.Services;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        o.JsonSerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
        o.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
        o.JsonSerializerOptions.WriteIndented = true;
        o.JsonSerializerOptions.Converters.Add(new JObjectConverter());
    })
    .AddJsonOptions("pascal", o => //header json-naming-policy: pascal, This uses "original formatting"
    {
        o.JsonSerializerOptions.PropertyNamingPolicy = null;
        o.JsonSerializerOptions.DictionaryKeyPolicy = null;
        o.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
        o.JsonSerializerOptions.WriteIndented = true;
        o.JsonSerializerOptions.Converters.Add(new JObjectConverter());
    })
    .AddJsonOptions("lower", o => //header json-naming-policy: lower
    {
        o.JsonSerializerOptions.PropertyNamingPolicy = new LowerCaseNamingPolicy();
        o.JsonSerializerOptions.DictionaryKeyPolicy = new LowerCaseNamingPolicy();
        o.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
        o.JsonSerializerOptions.WriteIndented = true;
        o.JsonSerializerOptions.Converters.Add(new JObjectConverter());
    });

//ConfigurationManager configuration = builder.Configuration;

builder.Services.AddSingleton<IOnlineNodeService, OnlineNodeService>();
builder.Services.AddSingleton<IOrchestratorConfigurationService, OrchestratorConfigurationService>();
builder.Services.AddSingleton<IRuleProcessorService, RuleProcessorService>();
builder.Services.AddSingleton<IStoredObjectService, StoredObjectService>();
builder.Services.AddSingleton<IFunctionService, FunctionService>();
builder.Services.AddSingleton<IMessageStateService, MessageStateService>();
builder.Services.AddSingleton<IOrchestratorMqttService, OrchestratorMqttService>();

//Start Mqtt Background service...
builder.Services.AddHostedService<MqttBackgroundService>();

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

IHostApplicationLifetime lifetime = app.Lifetime;
//lifetime.ApplicationStopping.Register(onShutdown);

//void onShutdown() //this code is called when the application stops
//{
//}

lifetime.ApplicationStarted.Register(() =>
{
    foreach (var variable in app.Services.GetService<IStoredObjectService>().GetAll<Variable>())
        app.Services.GetService<IMessageStateService>().SetState(variable.CreateReport());
});

// Configure the HTTP request pipeline.

//app.UseHttpsRedirection();

app.UseAuthorization();

app.UseCors();

app.MapControllers();

app.Run();