# Changelog

All notable changes to `RIoT2.Net.Orchestrator`. A version is released by pushing a git tag. CI
then builds and pushes the Docker image to GitHub Container Registry.

## [Unreleased]

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
