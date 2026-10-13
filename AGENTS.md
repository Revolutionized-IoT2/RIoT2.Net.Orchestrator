# AGENTS.md — RIoT2.Net.Orchestrator

Applies to: this repository. Read the platform guide first:
[.github/AGENTS.md](https://github.com/Revolutionized-IoT2/.github/blob/main/AGENTS.md). It covers the
workspace map, platform-wide rules and the documentation rules. In the local workspace, every
`https://github.com/Revolutionized-IoT2/<Repo>/blob/main/<path>` link is the file
`C:\Src\RIoT2\<Repo>\<path>`; read the local file instead of fetching the URL.

## What this is

An ASP.NET Core service targeting .NET 10. It is the hub of the RIoT2 platform: it stores node,
dashboard, variable and Matter bridge configuration, tracks online nodes, routes MQTT reports and
commands, and forwards accepted reports to Elsa 3 over gRPC. It publishes the Docker image
`ghcr.io/revolutionized-iot2/riot2-orchestrator`.

## Commands

Run from the workspace root (`C:\Src\RIoT2`), in PowerShell:

```powershell
dotnet build .\RIoT2.Net.Orchestrator\RIoT2.Net.Orchestrator.csproj
dotnet test .\RIoT2.Tests\RIoT2.Tests.csproj    # Cross-repository tests; project-references Core, Orchestrator and InfluxDB
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
Dockerfile writes `Data/Manifest.json` from the `VERSION` and `IMAGE_TAG` build arguments.
The cross-repository tests in RIoT2.Tests build this repository's `main` nightly.

## Layout

| Path | Contents |
|---|---|
| `Program.cs` | ASP.NET Core host, Serilog, CORS, JSON profiles, DI, health checks and hosted services |
| `Controllers/` | Anonymous REST controllers for nodes, templates, state, dashboard, variables, commands, reports and Matter |
| `CustomJsonSettings/` | `OrchestratorJsonOptions` (controller JSON settings) and the `json-naming-policy: pascal\|lower` MVC formatter profiles |
| `openapi/riot2-orchestrator.json` | Generated OpenAPI document (the REST route contract), committed |
| `Services/OrchestratorConfigurationService.cs` | Environment-variable configuration, stored node/dashboard configuration and template lookup |
| `Services/OrchestratorMqttService.cs` | MQTT subscriptions/publications, state updates, Matter report mirroring and Elsa gRPC forwarding |
| `Services/Persistence/` | File-backed `StoredObjects/<Type>/<Id>.json` object store |
| `Services/Matter/` | Matter Control Bridge configuration, endpoint composition, commissioning and report/command bridging |
| `Services/WorkflowTriggerClient.cs` | Reused gRPC client/channel with a five-second workflow trigger deadline |
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
  `Services/OrchestratorConfigurationService.cs`, `Dockerfile` and
  `.github/workflows/release.yml`.
- [configuration.md](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/configuration.md):
  `Services/OrchestratorConfigurationService.cs`, `Services/StoredObjectService.cs`,
  `Services/Persistence/FileObjectStore.cs` and `Services/Matter/`.

The gRPC service/message schema in `Protos/riot_trigger.proto` must stay compatible with
`RIoT2.Elsa/RIoT2.Elsa.Server/RIoT/Protos/riot.proto`; only the generated C# namespace differs
today.

## Rules

- Follow [ADR 0002](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/adr/0002-isolated-network-security-model.md):
  APIs are anonymous by design, CORS is permissive, and mandatory auth must not be added.
- New endpoints must not change state on `GET`. Existing state-changing `GET`s are legacy and are
  listed in [http-api.md](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/http-api.md).
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
- Current package pins are `RIoT2.Core` `1.1.0`, `RIoT2.Matter` `1.0.0` and
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
  the Docker context (backlog item
  [20](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/backlog/open-issues.md),
  fixed 2026-10-12). Add any new runtime folder to both.
- `Properties/launchSettings.json`, `StoredObjects/`, `Logs/` and `MatterCredentials/` can contain
  local values or secrets. Do not copy their contents into docs, images or examples.
- Workflow delivery is volatile: reports are dropped if no workflow node is online, if the gRPC
  call fails, or on shutdown. The in-memory workflow queue has capacity 1000 and no replay.
- `POST /api/command/execute` returns after the command is published and state is set. It does not
  prove the device executed the command.
- `OrchestratorMqttService` publishes retained `{"isOnline":true}` on `riot2/orchestrator/online`
  on every connect. Its last will (set with `MqttClient.SetLastWill` before `Start`) and its
  graceful stop replace it with retained `{"isOnline":false}`.
- .NET MQTT presence messages and last wills are not retained, while firmware presence is
  retained (divergence D1).
- Some legacy `GET` endpoints mutate state (`delete`, `history/reset`, Matter commissioning,
  refresh and reset). Keep them only for compatibility.
- `Json.Serialize` camel-cases dictionary keys. Configuration `deviceParameters` should use
  camelCase keys because device lookups are case-sensitive.
- Docker CI injects `Data/Manifest.json` with `docker create`, `docker cp` and `docker commit`.
  That is existing release behavior, not a pattern to expand.

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
  [M8](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/plans/m08-dotnet10-migration.md)
  (target-framework migration completed; nullable and threading-analyzer practice steps remain open).
- Designs
  [7.1](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/design/reliable-delivery.md),
  [7.2](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/design/desired-state-configuration.md) and
  [7.5](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/design/security-mode.md).
