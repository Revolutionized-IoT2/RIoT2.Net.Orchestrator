# Changelog

All notable changes to `RIoT2.Net.Orchestrator`. A version is released by pushing a git tag. CI
then builds and pushes the Docker image to GitHub Container Registry.

## [Unreleased]

## [1.14.0] - 2026-11-02

Typed device options ([I30](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/increments/phase-2.md#i30-typed-device-options-m)), with RIoT2.Core 1.16.0. No port, user, volume, variable or
stored-format changes.

- Added: `GET /api/nodes/{id}/device/templates` passes on the templates' `parameterDefinitions`.
- Changed: `POST /api/nodes/configuration` drops `parameterDefinitions` from every device before it
  stores and hashes the configuration, so stored files and hashes stay as they were.

## [1.13.1] - 2026-11-01

- Fixed: the image starts again (1.13.0 failed with `FileNotFoundException` for
  `Microsoft.Bcl.AsyncInterfaces`): built with RIoT2.Core 1.14.1, whose `RIoT2.Core.Mqtt` declares
  that dependency ([I28](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/increments/phase-2.md#i28-narrow-references-and-generated-models-m)). Don't run 1.13.0.

## [1.13.0] - 2026-11-01

Narrow Core references ([I28](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/increments/phase-2.md#i28-narrow-references-and-generated-models-m), plan M1 step 4), with RIoT2.Core 1.14.0. No route, port, user,
volume or JSON changes.

- Changed: references `RIoT2.Core.Contracts`, `RIoT2.Core.Http`, `RIoT2.Core.Mqtt` and
  `RIoT2.Core.Hosting` instead of the `RIoT2.Core` facade, so the image no longer carries the
  device SDK or the node runtime. Quartz (`CronExpression` for `RIOT2_BACKUP_SCHEDULE` and
  `/api/nodes/validatecron`) is a direct reference, at the same 3.13.1.

## [1.12.0] - 2026-11-01

Core package split ([I27](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/increments/phase-2.md#i27-core-package-split-m)), with RIoT2.Core 1.13.0. No route, port, user, volume or JSON
changes.

- Changed: `IOrchestratorConfigurationService`, `IOrchestratorMqttService`, `IMessageStateService`
  and `MessageStateService` are the orchestrator's own (namespace `RIoT2.Net.Orchestrator.Services`);
  RIoT2.Core's copies are `[Obsolete]` (plan M1 step 1).
- Changed: RIoT2.Core and RIoT2.Core.Hosting 1.13.0; the orchestrator still references the
  `RIoT2.Core` facade (narrower references are I28).

## [1.11.0] - 2026-11-01

Typed persistence ([I26](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/increments/phase-2.md#i26-typed-persistence-m), plan M2 steps 4–5), with RIoT2.Core 1.12.0. No route, port,
user or volume changes. The stored files keep their format, so configurations keep their hashes,
and a downgrade to 1.10.0 reads them.

- Changed: `StoredObjectService` is typed. It implements the orchestrator's `IStoredObjectStore`
  (`GetAll<T>`, `Get<T>(id)`, `Save`, `Delete` and `DeleteAll` for `IStoredObject` types) and
  raises a typed `StoredObjectChange` (type, id, object, operation) instead of Core's `dynamic`
  `StoredObjectEvent`. There is no `dynamic` and no Newtonsoft in the orchestrator any more.
- Changed: `StoredObjects/` is read and written with System.Text.Json (`StoredObjectJson`), not
  with Newtonsoft's type-name handling. Node configurations keep their nulls, a `ValueModel` is
  still written as `{"type":n}`, and emoji are written as they are (Core 1.12.0). The node and
  dashboard JSON posted by the UI is read with `Json.Deserialize`.
- Added: legacy `Rule` files, from the retired rule engine, are moved to `StoredObjects/.archive/Rule/`
  at startup and after a restore, with a warning. The archive is not loaded, backed up or replaced.
- Deprecated: the `json-naming-policy` request header (`pascal`, `lower`). A response to a request
  with it carries `Deprecation: true`, and the first use of each value is logged as a warning.
  Core 2.0 (I45) removes it; use the default camelCase.
- Removed: the `JObjectConverter` shim in the controller JSON settings; nothing yields a `JObject`.

## [1.10.0] - 2026-11-01

System.Text.Json on the wire ([I25](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/increments/phase-2.md#i25-systemtextjson-on-the-wire-m)), with RIoT2.Core 1.11.0. No route, port, user or
volume changes, and the JSON is the same: configurations keep their hashes, so no node re-applies
anything.

- Changed: responses, MQTT messages and the command outbox are written and read with Core's
  System.Text.Json `Json` helpers. Stored configuration (`StoredObjects/`) is still read and
  written with Newtonsoft's type-name handling until typed persistence (I26).
- Fixed: an unreadable presence message is ignored with a warning again; it now fails with
  `System.Text.Json.JsonException`.

## [1.9.0] - 2026-11-01

Orchestrator restructure ([I23](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/increments/phase-2.md#i23-orchestrator-restructure-m), plan M3 steps 1–3).
No route, port, user or volume changes: the OpenAPI document has the same paths, operations and
schemas (only their order changed).

- Changed: every template listing comes from one `ITemplateCatalog` (`Services/Templates/`).
  `GET /api/nodes/report/templates` now includes each template's `model`, like
  `/api/report/templates`, and `GET /api/nodes/variable/templates` includes `address` (empty), like
  `/api/variable/templates`. Golden files: RIoT2.Core `contracts/golden/http/templates-*.json`.
- Changed: `NodesController` is split by resource, with the same routes and OpenAPI tags:
  `NodesController` (configurations and online status), `TemplatesController` (every template
  listing, also those of `ReportController`, `CommandController` and `VariableController`),
  `StateController`, `VariablesController` (replaces `VariableController`), `PluginsController` and
  `CronController`. No controller has more than about 150 lines.
- Changed: without a stored state, `GET /api/nodes/report/{id}/state` asks only the node that owns
  the report for its templates, instead of every online node.
- Changed: `MatterBridgeService` keeps the lifecycle and delegates to `MatterCredentials`
  (credentials directory, fabric store key and fabric table), `MatterProvisioning` (onboarding
  bundle), `MatterHostNetwork` (network interfaces) and `MatterBridgedEndpoints` (endpoint
  composition, report routing and reachability).
- Security: pins `System.Security.Cryptography.Pkcs` 10.0.14. RIoT2.Matter 1.0.0 brings 10.0.12,
  which has two denial-of-service advisories (GHSA-846f-f764-76mx, GHSA-2fmv-rv2c-59x7).

## [1.8.0] - 2026-11-01

Metrics ([I22](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/increments/phase-2.md#i22-metrics-m), design 7.4 phase 4), with RIoT2.Core 1.10.0.
No port, user or volume changes.

- Added: the meter `RIoT2.Orchestrator`: `riot2.reports.received` and `riot2.reports.unknown_template`
  (by node), `riot2.workflow.deliveries` (by outcome), `riot2.outbox.pending` and
  `riot2.outbox.oldest_age_seconds` (by kind), `riot2.commands` and the `riot2.command.latency`
  histogram (by outcome), `riot2.nodes.online` (by type) and `riot2.nodes.out_of_sync` (by state).
- Added: `OTEL_EXPORTER_OTLP_ENDPOINT` exports them, with `riot2.mqtt.*`, ASP.NET Core and runtime
  metrics, over OTLP; `RIOT2_TRACING=true` adds traces. Unset, nothing is exported.

## [1.7.0] - 2026-11-01

Backup and restore ([I21](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/increments/phase-2.md#i21-backup-and-restore-m), design 7.4 phase 3).
No port or user changes; mount a volume at `/app/Backups` for scheduled backups.

- Added: `POST /api/v2/backup` returns a zip of `StoredObjects/` (without `outbox.db`) and the
  Matter credentials folder, with a `backup-manifest.json` (format version, time, kind,
  orchestrator id, versions, and each file's size and SHA-256).
- Added: `POST /api/v2/restore` (zip body) and `POST /api/v2/backups/{name}/restore`. The whole
  archive is checked first: a damaged, changed or foreign zip, an unsafe path, a file that doesn't
  load or a newer format is rejected with `400` and every reason, and nothing changes. Otherwise
  the current configuration is saved as `Backups/riot2-pre-restore-<time>.zip`, the Matter bridge
  stops, the files are replaced while saves wait, every cache reloads, the bridge starts again,
  and every node gets a configuration command.
- Added: `RIOT2_BACKUP_SCHEDULE` (a Quartz cron expression with seconds; unset is off) writes
  `Backups/riot2-backup-<time>.zip`; `RIOT2_BACKUP_RETENTION` (default 7) is how many backups are
  kept. `GET /api/v2/backups` lists them with the schedule and the last run, and
  `GET /api/v2/backups/{name}` downloads one.
- Changed: the image creates `/app/Backups`. The OpenAPI document describes the zip bodies.

## [1.6.0] - 2026-11-01

Consistent logging ([I20](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/increments/phase-2.md#i20-consistent-logging-s), design 7.4 phase 2), with RIoT2.Core 1.9.0.

- Changed: logging comes from `AddRIoT2Logging()` (RIoT2.Core.Hosting). The console and
  `Logs/RIoT2.log` get the same lines, in a new format (`<time> [INF] <category>: <message>`),
  or compact JSON with `RIOT2_LOG_FORMAT=json`; `RIOT2_LOG_LEVEL` sets the level. Files roll
  daily and at 50 MB, 14 are kept (before: daily, no limit). ASP.NET Core's own messages, which
  went to the console only and in another format, go through the same pipeline.
- Added: each outbox delivery runs in a `CorrelationId` (commands) or `MessageId` (workflow
  triggers) log scope; queueing a command (`Debug`) and its result are logged in it too.
- Removed: the direct Serilog package references.

## [1.5.0] - 2026-10-14

Desired-state configuration ([I16](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/increments/phase-2.md#i16-desired-state-configuration-for-net-nodes-m),
[design 7.2](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/design/desired-state-configuration.md) phases 2–3). Built on `RIoT2.Core` 1.8.0. No port, user, volume or
variable changes; update in any order with the nodes.

- Added: every node configuration has a `revision` (counts content changes) and a `hash`, also on
  each device entry. Configurations saved before get revision 1. `GET /api/nodes/{id}/configuration`
  sends the hash as `ETag` and answers `If-None-Match` with `304`, and configuration commands carry
  `revision` and `hash`, so a Node 1.5.0 skips a configuration it already runs.
- Added: the orchestrator subscribes to `riot2/node/+/status`. `GET /api/nodes` reports
  `desiredRevision`, `appliedRevision`, `syncState` (`inSync`, `pending`, `failed`, `legacy`) and
  `lastError`; a failed configuration is logged. Deleting a node clears its retained status.
- Fixed: `GET /api/nodes/{id}/configuration?state=true` wrote the last command values into every
  stored node configuration, so nodes downloaded them and could restart devices. It now returns a
  copy.
- Fixed: a configuration command sent right after a save could carry the configuration from before
  the save.
- Changed: an empty presence payload (a cleared retained message) is ignored without a warning.

## [1.4.0] - 2026-10-14

Command results and API v2 ([I15](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/increments/phase-2.md#i15-command-results-and-api-v2-m),
[design 7.1](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/design/reliable-delivery.md) phase 2). Built on `RIoT2.Core` 1.7.0. Update before
RIoT2.UI 1.2.0, which uses the new `/api/v2` routes; nodes can be updated in either order.

- Changed: every command goes through the outbox (`CommandOutbox`, one dispatcher per node), from
  `POST /api/command/execute`, Elsa and the Matter bridge alike. It waits while its node is offline
  and expires after `RIOT2_OUTBOX_COMMAND_TTL` (new, default 30 s); before, it was published once
  and lost if the node was offline. `POST /api/command/execute` still returns 200 once queued.
- Added: command results. Commands carry a `correlationId`; a node that advertises
  `command-result/1` (Node 1.4.0) must answer on `riot2/node/{id}/command/result` within five seconds,
  or the command is sent again with the same id. `executed` and `duplicate` (executed before, its
  answer lost) set the command state, `failed` and `unknownCommand` are final, `rejected` is retried. Other nodes are sent each
  command once (`sent`), as before.
- Added: `POST /api/v2/commands` (`{ id, value, ttlSeconds?, wait? }`, 202 or with `wait` 200 and
  the final status) and `GET /api/v2/commands/{correlationId}`.
- Added (backlog item 7): `DELETE /api/v2/nodes/{id}`, `DELETE /api/v2/variables/{id}`,
  `DELETE /api/v2/reports/history`, `POST /api/v2/matter/commissioning/open`,
  `POST /api/v2/matter/devices/refresh` and `POST /api/v2/matter/reset`. The old state-changing
  `GET` routes still work; they are deprecated, answer with a `Deprecation` header and log their
  first use.
- Changed: the outbox health check and summary include commands (`kind` `command`, `target` the node id).

## [1.3.1] - 2026-10-14

Image for the compose install ([I12](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/increments/phase-0-1.md#i12-one-command-install-s)). No code, port, user or variable changes.

- Added: a Docker `HEALTHCHECK` on `/health` (MQTT connection and outbox), on the port in
  `ASPNETCORE_HTTP_PORTS`, so `docker ps` and compose's `service_healthy` see the orchestrator's
  state.

## [1.3.0] - 2026-10-14

Durable workflow delivery ([I11](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/increments/phase-0-1.md#i11-durable-workflow-delivery-m),
[design 7.1](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/design/reliable-delivery.md) phase 1).
Built on `RIoT2.Core` 1.5.0. Upgrade RIoT2.Elsa to 1.3.0 first, so that redelivered triggers are
deduplicated; an older Elsa works too, but may run a redelivered trigger twice.

- Changed: workflow triggers go through a durable outbox, `StoredObjects/outbox.db` (SQLite) on the
  existing volume. A trigger is no longer dropped when Elsa is slow, restarting or not yet online,
  or when the orchestrator restarts. It is retried with backoff (1 s, 2 s, 4 s … at most 30 s), in
  order, and expires after its time-to-live. Before, a failed delivery was logged and dropped, a
  report was dropped while no workflow node was online, and queued deliveries were discarded on
  shutdown.
- Changed: a delivery attempt's gRPC deadline is five seconds or the trigger's expiry, whichever
  comes first, so a trigger never reaches Elsa after its time-to-live.
- Added `RIOT2_OUTBOX_WORKFLOW_TTL`, the trigger time-to-live in seconds (1–86400, default 300).
- Added `GET /api/v2/outbox/summary`: messages per status, the oldest pending message and the last
  ten errors.
- Changed: `/health` reports `Degraded` when more than 500 triggers are pending or one is older than
  half its time-to-live (new `outbox` check).
- Changed: each trigger carries its outbox id in the new gRPC field `TriggerRequest.message_id`
  (wire-compatible), which RIoT2.Elsa 1.3.0 deduplicates on.
- Changed: the orchestrator reports domain events to an `IAutomationProvider` (ADR 0008).
  `ElsaAutomationProvider` is the only implementation; `OrchestratorMqttService` no longer calls the
  gRPC client.
- Removed the volatile workflow queue and its log messages "Workflow delivery queue is full",
  "Could not process report … because no workflow node is online" and "Discarded … pending
  workflow deliveries during shutdown".

## [1.2.0] - 2026-10-13

MQTT client robustness ([I08](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/increments/phase-0-1.md#i08-mqtt-client-robustness-m)). Built on `RIoT2.Core` 1.3.0. No deployment change is needed.

- Changed: the orchestrator publishes and subscribes at QoS 1, keeps at most 10,000 outgoing messages
  while the broker is unreachable (the oldest are dropped and logged), and logs a failing MQTT
  handler instead of losing it.
- Added `RIOT2_MQTT_PORT` (default 1883) for a broker on another port.
- Changed: received MQTT messages are queued for processing asynchronously; when the queue is
  full, the client stops reading and QoS 1 redelivers.

## [1.1.0] - 2026-10-13

Typed configuration ([I07](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/increments/phase-0-1.md#i07-typed-configuration-s)), the route contract and golden files (I05), and fixes found by the
end-to-end tests (I06). Built on `RIoT2.Core` 1.2.0. No deployment change: the variable names are
the same.

- Changed: `OrchestratorOptions` reads and validates every setting at startup, before anything uses
  it. A start with missing or invalid settings fails with one message that lists them all, for
  example `Invalid orchestrator configuration: RIOT2_ORCHESTRATOR_ID is required; RIOT2_MQTT_IP is
  required.` `OrchestratorConfigurationService` receives `IOptions<OrchestratorOptions>`, and
  `OrchestratorConfigurationService.ValidateConfiguration` is gone. The settings can also come from
  `appsettings.json` or the command line; environment variables still take precedence over
  `appsettings.json`.

Route contract and golden files ([I05](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/increments/phase-0-1.md#i05-golden-files-and-route-contract-m)). No deployment change.

- Added `openapi/riot2-orchestrator.json`, the OpenAPI document of the REST routes, generated by
  every build (`Microsoft.AspNetCore.OpenApi`, `Microsoft.Extensions.ApiDescription.Server`) and
  committed. CI fails when a build changes it; the UI tests check their URLs against it. It isn't
  served at runtime and isn't part of the image.
- Fixed: stopping the orchestrator threw `ObjectDisposedException`, because the Matter bridge is
  registered for two interfaces and was disposed twice. `DisposeAsync` is now idempotent.
- Changed: the controller JSON settings are in `OrchestratorJsonOptions`, shared by the three
  naming profiles and the golden-file tests. Output is unchanged.
- Fixed: concurrent calls of `OrchestratorMqttService.Stop` (a host stopped by its owner and by
  `Run`) ran the shutdown twice and could race with disposal. They now share one stop
  ([I06](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/increments/phase-0-1.md#i06-in-process-end-to-end-harness-m), found by the end-to-end tests).
- Fixed: an empty or unreadable payload on `riot2/node/{id}/online` threw inside the MQTT queue
  and was logged as an error; an empty payload, which clears a retained message, also caused a
  `NullReferenceException`. Such a message is now ignored with a warning that shows the payload.

## [1.0.2] - 2026-10-12

- Fixed the orchestrator presence after a crash (contract divergence D2). The last will and the
  graceful stop now publish retained `{"isOnline":false}` on `riot2/orchestrator/online`, so a
  late subscriber no longer sees a stale `true`. Previously the last will went to
  `riot2/node/{orchestratorId}/online`. Subscribers already announce themselves on any message on
  this topic, so no other component changes.
- Changed the Core pin to `RIoT2.Core` 1.1.0.

## [1.0.1] - 2026-10-12

Includes the I01 publish fix (backlog item 20). No deployment change.

- Changed the Dockerfile: the GitHub Packages token is the BuildKit secret `nuget_token` and is no
  longer stored in an image layer (`docker build --secret id=nuget_token,env=NUGET_AUTH_TOKEN`).
  The `NUGET_AUTH_TOKEN` build argument is gone. `Data/Manifest.json` is written in the image from
  the `VERSION` and `IMAGE_TAG` build arguments instead of being injected with `docker commit`.
- Fixed: `FileObjectStore` rejects `\` in type names and ids on Linux too, not only on Windows.
- CI: every push runs `ci.yml` (build, image build, secret scan); a tag runs `release.yml`, which
  validates before it pushes the image.
- Fixed (backlog item 20): `dotnet publish` from a developer checkout no longer copies
  `StoredObjects/` (node configurations with device credentials), `MatterCredentials/`, `Logs/` or
  `Data/` into the output, and `.dockerignore` also excludes `Data`.

## [1.0.0] - 2026-10-12

Built on `RIoT2.Core` 1.0.1 and .NET 10. See the
[upgrade notes](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/guides/upgrading.md).

- Changed target framework and Docker runtime image to .NET 10; the build image remains Debian
  `sdk:10.0` for `Grpc.Tools`/`protoc`.
- Changed package pins to `RIoT2.Core` 1.0.1 and `RIoT2.Matter` /
  `RIoT2.Matter.ControlBridge` 1.0.0.
- Updated gRPC dependencies to `Grpc.Net.Client` 2.84.0, `Grpc.Tools` 2.84.0 and
  `Google.Protobuf` 3.36.2 through central package management.
- Documentation: `AGENTS.md` is the AI instruction file, `CLAUDE.md` imports it, and repository
  guidance moved from the README and old instruction files into the standard layout.

## Earlier versions

Tags `0.1.0`–`0.1.18`. See `git log` and the tags; there are no release notes for them.
