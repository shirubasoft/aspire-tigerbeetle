using System.Globalization;
using System.Net;
using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.TigerBeetle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Hosting;

/// <summary>
/// Adds and configures TigerBeetle container resources.
/// </summary>
public static class TigerBeetleResourceBuilderExtensions
{
    /// <summary>Adds a single TigerBeetle replica in development mode.</summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The resource name.</param>
    /// <param name="port">The optional host port. Aspire assigns one when this is <see langword="null" />.</param>
    /// <returns>The TigerBeetle resource builder.</returns>
    /// <remarks>
    /// The resource formats its data file when it does not exist, then starts TigerBeetle. Development mode is on by default.
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<TigerBeetleResource> AddTigerBeetle(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        int? port = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var resource = new TigerBeetleResource(name);
        string? clientAddresses = null;

        builder.Eventing.Subscribe<BeforeResourceStartedEvent>(resource, async (@event, cancellationToken) =>
        {
            clientAddresses = await resource.ClientAddressesExpression
                .GetValueAsync(cancellationToken)
                .ConfigureAwait(false)
                ?? throw new DistributedApplicationException(
                    $"The client addresses for the '{resource.Name}' resource are unavailable.");
        });

        var healthCheckKey = $"{name}_tcp_check";
        builder.Services.AddHealthChecks().Add(new HealthCheckRegistration(
            healthCheckKey,
            _ => new TigerBeetleTcpHealthCheck(() => clientAddresses),
            failureStatus: default,
            tags: default,
            timeout: TimeSpan.FromSeconds(5)));

        return builder.AddResource(resource)
            .WithImage(TigerBeetleContainerImageTags.Image)
            .WithImageRegistry(TigerBeetleContainerImageTags.Registry)
            .WithImageTag(TigerBeetleContainerImageTags.Tag)
            .WithEndpoint(
                targetPort: TigerBeetleResource.DefaultPort,
                port: port,
                scheme: "tcp",
                name: TigerBeetleResource.PrimaryEndpointName)
            .WithIconName("Database")
            .WithEntrypoint("/bin/sh")
            .WithArgs(context =>
            {
                context.Args.Add("-ec");
                context.Args.Add(BuildStartupScript(resource));
            })
            .WithContainerRuntimeArgs(context =>
            {
                context.Args.Add("--security-opt");
                context.Args.Add("seccomp=unconfined");
            })
            .WithHealthCheck(healthCheckKey);
    }

    /// <summary>Adds a structured TigerBeetle reference to a consuming resource.</summary>
    /// <param name="builder">The consuming resource builder.</param>
    /// <param name="tigerBeetleResource">The TigerBeetle resource.</param>
    /// <returns>The consuming resource builder.</returns>
    [AspireExportIgnore(Reason = "Polyglot app hosts use the custom withReference dispatcher.")]
    public static IResourceBuilder<TDestination> WithReference<TDestination>(
        this IResourceBuilder<TDestination> builder,
        IResourceBuilder<TigerBeetleResource> tigerBeetleResource)
        where TDestination : IResourceWithEnvironment =>
        WithReference(builder, tigerBeetleResource, connectionName: null);

    /// <summary>Adds a structured TigerBeetle reference to a consuming resource.</summary>
    /// <param name="builder">The consuming resource builder.</param>
    /// <param name="tigerBeetleResource">The TigerBeetle resource.</param>
    /// <param name="connectionName">An optional name used as the injected property prefix.</param>
    /// <returns>The consuming resource builder.</returns>
    [AspireExportIgnore(Reason = "Polyglot app hosts use the custom withReference dispatcher.")]
    public static IResourceBuilder<TDestination> WithReference<TDestination>(
        this IResourceBuilder<TDestination> builder,
        IResourceBuilder<TigerBeetleResource> tigerBeetleResource,
        string? connectionName)
        where TDestination : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(tigerBeetleResource);

        var resource = tigerBeetleResource.Resource;
        connectionName ??= resource.Name;
        builder.WithReferenceRelationship(resource);

        builder.Resource.TryGetLastAnnotation<ReferenceEnvironmentInjectionAnnotation>(out var injectionAnnotation);
        var flags = injectionAnnotation?.Flags ?? ReferenceEnvironmentInjectionFlags.All;

        if (!flags.HasFlag(ReferenceEnvironmentInjectionFlags.ConnectionProperties))
        {
            return builder;
        }

        var prefix = connectionName.Length == 0
            ? string.Empty
            : $"{EncodeEnvironmentVariableName(connectionName).ToUpperInvariant()}_";

        return builder.WithEnvironment(context =>
        {
            foreach (var property in resource.GetConnectionProperties())
            {
                context.EnvironmentVariables[$"{prefix}{property.Key.ToUpperInvariant()}"] = property.Value;
            }
        });
    }

    /// <summary>Sets the unsigned 128-bit TigerBeetle cluster ID.</summary>
    /// <param name="builder">The TigerBeetle resource builder.</param>
    /// <param name="clusterId">The cluster ID in decimal form.</param>
    /// <returns>The TigerBeetle resource builder.</returns>
    /// <remarks>Cluster ID 0 is reserved for tests. Changing this setting with an existing data volume creates a different data file.</remarks>
    [AspireExport]
    public static IResourceBuilder<TigerBeetleResource> WithClusterId(
        this IResourceBuilder<TigerBeetleResource> builder,
        string clusterId)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!UInt128.TryParse(clusterId, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            throw new ArgumentException("The cluster ID must be an unsigned 128-bit decimal value.", nameof(clusterId));
        }

        builder.Resource.ClusterId = clusterId;
        return builder;
    }

    /// <summary>Sets this replica's index and the cluster replica count.</summary>
    /// <param name="builder">The TigerBeetle resource builder.</param>
    /// <param name="replicaIndex">The zero-based replica index.</param>
    /// <param name="replicaCount">The cluster replica count, from 1 through 6.</param>
    /// <returns>The TigerBeetle resource builder.</returns>
    [AspireExport]
    public static IResourceBuilder<TigerBeetleResource> WithReplica(
        this IResourceBuilder<TigerBeetleResource> builder,
        int replicaIndex,
        int replicaCount)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (replicaCount is < 1 or > 6)
        {
            throw new ArgumentOutOfRangeException(nameof(replicaCount), "TigerBeetle supports from 1 through 6 replicas.");
        }

        if (replicaIndex < 0 || replicaIndex >= replicaCount)
        {
            throw new ArgumentOutOfRangeException(nameof(replicaIndex), "The replica index must be less than the replica count.");
        }

        builder.Resource.ReplicaIndex = replicaIndex;
        builder.Resource.ReplicaCount = replicaCount;
        return builder;
    }

    /// <summary>Sets the ordered numeric addresses passed to <c>tigerbeetle start</c>.</summary>
    /// <param name="builder">The TigerBeetle resource builder.</param>
    /// <param name="addresses">Comma-separated IPv4 or bracketed IPv6 endpoints.</param>
    /// <returns>The TigerBeetle resource builder.</returns>
    /// <remarks>TigerBeetle does not accept DNS names. This value is also exposed through the <c>Addresses</c> connection property.</remarks>
    [AspireExport]
    public static IResourceBuilder<TigerBeetleResource> WithAddresses(
        this IResourceBuilder<TigerBeetleResource> builder,
        string addresses)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ValidateAddresses(addresses);

        builder.Resource.Addresses = addresses;
        builder.Resource.ClientAddresses = addresses;
        return builder;
    }

    /// <summary>Sets client addresses without changing the replica listen addresses.</summary>
    /// <param name="builder">The TigerBeetle resource builder.</param>
    /// <param name="addresses">Comma-separated IPv4 or bracketed IPv6 endpoints.</param>
    /// <returns>The TigerBeetle resource builder.</returns>
    [AspireExport]
    public static IResourceBuilder<TigerBeetleResource> WithClientAddresses(
        this IResourceBuilder<TigerBeetleResource> builder,
        string addresses)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ValidateAddresses(addresses);

        builder.Resource.ClientAddresses = addresses;
        return builder;
    }

    /// <summary>Sets the TigerBeetle grid-cache size, such as <c>256MiB</c> or <c>12GiB</c>.</summary>
    /// <param name="builder">The TigerBeetle resource builder.</param>
    /// <param name="size">A TigerBeetle byte-size value.</param>
    /// <returns>The TigerBeetle resource builder.</returns>
    [AspireExport]
    public static IResourceBuilder<TigerBeetleResource> WithCacheGrid(
        this IResourceBuilder<TigerBeetleResource> builder,
        string size)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(size);

        builder.Resource.CacheGrid = size;
        return builder;
    }

    /// <summary>Enables or disables TigerBeetle development mode for both format and start.</summary>
    /// <param name="builder">The TigerBeetle resource builder.</param>
    /// <param name="enabled">Whether development mode is enabled.</param>
    /// <returns>The TigerBeetle resource builder.</returns>
    /// <remarks>Production mode requires TigerBeetle's host, memory-locking, disk, and networking requirements.</remarks>
    [AspireExport]
    public static IResourceBuilder<TigerBeetleResource> WithDevelopmentMode(
        this IResourceBuilder<TigerBeetleResource> builder,
        bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Resource.DevelopmentMode = enabled;
        return builder;
    }

    /// <summary>Sets the data-file path inside the container.</summary>
    /// <param name="builder">The TigerBeetle resource builder.</param>
    /// <param name="path">An absolute container path.</param>
    /// <returns>The TigerBeetle resource builder.</returns>
    [AspireExport]
    public static IResourceBuilder<TigerBeetleResource> WithDataFile(
        this IResourceBuilder<TigerBeetleResource> builder,
        string path)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!path.StartsWith("/", StringComparison.Ordinal))
        {
            throw new ArgumentException("The data-file path must be absolute inside the container.", nameof(path));
        }

        builder.Resource.ExplicitDataFile = path;
        return builder;
    }

    /// <summary>Adds a volume mounted at TigerBeetle's <c>/data</c> directory.</summary>
    /// <param name="builder">The TigerBeetle resource builder.</param>
    /// <param name="name">The optional volume name. Aspire generates a stable name when omitted.</param>
    /// <param name="isReadOnly">Whether the volume is read-only.</param>
    /// <returns>The TigerBeetle resource builder.</returns>
    [AspireExport]
    public static IResourceBuilder<TigerBeetleResource> WithDataVolume(
        this IResourceBuilder<TigerBeetleResource> builder,
        string? name = null,
        bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithVolume(
            name ?? VolumeNameGenerator.Generate(builder, "data"),
            TigerBeetleResource.DataDirectory,
            isReadOnly);
    }

    /// <summary>Adds a bind mount at TigerBeetle's <c>/data</c> directory.</summary>
    /// <param name="builder">The TigerBeetle resource builder.</param>
    /// <param name="source">The host directory.</param>
    /// <param name="isReadOnly">Whether the bind mount is read-only.</param>
    /// <returns>The TigerBeetle resource builder.</returns>
    [AspireExport]
    public static IResourceBuilder<TigerBeetleResource> WithDataBindMount(
        this IResourceBuilder<TigerBeetleResource> builder,
        string source,
        bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        return builder.WithBindMount(source, TigerBeetleResource.DataDirectory, isReadOnly);
    }

    /// <summary>Enables TigerBeetle's experimental StatsD exporter.</summary>
    /// <param name="builder">The TigerBeetle resource builder.</param>
    /// <param name="ipAddress">A numeric StatsD IPv4 or IPv6 address.</param>
    /// <param name="port">The StatsD UDP port.</param>
    /// <returns>The TigerBeetle resource builder.</returns>
    [AspireExport]
    public static IResourceBuilder<TigerBeetleResource> WithStatsD(
        this IResourceBuilder<TigerBeetleResource> builder,
        string ipAddress,
        int port = 8125)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!IPAddress.TryParse(ipAddress, out var parsedAddress))
        {
            throw new ArgumentException("TigerBeetle StatsD requires a numeric IP address.", nameof(ipAddress));
        }

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        builder.Resource.StatsDEndpoint = parsedAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{parsedAddress}]:{port.ToString(CultureInfo.InvariantCulture)}"
            : $"{parsedAddress}:{port.ToString(CultureInfo.InvariantCulture)}";
        return builder;
    }

    /// <summary>Adds one raw argument to <c>tigerbeetle format</c>.</summary>
    /// <param name="builder">The TigerBeetle resource builder.</param>
    /// <param name="argument">The argument.</param>
    /// <returns>The TigerBeetle resource builder.</returns>
    [AspireExport]
    public static IResourceBuilder<TigerBeetleResource> WithFormatArgument(
        this IResourceBuilder<TigerBeetleResource> builder,
        string argument)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument);
        builder.Resource.AdditionalFormatArguments.Add(argument);
        return builder;
    }

    /// <summary>Adds one raw argument to <c>tigerbeetle start</c>.</summary>
    /// <param name="builder">The TigerBeetle resource builder.</param>
    /// <param name="argument">The argument.</param>
    /// <returns>The TigerBeetle resource builder.</returns>
    [AspireExport]
    public static IResourceBuilder<TigerBeetleResource> WithStartArgument(
        this IResourceBuilder<TigerBeetleResource> builder,
        string argument)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument);
        builder.Resource.AdditionalStartArguments.Add(argument);
        return builder;
    }

    private static string BuildStartupScript(TigerBeetleResource resource)
    {
        var dataFile = ShellQuote(resource.DataFile);
        var script = new StringBuilder()
            .AppendLine("set -eu")
            .Append("if [ ! -e ").Append(dataFile).AppendLine(" ]; then")
            .Append("  /tigerbeetle format")
            .Append(" --cluster=").Append(ShellQuote(resource.ClusterId))
            .Append(" --replica=").Append(resource.ReplicaIndex.ToString(CultureInfo.InvariantCulture))
            .Append(" --replica-count=").Append(resource.ReplicaCount.ToString(CultureInfo.InvariantCulture));

        if (resource.DevelopmentMode)
        {
            script.Append(" --development");
        }

        AppendArguments(script, resource.AdditionalFormatArguments);
        script.Append(' ').Append(dataFile).AppendLine().AppendLine("fi");

        script.Append("exec /sbin/tini -- /tigerbeetle start")
            .Append(" --addresses=").Append(ShellQuote(resource.Addresses));

        if (resource.DevelopmentMode)
        {
            script.Append(" --development");
        }

        if (resource.CacheGrid is { Length: > 0 } cacheGrid)
        {
            script.Append(" --cache-grid=").Append(ShellQuote(cacheGrid));
        }

        if (resource.StatsDEndpoint is { Length: > 0 } statsD)
        {
            script.Append(" --experimental --statsd=").Append(ShellQuote(statsD));
        }

        AppendArguments(script, resource.AdditionalStartArguments);
        script.Append(' ').Append(dataFile);
        return script.ToString();
    }

    private static void AppendArguments(StringBuilder target, IEnumerable<string> arguments)
    {
        foreach (var argument in arguments)
        {
            target.Append(' ').Append(ShellQuote(argument));
        }
    }

    private static string ShellQuote(string value) => $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

    private static string EncodeEnvironmentVariableName(string name)
    {
        var encoded = new StringBuilder(name.Length + 1);

        if (name.Length > 0 && char.IsAsciiDigit(name[0]))
        {
            encoded.Append('_');
        }

        foreach (var character in name)
        {
            encoded.Append(char.IsAsciiLetterOrDigit(character) ? character : '_');
        }

        return encoded.ToString();
    }

    private static void ValidateAddresses(string addresses)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(addresses);

        foreach (var address in addresses.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(address, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535)
            {
                continue;
            }

            if (!IPEndPoint.TryParse(address, out var endpoint) || endpoint.Port is < 1 or > 65535)
            {
                throw new ArgumentException(
                    "TigerBeetle addresses must be numeric IPv4 or bracketed IPv6 endpoints separated by commas.",
                    nameof(addresses));
            }
        }
    }
}
