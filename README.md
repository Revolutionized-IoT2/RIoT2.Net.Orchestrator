# RIoT2.Net.Orchestrator

Central ASP.NET Core service for the [RIoT2](https://github.com/Revolutionized-IoT2) IoT
platform. It stores the user's node, dashboard, variable and Matter bridge configuration, tracks
online nodes, routes MQTT reports and commands, and delivers accepted reports to Elsa 3 over gRPC
through a durable outbox.

- Type: ASP.NET Core Web API / hub service
- Target framework: .NET 10
- Root namespace: `RIoT2.Net.Orchestrator`
- Container image: `ghcr.io/revolutionized-iot2/riot2-orchestrator`

How the orchestrator fits into the platform:
[architecture overview](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/architecture/overview.md).

## Contents

| Path | Contents |
| --- | --- |
| `Program.cs` | Host setup, logging, CORS, JSON profiles, health checks and DI registration |
| `Controllers/` | REST API controllers for nodes, templates, state, dashboard, variables, commands, reports, Matter and the outbox |
| `Services/OrchestratorOptions.cs` | The `RIOT2_*` settings, read and validated at startup |
| `Services/OrchestratorConfigurationService.cs` | Settings, stored configuration and template lookup |
| `Services/OrchestratorMqttService.cs` | MQTT report/command routing and state updates; reports go on to the automation provider, commands to the command outbox |
| `Services/Automation/` | `IAutomationProvider` and its only implementation, `ElsaAutomationProvider` (ADR 0008) |
| `Services/Commands/` | `CommandOutbox`: commands through the outbox, one dispatcher per node, and their results (design 7.1) |
| `Services/Outbox/` | The durable outbox: SQLite store, dispatcher, health check (design 7.1) |
| `Services/Persistence/FileObjectStore.cs` | File-backed JSON storage under `StoredObjects/` |
| `Services/Matter/` | Matter Control Bridge configuration, commissioning and endpoint bridging |
| `CustomJsonSettings/` | Controller JSON settings (`OrchestratorJsonOptions`) and the optional `json-naming-policy` formatter profiles |
| `openapi/riot2-orchestrator.json` | OpenAPI document of the REST routes, written by every build and committed |
| `Protos/riot_trigger.proto` | gRPC workflow trigger client contract |
| `Dockerfile` | Multi-stage .NET 10 container image |

## Runtime configuration

The orchestrator is configured with environment variables, read once at startup by
`Services/OrchestratorOptions.cs`. Startup fails with one message that lists every missing or
invalid setting, for example `Invalid orchestrator configuration: {problem}; {problem}.`
`RIOT2_ORCHESTRATOR_URL` must be an absolute `http://` or `https://` URL.

| Variable | Required | Purpose |
| --- | ---: | --- |
| `RIOT2_ORCHESTRATOR_ID` | Yes | MQTT client id and id for orchestrator-owned variable reports |
| `RIOT2_ORCHESTRATOR_URL` | Yes | Base URL sent to nodes in `ConfigurationCommand.apiBaseUrl`; must be reachable by nodes and firmware |
| `RIOT2_MQTT_IP` | Yes | MQTT broker host/IP |
| `RIOT2_MQTT_PORT` | No | MQTT broker port; default 1883 |
| `RIOT2_MQTT_USERNAME` | No | MQTT username; leave empty for an anonymous broker |
| `RIOT2_MQTT_PASSWORD` | No | MQTT password |
| `RIOT2_OUTBOX_WORKFLOW_TTL` | No | Seconds a workflow trigger waits for delivery before it expires; 1–86400, default 300 |
| `RIOT2_OUTBOX_COMMAND_TTL` | No | Seconds a command waits for its node and its answer before it expires; 1–86400, default 30 |

Hosting variables such as `ASPNETCORE_HTTP_PORTS`, `ASPNETCORE_URLS`, `ASPNETCORE_ENVIRONMENT` and
`TZ` are described in the platform
[environment contract](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/env-vars.md).
`RIOT2_USE_EXTERNAL_WORKFLOW_ENGINE` is obsolete; Elsa 3 is the only workflow engine.

Example local run from the workspace root (`C:\Src\RIoT2`):

```powershell
$env:RIOT2_ORCHESTRATOR_ID = "<orchestrator-id>"
$env:RIOT2_ORCHESTRATOR_URL = "http://<host>:8080"
$env:RIOT2_MQTT_IP = "<broker-host>"
$env:RIOT2_MQTT_USERNAME = ""
$env:RIOT2_MQTT_PASSWORD = ""
dotnet run --project .\RIoT2.Net.Orchestrator\RIoT2.Net.Orchestrator.csproj
```

Do not copy values from `Properties/launchSettings.json`, `StoredObjects/`, `Logs/` or
`MatterCredentials/` into documentation or images; those locations are for local/runtime data.

## Storage and Matter

`FileObjectStore` stores JSON as `StoredObjects/<TypeName>/<id>.json` under the application content
root. The outbox is `StoredObjects/outbox.db` (SQLite, with `-wal` and `-shm` files while it runs);
include it in full-system backups, not in configuration backups. It validates type names and ids before using them as file names. The stored node
configuration schema, variable behavior and persistence layout are documented in the platform
[configuration contract](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/configuration.md).

The Matter Control Bridge is disabled by default. Enable and configure it through the UI or
`POST /api/matter/configuration`. Generated test credentials and the fabric store are written under
`MatterConfiguration.CredentialsDirectory`, which must be relative to the content root and defaults
to `MatterCredentials`.

## API, MQTT and workflows

The orchestrator REST API is anonymous by design for the isolated single-user deployment model
([ADR 0002](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/adr/0002-isolated-network-security-model.md)).
CORS allows any origin, method and header. The route table lives in the platform
[HTTP and gRPC API contract](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/http-api.md);
this README intentionally does not duplicate it.

Every build also writes the routes to [openapi/riot2-orchestrator.json](openapi/riot2-orchestrator.json)
(`Microsoft.Extensions.ApiDescription.Server`). Commit it with the controller change: CI fails when
a build changes it, and the UI tests check every URL the UI calls against it. Responses written with
`Json.Serialize` appear without a schema; their formats are the
[golden files](https://github.com/Revolutionized-IoT2/RIoT2.Core/blob/main/contracts/golden/README.md).
The document isn't served at runtime.

The orchestrator subscribes to node reports, node presence, command results and node
configuration statuses, publishes its retained online announcement, sends node configuration
commands (with the configuration's revision and hash), publishes device commands, and publishes
variable changes as reports under the orchestrator id. `GET /api/nodes` reports each node's sync
state from its status ([desired state](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/configuration.md#desired-state)). The topic and payload contract is
[MQTT topics and payloads](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/mqtt-topics.md).

Reports that match configured templates update state and history, are mirrored into Matter when a
Matter endpoint is bound to the template, and become workflow triggers
([design 7.1](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/design/reliable-delivery.md)).
`OrchestratorMqttService` hands each report to `IAutomationProvider`; `ElsaAutomationProvider`
stores it as a trigger in the outbox before the next message is handled. A dispatcher delivers the
triggers in order over gRPC to the online Elsa workflow node, with the outbox id as `message_id`,
which Elsa deduplicates on:

- No workflow node online: the triggers wait, and are delivered when Elsa announces itself.
- Transient failure (unreachable, timeout after five seconds, any gRPC error except
  `InvalidArgument`): retried after 1 s, 2 s, 4 s … at most 30 s; later triggers wait behind it.
- Rejected (`success=false` or `InvalidArgument`): marked `failed`, logged as an error and not
  retried.
- Older than `RIOT2_OUTBOX_WORKFLOW_TTL`: marked `expired` and never delivered; expiries are logged
  as one warning a minute at most.

Pending triggers survive a restart. Final rows are deleted after 24 hours.
`GET /api/v2/outbox/summary` shows the messages per status, the oldest pending message and the last
ten errors. `WorkflowTriggerClient` reuses one channel per workflow endpoint; each call's deadline
is five seconds or the trigger's expiry, whichever comes first.

Commands go through the same outbox (Orchestrator 1.4.0), from `POST /api/command/execute`,
`POST /api/v2/commands`, Elsa and the Matter bridge. `CommandOutbox` runs one dispatcher per node,
so a node's commands keep their order, and the outbox id is the command's `correlationId`:

- Node offline: the command waits until the node announces itself, or until
  `RIOT2_OUTBOX_COMMAND_TTL` (or the request's `ttlSeconds`) passes and it is marked `expired`.
- Node with `command-result/1` (Node 1.4.0): it must answer on `riot2/node/{id}/command/result`
  within five seconds, or the command is sent again with the same id, which the node runs only once.
  `executed` sets the command state; `failed` and `unknownCommand` are final; `rejected` is retried.
- Node without it (older nodes, firmware): the command is sent once, the state is set, and the
  delivery is final as `sent`.

`GET /api/v2/commands/{correlationId}` shows a delivery's status. The state-changing `GET` routes
(`/api/nodes/{id}/delete`, `/api/nodes/variable/{id}/delete`, `/api/dashboard/reports/history/reset`
and the three Matter actions) have `DELETE`/`POST` routes under `/api/v2`; the old routes still work,
marked with `[DeprecatedGetAlias]`, which sends a `Deprecation` header and logs their first use.

## Docker

The Dockerfile uses multi-stage .NET 10 images. The runtime stage is `aspnet:10.0-alpine`, runs as
the non-root .NET app user, and listens on HTTP port `8080`. The build stage stays on the Debian
`sdk:10.0` image because `Grpc.Tools` needs glibc-linked `protoc`.

Build from this repository root (`C:\Src\RIoT2\RIoT2.Net.Orchestrator`). The package-feed token
is passed as a BuildKit secret, so it never lands in an image layer:

```powershell
$env:NUGET_AUTH_TOKEN = '<github-package-token>'
docker build --secret id=nuget_token,env=NUGET_AUTH_TOKEN -t riot2-orchestrator .
```

Run with explicit configuration:

```powershell
docker run --rm -p 8080:8080 `
  -e RIOT2_ORCHESTRATOR_ID=<orchestrator-id> `
  -e RIOT2_ORCHESTRATOR_URL=http://<host>:8080 `
  -e RIOT2_MQTT_IP=<broker-host> `
  -e RIOT2_MQTT_USERNAME=<mqtt-user> `
  -e RIOT2_MQTT_PASSWORD=<mqtt-password> `
  riot2-orchestrator
```

Use `/health` for container or orchestrator health checks; the image's `HEALTHCHECK` calls it, so
`docker ps` shows the state. It is `Unhealthy` while the MQTT client
is disconnected from the broker, and `Degraded` (still HTTP 200) when more than 500 workflow
triggers are pending or one is older than half its time-to-live. For persistent deployments, mount writable directories or volumes for
`/app/StoredObjects`, `/app/Logs` and `/app/MatterCredentials`. Don't mount `/app/Data`: it only
holds the image's `Manifest.json`. Matter commissioning usually also
needs host networking for mDNS and IPv6. The compose file in
[deploy/](https://github.com/Revolutionized-IoT2/.github/tree/main/deploy) runs the orchestrator
with the rest of the stack (`compose.matter.yml` for host networking).

## Build and test

From the workspace root (`C:\Src\RIoT2`):

```powershell
dotnet build .\RIoT2.Net.Orchestrator\RIoT2.Net.Orchestrator.csproj
dotnet test .\RIoT2.Tests\RIoT2.Tests.csproj
dotnet test .\RIoT2.Tests\RIoT2.Tests.Integration\RIoT2.Tests.Integration.csproj -c Release
```

Add `-p:CI=true` to `dotnet build` to reproduce CI analyzer settings locally. Package versions are
centralized in `Directory.Packages.props`; `PackageReference` items do not carry versions.

[RIoT2.Tests](https://github.com/Revolutionized-IoT2/RIoT2.Tests) references Core, the
orchestrator and the InfluxDB connector as projects, so those repositories must be checked out
next to this one. Its [end-to-end tests](https://github.com/Revolutionized-IoT2/RIoT2.Tests/blob/main/README.md#end-to-end-tests) run this `Program` through
`WebApplicationFactory`, with a broker, the Node and a workflow stub.

## Versions and releases

- Release notes are in [CHANGELOG.md](CHANGELOG.md).
- To release, push a tag `x.y.z`. The release workflow validates, then pushes
  `ghcr.io/revolutionized-iot2/riot2-orchestrator:latest` and `:<tag>`. Every push runs CI
  ([CI guide](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/guides/ci.md)).
- The project references the published packages `RIoT2.Core` `1.5.0`, `RIoT2.Matter` `1.0.0`
  and `RIoT2.Matter.ControlBridge` `1.0.0`. To try unreleased versions, restore with
  `C:\Src\RIoT2\.localfeed` as an extra source.

## Contributing

- Instructions for AI coding agents: [AGENTS.md](AGENTS.md).
- Platform documentation: [.github/docs](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/README.md).

## License

See [LICENSE](LICENSE).
