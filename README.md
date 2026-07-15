# RIoT2.Net.Orchestrator

The central orchestrator for the **RIoT2** IoT platform. It is an ASP.NET Core Web API (targeting **.NET 9**) that manages IoT nodes and devices, processes rules, maintains message/variable state, and communicates with nodes over MQTT.

## Overview

The orchestrator is responsible for:

- **Node management** – tracking online/offline nodes and pushing configuration to them.
- **Rule processing** – evaluating rules against incoming device reports and producing commands (internal `IRuleProcessorService`, or an external workflow engine when `UseExtWorkflowEngine` is enabled).
- **State management** – maintaining the current state of reports, commands, and variables.
- **MQTT messaging** – bidirectional communication with nodes/devices (see [MQTT](#mqtt)).
- **Dashboard configuration** – serving dashboard layout/config to clients.

Shared interfaces and models (`RIoT2.Core`) are consumed from a private GitHub NuGet feed.

## Architecture

The app uses the ASP.NET Core minimal hosting model (`Program.cs`):

- **Logging** – Serilog, writing to console and a rolling file at `Logs/RIoT2.log`.
- **JSON** – controllers use custom named JSON option profiles selectable via the `json-naming-policy` request header:
  - default ? camelCase
  - `pascal` ? original/PascalCase
  - `lower` ? lowercase
- **Background service** – `MqttBackgroundService` (`IHostedService`) starts/stops the MQTT service with the app lifetime.
- **CORS** – a permissive default policy (`AllowAnyOrigin/Method/Header`).

### Core services (registered as singletons)

| Interface | Implementation | Responsibility |
|---|---|---|
| `IOrchestratorConfigurationService` | `OrchestratorConfigurationService` | Loads orchestrator + node configuration, manifest, dashboard, and resolves report/command templates. |
| `IOnlineNodeService` | `OnlineNodeService` | Tracks currently online nodes. |
| `IRuleProcessorService` | `RuleProcessorService` | Evaluates rules and produces command outputs. |
| `IStoredObjectService` | `StoredObjectService` | Generic persistence for stored objects (e.g. `Rule`, `Variable`). |
| `IFunctionService` | `FunctionService` | Function execution used by rules. |
| `IMessageStateService` | `MessageStateService` | Maintains current report/command/variable state. |
| `IOrchestratorMqttService` | `OrchestratorMqttService` | MQTT broker communication. |

## Configuration

MQTT broker settings come from `OrchestratorConfiguration.Mqtt` (`ServerUrl`, `ClientId`, `Username`, `Password`). The orchestrator's own identity/URL come from `OrchestratorConfiguration` (`Id`, `Url`).

## API Endpoints

Controllers are routed under `api/[controller]`.

### `api/Nodes`
- `GET api/Nodes` – list configured nodes and their status.

### `api/Rules`
- `GET api/Rules` – list rules (id, name, description, active state, tags).
- `GET api/Rules/tags` – list distinct rule tags.
- `POST api/Rules/save` – create/update a rule.

### `api/Variable`
- `GET api/Variable/templates` – list variable templates.

### `api/Report`
- `GET api/Report/{id}/value` – current or default value of a report/variable/command.

### `api/Command`
- Command APIs (send/read command values).

### `api/Dashboard`
- `GET api/Dashboard/configuration` – dashboard configuration (optional `?history=true`).

## MQTT

All device communication flows through an MQTT broker. The orchestrator's MQTT logic lives in `Services/OrchestratorMqttService.cs`. Topics are built with `Constants.Get(id, MqttTopic.<Kind>)` from `RIoT2.Core`; payloads are JSON-serialized models from `RIoT2.Core.Models`.

### Subscriptions (Node ? Orchestrator)

On `Start()` the orchestrator subscribes to wildcard topics for all nodes:

| Purpose | Topic key | Subscription | Payload | Handling |
|---|---|---|---|---|
| Device reports | `MqttTopic.Report` | `Constants.Get("+", MqttTopic.Report)` | `Report` | Stored via `IMessageStateService`; routed to the external workflow engine (if `UseExtWorkflowEngine`) or the internal `IRuleProcessorService`. Reports without a matching template are ignored. |
| Node online/offline | `MqttTopic.NodeOnline` | `Constants.Get("+", MqttTopic.NodeOnline)` | `NodeOnlineMessage` | Node added to / removed from `IOnlineNodeService`; a configuration command is sent when a node comes online. |

### Publications (Orchestrator ? Node)

| Purpose | Topic | Payload | Trigger |
|---|---|---|---|
| Orchestrator online | `Constants.Get("", MqttTopic.OrchestratorOnline)` | none (empty, **retained**) | Sent on `Start()` so nodes discover the orchestrator on (re)connect. |
| Configuration command | `Constants.Get(nodeId, MqttTopic.Configuration)` | `ConfigurationCommand` (`ApiBaseUrl`) | Node comes online, or a `NodeDeviceConfiguration` is updated. |
| Device command | `Constants.Get(nodeId, MqttTopic.Command)` | `Command` (`Id`, `Value`) | Produced by rule outputs; node id resolved via `FindNodeId`. |
| Orchestrator report | `Constants.Get(OrchestratorConfiguration.Id, MqttTopic.Report)` | `Report` | Published when a `Variable` changes. |

> Note: rule outputs with `OutputOperation.Variable` are not published to MQTT — they update a stored `Variable` internally (which may in turn trigger a report publication). Commands are serialized with `Json.SerializeIgnoreNulls(...)`.

## Debugging / running locally

1. Update environment parameters in `Properties/launchSettings.json` for the `RIoT2.Net.Orchestrator` profile.
2. Start the `RIoT2.Net.Orchestrator` profile in debugging mode.

## Docker

A `Dockerfile` is provided (based on `mcr.microsoft.com/dotnet/aspnet:9.0-alpine`). Building requires build args to restore from the private GitHub NuGet feed:

docker build \
  --build-arg NUGET_AUTH_TOKEN=<your-token> \
  --build-arg NUGET_URL=https://nuget.pkg.github.com/Revolutionized-IoT2/index.json \
  -t riot2-orchestrator .

The container listens on port `80` (`ASPNETCORE_HTTP_PORTS=80`).

This revised README maintains the original structure while enhancing clarity and coherence, ensuring that all relevant information is presented in a logical flow.
