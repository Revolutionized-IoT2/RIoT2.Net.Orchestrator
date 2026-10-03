# Changelog

All notable changes to `RIoT2.Net.Orchestrator`. A version is released by pushing a git tag. CI
then builds and pushes the Docker image to GitHub Container Registry.

## [Unreleased]

- Changed target framework and Docker runtime image to .NET 10; the build image remains Debian
  `sdk:10.0` for `Grpc.Tools`/`protoc`.
- Changed package pins to `RIoT2.Core` 1.0.1 and `RIoT2.Matter` /
  `RIoT2.Matter.ControlBridge` 0.1.15.
- Updated gRPC dependencies to `Grpc.Net.Client` 2.84.0, `Grpc.Tools` 2.84.0 and
  `Google.Protobuf` 3.36.2 through central package management.
- Documentation: `AGENTS.md` is the AI instruction file, `CLAUDE.md` imports it, and repository
  guidance moved from the README and old instruction files into the standard layout.

## Earlier versions

Tags `0.1.0`–`0.1.18`. See `git log` and the tags; there are no release notes for them.
