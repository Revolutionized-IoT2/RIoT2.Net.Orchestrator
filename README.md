# RIoT2.Net.Orchestrator

Central ASP.NET Core Web API for the RIoT2 IoT platform. It targets .NET 9, stores node/dashboard/variable configuration on disk, tracks online nodes, routes MQTT reports/commands, forwards accepted reports to Elsa 3 over gRPC, and can expose RIoT devices through the Matter Control Bridge.

## Runtime configuration

The orchestrator reads these environment variables at startup:

| Variable | Required | Meaning |
|---|---:|---|
| `RIOT2_ORCHESTRATOR_ID` | Yes | MQTT client id and the id used when publishing orchestrator-owned variable reports. |
| `RIOT2_ORCHESTRATOR_URL` | Yes | Base HTTP URL sent to nodes in `ConfigurationCommand.ApiBaseUrl`. |
| `RIOT2_MQTT_IP` | Yes | MQTT broker host/IP. Port is currently fixed by `RIoT2.Core.Utils.MqttClient` at `1883`. |
| `RIOT2_MQTT_USERNAME` | No | MQTT username. |
| `RIOT2_MQTT_PASSWORD` | No | MQTT password. |
| `ASPNETCORE_HTTP_PORTS` / `ASPNETCORE_URLS` | Hosting | ASP.NET Core listen configuration. The Dockerfile defaults to HTTP port `8080`. |
| `ASPNETCORE_ENVIRONMENT` | Hosting | `Development`, `Production`, etc. |
| `TZ` | Optional | Container timezone; the Dockerfile defaults to `Europe/Helsinki`. |

`RIOT2_USE_EXTERNAL_WORKFLOW_ENGINE` is obsolete and is not read. Elsa 3 is the only workflow engine.

Startup fails fast when `RIOT2_ORCHESTRATOR_ID`, `RIOT2_ORCHESTRATOR_URL`, or `RIOT2_MQTT_IP` is missing. `RIOT2_ORCHESTRATOR_URL` must be an absolute `http://` or `https://` URL. MQTT username/password can be empty when the broker allows anonymous clients.

## Storage

`FileObjectStore` persists JSON under `<content-root>\StoredObjects\<Type>\<Id>.json`.

- Writes use a complete same-directory temporary file and then replace the target file.
- Type names and object ids are validated as file names; path separators and traversal segments are rejected.
- `MatterConfiguration` and `MatterBridgeState` are stored through the same object-store abstraction.
- Matter generated credentials and the fabric store are written under `MatterConfiguration.CredentialsDirectory`, which must be relative to the content root (default `MatterCredentials`).
- Runtime folders such as `StoredObjects`, `Logs`, and `MatterCredentials` are excluded from Docker build context.

## Services

Registered as singletons in `Program.cs`:

| Interface | Implementation | Responsibility |
|---|---|---|
| `IOrchestratorConfigurationService` | `OrchestratorConfigurationService` | Loads env configuration and stored node/dashboard configuration; resolves report/command templates. |
| `IOnlineNodeService` | `OnlineNodeService` | Tracks online nodes and calls node REST APIs for device templates/status. |
| `IStoredObjectService` | `StoredObjectService` | Cached generic persistence with change events. |
| `IMessageStateService` | `MessageStateService` from `RIoT2.Core` | Current report/command state and report history. |
| `IOrchestratorMqttService` | `OrchestratorMqttService` | MQTT connection, routing, command/report publishing. |
| `IMatterBridgeService` / `IMatterReportSink` | `MatterBridgeService` | Matter Control Bridge and report mirroring. |

Hosted services:

- `MqttBackgroundService` starts/stops MQTT.
- `MatterBackgroundService` starts/stops the Matter bridge when enabled.

## REST API

Controllers are routed as `api/[controller]` and currently have no authentication. CORS allows any origin, method, and header.

### `api/Nodes`

- `GET api/Nodes` - configured nodes with online status and device status.
- `GET api/Nodes/online` - currently online nodes.
- `GET api/Nodes/{id}/configuration` - stored node configuration; `?state=true` overlays current command state.
- `POST api/Nodes/configuration` - save a node configuration JSON document.
- `GET api/Nodes/{id}/delete` - delete stored node configuration.
- `GET api/Nodes/{id}/devices/status` - ask an online node for device status.
- `GET api/Nodes/{id}/device/templates` - ask an online node for device configuration templates.
- `POST api/Nodes/checkplugin` - validate plugin URL reachability and return content metadata.
- `POST api/Nodes/validatecron` - validate and summarize a Quartz cron expression.
- `GET api/Nodes/report/{id}/state` - current report state, or online template fallback.
- `GET api/Nodes/command/{id}/state` - current command state.
- `POST api/Nodes/report/state` - set report states.
- `GET api/Nodes/report/templates` - report templates with node/device metadata.
- `GET api/Nodes/command/templates` - command templates with node/device metadata.
- `GET api/Nodes/variable/templates` - variable templates in the legacy nodes shape used by the UI.
- `GET api/Nodes/variables` - stored variables.
- `POST api/Nodes/variable/save` - create/update a variable.
- `GET api/Nodes/variable/{id}/delete` - delete a variable.

### `api/Report`, `api/Command`, `api/Variable`

- `GET api/Report/{id}/value` - current report or template default.
- `GET api/Report/templates` - report templates.
- `GET api/Command/{id}/value` - current command or template default.
- `GET api/Command/templates` - command templates.
- `POST api/Command/execute` - publish a command to the node owning the command id.
- `GET api/Variable/templates` - variable templates.
- `GET api/Variable/{id}/value` - variable DTO.

### `api/Dashboard`

- `GET api/Dashboard/configuration` - dashboard configuration; `?history=true` includes report history in elements.
- `POST api/Dashboard/configuration` - save dashboard configuration.
- `GET api/Dashboard/reports` - current report states.
- `GET api/Dashboard/report/{id}/history` - report history, or current value if no history is maintained.
- `GET api/Dashboard/reports/history/reset` - clear report state/history.

### `api/Matter`

- `GET api/Matter/status` - bridge running/error state, onboarding codes, commissioned fabrics, and endpoints.
- `GET api/Matter/configuration` - bridge configuration.
- `POST api/Matter/configuration` - save bridge configuration and restart the bridge if needed.
- `GET api/Matter/qr` - onboarding QR as PNG.
- `GET api/Matter/commissioning/open` - reopen commissioning window.
- `GET api/Matter/devices/refresh` - reconcile endpoints from stored node configuration.
- `GET api/Matter/reset` - delete commissioned fabrics/provisioning state and generate a new pairing identity.

### Health

- `GET /health` - ASP.NET Core health check endpoint. It reports unhealthy when the MQTT client is disconnected and healthy when the broker connection is active. The endpoint is anonymous and intended for Docker/Kubernetes health checks.

## MQTT

Topics are built with `RIoT2.Core.Constants.Get(id, MqttTopic.<Kind>)`.

### Subscriptions

| Purpose | Subscription | Payload | Handling |
|---|---|---|---|
| Reports | `riot2/node/+/report` (`Constants.Get("+", MqttTopic.Report)`) | `Report` | Ignore unknown ids; store state/history; mirror to Matter; enqueue Elsa gRPC delivery. |
| Node presence | `riot2/node/+/online` (`Constants.Get("+", MqttTopic.NodeOnline)`) | `NodeOnlineMessage` | Add/remove online node; update Matter reachability; send configuration command on online. |

### Publications

| Purpose | Topic | Payload | Trigger |
|---|---|---|---|
| Orchestrator online | `riot2/orchestrator/online` | `{"isOnline":true}` retained | Broker connection/reconnection. |
| Node configuration | `riot2/node/{nodeId}/configuration` | `ConfigurationCommand` with `ApiBaseUrl` | Node comes online or node configuration changes. |
| Device command | `riot2/node/{nodeId}/command` | `Command` | `api/Command/execute`, Elsa, dashboard, or Matter. |
| Orchestrator variable report | `riot2/node/{orchestratorId}/report` | `Report` | Variable create/update. |

`OrchestratorMqttService` uses a bounded MQTT work queue and a separate bounded workflow-delivery queue (1000 items each). Workflow gRPC calls use one reusable `WorkflowTriggerClient` channel, a five-second deadline, and no automatic retry.

## gRPC workflow contract

`Protos\riot_trigger.proto` matches the Elsa server proto:

- package `riot`
- service `RIoTTriggerService`
- rpc `Trigger(TriggerRequest) returns (TriggerResponse)`
- `TriggerRequest`: `id` (report id), `data` (JSON report payload)
- `TriggerResponse`: `success`

Workflow nodes advertise `GrpcBaseUrl`; if absent, the orchestrator falls back to `NodeBaseUrl`.

## Matter Control Bridge

The bridge is disabled by default. Enable it through the UI or by posting `{ "enabled": true }` to `api/Matter/configuration`.

Important settings:

- `VendorId` / `ProductId`: Matter identity; defaults are CSA test VID `0xFFF1` and PID `0x8000`.
- `Discriminator`: 12-bit setup discriminator.
- `AttestationPath`: optional operator-supplied DAC/PAI/CD/key directory.
- `CredentialsDirectory`: relative content-root directory for generated TEST credentials and `fabrics.json`.
- `FabricStoreKey`: generated once; changing it loses commissioned fabrics.

Matter commissioning needs IPv6, UDP 5540 and UDP 5353, and usually host networking in Docker because mDNS and link-local IPv6 do not work through bridge networking.

## Running locally

1. Set the environment variables in `Properties\launchSettings.json` or your shell.
2. Start the `RIoT2.Net.Orchestrator` launch profile or run:

```powershell
dotnet run --project .\RIoT2.Net.Orchestrator.csproj
```

## Upgrading / breaking changes

- Required env vars no longer have Dockerfile defaults. Set `RIOT2_ORCHESTRATOR_ID`, `RIOT2_ORCHESTRATOR_URL`, and `RIOT2_MQTT_IP` explicitly or the app exits during startup.
- The container now listens on `8080`, not `80`. Either map `-p 80:8080` or override `ASPNETCORE_HTTP_PORTS`.
- `RIOT2_ORCHESTRATOR_URL` examples should include the externally reachable port, for example `http://<host>:8080` when nodes reach the container on 8080.
- The runtime process runs as the non-root .NET app user (`APP_UID`, currently UID `1654`). Bind-mounted `StoredObjects`, `Logs`, and `MatterCredentials` directories must be writable by that UID, for example `sudo chown -R 1654:1654 <dir>`.
- With `--network host`, a non-root process cannot bind privileged ports below 1024. Keep port `8080` or grant/broker capabilities intentionally.

## Docker

The Dockerfile uses multi-stage .NET 9 images. The runtime stage runs as the non-root .NET `app` user and listens on port `8080`.

Build with a private-feed token:

```powershell
docker build `
  --build-arg NUGET_AUTH_TOKEN=<your-token> `
  --build-arg NUGET_URL=https://nuget.pkg.github.com/Revolutionized-IoT2/index.json `
  -t riot2-orchestrator .
```

Run with explicit configuration; no MQTT or orchestrator secrets are baked into the image:

```powershell
docker run --rm -p 8080:8080 `
  -e RIOT2_ORCHESTRATOR_ID=<guid-or-node-id> `
  -e RIOT2_ORCHESTRATOR_URL=http://<host>:8080 `
  -e RIOT2_MQTT_IP=<broker-host> `
  -e RIOT2_MQTT_USERNAME=<user> `
  -e RIOT2_MQTT_PASSWORD=<password> `
  riot2-orchestrator
```

For Matter, prefer host networking on Linux and mount persistent volumes for `StoredObjects`, `Logs`, and `MatterCredentials`.

Use `/health` as a health-check endpoint, for example Docker `HEALTHCHECK CMD wget -qO- http://127.0.0.1:8080/health || exit 1`.
