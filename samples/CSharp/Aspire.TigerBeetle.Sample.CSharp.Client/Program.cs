using System.Globalization;
using System.Net;
using TigerBeetle;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("tigerbeetle")
    ?? throw new InvalidOperationException("ConnectionStrings:tigerbeetle is required.");
var settings = ParseConnectionString(connectionString);
var resolvedAddresses = await ResolveAddressesAsync(
    settings["Addresses"],
    CancellationToken.None);

builder.Services.AddSingleton(new Client(
    clusterID: UInt128.Parse(settings["ClusterID"], CultureInfo.InvariantCulture),
    addresses: resolvedAddresses));

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    connectionString,
    message = "The TigerBeetle connection string was injected by Aspire."
}));

app.MapGet("/accounts/{id}", async (string id, Client client) =>
{
    var accountId = UInt128.Parse(id, CultureInfo.InvariantCulture);
    var accounts = await client.LookupAccountsAsync(new UInt128[] { accountId });
    return Results.Ok(accounts);
});

app.Run();

static Dictionary<string, string> ParseConnectionString(string value) => value
    .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
    .Select(part => part.Split('=', 2, StringSplitOptions.TrimEntries))
    .ToDictionary(
        part => part[0],
        part => part.Length == 2 ? part[1] : string.Empty,
        StringComparer.OrdinalIgnoreCase);

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
