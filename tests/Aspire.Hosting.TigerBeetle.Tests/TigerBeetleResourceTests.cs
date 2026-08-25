#pragma warning disable CS0618 // Aspire's public test inspection helpers are obsolete in favor of a lower-level configuration builder.

using System.Net.Sockets;
using Aspire.Hosting.ApplicationModel;
using Xunit;

namespace Aspire.Hosting.TigerBeetle.Tests;

public sealed class TigerBeetleResourceTests
{
    [Fact]
    public async Task AddTigerBeetleConfiguresAWorkingDevelopmentContainer()
    {
        var builder = DistributedApplication.CreateBuilder();

        var tigerBeetle = builder.AddTigerBeetle("tigerbeetle");

        var image = Assert.Single(tigerBeetle.Resource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal("ghcr.io", image.Registry);
        Assert.Equal("tigerbeetle/tigerbeetle", image.Image);
        Assert.Equal("0.17.9", image.Tag);

        var endpoint = Assert.Single(tigerBeetle.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(TigerBeetleResource.PrimaryEndpointName, endpoint.Name);
        Assert.Equal(TigerBeetleResource.DefaultPort, endpoint.TargetPort);
        Assert.Equal("tcp", endpoint.UriScheme);
        Assert.Equal(ProtocolType.Tcp, endpoint.Protocol);
        Assert.Null(endpoint.Port);

        Assert.Equal("/bin/sh", tigerBeetle.Resource.Entrypoint);
        var arguments = await tigerBeetle.Resource.GetArgumentValuesAsync();
        Assert.Equal("-ec", arguments[0]);
        Assert.Contains("/tigerbeetle format --cluster='0' --replica=0 --replica-count=1 --development", arguments[1]);
        Assert.Contains("exec /sbin/tini -- /tigerbeetle start --addresses='0.0.0.0:3000' --development", arguments[1]);
        Assert.Contains("/data/0_0.tigerbeetle", arguments[1]);

        var runtimeArgs = Assert.Single(
            tigerBeetle.Resource.Annotations.OfType<ContainerRuntimeArgsCallbackAnnotation>());
        var values = new List<object>();
        await runtimeArgs.Callback(new ContainerRuntimeArgsCallbackContext(values));
        Assert.Equal(["--security-opt", "seccomp=unconfined"], values);

        Assert.Single(tigerBeetle.Resource.Annotations.OfType<HealthCheckAnnotation>());
    }

    [Fact]
    public async Task ConnectionStringUsesTheAllocatedIpv4Endpoint()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tigerBeetle = builder.AddTigerBeetle("tigerbeetle")
            .WithEndpoint(TigerBeetleResource.PrimaryEndpointName, endpoint =>
                endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "127.0.0.1", 4567));

        var connectionString = await ((IResourceWithConnectionString)tigerBeetle.Resource)
            .GetConnectionStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal("ClusterID=0;Addresses=127.0.0.1:4567", connectionString);
        Assert.Equal(
            "ClusterID=0;Addresses={tigerbeetle.bindings.tcp.host}:{tigerbeetle.bindings.tcp.port}",
            tigerBeetle.Resource.ConnectionStringExpression.ValueExpression);
    }

    [Fact]
    public async Task WithReferenceInjectsTheConnectionStringAndProperties()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tigerBeetle = builder.AddTigerBeetle("tigerbeetle")
            .WithEndpoint(TigerBeetleResource.PrimaryEndpointName, endpoint =>
                endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "127.0.0.1", 4567));
        var consumer = builder.AddExecutable("consumer", "echo", ".")
            .WithReference(tigerBeetle);

        var environment = await consumer.Resource.GetEnvironmentVariableValuesAsync();

        Assert.Equal("ClusterID=0;Addresses=127.0.0.1:4567", environment["ConnectionStrings__tigerbeetle"]);
        Assert.Equal("0", environment["TIGERBEETLE_CLUSTERID"]);
        Assert.Equal("127.0.0.1:4567", environment["TIGERBEETLE_ADDRESSES"]);
    }

    [Fact]
    public async Task ConfigurationMethodsAffectFormatAndStart()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tigerBeetle = builder.AddTigerBeetle("tigerbeetle")
            .WithClusterId("340282366920938463463374607431768211455")
            .WithReplica(replicaIndex: 2, replicaCount: 3)
            .WithAddresses("10.0.0.10:3000,10.0.0.11:3000,10.0.0.12:3000")
            .WithCacheGrid("12GiB")
            .WithDevelopmentMode(false)
            .WithDataFile("/var/lib/tigerbeetle/data.tigerbeetle")
            .WithStatsD("10.0.0.20", 9125)
            .WithFormatArgument("--verbose")
            .WithStartArgument("--trace");

        var arguments = await tigerBeetle.Resource.GetArgumentValuesAsync();
        var script = arguments[1];

        Assert.Contains("--cluster='340282366920938463463374607431768211455'", script);
        Assert.Contains("--replica=2 --replica-count=3", script);
        Assert.Contains("--addresses='10.0.0.10:3000,10.0.0.11:3000,10.0.0.12:3000'", script);
        Assert.Contains("--cache-grid='12GiB'", script);
        Assert.Contains("--experimental --statsd='10.0.0.20:9125'", script);
        Assert.Contains("'--verbose'", script);
        Assert.Contains("'--trace'", script);
        Assert.DoesNotContain("--development", script);
        Assert.EndsWith("'/var/lib/tigerbeetle/data.tigerbeetle'", script, StringComparison.Ordinal);
        Assert.Equal(
            "ClusterID=340282366920938463463374607431768211455;Addresses=10.0.0.10:3000,10.0.0.11:3000,10.0.0.12:3000",
            await ((IResourceWithConnectionString)tigerBeetle.Resource)
                .GetConnectionStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void PersistenceMethodsUseTheTigerBeetleDataDirectory()
    {
        var builder = DistributedApplication.CreateBuilder();

        var volume = builder.AddTigerBeetle("volume").WithDataVolume("tigerbeetle-data");
        var volumeMount = Assert.Single(volume.Resource.Annotations.OfType<ContainerMountAnnotation>());
        Assert.Equal("tigerbeetle-data", volumeMount.Source);
        Assert.Equal(TigerBeetleResource.DataDirectory, volumeMount.Target);
        Assert.False(volumeMount.IsReadOnly);

        var bind = builder.AddTigerBeetle("bind").WithDataBindMount("./data", isReadOnly: true);
        var bindMount = Assert.Single(bind.Resource.Annotations.OfType<ContainerMountAnnotation>());
        Assert.EndsWith(Path.Combine("data"), bindMount.Source, StringComparison.Ordinal);
        Assert.Equal(TigerBeetleResource.DataDirectory, bindMount.Target);
        Assert.True(bindMount.IsReadOnly);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("not-a-number")]
    [InlineData("340282366920938463463374607431768211456")]
    public void WithClusterIdRejectsInvalidValues(string value)
    {
        var builder = DistributedApplication.CreateBuilder();
        var tigerBeetle = builder.AddTigerBeetle("tigerbeetle");

        Assert.Throws<ArgumentException>(() => tigerBeetle.WithClusterId(value));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(1, 1)]
    [InlineData(0, 0)]
    [InlineData(0, 7)]
    public void WithReplicaRejectsInvalidValues(int replicaIndex, int replicaCount)
    {
        var builder = DistributedApplication.CreateBuilder();
        var tigerBeetle = builder.AddTigerBeetle("tigerbeetle");

        Assert.Throws<ArgumentOutOfRangeException>(() => tigerBeetle.WithReplica(replicaIndex, replicaCount));
    }

    [Theory]
    [InlineData("tigerbeetle:3000")]
    [InlineData("10.0.0.1:0")]
    [InlineData("10.0.0.1:65536")]
    public void WithAddressesRejectsDnsNamesAndInvalidPorts(string value)
    {
        var builder = DistributedApplication.CreateBuilder();
        var tigerBeetle = builder.AddTigerBeetle("tigerbeetle");

        Assert.Throws<ArgumentException>(() => tigerBeetle.WithAddresses(value));
    }

    [Fact]
    public async Task WithAddressesAcceptsBracketedIpv6Endpoints()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tigerBeetle = builder.AddTigerBeetle("tigerbeetle")
            .WithAddresses("[2001:db8::1]:3000,[2001:db8::2]:3000");

        Assert.Equal(
            "ClusterID=0;Addresses=[2001:db8::1]:3000,[2001:db8::2]:3000",
            await ((IResourceWithConnectionString)tigerBeetle.Resource)
                .GetConnectionStringAsync(TestContext.Current.CancellationToken));
    }
}
