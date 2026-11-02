# AGENTS.md — RIoT2.Net.Orchestrator

Applies to: this repository. Read the platform guide first:
[.github/AGENTS.md](https://github.com/Revolutionized-IoT2/.github/blob/main/AGENTS.md). It covers the
workspace map, platform-wide rules and the documentation rules. In the local workspace, every
`https://github.com/Revolutionized-IoT2/<Repo>/blob/main/<path>` link is the file
`C:\Src\RIoT2\<Repo>\<path>`; read the local file instead of fetching the URL.

## What this is

An ASP.NET Core service targeting .NET 10. It is the hub of the RIoT2 platform: it stores node,
dashboard, variable and Matter bridge configuration, tracks online nodes, routes MQTT reports and
commands, and delivers accepted reports to Elsa 3 over gRPC through a durable SQLite outbox. It
publishes the Docker image
`ghcr.io/revolutionized-iot2/riot2-orchestrator`.

## Commands

Run from the workspace root (`C:\Src\RIoT2`), in PowerShell:

```powershell
dotnet build .\RIoT2.Net.Orchestrator\RIoT2.Net.Orchestrator.csproj
dotnet test .\RIoT2.Tests\RIoT2.Tests.csproj    # Cross-repository tests; project-references Core, Orchestrator and InfluxDB
dotnet test .\RIoT2.Tests\RIoT2.Tests.Integration\RIoT2.Tests.Integration.csproj -c Release    # End-to-end: runs this Program with a broker, the Node and a workflow stub
```

Run locally only after setting the required environment variables (`RIOT2_ORCHESTRATOR_ID`,
`RIOT2_ORCHESTRATOR_URL`, `RIOT2_MQTT_IP`):

```powershell
$env:RIOT2_ORCHESTRATOR_ID = "<orchestrator-id>"
$env:RIOT2_ORCHESTRATOR_URL = "http://<host>:8080"
$env:RIOT2_MQTT_IP = "<broker-host>"
$env:RIOT2_MQTT_USERNAME = ""
$env:RIOT2_MQTT_PASSWORD = ""
dotnet run --project .\RIoT2.Net.Orchestrator\RIoT2.Net.Orchestrator.csproj
```

Build the container from this repository root (`C:\Src\RIoT2\RIoT2.Net.Orchestrator`):

```powershell
$env:NUGET_AUTH_TOKEN = '<github-package-token>'   # a BuildKit secret, never a build argument
docker build --secret id=nuget_token,env=NUGET_AUTH_TOKEN -t riot2-orchestrator .
```

Every push runs `.github/workflows/ci.yml` (build, image build, secret scan). To release, push a
clean git tag `x.y.z`: `release.yml` validates, then pushes
`ghcr.io/revolutionized-iot2/riot2-orchestrator:latest` and `:<tag>` ([CI guide](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/guides/ci.md)). The
Dockerfile writes `Data/Manifest.json` from the `VERSION` and `IMAGE_TAG` build arguments, and has
a `HEALTHCHECK` on `/health` that reads the port from `ASPNETCORE_HTTP_PORTS`.
The cross-repository tests in RIoT2.Tests build this repository's `main` nightly; the end-to-end
tests run `Program.cs` itself, so a startup change that needs a new environment variable or service
must keep them passing.

## Layout

| Path | Contents |
|---|---|
| `Program.cs` | ASP.NET Core host, logging (`AddRIoT2Logging()`), `AddRIoT2Security()`, JSON profiles, DI, health checks and hosted services |
| `Controllers/` | REST controllers (`Viewer` by default, writes `Operator`/`Admin`), one per resource: nodes, templates, state, variables, plugin and cron checks, dashboard, reports, commands (`api/v2/commands`), Matter, backup and the outbox. `DeprecatedGetAliasAttribute` marks the old state-changing `GET` routes |
| `CustomJsonSettings/` | `OrchestratorJsonOptions` (controller JSON settings) and the deprecated `json-naming-policy: pascal\|lower` profiles (`NamingPolicyHeaderDeprecation`; removed in I45) |
| `openapi/riot2-orchestrator.json` | Generated OpenAPI document (the REST route contract), committed |
| `Services/OrchestratorOptions.cs` | Every `RIOT2_*` setting the orchestrator reads, validated at startup with Core's `SettingsReader` |
| `Services/OrchestratorConfigurationService.cs` | Settings from `IOptions<OrchestratorOptions>`, stored node/dashboard configuration and template lookup |
| `Services/Templates/` | `ITemplateCatalog`: the only place that shapes the REST template listings |
| `Services/OrchestratorMqttService.cs` | MQTT subscriptions/publications, state updates, Matter report mirroring; reports and presence go to `IAutomationProvider`, commands, results and presence to `ICommandOutbox`; it is the outbox's `ICommandTransport` |
| `Services/Commands/` | `CommandOutbox` (one `OutboxDispatcher` per node, result waiting, late results) and `CommandDelivery` (the v2 status) |
| `Services/Automation/` | `IAutomationProvider` (ADR 0008) and `ElsaAutomationProvider`, which stores triggers in the outbox and runs their dispatcher as a hosted service |
| `Services/Outbox/` | `OutboxStore` (`StoredObjects/outbox.db`, plain SQL over `Microsoft.Data.Sqlite`), `OutboxDispatcher` (FIFO per target, backoff, TTL, cleanup), `OutboxHealthCheck` |
| `Services/Persistence/` | `IStoredObjectStore` (typed; `StoredObjectService`), `StoredObjectJson` (storage format), the `StoredObjects/<Type>/<Id>.json` files, `IObjectStoreArchive` (export, replace, `.archive/`), `LegacyStoredObjects` |
| `Services/OrchestratorMetrics.cs` | Meter `RIoT2.Orchestrator` (design 7.4); names in the hub's `contracts/metrics.md`, pinned by RIoT2.Tests |
| `Services/Backup/` | Configuration backup and restore (design 7.4): `BackupArchive` (zip and manifest validation), `BackupService`, `BackupScheduler` (`RIOT2_BACKUP_SCHEDULE`) |
| `Services/Matter/` | `MatterBridgeService` (lifecycle) with `MatterCredentials`, `MatterProvisioning`, `MatterHostNetwork` and `MatterBridgedEndpoints` (endpoints, reports, reachability) |
| `Services/WorkflowTriggerClient.cs` | Reused gRPC client/channel with a five-second workflow trigger deadline; used only by `ElsaAutomationProvider` |
| `Protos/riot_trigger.proto` | gRPC trigger contract, kept in lockstep with Elsa's `riot.proto` |
| `Dockerfile` | .NET 10 multi-stage, Alpine, non-root, port 8080; bases pinned by digest |
| `.github/workflows/ci.yml`, `release.yml` | CI on every push; GHCR image release on a tag |

## Contracts implemented here

Change code and hub contracts together. Per-repository docs link to the hub; don't copy route,
topic or environment-variable tables here.

- [http-api.md](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/http-api.md):
  `Controllers/`, `Program.cs`, `CustomJsonSettings/Formatters.cs`, `/health`, and
  `Protos/riot_trigger.proto`.
- [mqtt-topics.md](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/mqtt-topics.md):
  `Services/OrchestratorMqttService.cs`, `Services/MqttBackgroundService.cs` and
  `Services/MqttHealthCheck.cs`.
- [env-vars.md](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/env-vars.md):
  `Services/OrchestratorOptions.cs` (including `RIOT2_OUTBOX_WORKFLOW_TTL` and `RIOT2_OUTBOX_COMMAND_TTL`), `Dockerfile` and
  `.github/workflows/release.yml`. A new
  setting is added to `OrchestratorOptions` and to the contract; nothing else reads the
  environment.
- [configuration.md](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/configuration.md):
  `Services/OrchestratorConfigurationService.cs`, `Services/StoredObjectService.cs`,
  `Services/Persistence/FileObjectStore.cs` and `Services/Matter/`.

The gRPC service/message schema in `Protos/riot_trigger.proto` must stay compatible with
`RIoT2.Elsa/RIoT2.Elsa.Server/RIoT/Protos/riot.proto`; only the generated C# namespace differs
today. Delivery follows [design 7.1](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/design/reliable-delivery.md)
and [ADR 0006](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/adr/0006-reliable-delivery.md).

## Rules

- Follow [ADR 0002](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/adr/0002-isolated-network-security-model.md):
  nothing is enforced while `RIOT2_SECURITY_MODE` is `off`; mandatory auth must not be added.
- New endpoints must not change state on `GET`. The old state-changing `GET` routes are deprecated
  aliases of `/api/v2` routes on the same action (`[DeprecatedGetAlias]`); don't add new ones, and
  remove them with backlog item 7.
- Every action gets `[Authorize(Policy = SecurityPolicies.…)]` (design 7.5): `Viewer` reads,
  `Operator` commands, `Admin` writes; the end-to-end suite fails otherwise. No ad-hoc auth.
- Outbound calls go through an `IHttpClientFactory` client with `OutboundUrlPolicyHandler`;
  only clients to RIoT2 services get `ApiKeyHandler`.
- Validate every input, even on an isolated network.
- Keep REST routes, MQTT topics, JSON payloads and the gRPC proto additive and compatible with
  `RIoT2.Core`, `RIoT2.UI`, `RIoT2.Elsa`, firmware and connectors.
- Use `RIoT2.Core.Constants.Get` / `GetTopicId` for topics and Core `Json.Serialize` /
  `Json.SerializeIgnoreNulls` for wire payloads that use Core models.
- Keep the Core and Matter packages as package references: the Dockerfile restores from GitHub
  Packages. Pins: `RIoT2.Core.Contracts`, `.Http`, `.Mqtt` and `.Hosting` `1.19.0` (never the facade
  or `.Node`; Quartz is a direct reference), `RIoT2.Matter` and
  `RIoT2.Matter.ControlBridge` `1.0.0`, and the transitive `System.Security.Cryptography.Pkcs`
  `10.0.14` (drop it once RIoT2.Matter brings a patched one). Use `C:\Src\RIoT2\.localfeed` only to
  try unreleased versions.
- `FileObjectStore` must validate type names and ids as file names. Never build persistence paths
  from raw ids.
- Matter generated credentials and the fabric store must stay under a content-root-relative
  `CredentialsDirectory` (default `MatterCredentials`); reject absolute or escaping paths.
- Images must not contain credentials, IDs, IPs, `StoredObjects`, logs or local settings.
- Keep the image on port `8080` and the non-root .NET app user.
- Elsa 3 is the only automation engine; don't reintroduce the rule engine.
- Reach automation only through `IAutomationProvider` (ADR 0008); nothing outside
  `Services/Automation/` calls `WorkflowTriggerClient`. A trigger must be in the outbox before
  `ReportReceivedAsync` returns, and its outbox id is its `message_id`: never send a redelivery with
  a new id.
- Every device command goes through `ICommandOutbox` (`OrchestratorMqttService.ExecuteCommand`
  enqueues). Its outbox id is its `correlationId`; a redelivery must repeat it, because nodes run an
  id only once. Set the command state only on `executed` or `duplicate` (or on `sent` to a node
  without `command-result/1`).
- The outbox SQL is plain `Microsoft.Data.Sqlite`; no EF Core. A schema change raises
  `PRAGMA user_version` in `OutboxStore` and must open an existing `outbox.db`.
- Stored types implement `IStoredObject` and go through `IStoredObjectStore`, written only with
  `StoredObjectJson`. No `dynamic` or Newtonsoft; I45 removes the `json-naming-policy` profiles.
- A route change must commit the regenerated `openapi/riot2-orchestrator.json`; CI
  (`verify-generated`) fails otherwise, and the RIoT2.UI route test reads it. A change to a response
  or stored format must regenerate the golden files in `RIoT2.Core/contracts/golden` (RIoT2.Tests
  with `RIOT2_UPDATE_GOLDEN=1`).
- New persisted state goes in `StoredObjects/` (or the Matter credentials folder), so backups carry it;
  a new stored type that must load is added to `BackupService.CheckRestorable`. A restore replaces
  files under the `StoredObjectService` lock inside `IMatterBridgeService.RunStoppedAsync`.
- Logging is `AddRIoT2Logging()` (RIoT2.Core.Hosting), set by `RIOT2_LOG_*`; add no sinks,
  providers or `Logging` sections. Scope work for one message with `BeginIdScope(LogScope…)`.

## Pitfalls

- The build-time OpenAPI generator runs `Program.cs` and starts the host. `Program.cs` therefore
  skips the hosted services, configuration validation and startup work when the entry assembly is
  `GetDocument.Insider`; keep new startup side effects inside that check. Generation is off in the
  SDK container (`DOTNET_RUNNING_IN_CONTAINER`).
- `MatterBridgeService` is registered as `IMatterBridgeService` and, through a factory, as
  `IMatterReportSink`, so the container disposes it twice; `DisposeAsync` must stay idempotent.
- The Web SDK includes `**/*.json` as content, so the `.csproj` (publish) and `.dockerignore`
  exclude `StoredObjects/`, `MatterCredentials/`, `Logs/`, `Data/` and `Backups/`. Add new runtime folders to both.
- `Properties/launchSettings.json`, `StoredObjects/`, `Logs/` and `MatterCredentials/` can hold
  local values or secrets: never copy them into docs, images or examples.
- Workflow delivery is at-least-once: a trigger whose answer is lost is delivered again with the
  same `message_id`; Elsa deduplicates it.
- Delivery is FIFO per target, so a failing trigger holds up the ones behind it until it succeeds,
  fails or expires (at most `RIOT2_OUTBOX_WORKFLOW_TTL`, default 300 s).
- `ElsaAutomationProvider` and `CommandOutbox` are singletons and, through factories, hosted
  services registered after `MqttBackgroundService`. They stop first, and the reports MQTT drains
  while stopping are stored for the next start. Keep that order.
- `CommandOutbox` needs its transport: `OrchestratorMqttService` calls `UseTransport(this)` in its
  constructor (no dependency cycle). Until then, and while MQTT is down, commands wait.
- Elsa sets no MQTT last will, so a crashed Elsa still looks online: triggers are retried until
  they expire. Elsa's next announcement resets the backoff.
- `outbox.db` is opened without connection pooling; the file is released when the store is
  disposed. Unit tests create the store in a temporary directory.
- `POST /api/command/execute` returns once the command is queued. Use
  `POST /api/v2/commands` with `"wait": true` to learn whether the device executed it.
- A command delivered while its node is still applying its configuration is answered `rejected`
  and retried, so the first commands after a node comes online can take a second or two.
- `OrchestratorMqttService` publishes retained `{"isOnline":true}` on `riot2/orchestrator/online`
  on every connect. Its last will (set with `MqttClient.SetLastWill` before `Start`) and its
  graceful stop replace it with retained `{"isOnline":false}`.
- Node configurations carry a `revision` and `hash` (design 7.2), stamped on save and load; never
  serve or hash a configuration you changed in place (`?state=true` works on a copy).
- `Json.Serialize` camel-cases dictionary keys. Configuration `deviceParameters` should use
  camelCase keys because device lookups are case-sensitive.
- `Data/Manifest.json` is written into the image at build time. Don't make `/app/Data` a volume:
  a mount would hide it or keep an old copy. Persistent data belongs in `StoredObjects/`.

## Related work

- [Backlog items](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/backlog/open-issues.md) 2, 7, 13, 14, 17 and 18.
- [Plans](https://github.com/Revolutionized-IoT2/.github/tree/main/docs/plans) M2, M3, M4, M7 and M8.
- Designs
  [7.1](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/design/reliable-delivery.md),
  [7.2](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/design/desired-state-configuration.md),
  [7.4](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/design/operations.md) and
  [7.5](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/design/security-mode.md).
