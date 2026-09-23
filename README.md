# RIoT2.Net.Orchestrator

The central orchestrator for the **RIoT2** IoT platform. It is an ASP.NET Core Web API (targeting **.NET 9**) that manages IoT nodes and devices, forwards reports to Elsa 3 for automation, maintains message/variable state, and communicates with nodes over MQTT.

## Overview

The orchestrator is responsible for:

- **Node management** � tracking online/offline nodes and pushing configuration to them.
- **Automation** - forwarding incoming device reports to the online Elsa 3 workflow node over gRPC.
- **State management** � maintaining the current state of reports, commands, and variables.
- **MQTT messaging** � bidirectional communication with nodes/devices (see [MQTT](#mqtt)).
- **Dashboard configuration** � serving dashboard layout/config to clients.

Shared interfaces and models (`RIoT2.Core`) are consumed from a private GitHub NuGet feed.

## Architecture

The app uses the ASP.NET Core minimal hosting model (`Program.cs`):

- **Logging** � Serilog, writing to console and a rolling file at `Logs/RIoT2.log`.
- **JSON** � controllers use custom named JSON option profiles selectable via the `json-naming-policy` request header:
  - default ? camelCase
  - `pascal` ? original/PascalCase
  - `lower` ? lowercase
- **Background service** � `MqttBackgroundService` (`IHostedService`) starts/stops the MQTT service with the app lifetime.
- **CORS** � a permissive default policy (`AllowAnyOrigin/Method/Header`).

### Core services (registered as singletons)

| Interface | Implementation | Responsibility |
|---|---|---|
| `IOrchestratorConfigurationService` | `OrchestratorConfigurationService` | Loads orchestrator + node configuration, manifest, dashboard, and resolves report/command templates. |
| `IOnlineNodeService` | `OnlineNodeService` | Tracks currently online nodes. |
| `IStoredObjectService` | `StoredObjectService` | Generic persistence for stored objects (e.g. `Variable`). |
| `IMessageStateService` | `MessageStateService` | Maintains current report/command/variable state. |
| `IOrchestratorMqttService` | `OrchestratorMqttService` | MQTT broker communication. |
| `IMatterBridgeService` | `MatterBridgeService` | Hosts the Matter Control Bridge and exposes RIoT devices as bridged Matter endpoints (see [Matter Control Bridge](#matter-control-bridge)). |

## Configuration

MQTT broker settings come from `OrchestratorConfiguration.Mqtt` (`ServerUrl`, `ClientId`, `Username`, `Password`). The orchestrator's own identity/URL come from `OrchestratorConfiguration` (`Id`, `Url`).

### Retiring the internal workflow engine

Elsa 3 is now the only workflow engine. The `RIOT2_USE_EXTERNAL_WORKFLOW_ENGINE` environment
variable and `UseExtWorkflowEngine` configuration property are no longer used. Every accepted report
is stored and mirrored to Matter before being forwarded to the online workflow node. If no workflow
node is online, a warning is logged; there is no internal fallback or durable replay queue.

Workflow delivery has a separate, ordered, in-memory queue of at most 1000 pending reports, with
a five-second deadline per gRPC call. A stalled workflow does not block report state, Matter,
or node presence processing. Overflow, rejected requests, delivery failures, and pending deliveries
discarded at shutdown are logged explicitly. Delivery is not retried automatically because a
timeout does not prove that a workflow was not started. The MQTT state-processing queue instead
applies backpressure when full. Workflow nodes can advertise `GrpcBaseUrl`; older nodes fall back
to `NodeBaseUrl`. Elsa deployments must expose their dedicated HTTP/2 port.

The internal rule CRUD, simulation, validation, function-execution, and function-template APIs have
been removed. Use Elsa Studio to author workflows. Existing `StoredObjects/Rule` data is left
untouched but is no longer loaded or executed; archive it separately if needed.

Publish `RIoT2.Core` version `0.1.41` before building this orchestrator. Deploy the updated UI
together with the orchestrator: the old `POST api/Nodes/command/{type}` endpoint has been removed.
Device commands now use `POST api/Command/execute` with `{ "id": "...", "value": ... }`.
Missing or unknown command identifiers return HTTP 400 instead of silently succeeding.
Publish `RIoT2.Matter` and `RIoT2.Matter.ControlBridge` version `0.1.14` before the container build
as well; the bridge package must consume the matching Matter library.

### Persistence and variable updates

Object writes flush a complete temporary file before atomically replacing the previous JSON file.
A failed replacement preserves the previous durable value and does not emit a successful update.
Node configuration APIs propagate save failures instead of returning an unsaved node ID.
Variable report/command templates are read from current storage on each lookup, so creating,
updating, or deleting a variable no longer requires an orchestrator restart.

## API Endpoints

Controllers are routed under `api/[controller]`.

### `api/Nodes`
- `GET api/Nodes` � list configured nodes and their status.

### `api/Variable`
- `GET api/Variable/templates` � list variable templates.

### `api/Report`
- `GET api/Report/{id}/value` � current or default value of a report/variable/command.

### `api/Command`
- Command APIs (send/read command values).

### `api/Dashboard`
- `GET api/Dashboard/configuration` � dashboard configuration (optional `?history=true`).

### `api/Matter`
- `GET api/Matter/status` - bridge state: running, onboarding codes, commissioned fabrics, bridged endpoints.
- `GET api/Matter/configuration` / `POST api/Matter/configuration` - read/save the bridge configuration.
- `GET api/Matter/qr` - the onboarding payload rendered as a PNG QR code.
- `GET api/Matter/commissioning/open` - re-open the pairing window.
- `GET api/Matter/devices/refresh` - reconcile bridged endpoints with the current node configuration.

Node configuration creation, changes, and deletion also trigger reconciliation automatically.
Unchanged endpoints remain attached; changed declarations reuse their persisted endpoint IDs.
Node online/offline MQTT announcements update live endpoint reachability. The orchestrator republishes
nonempty retained presence after each broker connection, allowing nodes to rediscover it after reconnect.
- `GET api/Matter/reset` - decommission: drop every fabric and generate a new pairing code.

## MQTT

All device communication flows through an MQTT broker. The orchestrator's MQTT logic lives in `Services/OrchestratorMqttService.cs`. Topics are built with `Constants.Get(id, MqttTopic.<Kind>)` from `RIoT2.Core`; payloads are JSON-serialized models from `RIoT2.Core.Models`.

### Subscriptions (Node ? Orchestrator)

On `Start()` the orchestrator subscribes to wildcard topics for all nodes:

| Purpose | Topic key | Subscription | Payload | Handling |
|---|---|---|---|---|
| Device reports | `MqttTopic.Report` | `Constants.Get("+", MqttTopic.Report)` | `Report` | Stored via `IMessageStateService`, mirrored to Matter, then forwarded to the online Elsa 3 workflow node. Reports without a matching template are ignored. |
| Node online/offline | `MqttTopic.NodeOnline` | `Constants.Get("+", MqttTopic.NodeOnline)` | `NodeOnlineMessage` | Node added to / removed from `IOnlineNodeService`; a configuration command is sent when a node comes online. |

### Publications (Orchestrator ? Node)

| Purpose | Topic | Payload | Trigger |
|---|---|---|---|
| Orchestrator online | `Constants.Get("", MqttTopic.OrchestratorOnline)` | `{"isOnline":true}` (**retained**) | Sent after every broker connection so nodes discover the orchestrator on (re)connect. |
| Configuration command | `Constants.Get(nodeId, MqttTopic.Configuration)` | `ConfigurationCommand` (`ApiBaseUrl`) | Node comes online, or a `NodeDeviceConfiguration` is updated. |
| Device command | `Constants.Get(nodeId, MqttTopic.Command)` | `Command` (`Id`, `Value`) | Requested by Elsa, the dashboard, or Matter; node id resolved via `FindNodeId`. |
| Orchestrator report | `Constants.Get(OrchestratorConfiguration.Id, MqttTopic.Report)` | `Report` | Published when a `Variable` changes. |

> Variable updates use the variable APIs, which may trigger report publication. Commands are serialized with `Json.SerializeIgnoreNulls(...)`.

Workflow delivery owns one reusable gRPC channel, replacing it when the advertised destination
changes and disposing it on shutdown. Transport ownership is isolated in `WorkflowTriggerClient`;
the MQTT service still owns routing and its bounded workflow queue. The five-second deadline,
no-automatic-retry policy, and explicit overflow/shutdown logs are unchanged.

## Matter Control Bridge

### Upgrading persisted Matter fabrics

Matter `0.1.13` persists the current ACL alongside fabric credentials. Legacy snapshots without
ACL state are rejected rather than silently restoring the original commissioner's administrator
access. Back up the existing Matter credentials/state before upgrading. An affected bridge must
be reset and recommissioned using the existing Matter Reset action; do not treat a failed restore
as a successful fresh startup. Reset removes the old commissioned identity and pairing state.

The orchestrator can present itself to a Matter ecosystem (Google Home, Apple Home, Alexa) as a single
**Control Bridge**, with every Matter-capable RIoT device exposed as a bridged endpoint under its
aggregator. It is off by default; enable it in the RIoT UI's *Matter* view or by posting
`{ "enabled": true }` to `api/Matter/configuration`.

### How a device becomes a Matter endpoint

A node plugin implements `RIoT2.Core.Interfaces.IMatterDevice` and returns one or more
`MatterEndpointTemplate` descriptors for the configuration it was given. The node surfaces them on
`GET /api/device/configuration/templates`, and they travel to the orchestrator as
`DeviceConfiguration.MatterEndpoints` through the normal template import / node configuration save.
`MatterBridgeService` walks the stored node configurations and adds one bridged endpoint per declared
template. See `RIoT2.Net.Devices/Catalog/Hue.cs` for the reference implementation.

The two directions are:

- **Google Home -> RIoT:** a cluster attribute is written by a Matter Invoke, the adapter resolves the
  matching `MatterCommandBinding` and calls `IOrchestratorMqttService.ExecuteCommand`, which publishes
  the usual device `Command`.
- **RIoT -> Google Home:** `OrchestratorMqttService` mirrors every accepted `Report` into the bridge,
  the adapter writes the scaled value onto the cluster attribute, and the Matter stack turns that into
  a subscription report.

### Configuration

`MatterConfiguration` (persisted through `IObjectStore`, editable from the UI):

| Setting | Meaning |
|---|---|
| `Enabled` | Whether the bridge starts with the orchestrator. |
| `NodeLabel` | The name a commissioner shows for the bridge itself. |
| `VendorId` / `ProductId` | The Matter identity, encoded into the onboarding codes. Defaults are the CSA test VID `0xFFF1` and PID `0x8000`. |
| `Discriminator` | The 12-bit setup discriminator advertised over DNS-SD. |
| `AttestationPath` | A directory holding operator-supplied `dac.der`, `pai.der`, `cd.der` and `dac-key.pem`. Empty means TEST credentials are generated for the configured VID/PID. |
| `CredentialsDirectory` | Where generated credentials and the persisted fabric table are written (relative to the content root when not rooted). |
| `FabricStoreKey` | The passphrase sealing the persisted fabric table. Generated once; changing it loses every commissioned fabric. |

The setup passcode, PBKDF salt and the endpoint id map are held separately in `MatterBridgeState`, so
saving configuration from the UI never invalidates a printed QR code and never re-numbers endpoints a
controller has already learned.

### Prerequisites

- **Google Home requires a Developer Console project.** Google Home only commissions an uncertified
  Matter device whose Vendor ID / Product ID are registered as an integration in the
  [Google Home Developer Console](https://console.home.google.com/). Register the configured VID/PID
  pair (by default the test VID `0xFFF1`) before pairing, or commissioning will fail.
- **Network.** IPv6 is mandatory. UDP 5540 (operational) and UDP 5353 (mDNS) must be reachable, and the
  commissioner must be on the same L2 segment. In Docker the orchestrator must run with
  `network_mode: host` - bridge networking breaks mDNS and IPv6 link-local discovery.
- **Packaging.** `RIoT2.Matter` and `RIoT2.Matter.ControlBridge` must be published to the private GitHub
  NuGet feed and referenced as packages before a container build; the Dockerfile restores from that feed
  and cannot see a `ProjectReference`.

### Known gaps

Inherited from `RIoT2.Matter` and not closed by the bridge: no BLE/BTP transport (on-network
commissioning only, so the commissioner must already be on the same network), no group-cast security
path, and no Wi-Fi/Thread network commissioning.

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
