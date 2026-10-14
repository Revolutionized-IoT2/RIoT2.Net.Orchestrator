# Changelog

All notable changes to `RIoT2.Net.Orchestrator`. A version is released by pushing a git tag. CI
then builds and pushes the Docker image to GitHub Container Registry.

## [Unreleased]

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
