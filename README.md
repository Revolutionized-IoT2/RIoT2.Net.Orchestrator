# RIoT2.Net.Orchestrator

Central ASP.NET Core service for the [RIoT2](https://github.com/Revolutionized-IoT2) IoT
platform. It stores the user's node, dashboard, variable and Matter bridge configuration, tracks
online nodes, routes MQTT reports and commands, and runs the Elsa 3 workflows: in process by
default, or on the Elsa container over gRPC, either way through a durable outbox. It serves the UI's
workflow designer and keeps the *Run LLM* step's model connections
([ADR 0016](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/adr/0016-integrated-workflows-and-llm.md)). Optionally it is an MCP server, so AI assistants such as
Claude and ChatGPT can read and control the home, also from the internet through Cloudflare Access
([ADR 0018](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/adr/0018-mcp-server-and-remote-ai-access.md),
[remote AI access guide](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/guides/remote-ai-access.md)).

- Type: ASP.NET Core Web API / hub service
- Target framework: .NET 10
- Root namespace: `RIoT2.Net.Orchestrator`
- Container image: `ghcr.io/revolutionized-iot2/riot2-orchestrator`

How the orchestrator fits into the platform:
[architecture overview](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/architecture/overview.md).

## Contents

| Path | Contents |
| --- | --- |
| `Program.cs` | Host setup, logging, security seams, JSON profiles, health checks and DI registration |
| `Controllers/` | REST API controllers for nodes, connectors, templates, state, dashboard, variables, commands, reports, Matter, the outbox, backups, workflows, language models and device sign-in |
| `Services/OrchestratorOptions.cs` | The `RIOT2_*` settings, read and validated at startup |
| `Services/OrchestratorConfigurationService.cs` | Settings, stored configuration and template lookup |
| `Services/OrchestratorMqttService.cs` | MQTT report/command routing and state updates; reports go on to the automation provider, commands to the command outbox |
| `Services/Automation/` | `IAutomationProvider` (ADR 0008): `EmbeddedAutomationProvider` (in process) or `ElsaAutomationProvider` (gRPC), both on `OutboxAutomationProvider` |
| `Services/Workflows/` | `IWorkflowEngine` behind `/api/workflows`: `EmbeddedWorkflowEngine` or `RemoteWorkflowEngine`; `OrchestratorRIoTDataService` for the embedded activities |
| `Services/Llm/` | Run LLM: connections (`LlmConnectionService`), the OpenAI-style and Anthropic adapters, the tools, `LlmService` (`ILlmRunner`) and the usage store |
| `Services/Tools/`, `Services/Mcp/` | The tools Run LLM and MCP share (`RiotTools`); the MCP server: settings, tool policy and filters, the remote listener's token validation, the tools |
| `Services/Commands/` | `CommandOutbox`: commands through the outbox, one dispatcher per node, and their results (design 7.1) |
| `Services/Outbox/` | The durable outbox: SQLite store, dispatcher, health check (design 7.1) |
| `Services/Persistence/` | The typed store `IStoredObjectStore`, its storage format `StoredObjectJson`, and the file-backed storage under `StoredObjects/` |
| `Services/Backup/` | Configuration backup and restore, and the backup schedule (design 7.4) |
| `Services/Health/` | The health page's report (`GET /api/v2/system/health`) and the node history behind it (I46) |
| `Services/DeviceSignIn/` | Device account sign-in (Core 2.4.0): the single-use OAuth states, the node calls and the callback page |
| `Services/OrchestratorMetrics.cs` | The `RIoT2.Orchestrator` meter: reports, workflow deliveries, outbox, commands, nodes (design 7.4) |
| `Services/Templates/` | `ITemplateCatalog`, which shapes every REST template listing |
| `Services/Matter/` | Matter Control Bridge: lifecycle, credentials, provisioning and bridged endpoints |
| `CustomJsonSettings/` | Controller JSON settings (`OrchestratorJsonOptions`, camelCase) |
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
| `RIOT2_MQTT_USERNAME`, `_PASSWORD` | No | MQTT account; leave empty for an anonymous broker |
| `RIOT2_MQTT_TLS`, `RIOT2_MQTT_CA_FILE` | No | TLS to the broker (port 8883), optionally with a private CA |
| `RIOT2_SECURITY_MODE`, `RIOT2_API_KEY`, … | No | The optional security mode; only `off` yet ([env-vars](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/env-vars.md#shared-variables)) |
| `RIOT2_OUTBOX_WORKFLOW_TTL` | No | Seconds a workflow trigger waits for delivery before it expires; 1–86400, default 300 |
| `RIOT2_OUTBOX_COMMAND_TTL` | No | Seconds a command waits for its node and its answer before it expires; 1–86400, default 30 |
| `RIOT2_WORKFLOW_MODE` | No | `embedded` (default): Elsa runs here; `remote`: on the online Elsa container (1.21.0) |
| `RIOT2_LATITUDE`, `RIOT2_LONGITUDE` | No | Embedded mode: where `RIoT Schedule` finds sunrise and sunset; both or neither |
| `RIOT2_LOG_FORMAT` | No | `text` (default) or `json` for the console and `Logs/RIoT2.log` |
| `RIOT2_LOG_LEVEL` | No | `Trace` … `Critical`; default `Information` |
| `RIOT2_BACKUP_SCHEDULE` | No | Backups to `Backups/`: a Quartz cron expression with seconds, e.g. `0 0 3 * * ?`; unset is off |
| `RIOT2_BACKUP_RETENTION` | No | Backups kept; 1–365, default 7 |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | No | OTLP collector for metrics, such as `http://otel-collector:4317`; unset exports nothing |
| `RIOT2_TRACING` | No | `true` also exports traces; needs the endpoint |

Logs go to the console and to `Logs/RIoT2.log` (`/app/Logs`) through `AddRIoT2Logging()`. Lines logged while a command or a
workflow trigger is delivered end with `[CorrelationId=…]` or `[MessageId=…]`.

Metrics (meter `RIoT2.Orchestrator`, plus `riot2.mqtt.*`, ASP.NET Core and the runtime) go out through
`AddRIoT2Telemetry()`; every name is in the
[metrics contract](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/metrics.md).

Hosting variables such as `ASPNETCORE_HTTP_PORTS`, `ASPNETCORE_URLS`, `ASPNETCORE_ENVIRONMENT` and
`TZ` are described in the platform
[environment contract](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/env-vars.md).

Example local run from the workspace root (`C:\Src\RIoT2`):

```powershell
$env:RIOT2_ORCHESTRATOR_ID = "<orchestrator-id>"
$env:RIOT2_ORCHESTRATOR_URL = "http://<host>:8080"
$env:RIOT2_MQTT_IP = "<broker-host>"
$env:RIOT2_MQTT_USERNAME = ""
$env:RIOT2_MQTT_PASSWORD = ""
dotnet run --project .\RIoT2.Net.Orchestrator\RIoT2.Net.Orchestrator.csproj
```

Never copy values from `launchSettings.json`, `StoredObjects/`, `Logs/`, `MatterCredentials/` or
`Backups/` into documentation or images.

## Storage and Matter

`StoredObjectService` keeps node configurations, variables and the dashboard in memory and
stores them with System.Text.Json; `FileObjectStore` writes `StoredObjects/<TypeName>/<id>.json`,
validating type names and ids. Types no longer read (the old `Rule/`) move to
`StoredObjects/.archive/`. Every dashboard page, card and element has a stable `id`: one without an id gets a new
one when the dashboard is loaded (in memory) and saved (stored); an existing id never changes. The outbox is `StoredObjects/outbox.db` (SQLite); include it in
full-system backups, not in configuration backups. The same goes for the embedded workflows
(`StoredObjects/elsa.sqlite.db`), the Run LLM usage (`llm-usage.db`) and the data protection keys that
encrypt the connections' API keys (`StoredObjects/.keys/`). The stored formats are documented in the platform
[configuration contract](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/configuration.md).

The Matter Control Bridge is disabled by default. Enable and configure it through the UI or
`POST /api/matter/configuration`. Generated test credentials and the fabric store are written under
`MatterConfiguration.CredentialsDirectory`, which must be relative to the content root and defaults
to `MatterCredentials`.

Bridged endpoint ids never change (1.18.0): each is recorded per device and template, and a new
endpoint gets an id no endpoint ever had. Controller bindings reach their peers through DNS-SD.

## Backup and restore

`POST /api/v2/backup` returns a zip of `StoredObjects/` (without the outbox) and the Matter
credentials folder, with a `backup-manifest.json` that lists each file's size and SHA-256.
`POST /api/v2/restore` (zip body) checks the whole archive first and changes nothing when anything
is wrong. Otherwise it saves the current configuration to `Backups/riot2-pre-restore-<time>.zip`,
stops the Matter bridge, replaces the files while saves wait, reloads every cache, restarts the
bridge and sends every node a configuration command. `RIOT2_BACKUP_SCHEDULE` writes
`Backups/riot2-backup-<time>.zip` and keeps the newest `RIOT2_BACKUP_RETENTION`. Backups hold
device credentials and Matter keys. Use and file format:
[backup-and-restore.md](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/guides/backup-and-restore.md).

## API, MQTT and workflows

Nothing is enforced by default ([ADR 0002](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/adr/0002-isolated-network-security-model.md)):
every action has a `Viewer`, `Operator` or `Admin` policy for the optional security mode
([design 7.5](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/design/security-mode.md)), and while `RIOT2_SECURITY_MODE` is `off`, the only accepted value yet, every request
passes. `GET /api/security/info` reports the mode. The route table lives in the platform
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
`OrchestratorMqttService` hands each report to `IAutomationProvider`, which stores it as a trigger
in the outbox before the next message is handled. A dispatcher hands the triggers in order to Elsa,
with the outbox id as `message_id`, which Elsa deduplicates on: in process with
`RIOT2_WORKFLOW_MODE=embedded` (the default), or over gRPC to the online Elsa workflow node with
`remote`. `/api/workflows` serves the UI's designer from the same engine.

- No workflow node online (remote): the triggers wait, and are delivered when Elsa announces itself.
- Transient failure (unreachable, five-second timeout, any gRPC error but
  `InvalidArgument`): retried after 1 s, 2 s, 4 s … at most 30 s; later triggers wait behind it.
- Rejected (`success=false` or `InvalidArgument`): marked `failed`, logged as an error and not
  retried.
- Older than `RIOT2_OUTBOX_WORKFLOW_TTL`: marked `expired` and never delivered; expiries are logged
  as one warning a minute at most.

Pending triggers survive a restart. Final rows are deleted after 24 hours.
`GET /api/v2/outbox/summary` shows the messages per status, the oldest pending message and the last
ten errors; `GET /api/v2/system/health` (the UI's System page) adds it to the node and device health.

Commands go through the same outbox, from `POST /api/command/execute`,
`POST /api/v2/commands`, Elsa and the Matter bridge. `CommandOutbox` runs one dispatcher per node,
so a node's commands keep their order, and the outbox id is the command's `correlationId`:

- Node offline: the command waits until the node announces itself, or until
  `RIOT2_OUTBOX_COMMAND_TTL` (or the request's `ttlSeconds`) passes and it is marked `expired`.
- Node with `command-result/1`: it must answer on `riot2/node/{id}/command/result`
  within five seconds, or the command is sent again with the same id, which the node runs only once.
  `executed` sets the command state; `failed` and `unknownCommand` are final; `rejected` is retried.
- Node without it (older nodes, firmware): the command is sent once, the state is set, and the
  delivery is final as `sent`.

`GET /api/v2/commands/{correlationId}` shows a delivery's status. The state-changing `GET` routes
have `DELETE`/`POST` routes under `/api/v2`; the old routes still work,
marked with `[DeprecatedGetAlias]`, which sends a `Deprecation` header and logs their first use.

A device that signs in to a cloud account (Core 2.4.0, for example Netatmo) is connected from the UI: the
orchestrator creates a single-use state, asks the node for the provider's sign-in page, and the provider sends the
popup back to the anonymous `GET /api/oauth/callback`, which hands the code to the node. Register
`<UI address>/api/oauth/callback` as the redirect URI in the provider's app: the UI proxies `/api` to the
orchestrator, so the UI's address is the one the browser sees.

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
`/app/StoredObjects`, `/app/Logs`, `/app/MatterCredentials` and `/app/Backups`. Don't mount `/app/Data`: it only
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
- Package versions are in `Directory.Packages.props`. To try unreleased versions, restore with
  `C:\Src\RIoT2\.localfeed` as an extra source.

## Contributing

- Instructions for AI coding agents: [AGENTS.md](AGENTS.md).
- Platform documentation: [.github/docs](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/README.md).

## License

See [LICENSE](LICENSE).
