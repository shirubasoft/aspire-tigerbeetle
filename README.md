# Aspire hosting integration for TigerBeetle

`Shirubasoft.Aspire.Hosting.TigerBeetle` adds a TigerBeetle replica to an Aspire AppHost as a custom container resource. It handles first-run formatting, polyglot connection properties, a TCP health check, persistent storage helpers, TypeScript AppHost exports, and Aspire container publication.

The package uses TigerBeetle `0.17.9` by default and targets Aspire `13.5.2` on .NET 10.

> [!WARNING]
> `AddTigerBeetle` creates one replica in TigerBeetle development mode. This is useful for local development, tests, and samples. It is not a production TigerBeetle topology. TigerBeetle recommends six replicas on separate machines and disks for production. See [production deployment](#production-deployment) before publishing this resource.

## Requirements

- .NET SDK 10.0.301 or a compatible .NET 10 SDK
- Aspire CLI 13.5.2
- Docker or another Aspire-supported container runtime
- Node.js 20.19 or later for the TypeScript sample

Install the Aspire CLI if it is not already available:

```bash
curl -sSL https://aspire.dev/install.sh | bash
```

## Install

Add the hosting integration to a C# AppHost:

```bash
dotnet add package Shirubasoft.Aspire.Hosting.TigerBeetle --version 1.0.0
```

Applications that connect from .NET should also reference the matching official client:

```bash
dotnet add package TigerBeetle --version 0.17.9
```

For a TypeScript AppHost, add the package to `aspire.config.json`:

```json
{
  "appHost": {
    "path": "apphost.mts",
    "language": "typescript/nodejs"
  },
  "packages": {
    "Aspire.Hosting.JavaScript": "13.5.2",
    "Shirubasoft.Aspire.Hosting.TigerBeetle": "1.0.0"
  }
}
```

Restore the AppHost after changing its package list. Aspire generates the TypeScript modules under `.aspire/modules`:

```bash
aspire restore --apphost . --non-interactive
```

Do not edit generated `.aspire/modules` files.

## C# AppHost

The smallest useful resource has persistent data and a small development cache:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var tigerBeetle = builder.AddTigerBeetle("tigerbeetle")
    .WithDataVolume()
    .WithCacheGrid("256MiB");

builder.AddProject<Projects.LedgerApi>("ledger-api")
    .WithReference(tigerBeetle)
    .WaitFor(tigerBeetle);

builder.Build().Run();
```

`WithReference` injects language-neutral connection properties. `WaitFor` waits for the TCP health check before Aspire starts the dependent resource.

To use a fixed host port, pass it to `AddTigerBeetle`:

```csharp
var tigerBeetle = builder.AddTigerBeetle("tigerbeetle", port: 3000)
    .WithDataVolume();
```

The complete C# AppHost sample is in [`samples/CSharp/Aspire.TigerBeetle.Sample.CSharp.AppHost/Program.cs`](samples/CSharp/Aspire.TigerBeetle.Sample.CSharp.AppHost/Program.cs).

## TypeScript AppHost

The package exports the same resource and builder methods to generated TypeScript modules:

```typescript
import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

const tigerBeetle = await builder.addTigerBeetle('tigerbeetle');
await tigerBeetle.withDataVolume();
await tigerBeetle.withCacheGrid('256MiB');

const client = await builder.addNodeApp('client', './client', 'dist/index.js');
await client.withReference(tigerBeetle);
await client.waitFor(tigerBeetle);
await client.withHttpEndpoint({ env: 'PORT' });

await builder.build().run();
```

The complete TypeScript AppHost is in [`samples/TypeScript/apphost.mts`](samples/TypeScript/apphost.mts).

Install the official Node client in the consuming application. Pin it to the server version unless you have checked the compatibility range in the TigerBeetle release notes:

```bash
npm install --save-exact tigerbeetle-node@0.17.9
```

## Connection properties

Aspire's polyglot resource contract exposes each value separately. For a resource named `tigerbeetle`, `WithReference` injects these variables into the consumer:

| Variable | Example | Purpose |
| --- | --- | --- |
| `TIGERBEETLE_HOST` | `127.0.0.1` | Primary endpoint host |
| `TIGERBEETLE_PORT` | `43127` | Primary endpoint port |
| `TIGERBEETLE_CLUSTERID` | `0` | TigerBeetle cluster ID |
| `TIGERBEETLE_ADDRESSES` | `127.0.0.1:43127` | Comma-separated client addresses |

The prefix comes from the resource name. A resource named `ledger` produces `LEDGER_HOST`, `LEDGER_PORT`, `LEDGER_CLUSTERID`, and `LEDGER_ADDRESSES`.

TigerBeetle clients take a cluster ID and address array as separate constructor values. They do not define a native connection-string format, so applications should read `ClusterId` and `Addresses` directly instead of parsing a package-specific string.

The resource still implements Aspire's `IResourceWithConnectionString` contract for dashboard display, manifest compatibility, and consumers that explicitly request `ConnectionStrings__tigerbeetle`. That compatibility value is `ClusterID=<u128>;Addresses=<address>[,<address>...]`; it is not the recommended client API.

TigerBeetle `0.17.9` accepts numeric IPv4 addresses, bracketed IPv6 addresses, or a bare port. It does not accept DNS names. Aspire may publish a service address such as `tigerbeetle:3000`, so applications running from a published manifest must resolve the host to a numeric address before constructing an official TigerBeetle client.

### Connect from .NET

Read the injected properties through normal .NET configuration:

```csharp
using System.Globalization;
using TigerBeetle;

var clusterId = builder.Configuration["TIGERBEETLE_CLUSTERID"]
    ?? throw new InvalidOperationException("TIGERBEETLE_CLUSTERID is required.");
var addresses = builder.Configuration["TIGERBEETLE_ADDRESSES"]
    ?? throw new InvalidOperationException("TIGERBEETLE_ADDRESSES is required.");

var replicaAddresses = addresses
    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

builder.Services.AddSingleton(new Client(
    clusterID: UInt128.Parse(clusterId, CultureInfo.InvariantCulture),
    addresses: replicaAddresses));
```

The published-container case needs a DNS-to-IP step before `new Client(...)`. The repository sample includes that step in [`samples/CSharp/Aspire.TigerBeetle.Sample.CSharp.Client/Program.cs`](samples/CSharp/Aspire.TigerBeetle.Sample.CSharp.Client/Program.cs).

Create one `Client` and share it. The official client is thread-safe and combines concurrent work into batches.

### Connect from TypeScript

```typescript
import { createClient } from 'tigerbeetle-node';

const clusterId = process.env.TIGERBEETLE_CLUSTERID;
const addresses = process.env.TIGERBEETLE_ADDRESSES;

if (!clusterId || !addresses) {
  throw new Error('TIGERBEETLE_CLUSTERID and TIGERBEETLE_ADDRESSES are required.');
}

const client = createClient({
  cluster_id: BigInt(clusterId),
  replica_addresses: addresses.split(',').map(value => value.trim())
});
```

The TypeScript sample resolves published DNS names before creating the client. See [`samples/TypeScript/client/src/index.ts`](samples/TypeScript/client/src/index.ts).

## Builder reference

The C# names appear in PascalCase. Aspire exports equivalent camelCase methods to TypeScript.

| Method | Default or example | Behavior |
| --- | --- | --- |
| `AddTigerBeetle(name, port: null)` | Dynamic host port, container port `3000` | Adds one `ghcr.io/tigerbeetle/tigerbeetle:0.17.9` container, enables development mode, adds the TCP health check, and adds `seccomp=unconfined` to local container runtime arguments |
| `WithClusterId(clusterId)` | `"0"` | Sets the unsigned 128-bit decimal cluster ID used by `format` and exposed through the `ClusterId` connection property. Cluster ID `0` is for tests and development |
| `WithReplica(replicaIndex, replicaCount)` | `0, 1` | Sets this replica's zero-based index and cluster size. The integration accepts 1 through 6 replicas |
| `WithAddresses(addresses)` | `"0.0.0.0:3000"` for server startup | Sets the ordered numeric addresses passed to `tigerbeetle start` and also uses them as client addresses |
| `WithClientAddresses(addresses)` | Aspire allocated endpoint | Overrides only the client address list. Use it when listen and client addresses differ |
| `WithCacheGrid(size)` | TigerBeetle default | Passes a value such as `256MiB` or `12GiB` to `--cache-grid` |
| `WithDevelopmentMode(enabled)` | `true` | Adds or removes `--development` from both `format` and `start` |
| `WithDataFile(path)` | `/data/{clusterId}_{replicaIndex}.tigerbeetle` | Sets an absolute data-file path inside the container |
| `WithDataVolume(name, isReadOnly)` | Generated volume name, writable | Mounts a named volume at `/data` |
| `WithDataBindMount(source, isReadOnly)` | No bind mount | Mounts a host directory at `/data` |
| `WithStatsD(ipAddress, port)` | Port `8125` | Enables TigerBeetle's experimental StatsD output with `--experimental --statsd=...`. The host must be a numeric IP |
| `WithFormatArgument(argument)` | None | Appends one shell-quoted raw argument to `tigerbeetle format` |
| `WithStartArgument(argument)` | None | Appends one shell-quoted raw argument to `tigerbeetle start` |

TypeScript represents optional .NET parameters as options objects. For example:

```typescript
const tigerBeetle = await builder.addTigerBeetle('tigerbeetle', { port: 3000 });
await tigerBeetle.withDataVolume({ name: 'tigerbeetle-data' });
await tigerBeetle.withDevelopmentMode({ enabled: false });
await tigerBeetle.withStatsD('10.0.0.20', { port: 9125 });
```

TigerBeetle keeps most advanced server flags behind `--experimental`. Prefer the typed methods for stable options. Use raw arguments only after checking the CLI for the pinned TigerBeetle release.

Standard Aspire container methods remain available. For example, `WithImageTag` can select a different TigerBeetle image. Update the official application client at the same time and follow TigerBeetle's upgrade compatibility rules.

## Startup and persistence

The container starts through `/bin/sh` and performs this sequence:

1. Check whether the configured data file exists.
2. Run `tigerbeetle format` when it is absent.
3. Execute `tini` and `tigerbeetle start` as PID 1.

By default the file is `/data/0_0.tigerbeetle`. The image has no built-in Docker volume, and `AddTigerBeetle` does not add one automatically. Container replacement loses all data unless the AppHost calls `WithDataVolume` or `WithDataBindMount`.

Use a named volume for normal development:

```csharp
var tigerBeetle = builder.AddTigerBeetle("tigerbeetle")
    .WithDataVolume("tigerbeetle-data");
```

Use a bind mount when the data file needs a known host path:

```csharp
var dataPath = Path.GetFullPath("./.tigerbeetle-data");

var tigerBeetle = builder.AddTigerBeetle("tigerbeetle")
    .WithDataBindMount(dataPath);
```

TigerBeetle needs a writable data file. A read-only mount cannot run a replica.

Cluster ID, replica index, and replica count are recorded when the file is formatted. Do not change those values while reusing a custom fixed data-file path. The exported connection properties could then describe a different cluster than the file contains.

The automatic format-if-missing behavior is intended for first startup and single-replica development. It is not a production recovery procedure. If a production replica loses its data file, use `tigerbeetle recover`. Formatting a replacement replica can violate durability guarantees.

## Health checks

The integration registers a five-second TCP health check. It connects to the first exported client address and reports healthy when the socket accepts a connection.

This proves that the process is listening. It does not prove that a replicated cluster can commit a request. TigerBeetle has no HTTP health endpoint. For production monitoring, enable StatsD and monitor at least:

- `tb.replica_status`, where `0` means normal
- `tb.replica_sync_stage`, where `0` means no state sync is in progress

Official TigerBeetle clients retry requests indefinitely. Application health checks that issue TigerBeetle requests need an external timeout and must close their client on timeout.

## Run the repository samples

Restore and build the repository first:

```bash
dotnet restore Aspire.TigerBeetle.slnx
dotnet build Aspire.TigerBeetle.slnx --configuration Release --no-restore
```

Start the C# sample in the background and wait for TigerBeetle:

```bash
aspire start \
  --apphost samples/CSharp/Aspire.TigerBeetle.Sample.CSharp.AppHost \
  --non-interactive

aspire wait tigerbeetle \
  --apphost samples/CSharp/Aspire.TigerBeetle.Sample.CSharp.AppHost \
  --timeout 120 \
  --non-interactive
```

The client URL appears in the Aspire dashboard. `GET /` returns the injected cluster ID and addresses. `GET /accounts/1` performs a TigerBeetle account lookup.

Prepare and start the TypeScript sample:

```bash
npm ci --prefix samples/TypeScript
npm ci --prefix samples/TypeScript/client
aspire restore --apphost samples/TypeScript --non-interactive
npm run build --prefix samples/TypeScript
aspire start --apphost samples/TypeScript --non-interactive
aspire wait tigerbeetle --apphost samples/TypeScript --timeout 120 --non-interactive
```

Stop either sample with its AppHost path:

```bash
aspire stop --apphost samples/TypeScript --non-interactive
```

## Publish a container manifest

The C# sample includes `Aspire.Hosting.Docker` and adds a Docker Compose environment:

```csharp
builder.AddDockerComposeEnvironment("docker");
```

Publish it with the Aspire CLI:

```bash
aspire publish \
  --apphost samples/CSharp/Aspire.TigerBeetle.Sample.CSharp.AppHost \
  --output-path artifacts/docker \
  --non-interactive
```

Aspire writes the Compose project under `artifacts/docker`. The manifest contains the TigerBeetle image, startup command, TCP binding, volume, and injected connection values.

The integration adds `--security-opt seccomp=unconfined` to local Aspire container runs. Container runtime arguments are not portable across every Aspire publisher. Check the generated deployment and target platform. Docker 25 and later blocks the `io_uring` calls TigerBeetle needs unless the container has a suitable seccomp profile. For Docker Compose, the TigerBeetle service normally needs:

```yaml
security_opt:
  - seccomp=unconfined
```

Production mode also needs permission to lock memory. TigerBeetle recommends `CAP_IPC_LOCK` or an unlimited memlock setting. `WithDevelopmentMode(false)` only removes TigerBeetle's development flag. It does not configure host networking, storage, Linux capabilities, seccomp, or replica placement for the deployment target.

## Production deployment

TigerBeetle's official production recommendation is a six-replica cluster. Each replica should have its own machine and data disk, with independent fault domains. The same ordered numeric address list must reach every replica and every client.

That model does not map cleanly to a generic scaled container service:

- TigerBeetle `0.17.9` rejects DNS names in replica and client addresses.
- The official Docker multi-replica recipe uses host networking so every replica has a stable numeric address.
- Production storage should use local NVMe where possible. Each replica needs a separate disk.
- Production replicas require Direct IO and locked memory. Do not use `--development` in production.
- TigerBeetle has no authentication. Keep its network and data files inaccessible to untrusted users and services.
- A supervisor must restart crashed replicas.
- Lost replicas require `tigerbeetle recover`, not a new formatted file.

Many managed container services cannot provide stable literal IP addresses, host networking, `seccomp=unconfined`, `CAP_IPC_LOCK`, or local disks with the required placement. Publishing this resource through Aspire confirms that the deployment model can represent the container. It does not prove that the target meets TigerBeetle's production requirements.

For production, design and review the six-replica deployment against TigerBeetle's operating guide. Use `WithDevelopmentMode(false)`, an explicit nonzero random cluster ID, persistent storage, numeric replica addresses, and a pinned image only after the target meets those requirements.

## Versioning and upgrades

The hosting package version and TigerBeetle version are independent. Version `1.0.0` of this package pins the server image to TigerBeetle `0.17.9`.

TigerBeetle clients cannot be newer than the cluster. Each TigerBeetle release states the oldest supported client and oldest upgradable replica versions. Upgrade replicas first, then clients. Do not change the container tag without reading the release notes and upgrade guide.

## Build and test

```bash
dotnet restore Aspire.TigerBeetle.slnx
dotnet build Aspire.TigerBeetle.slnx --configuration Release --no-restore
dotnet test Aspire.TigerBeetle.slnx --configuration Release --no-build
```

## Authoritative references

- [TigerBeetle 0.17.9 release](https://github.com/tigerbeetle/tigerbeetle/releases/tag/0.17.9)
- [TigerBeetle quick start](https://docs.tigerbeetle.com/start/)
- [Official Docker image and container guidance](https://docs.tigerbeetle.com/operating/deploying/docker/)
- [Deployment procedure](https://docs.tigerbeetle.com/operating/deploying/)
- [Production cluster recommendations](https://docs.tigerbeetle.com/operating/cluster/)
- [Hardware and storage requirements](https://docs.tigerbeetle.com/operating/hardware/)
- [Monitoring](https://docs.tigerbeetle.com/operating/monitoring/)
- [Replica recovery](https://docs.tigerbeetle.com/operating/recovering/)
- [Upgrade compatibility](https://docs.tigerbeetle.com/operating/upgrading/)
- [TigerBeetle in an application architecture](https://docs.tigerbeetle.com/coding/system-architecture/)
- [Official .NET client](https://docs.tigerbeetle.com/coding/clients/dotnet/)
- [Official Node.js client](https://docs.tigerbeetle.com/coding/clients/node/)

## License

[MIT](LICENSE)
