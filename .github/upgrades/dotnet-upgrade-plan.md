# .NET 9.0 Upgrade Notes

This project has already been upgraded to .NET 9. Use this file as a maintenance checklist for future upgrades.

## Current state

- `RIoT2.Net.Orchestrator.csproj` targets `net9.0`.
- Runtime Docker base image: `mcr.microsoft.com/dotnet/aspnet:9.0-alpine`.
- Build Docker base image: `mcr.microsoft.com/dotnet/sdk:9.0`.
- The SDK image is intentionally Debian-based, not Alpine: `Grpc.Tools` uses a glibc-linked `protoc`.
- The runtime image runs as the non-root .NET app user and listens on HTTP port `8080`.

## Future upgrade checklist

1. Validate that the target .NET SDK is installed.
2. Check any `global.json` for SDK compatibility.
3. Update `RIoT2.Net.Orchestrator.csproj` target framework and package versions.
4. Update Docker runtime and SDK base images. Keep a glibc SDK image unless `Grpc.Tools` supports musl.
5. Confirm Docker still creates writable `StoredObjects`, `Logs`, and `MatterCredentials` folders for the non-root user.
6. Run `dotnet build .\RIoT2.Net.Orchestrator.csproj`.

Do not add default MQTT credentials or persisted runtime data to the Docker image.
