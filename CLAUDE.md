# CLAUDE.md

This file provides guidance to Claude Code (and other AI coding agents) when working with code in this repository.

## Project Overview

`RIoT2.Net.Orchestrator` is an ASP.NET Core Web API (targeting **.NET 9**) that acts as the central orchestrator for the RIoT2 IoT platform. It manages IoT nodes, forwards reports to Elsa 3, maintains message/variable state, and communicates with devices over MQTT.

## Build, Run & Debug

- **Restore & build:** `dotnet build`
- **Run locally:** `dotnet run` (or start the `RIoT2.Net.Orchestrator` profile in Visual Studio via debugging mode).
- **Configuration:** Update environment parameters in `Properties/launchSettings.json` for the `RIoT2.Net.Orchestrator` profile before running.
- **Docker:** A `Dockerfile` is provided (based on `mcr.microsoft.com/dotnet/aspnet:9.0-alpine`). It requires build args `NUGET_AUTH_TOKEN` and `NUGET_URL` to restore packages from the private GitHub NuGet feed (`https://nuget.pkg.github.com/Revolutionized-IoT2/index.json`). The runtime stage runs as the non-root .NET app user and listens on HTTP port `8080`.

## Architecture

### Entry Point
`Program.cs` uses the minimal hosting model (`WebApplication.CreateBuilder`). It:
- Configures **Serilog** logging (console + rolling file at `Logs/RIoT2.log`).
- Registers controllers with custom JSON options (see below).
- Registers services in the DI container (all as singletons).
- Starts a hosted background service for MQTT.
- On application start, seeds message state from stored `Variable` objects.

### Core Services (registered as singletons in `Program.cs`)
- `IOrchestratorConfigurationService` → `OrchestratorConfigurationService` — loads/holds orchestrator configuration and manifest.
- `IOnlineNodeService` → `OnlineNodeService` — tracks online IoT nodes.
- `IStoredObjectService` → `StoredObjectService` — generic persistence for stored objects (e.g. `Variable`).
- `IMessageStateService` → `MessageStateService` — maintains current message/report state.
- `IOrchestratorMqttService` → `OrchestratorMqttService` — MQTT client/broker communication.
- `IMatterBridgeService` → `MatterBridgeService` — hosts the Matter Control Bridge (`Services/Matter/`) and exposes Matter-capable RIoT devices as bridged endpoints. Also registered as `IMatterReportSink`, which `OrchestratorMqttService` takes as an optional dependency to mirror reports into the bridge (this breaks the singleton construction cycle; the bridge resolves `IOrchestratorMqttService` lazily for the command direction).
- `MqttBackgroundService` (`IHostedService`) — starts/stops the MQTT service with the app lifetime.
- `MatterBackgroundService` (`IHostedService`) — starts/stops the Matter bridge with the app lifetime; it is a no-op while the bridge is disabled in its configuration.

Service interfaces and shared models come from the external `RIoT2.Core` package.
Use Core `0.1.41` for reconnect presence, snapshot-safe state, the additive workflow `GrpcBaseUrl` announcement field and lossless
integer/text message decoding. Workflow delivery is isolated in a bounded in-memory queue with
a five-second deadline and no automatic retry; preserve that policy when changing report routing.

### Controllers (`Controllers/`)
REST API endpoints including `ReportController`, `CommandController`, `VariableController`, `NodesController`, `DashboardController`, `MatterController`.

Elsa 3 is the only workflow engine. Do not register an internal rule processor or restore the retired
rule/function APIs. The old external-engine environment switch is no longer read. Commands use
`POST api/Command/execute` and `IOrchestratorMqttService.ExecuteCommand(Command)`; the former
`api/Nodes/command/{type}` endpoint is retired.

### Custom JSON Settings (`CustomJsonSettings/`)
Named JSON option profiles selectable via the `json-naming-policy` request header:
- Default → camelCase
- `pascal` → original/PascalCase (null naming policy)
- `lower` → lowercase (`LowerCaseNamingPolicy`)

All profiles enable `WriteIndented` and register `JObjectConverter`. The `AddJsonOptionsExtension` provides the `AddJsonOptions(settingsName, configure)` builder extension.

## Conventions

- **DI lifetime:** Services are registered as **singletons**; follow this pattern unless there is a specific reason otherwise.
- **Namespaces:** Use `RIoT2.Net.Orchestrator.*` matching folder structure (`Services`, `Controllers`, `Models`, `CustomJsonSettings`).
- **Logging:** Use the injected `Microsoft.Extensions.Logging.ILogger` (backed by Serilog).
- **Argument validation:** Prefer `ArgumentNullException.ThrowIfNull(...)`.
- **Shared types:** Reuse interfaces/models from `RIoT2.Core` rather than redefining them.

## Notes

- CORS is configured with a permissive default policy (`AllowAnyOrigin/Method/Header`).
- HTTPS redirection is currently disabled in the request pipeline.
- No authentication/authorization scheme is registered; controllers are currently anonymous even though `UseAuthorization()` is in the pipeline.
- Runtime JSON storage lives under `StoredObjects`. `FileObjectStore` rejects path separators/traversal in logical type names and object ids.
- Matter credentials must stay under a content-root-relative `CredentialsDirectory` (default `MatterCredentials`); absolute or escaping paths are rejected.

## MQTT Message Transfers (Orchestrator ↔ Nodes)

All device communication flows through an MQTT broker. The orchestrator's MQTT logic lives in `Services/OrchestratorMqttService.cs`, hosted by `MqttBackgroundService`. The broker connection (`ServerUrl`, `ClientId`, `Username`, `Password`) comes from `OrchestratorConfiguration.Mqtt`.

Topics are built with `Constants.Get(id, MqttTopic.<Kind>)` from `RIoT2.Core`, where `id` is a client/node/orchestrator id (or `"+"` as an MQTT single-level wildcard). Payloads are JSON-serialized models from `RIoT2.Core.Models`.

### Subscriptions (Node → Orchestrator)

On `Start()`, the orchestrator subscribes to two wildcard topics to receive messages from all nodes:

| Purpose | Topic key | Payload model | Handling |
|---|---|---|---|---|
| Device reports | riot2/node/+/report | `Report` | Matched via `Report.Create(...)`. Ignored if no matching report template. State stored via `IMessageStateService.SetState`, mirrored to Matter, then routed to the Elsa workflow node over gRPC. |
| Node online/offline | riot2/node/+/online | `NodeOnlineMessage` | Node id extracted via `Constants.GetTopicId(topic, MqttTopic.NodeOnline)`. If `IsOnline`, node is added to `IOnlineNodeService` and a configuration command is sent back; otherwise the node is removed. |

Incoming topics are disambiguated with `MqttClient.IsMatch(topic, subscription)`.

### Publications (Orchestrator → Node)

| Purpose | Topic | Payload model | Trigger |
|---|---|---|---|
| Orchestrator online announcement | riot2/orchestrator/online | `{"isOnline":true}` (**retained**) | Sent on broker connection/reconnection so nodes can discover the orchestrator. |
| Configuration command | riot2/node/{nodeId}/configuration | `ConfigurationCommand` (contains `ApiBaseUrl`) | Sent when a node comes online, and when a `NodeDeviceConfiguration` is updated. |
| Device command | riot2/node/{nodeId}/command | `Command` (`Id`, `Value`) | Requested by Elsa, the dashboard, or Matter; node id resolved via `IOrchestratorConfigurationService.FindNodeId`. Command state is recorded via `IMessageStateService.SetState` after publishing. |
| Orchestrator report | riot2/node/{orchestratorId}/report | `Report` | Published when a `Variable` changes (via `Variable.CreateReport()`), so the orchestrator's own variables are visible as reports. |

### Message Content Notes

- Commands are serialized with `Json.SerializeIgnoreNulls(...)` (null properties omitted); reports use `Report.ToJson()`.
- Variable updates use the variable APIs and may trigger report publication.
- The `OrchestratorOnline` message is published **retained** so late-joining nodes still receive it.