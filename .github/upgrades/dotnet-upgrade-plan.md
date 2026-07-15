# .NET 9.0 Upgrade Plan

## Execution Steps

Execute steps below sequentially one by one in the order they are listed.

1. Validate that an .NET 9.0 SDK required for this upgrade is installed on the machine and if not, help to get it installed.
2. Ensure that the SDK version specified in global.json files is compatible with the .NET 9.0 upgrade.
3. Upgrade RIoT2.Net.Orchestrator.csproj
4. Upgrade Dockerfile to use .NET 9.0 base images.

## Settings

This section contains settings and data used by execution steps.

### Project upgrade details

This section contains details about each project upgrade and modifications that need to be done in the project.

#### RIoT2.Net.Orchestrator.csproj modifications

Project properties changes:
  - Target framework should be changed from `net8.0` to `net9.0`

Other changes:
  - None

#### Dockerfile modifications

Docker base image changes:
  - Update runtime base image from `mcr.microsoft.com/dotnet/aspnet:8.0-alpine` to `mcr.microsoft.com/dotnet/aspnet:9.0-alpine`.
  - Update SDK build image from `mcr.microsoft.com/dotnet/sdk:8.0-alpine` to `mcr.microsoft.com/dotnet/sdk:9.0-alpine`.
