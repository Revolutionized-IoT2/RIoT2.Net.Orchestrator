# RIoT2.Net.Orchestrator

Central ASP.NET Core service for the [RIoT2](https://github.com/Revolutionized-IoT2) IoT
platform. It stores the user's node, dashboard, variable and Matter bridge configuration, tracks
online nodes, routes MQTT reports and commands, and forwards accepted reports to Elsa 3 over gRPC.

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
| `Controllers/` | REST API controllers for nodes, templates, state, dashboard, variables, commands, reports and Matter |
| `Services/OrchestratorConfigurationService.cs` | Environment configuration, stored configuration and template lookup |
| `Services/OrchestratorMqttService.cs` | MQTT report/command routing, state updates and workflow forwarding |
| `Services/Persistence/FileObjectStore.cs` | File-backed JSON storage under `StoredObjects/` |
| `Services/Matter/` | Matter Control Bridge configuration, commissioning and endpoint bridging |
| `CustomJsonSettings/` | Optional `json-naming-policy` formatter profiles |
| `Protos/riot_trigger.proto` | gRPC workflow trigger client contract |
| `Dockerfile` | Multi-stage .NET 10 container image |

## Runtime configuration

The required orchestrator settings are environment variables. Startup fails if a required value is
missing or if `RIOT2_ORCHESTRATOR_URL` is not an absolute `http://` or `https://` URL.

| Variable | Required | Purpose |
| --- | ---: | --- |
| `RIOT2_ORCHESTRATOR_ID` | Yes | MQTT client id and id for orchestrator-owned variable reports |
| `RIOT2_ORCHESTRATOR_URL` | Yes | Base URL sent to nodes in `ConfigurationCommand.apiBaseUrl`; must be reachable by nodes and firmware |
| `RIOT2_MQTT_IP` | Yes | MQTT broker host/IP; .NET MQTT currently uses port 1883 |
| `RIOT2_MQTT_USERNAME` | No | MQTT username; leave empty for an anonymous broker |
| `RIOT2_MQTT_PASSWORD` | No | MQTT password |

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
root. It validates type names and ids before using them as file names. The stored node
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

The orchestrator subscribes to node reports and node presence, publishes its retained online
announcement, sends node configuration commands, publishes device commands, and publishes variable
changes as reports under the orchestrator id. The topic and payload contract is
[MQTT topics and payloads](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/mqtt-topics.md).

Reports that match configured templates update state and history, are mirrored into Matter when a
Matter endpoint is bound to the template, and are forwarded to the online Elsa workflow node over
gRPC. `WorkflowTriggerClient` reuses one channel per workflow endpoint and uses a five-second
deadline. Delivery is in-memory and not retried automatically; durable delivery is planned in
[design 7.1](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/design/reliable-delivery.md).

## Docker

The Dockerfile uses multi-stage .NET 10 images. The runtime stage is `aspnet:10.0-alpine`, runs as
the non-root .NET app user, and listens on HTTP port `8080`. The build stage stays on the Debian
`sdk:10.0` image because `Grpc.Tools` needs glibc-linked `protoc`.

Build from this repository root (`C:\Src\RIoT2\RIoT2.Net.Orchestrator`) with a package-feed token:

```powershell
docker build `
  --build-arg NUGET_AUTH_TOKEN=<github-package-token> `
  --build-arg NUGET_URL=https://nuget.pkg.github.com/Revolutionized-IoT2/index.json `
  -t riot2-orchestrator .
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

Use `/health` for container or orchestrator health checks. It is healthy only while the MQTT client
is connected to the broker. For persistent deployments, bind-mount writable directories for
`/app/StoredObjects`, `/app/Logs` and `/app/MatterCredentials`; Matter commissioning usually also
needs host networking for mDNS and IPv6.

## Build and test

From the workspace root (`C:\Src\RIoT2`):

```powershell
dotnet build .\RIoT2.Net.Orchestrator\RIoT2.Net.Orchestrator.csproj
dotnet test .\RIoT2.Tests\RIoT2.Tests.csproj
```

Add `-p:CI=true` to `dotnet build` to reproduce CI analyzer settings locally. Package versions are
centralized in `Directory.Packages.props`; `PackageReference` items do not carry versions.

[RIoT2.Tests](https://github.com/Revolutionized-IoT2/RIoT2.Tests) references Core, the
orchestrator and the InfluxDB connector as projects, so those repositories must be checked out
next to this one.

## Versions and releases

- Release notes are in [CHANGELOG.md](CHANGELOG.md).
- To release, push a tag `x.y.z`. CI builds and pushes
  `ghcr.io/revolutionized-iot2/riot2-orchestrator:latest` and `:<tag>`.
- The project references `RIoT2.Core` `0.1.45`, `RIoT2.Matter` `0.1.15` and
  `RIoT2.Matter.ControlBridge` `0.1.15` as NuGet packages. Until those packages are published,
  restore with `C:\Src\RIoT2\.localfeed` as an extra source.

## Contributing

- Instructions for AI coding agents: [AGENTS.md](AGENTS.md).
- Platform documentation: [.github/docs](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/README.md).

## License

See [LICENSE](LICENSE).
