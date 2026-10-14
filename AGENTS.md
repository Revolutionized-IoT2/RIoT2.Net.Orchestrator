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
| `Program.cs` | ASP.NET Core host, Serilog, CORS, JSON profiles, DI, health checks and hosted services |
| `Controllers/` | Anonymous REST controllers for nodes, templates, state, dashboard, variables, commands (`api/v2/commands`), reports, Matter and the outbox (`api/v2/outbox`); `DeprecatedGetAliasAttribute` marks the old state-changing `GET` routes |
| `CustomJsonSettings/` | `OrchestratorJsonOptions` (controller JSON settings) and the `json-naming-policy: pascal\|lower` MVC formatter profiles |
| `openapi/riot2-orchestrator.json` | Generated OpenAPI document (the REST route contract), committed |
| `Services/OrchestratorOptions.cs` | Every `RIOT2_*` setting the orchestrator reads, validated at startup with Core's `SettingsReader` |
| `Services/OrchestratorConfigurationService.cs` | Settings from `IOptions<OrchestratorOptions>`, stored node/dashboard configuration and template lookup |
| `Services/OrchestratorMqttService.cs` | MQTT subscriptions/publications, state updates, Matter report mirroring; reports and presence go to `IAutomationProvider`, commands, results and presence to `ICommandOutbox`; it is the outbox's `ICommandTransport` |
| `Services/Commands/` | `CommandOutbox` (one `OutboxDispatcher` per node, result waiting, late results) and `CommandDelivery` (the v2 status) |
| `Services/Automation/` | `IAutomationProvider` (ADR 0008) and `ElsaAutomationProvider`, which stores triggers in the outbox and runs their dispatcher as a hosted service |
| `Services/Outbox/` | `OutboxStore` (`StoredObjects/outbox.db`, plain SQL over `Microsoft.Data.Sqlite`), `OutboxDispatcher` (FIFO per target, backoff, TTL, cleanup), `OutboxHealthCheck` |
| `Services/Persistence/` | File-backed `StoredObjects/<Type>/<Id>.json` object store |
| `Services/Matter/` | Matter Control Bridge configuration, endpoint composition, commissioning and report/command bridging |
| `Services/WorkflowTriggerClient.cs` | Reused gRPC client/channel with a five-second workflow trigger deadline; used only by `ElsaAutomationProvider` |
| `Protos/riot_trigger.proto` | gRPC trigger contract, kept in lockstep with Elsa's `riot.proto` |
| `Dockerfile` | .NET 10 multi-stage image; runtime is Alpine, non-root, HTTP port 8080 |
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
  APIs are anonymous by design, CORS is permissive, and mandatory auth must not be added.
- New endpoints must not change state on `GET`. The old state-changing `GET` routes are deprecated
  aliases of `/api/v2` routes on the same action (`[DeprecatedGetAlias]`); don't add new ones, and
  remove them with backlog item 7.
- Do not add ad-hoc per-endpoint authentication. Optional security belongs behind the single
  security-mode design.
- Validate every input even though the current deployment model is an isolated single-user
  network.
- Keep REST routes, MQTT topics, JSON payloads and the gRPC proto additive and compatible with
  `RIoT2.Core`, `RIoT2.UI`, `RIoT2.Elsa`, firmware and connectors.
- Use `RIoT2.Core.Constants.Get` / `GetTopicId` for topics and Core `Json.Serialize` /
  `Json.SerializeIgnoreNulls` for wire payloads that use Core models.
- Keep `RIoT2.Core`, `RIoT2.Matter` and `RIoT2.Matter.ControlBridge` as package references. The
  Dockerfile restores from GitHub Packages and cannot see project references.
- Current package pins are `RIoT2.Core` `1.7.0`, `RIoT2.Matter` `1.0.0` and
  `RIoT2.Matter.ControlBridge` `1.0.0`, all published. Use `C:\Src\RIoT2\.localfeed` as an extra
  NuGet source only to try unreleased versions.
- Keep `PackageReference` items versionless; package versions belong in `Directory.Packages.props`.
- `FileObjectStore` must validate type names and ids as file names. Never build persistence paths
  from raw ids.
- Matter generated credentials and the fabric store must stay under a content-root-relative
  `CredentialsDirectory` (default `MatterCredentials`); reject absolute or escaping paths.
- Container images must not contain credentials, IDs, IPs, `StoredObjects`, logs or local
  development settings. Use placeholders in docs and environment variables at runtime.
- Keep the Docker runtime on unprivileged port `8080` and the non-root .NET app user unless an
  explicit deployment design changes it.
- Elsa 3 is the only automation engine. Do not reintroduce the retired rule engine or the obsolete
  `RIOT2_USE_EXTERNAL_WORKFLOW_ENGINE` switch.
- Reach automation only through `IAutomationProvider` (ADR 0008); nothing outside
  `Services/Automation/` calls `WorkflowTriggerClient`. A trigger must be in the outbox before
  `ReportReceivedAsync` returns, and its outbox id is its `message_id`: never send a redelivery with
  a new id.
- Every device command goes through `ICommandOutbox` (`OrchestratorMqttService.ExecuteCommand`
  enqueues). Its outbox id is its `correlationId`; a redelivery must repeat it, because nodes run an
  id only once. Set the command state only on `executed` (or on `sent` to a node without
  `command-result/1`).
- The outbox SQL is plain `Microsoft.Data.Sqlite`; no EF Core. A schema change raises
  `PRAGMA user_version` in `OutboxStore` and must open an existing `outbox.db`.
- Preserve the `json-naming-policy` header profiles until M2 removes them through a compatibility
  plan.
- A route change must commit the regenerated `openapi/riot2-orchestrator.json`; CI
  (`verify-generated`) fails otherwise, and the RIoT2.UI route test reads it. A change to a response
  or stored format must regenerate the golden files in `RIoT2.Core/contracts/golden` (RIoT2.Tests
  with `RIOT2_UPDATE_GOLDEN=1`).

## Pitfalls

- The build-time OpenAPI generator runs `Program.cs` and starts the host. `Program.cs` therefore
  skips the hosted services, configuration validation and startup work when the entry assembly is
  `GetDocument.Insider`; keep new startup side effects inside that check. Generation is off in the
  SDK container (`DOTNET_RUNNING_IN_CONTAINER`).
- `MatterBridgeService` is registered as `IMatterBridgeService` and, through a factory, as
  `IMatterReportSink`, so the container disposes it twice; `DisposeAsync` must stay idempotent.
- The Web SDK includes `**/*.json` as content. The `.csproj` therefore excludes `StoredObjects/`,
  `MatterCredentials/`, `Logs/` and `Data/` from publish, and `.dockerignore` excludes them from
  the Docker context. Add any new runtime folder to both.
- `Properties/launchSettings.json`, `StoredObjects/`, `Logs/` and `MatterCredentials/` can contain
  local values or secrets. Do not copy their contents into docs, images or examples.
- Workflow delivery is at-least-once: a trigger whose answer is lost is delivered again with the
  same `message_id`. Elsa 1.3.0 deduplicates; an older Elsa runs it twice.
- Delivery is FIFO per target, so a failing trigger holds up the ones behind it until it succeeds,
  fails or expires (at most `RIOT2_OUTBOX_WORKFLOW_TTL`, default 300 s).
- `ElsaAutomationProvider` and `CommandOutbox` are singletons and, through factories, hosted
  services registered after `MqttBackgroundService`. They stop first, and the reports MQTT drains
  while stopping are stored for the next start. Keep that order.
- `CommandOutbox` needs its transport: `OrchestratorMqttService` calls `UseTransport(this)` in its
  constructor, which avoids a dependency cycle. Until then, and while MQTT is down, commands wait.
- Elsa sets no MQTT last will, so a crashed Elsa still looks online: triggers are retried until
  they expire. Elsa's next announcement resets the backoff.
- `outbox.db` is opened without connection pooling; the file is released when the store is
  disposed. Unit tests create the store in a temporary directory.
- `POST /api/command/execute` returns once the command is queued (Orchestrator 1.4.0). Use
  `POST /api/v2/commands` with `"wait": true` to learn whether the device executed it.
- A command delivered while its node is still applying its configuration is answered `rejected`
  and retried, so the first commands after a node comes online can take a second or two.
- `OrchestratorMqttService` publishes retained `{"isOnline":true}` on `riot2/orchestrator/online`
  on every connect. Its last will (set with `MqttClient.SetLastWill` before `Start`) and its
  graceful stop replace it with retained `{"isOnline":false}`.
- .NET MQTT presence messages and last wills are not retained, while firmware presence is
  retained (divergence D1).
- `Json.Serialize` camel-cases dictionary keys. Configuration `deviceParameters` should use
  camelCase keys because device lookups are case-sensitive.
- `Data/Manifest.json` is written into the image at build time. Don't make `/app/Data` a volume:
  a mount would hide it or keep an old copy. Persistent data belongs in `StoredObjects/`.

## Related work

- Backlog items
  [2](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/backlog/open-issues.md),
  [7](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/backlog/open-issues.md),
  [13](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/backlog/open-issues.md),
  [14](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/backlog/open-issues.md),
  [17](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/backlog/open-issues.md) and
  [18](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/backlog/open-issues.md).
- Maintainer action
  [MA2](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/backlog/README.md#ma2-cut-a-core-release-and-align-all-consumers).
- Contract divergences
  [D1](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/backlog/README.md#contract-divergences).
- Plans
  [M2](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/plans/m02-system-text-json-persistence.md),
  [M3](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/plans/m03-split-oversized-classes.md),
  [M4](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/plans/m04-typed-configuration.md),
  [M7](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/plans/m07-contract-integration-tests.md) and
  [M8](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/plans/m08-dotnet10-migration.md).
- Designs
  [7.1](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/design/reliable-delivery.md),
  [7.2](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/design/desired-state-configuration.md) and
  [7.5](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/design/security-mode.md).
