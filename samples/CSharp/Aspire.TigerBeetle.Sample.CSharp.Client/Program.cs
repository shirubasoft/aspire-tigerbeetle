using System.Globalization;
using System.Net;
using TigerBeetle;

var builder = WebApplication.CreateBuilder(args);

var clusterId = builder.Configuration["TIGERBEETLE_CLUSTERID"]
    ?? throw new InvalidOperationException("TIGERBEETLE_CLUSTERID is required.");
var addresses = builder.Configuration["TIGERBEETLE_ADDRESSES"]
    ?? throw new InvalidOperationException("TIGERBEETLE_ADDRESSES is required.");
var resolvedAddresses = await ResolveAddressesAsync(
    addresses,
    CancellationToken.None);

builder.Services.AddSingleton(new Client(
    clusterID: UInt128.Parse(clusterId, CultureInfo.InvariantCulture),
    addresses: resolvedAddresses));

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    clusterId,
    addresses,
    message = "TigerBeetle connection properties were injected by Aspire."
}));

app.MapGet("/accounts/{id}", async (string id, Client client) =>
{
    var accountId = UInt128.Parse(id, CultureInfo.InvariantCulture);
    var accounts = await client.LookupAccountsAsync(new UInt128[] { accountId });
    return Results.Ok(accounts);
});

app.Run();

static async Task<string[]> ResolveAddressesAsync(string value, CancellationToken cancellationToken)
{
    var addresses = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    for (var index = 0; index < addresses.Length; index++)
    {
        if (int.TryParse(addresses[index], NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            continue;
        }

        if (!Uri.TryCreate($"tcp://{addresses[index]}", UriKind.Absolute, out var endpoint))
        {
            throw new FormatException($"'{addresses[index]}' is not a valid TigerBeetle address.");
        }

        if (IPAddress.TryParse(endpoint.DnsSafeHost, out _))
        {
            continue;
        }

        var resolved = await Dns.GetHostAddressesAsync(endpoint.DnsSafeHost, cancellationToken);
        var ipAddress = resolved.FirstOrDefault(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            ?? resolved.FirstOrDefault()
            ?? throw new InvalidOperationException($"Could not resolve {endpoint.DnsSafeHost}.");
        addresses[index] = ipAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{ipAddress}]:{endpoint.Port}"
            : $"{ipAddress}:{endpoint.Port}";
    }

    return addresses;
}
