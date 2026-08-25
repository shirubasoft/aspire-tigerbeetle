# Agent guide

This repository contains an Aspire hosting integration for TigerBeetle, its tests, and working C# and TypeScript samples.

## Required workflow

- Run `./build.sh` before handing off a change. It restores tools and dependencies, formats, builds, tests, compiles the TypeScript sample, and packs the NuGet package.
- Use `./build.sh --containers` when changing container startup, endpoints, connection strings, persistence, health checks, AppHost code, or deployment output.
- On Windows, use `./build.ps1`. Container smoke tests run on Linux CI.
- Start AppHosts with `dotnet tool run aspire -- start --apphost <path>`. Do not use `dotnet run` for an AppHost.
- Inspect running resources with `dotnet tool run aspire -- wait`, `describe`, and `logs`. Stop them with `dotnet tool run aspire -- stop` before editing or rebuilding the AppHost.
- Never edit or commit `.aspire/modules`. `aspire restore` generates the TypeScript SDK from `samples/TypeScript/aspire.config.json`.

## Implementation rules

- Keep the versions in `global.json`, `Directory.Packages.props`, `.config/dotnet-tools.json`, and the TypeScript Aspire configuration compatible.
- Preserve the public connection-string contract: `ClusterID=<u128>;Addresses=<address>[,<address>...]`.
- TigerBeetle clients require numeric IP addresses. Samples may receive Aspire service DNS names after deployment, so they must resolve those names before constructing the official client.
- Keep `--development` consistent between `tigerbeetle format` and `tigerbeetle start`.
- The default resource is one development replica. Do not imply that a generic container deployment satisfies TigerBeetle's production topology, storage, memory-locking, or network requirements.
- Local container startup needs `seccomp=unconfined`. Deployment targets need the equivalent target-specific security configuration.
- Public C# AppHost APIs and types must carry `[AspireExport]` so the generated TypeScript SDK exposes them.
- Add tests for every public builder method and any change to the generated startup command or connection-string expression.
- Keep all samples runnable without private services or credentials. A sample health check must include a real TigerBeetle client operation, not only a TCP connection.

## Release rules

- Use Conventional Commits. `fix:` releases a patch, `feat:` releases a minor, and `feat!:` or a `BREAKING CHANGE:` footer releases a major.
- Do not publish packages manually from a workstation. The successful main-branch CI run hands its exact package artifact to the publish workflow.
- `NUGET_PUBLISH_ENABLED` must be `true` and the inherited `NUGET_API_KEY` organization secret must be available before a release can publish.
- Never commit API keys, generated package files, Aspire deployment output, generated SDK modules, build output, or dependency folders.

## Review checklist

- `dotnet format Aspire.TigerBeetle.slnx --verify-no-changes`
- `dotnet test Aspire.TigerBeetle.slnx --configuration Release`
- Both sample clients read `ConnectionStrings__tigerbeetle` and complete `lookupAccounts` against the managed resource.
- Docker Compose output contains the TigerBeetle image, data volume, format/start command, and `security_opt: seccomp=unconfined`.
- Package metadata, XML documentation, README examples, and TypeScript exports match the public API.
