#pragma warning disable CS0618 // Aspire's public test inspection helpers are obsolete in favor of a lower-level configuration builder.

using System.Net.Sockets;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;
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
    public async Task StructuredPropertiesUseTheAllocatedEndpoint()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tigerBeetle = builder.AddTigerBeetle("tigerbeetle")
            .WithEndpoint(TigerBeetleResource.PrimaryEndpointName, endpoint =>
                endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "127.0.0.1", 4567));

        var properties = tigerBeetle.Resource.GetConnectionProperties()
            .ToDictionary(property => property.Key, property => property.Value);

        Assert.DoesNotContain(typeof(IResourceWithConnectionString), typeof(TigerBeetleResource).GetInterfaces());
        Assert.Equal("127.0.0.1", await properties["Host"].GetValueAsync(TestContext.Current.CancellationToken));
        Assert.Equal("4567", await properties["Port"].GetValueAsync(TestContext.Current.CancellationToken));
        Assert.Equal("0", await properties["ClusterId"].GetValueAsync(TestContext.Current.CancellationToken));
        Assert.Equal("127.0.0.1:4567", await properties["Addresses"].GetValueAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WithReferenceInjectsStandardAndTigerBeetleConnectionProperties()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tigerBeetle = builder.AddTigerBeetle("tigerbeetle")
            .WithEndpoint(TigerBeetleResource.PrimaryEndpointName, endpoint =>
                endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "127.0.0.1", 4567));
        var consumer = builder.AddExecutable("consumer", "echo", ".")
            .WithReference(tigerBeetle);

        var environment = await consumer.Resource.GetEnvironmentVariableValuesAsync();

        Assert.DoesNotContain("ConnectionStrings__tigerbeetle", environment.Keys);
        Assert.Equal("127.0.0.1", environment["TIGERBEETLE_HOST"]);
        Assert.Equal("4567", environment["TIGERBEETLE_PORT"]);
        Assert.Equal("0", environment["TIGERBEETLE_CLUSTERID"]);
        Assert.Equal("127.0.0.1:4567", environment["TIGERBEETLE_ADDRESSES"]);
        Assert.Equal("127.0.0.1:4567", environment["TIGERBEETLE_ADDRESSES__0"]);

        Assert.Equal(
            ["Host", "Port", "ClusterId", "Addresses", "Addresses__0"],
            tigerBeetle.Resource.GetConnectionProperties().Select(property => property.Key));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TIGERBEETLE_ADDRESSES"] = environment["TIGERBEETLE_ADDRESSES"],
                ["TIGERBEETLE_ADDRESSES:0"] = environment["TIGERBEETLE_ADDRESSES__0"],
            })
            .Build();
        var boundAddresses = configuration.GetSection("TIGERBEETLE_ADDRESSES").Get<string[]>();
        Assert.NotNull(boundAddresses);
        Assert.Equal(["127.0.0.1:4567"], boundAddresses);
    }

    [Fact]
    public async Task WithReferenceSupportsAnEncodedPropertyPrefix()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tigerBeetle = builder.AddTigerBeetle("tigerbeetle")
            .WithEndpoint(TigerBeetleResource.PrimaryEndpointName, endpoint =>
                endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "127.0.0.1", 4567));
        var consumer = builder.AddExecutable("consumer", "echo", ".")
            .WithReference(tigerBeetle, "ledger-east");

        var environment = await consumer.Resource.GetEnvironmentVariableValuesAsync();

        Assert.Equal("0", environment["LEDGER_EAST_CLUSTERID"]);
        Assert.Equal("127.0.0.1:4567", environment["LEDGER_EAST_ADDRESSES"]);
        Assert.DoesNotContain(environment.Keys, key => key.StartsWith("ConnectionStrings__", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithReferenceInjectsOrderedIndexedClientAddresses()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tigerBeetle = builder.AddTigerBeetle("tigerbeetle")
            .WithClientAddresses("10.0.0.10:3000, [2001:db8::1]:3000, 3002")
            .WithEndpoint(TigerBeetleResource.PrimaryEndpointName, endpoint =>
                endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "127.0.0.1", 4567));
        var consumer = builder.AddExecutable("consumer", "echo", ".")
            .WithReference(tigerBeetle);

        var environment = await consumer.Resource.GetEnvironmentVariableValuesAsync();

        Assert.Equal(
            "10.0.0.10:3000, [2001:db8::1]:3000, 3002",
            environment["TIGERBEETLE_ADDRESSES"]);
        Assert.Equal("10.0.0.10:3000", environment["TIGERBEETLE_ADDRESSES__0"]);
        Assert.Equal("[2001:db8::1]:3000", environment["TIGERBEETLE_ADDRESSES__1"]);
        Assert.Equal("3002", environment["TIGERBEETLE_ADDRESSES__2"]);
        Assert.DoesNotContain("TIGERBEETLE_ADDRESSES__3", environment.Keys);
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
            "340282366920938463463374607431768211455",
            await tigerBeetle.Resource.ClusterIdExpression.GetValueAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            "10.0.0.10:3000,10.0.0.11:3000,10.0.0.12:3000",
            await tigerBeetle.Resource.ClientAddressesExpression.GetValueAsync(TestContext.Current.CancellationToken));
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
            "[2001:db8::1]:3000,[2001:db8::2]:3000",
            await tigerBeetle.Resource.ClientAddressesExpression.GetValueAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            ["[2001:db8::1]:3000", "[2001:db8::2]:3000"],
            await Task.WhenAll(tigerBeetle.Resource.ClientAddressExpressions.Select(
                expression => expression.GetValueAsync(TestContext.Current.CancellationToken).AsTask())));
    }
}
