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
docker build --build-arg NUGET_AUTH_TOKEN=<github-package-token> --build-arg NUGET_URL=https://nuget.pkg.github.com/Revolutionized-IoT2/index.json -t riot2-orchestrator .
```

To release, push a clean git tag `x.y.z`. CI (`.github/workflows/docker-image.yml`) builds and
pushes `ghcr.io/revolutionized-iot2/riot2-orchestrator:latest` and `:<tag>`.

## Layout

| Path | Contents |
|---|---|
| `Program.cs` | ASP.NET Core host, Serilog, CORS, JSON profiles, DI, health checks and hosted services |
| `Controllers/` | Anonymous REST controllers for nodes, templates, state, dashboard, variables, commands, reports and Matter |
| `CustomJsonSettings/` | `json-naming-policy: pascal|lower` MVC input/output formatter profiles |
| `Services/OrchestratorConfigurationService.cs` | Environment-variable configuration, stored node/dashboard configuration and template lookup |
| `Services/OrchestratorMqttService.cs` | MQTT subscriptions/publications, state updates, Matter report mirroring and Elsa gRPC forwarding |
| `Services/Persistence/` | File-backed `StoredObjects/<Type>/<Id>.json` object store |
| `Services/Matter/` | Matter Control Bridge configuration, endpoint composition, commissioning and report/command bridging |
| `Services/WorkflowTriggerClient.cs` | Reused gRPC client/channel with a five-second workflow trigger deadline |
| `Protos/riot_trigger.proto` | gRPC trigger contract, kept in lockstep with Elsa's `riot.proto` |
| `Dockerfile` | .NET 10 multi-stage image; runtime is Alpine, non-root, HTTP port 8080 |
| `.github/workflows/docker-image.yml` | Tag-triggered GHCR image publish workflow |

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
  `.github/workflows/docker-image.yml`.
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
- Current package pins are `RIoT2.Core` `1.0.1` (published), `RIoT2.Matter` `0.1.15` and
  `RIoT2.Matter.ControlBridge` `0.1.15`. Use `C:\Src\RIoT2\.localfeed` as an extra NuGet source
  until the Matter versions are published.
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

## Pitfalls

- The Web SDK includes `**/*.json` as content, so `dotnet publish` from a developer checkout can
  copy local `StoredObjects/` into the publish output. This is backlog item
  [20](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/backlog/open-issues.md).
- `Properties/launchSettings.json`, `StoredObjects/`, `Logs/` and `MatterCredentials/` can contain
  local values or secrets. Do not copy their contents into docs, images or examples.
- Workflow delivery is volatile: reports are dropped if no workflow node is online, if the gRPC
  call fails, or on shutdown. The in-memory workflow queue has capacity 1000 and no replay.
- `POST /api/command/execute` returns after the command is published and state is set. It does not
  prove the device executed the command.
- `OrchestratorMqttService` publishes retained `riot2/orchestrator/online`, but the Core MQTT
  last will still targets `riot2/node/{orchestratorId}/online` (divergence D2).
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
  [17](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/backlog/open-issues.md),
  [18](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/backlog/open-issues.md) and
  [20](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/backlog/open-issues.md).
- Maintainer actions
  [MA1](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/backlog/README.md#ma1-rotate-the-leaked-credentials-and-scrub-them-from-git-history) and
  [MA2](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/backlog/README.md#ma2-cut-a-core-release-and-align-all-consumers).
- Contract divergences
  [D1 and D2](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/backlog/README.md#contract-divergences).
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
