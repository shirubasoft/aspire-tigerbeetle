using System.Globalization;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a TigerBeetle replica running in a container.
/// </summary>
[AspireExport]
public sealed class TigerBeetleResource([ResourceName] string name)
    : ContainerResource(name), IResourceWithConnectionString
{
    /// <summary>The name of the TigerBeetle TCP endpoint.</summary>
    public const string PrimaryEndpointName = "tcp";

    /// <summary>The container directory used for TigerBeetle data files.</summary>
    public const string DataDirectory = "/data";

    /// <summary>The default TigerBeetle container port.</summary>
    public const int DefaultPort = 3000;

    private EndpointReference? _primaryEndpoint;

    /// <summary>Gets the TCP endpoint used by TigerBeetle clients.</summary>
    public EndpointReference PrimaryEndpoint =>
        _primaryEndpoint ??= new EndpointReference(this, PrimaryEndpointName);

    /// <summary>Gets the cluster ID passed to TigerBeetle clients and the replica formatter.</summary>
    public string ClusterId { get; internal set; } = "0";

    /// <summary>Gets the zero-based replica index.</summary>
    public int ReplicaIndex { get; internal set; }

    /// <summary>Gets the number of replicas in the cluster.</summary>
    public int ReplicaCount { get; internal set; } = 1;

    /// <summary>Gets the comma-separated addresses passed to <c>tigerbeetle start</c>.</summary>
    public string Addresses { get; internal set; } = $"0.0.0.0:{DefaultPort}";

    /// <summary>
    /// Gets the optional client addresses used in the connection string. When unset, Aspire's allocated endpoint is used.
    /// </summary>
    public string? ClientAddresses { get; internal set; }

    /// <summary>Gets the configured data-file path inside the container.</summary>
    public string DataFile =>
        ExplicitDataFile ?? $"{DataDirectory}/{ClusterId}_{ReplicaIndex.ToString(CultureInfo.InvariantCulture)}.tigerbeetle";

    /// <summary>Gets a value indicating whether TigerBeetle development mode is enabled.</summary>
    public bool DevelopmentMode { get; internal set; } = true;

    /// <summary>Gets the optional TigerBeetle grid-cache size.</summary>
    public string? CacheGrid { get; internal set; }

    /// <summary>Gets the optional StatsD endpoint.</summary>
    public string? StatsDEndpoint { get; internal set; }

    /// <inheritdoc />
    public ReferenceExpression ConnectionStringExpression => ClientAddresses is { Length: > 0 } addresses
        ? ReferenceExpression.Create($"ClusterID={ClusterId};Addresses={addresses}")
        : ReferenceExpression.Create(
            $"ClusterID={ClusterId};Addresses={PrimaryEndpoint.Property(EndpointProperty.IPV4Host)}:{PrimaryEndpoint.Property(EndpointProperty.Port)}");

    /// <inheritdoc />
    public IEnumerable<KeyValuePair<string, ReferenceExpression>> GetConnectionProperties()
    {
        yield return new KeyValuePair<string, ReferenceExpression>(
            "ClusterID",
            ReferenceExpression.Create($"{ClusterId}"));
        yield return new KeyValuePair<string, ReferenceExpression>(
            "Addresses",
            ClientAddresses is { Length: > 0 } addresses
                ? ReferenceExpression.Create($"{addresses}")
                : ReferenceExpression.Create(
                    $"{PrimaryEndpoint.Property(EndpointProperty.IPV4Host)}:{PrimaryEndpoint.Property(EndpointProperty.Port)}"));
    }

    internal string? ExplicitDataFile { get; set; }

    internal IList<string> AdditionalFormatArguments { get; } = [];

    internal IList<string> AdditionalStartArguments { get; } = [];
}
