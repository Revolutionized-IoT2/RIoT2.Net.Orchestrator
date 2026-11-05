# AGENTS.md — RIoT2.Net.Orchestrator

Applies to: this repository. Read the platform guide first:
[.github/AGENTS.md](https://github.com/Revolutionized-IoT2/.github/blob/main/AGENTS.md). It covers the
workspace map, platform-wide rules and the documentation rules. In the local workspace, every
`https://github.com/Revolutionized-IoT2/<Repo>/blob/main/<path>` link is the file
`C:\Src\RIoT2\<Repo>\<path>`; read the local file instead of fetching the URL.

## What this is

An ASP.NET Core service targeting .NET 10. It is the hub of the RIoT2 platform: it stores node,
dashboard, variable and Matter bridge configuration, tracks online nodes, routes MQTT reports and
commands, and runs the Elsa 3 workflows through a durable SQLite outbox: in process
(`RIOT2_WORKFLOW_MODE=embedded`, the default) or on the Elsa container over gRPC (`remote`). It serves
the UI's workflow designer (`/api/workflows`) and the Run LLM connections (`/api/llm`)
([ADR 0016](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/adr/0016-integrated-workflows-and-llm.md)), and an optional MCP server for AI assistants (`/mcp`,
with an authenticated remote listener; [ADR 0018](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/adr/0018-mcp-server-and-remote-ai-access.md)). It publishes the Docker image
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

Build and run the container as in the README's [Docker](README.md#docker) section; the package-feed
token is a BuildKit secret, never a build argument. CI and releases: [Versions and releases](README.md#versions-and-releases)
and the [CI guide](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/guides/ci.md). The
Dockerfile writes `Data/Manifest.json` from the `VERSION` and `IMAGE_TAG` build arguments, and has
a `HEALTHCHECK` on `/health` that reads the port from `ASPNETCORE_HTTP_PORTS`.
The cross-repository tests in RIoT2.Tests build this repository's `main` nightly; the end-to-end
tests run `Program.cs` itself, so a startup change that needs a new environment variable or service
must keep them passing.

## Layout

The file map is the [Contents](README.md#contents) table in the README; update it when you add a folder or a
service area. What it doesn't say:

- `Services/OrchestratorMqttService.cs` is the outbox's `ICommandTransport`; `Services/Commands/` has one
  `OutboxDispatcher` per node over `Services/Outbox/` (`StoredObjects/outbox.db`, FIFO per target, backoff, TTL).
- `Services/Automation/`: `OutboxAutomationProvider` stores triggers and runs their dispatcher as a hosted
  service; `Services/WorkflowTriggerClient.cs` (gRPC, five-second deadline) serves only `ElsaAutomationProvider`.
- `Services/Llm/` protects API keys with Data Protection and keeps usage in `StoredObjects/llm-usage.db`.
  `Services/Mcp/`: `McpServerSetup` (remote port gate, JWT profiles), `McpGuard` (filters and audit), `Tools/`.
- `Services/Persistence/`: `StoredObjects/<Type>/<Id>.json`, `IObjectStoreArchive` (export, replace,
  `.archive/`) and `LegacyStoredObjects`. `Services/Health/NodeHistory` is in memory and bounded.
- `Services/OrchestratorMetrics.cs`: the names are in the hub's `contracts/metrics.md`, pinned by RIoT2.Tests.
- `.github/workflows/ci.yml` runs on every push, `release.yml` on a tag.
## Contracts implemented here

Change code and hub contracts together. Per-repository docs link to the hub; don't copy route,
topic or environment-variable tables here.

- [http-api.md](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/http-api.md):
  `Controllers/`, `Program.cs`, `CustomJsonSettings/Formatters.cs`, `/health`, and
  `Protos/riot_trigger.proto`.
- [mcp.md](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/mcp.md):
  `Controllers/McpController.cs` and `Services/Mcp/` (the tools, their groups and the remote listener).
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
- [dashboard.md](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/dashboard.md): the dashboard ids in
  `Services/OrchestratorConfigurationService.cs`, and the templates' `schema` in `Services/Templates/TemplateCatalog.cs`.

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
  `Operator` commands, `Admin` writes; the end-to-end suite fails otherwise. No ad-hoc auth. Only
  `GET /api/oauth/callback` is `[AllowAnonymous]` (its `state` is the credential).
- Outbound calls go through an `IHttpClientFactory` client with `OutboundUrlPolicyHandler`;
  only clients to RIoT2 services get `ApiKeyHandler`.
- Validate every input, even on an isolated network.
- Keep REST routes, MQTT topics, JSON payloads and the gRPC proto additive and compatible with
  `RIoT2.Core`, `RIoT2.UI`, `RIoT2.Elsa`, firmware and connectors.
- Use `RIoT2.Core.Constants.Get` / `GetTopicId` for topics and Core `Json.Serialize` /
  `Json.SerializeIgnoreNulls` for wire payloads that use Core models.
- Keep the Core and Matter packages as package references: the Dockerfile restores from GitHub
  Packages. Pins: `RIoT2.Core.Contracts`, `.Http`, `.Mqtt` and `.Hosting` `2.4.0` (never
  `.Node`; Quartz is a direct reference), `RIoT2.Elsa.Workflows` `2.1.0`, `RIoT2.Matter` and `.ControlBridge` `1.1.0`, and the
  transitive `System.Security.Cryptography.Pkcs` `10.0.14`. `.localfeed` is only for unreleased versions.
- `FileObjectStore` must validate type names and ids as file names. Never build persistence paths
  from raw ids.
- Every dashboard page, card and element keeps a stable `id` (Core 2.2.0): `OrchestratorConfigurationService`
  creates a missing one on load and save, and never changes an existing one. Card `kind` and `settings` are
  stored as the UI sends them. Template listings and the dashboard's templates carry the device's `schema`.
- Matter generated credentials and the fabric store must stay under a content-root-relative
  `CredentialsDirectory` (default `MatterCredentials`); reject absolute or escaping paths.
- Bridged endpoint ids are stable (ADR 0010): never prune `MatterBridgeState.EndpointMap`; a new
  endpoint takes `NextUnusedEndpointId` (`MatterEndpointStabilityTests` in RIoT2.Tests).
- A bridged thermostat's setpoint limits come from the bound setpoint commands' schema range
  (`MatterEndpointComposer.GetThermostatLimits`), not from the template: Core's `MatterEndpointTemplate` has
  no such property, and adding one would need a Core release and a Node that has it before a plugin could
  use it. The limits are in the endpoint signature, so a changed range composes the endpoint again.
- Images must not contain credentials, IDs, IPs, `StoredObjects`, logs or local settings.
- Keep the image on port `8080` and the non-root .NET app user.
- Elsa 3 is the only automation engine; don't reintroduce the rule engine. The workflow mode is read
  once at start; register only one mode's provider and engine, and keep `/api/workflows` the same in
  both (the UI doesn't know the mode). Activities never get an API key: Run LLM calls `ILlmRunner`.
- Reach automation only through `IAutomationProvider` (ADR 0008); nothing outside
  `Services/Automation/` calls `WorkflowTriggerClient`. A trigger must be in the outbox before
  `ReportReceivedAsync` returns, and its outbox id is its `message_id`: never send a redelivery with
  a new id.
- Every device command goes through `ICommandOutbox` (`OrchestratorMqttService.ExecuteCommand`
  and `CommandPlanService` enqueue). Its outbox id is its `correlationId`; a redelivery must repeat it, because nodes run an
  id only once. Set the command state only on `executed` or `duplicate` (or on `sent` to a node
  without `command-result/1`).
- The outbox SQL is plain `Microsoft.Data.Sqlite`; no EF Core. A schema change raises
  `PRAGMA user_version` in `OutboxStore` and must open an existing `outbox.db`.
- Stored types implement `IStoredObject` and go through `IStoredObjectStore`, written only with
  `StoredObjectJson`. No `dynamic` or Newtonsoft. The `json-naming-policy` header is gone (1.19.0, I45): JSON is camelCase only.
- A route change must commit the regenerated `openapi/riot2-orchestrator.json`; CI
  (`verify-generated`) fails otherwise, and the RIoT2.UI route test reads it. A change to a response
  or stored format must regenerate the golden files in `RIoT2.Core/contracts/golden` (RIoT2.Tests
  with `RIOT2_UPDATE_GOLDEN=1`).
- New persisted state goes in `StoredObjects/` (or the Matter credentials folder), so backups carry it;
  a new stored type that must load is added to `BackupService.CheckRestorable`. A restore replaces
  files under the `StoredObjectService` lock inside `IMatterBridgeService.RunStoppedAsync`.
- Logging is `AddRIoT2Logging()` (RIoT2.Core.Hosting), set by `RIOT2_LOG_*`; add no sinks,
  providers or `Logging` sections. Scope work for one message with `BeginIdScope(LogScope…)`.
- MCP (ADR 0018): each tool has `[McpServerTool(Name = …)]`, a `[McpToolGroup]` and honest hints, and
  its class is in `McpToolCatalog.ToolTypes`; `McpGuard` enforces groups, roles, the allowlist and the
  write rate. No tool deletes or reaches security, backups, LLM connections, Matter or `McpConfiguration`;
  device parameters leave redacted. The remote listener is told apart by `Connection.LocalPort`, never
  by `Host`, and always checks its token.

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
- The automation provider (`OutboxAutomationProvider`) and `CommandOutbox` are singletons and, through factories, hosted
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
- Embedded Elsa's database is `StoredObjects/elsa.sqlite.db` and the Data Protection keys are in the
  hidden `StoredObjects/.keys`: root files and hidden folders survive a configuration restore, which
  moves away every other folder. Neither is in a configuration backup.
- JsonSchema.Net (an Elsa dependency) has a global `Json` namespace that hides `RIoT2.Core.Utils.Json`.
  The package and `JsonPointer.Net`, `Json.More.Net` are referenced with `Aliases="JsonSchemaNet"`;
  use `extern alias JsonSchemaNet` where the schema types are needed.
- Elsa's HTTP endpoint activities listen at `/workflows/*` here, apart from the designer's `/api/workflows`.
- `Data/Manifest.json` is written into the image at build time. Don't make `/app/Data` a volume:
  a mount would hide it or keep an old copy. Persistent data belongs in `StoredObjects/`.
- The remote MCP port joins the host's URLs (`UseUrls`; explicit Kestrel endpoints would replace
  `ASPNETCORE_HTTP_PORTS`). `WebApplicationFactory.UseKestrel(port)` replaces them too, so the end-to-end
  harness binds both ports. `UseRIoT2McpGate` (it calls `UseRouting()`) stays before `UseRIoT2Security()`.
  `WithTools` needs an `IEnumerable<Type>`, or the SDK takes the list for a tool object.
- Device sign-in: never log or echo the OAuth `code` or `state`; post only to the stored origin. Behind the
  UI's `/api` proxy the redirect URI is on the UI's origin.

## Related work

- [Backlog items](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/backlog/open-issues.md) 2, 7, 13, 14, 17 and 18.
- [Plans](https://github.com/Revolutionized-IoT2/.github/tree/main/docs/plans) M2, M3, M4, M7 and M8.
- [Designs](https://github.com/Revolutionized-IoT2/.github/tree/main/docs/design) 7.1, 7.2, 7.4 and 7.5.
