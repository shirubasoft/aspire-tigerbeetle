using System.Globalization;
using System.Net.Sockets;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Hosting.TigerBeetle;

internal sealed class TigerBeetleTcpHealthCheck(Func<string?> connectionStringProvider) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var connectionString = connectionStringProvider();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return HealthCheckResult.Unhealthy("The TigerBeetle connection string is not available yet.");
        }

        try
        {
            var addresses = ParseSetting(connectionString, "Addresses");
            foreach (var address in addresses.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var (host, port) = ParseEndpoint(address);
                using var client = new TcpClient();
                await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
                return HealthCheckResult.Healthy("The TigerBeetle TCP endpoint is accepting connections.");
            }

            return HealthCheckResult.Unhealthy("The TigerBeetle connection string has no addresses.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("The TigerBeetle TCP endpoint is unavailable.", exception);
        }
    }

    private static string ParseSetting(string connectionString, string name)
    {
        foreach (var part in connectionString.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0 && part[..separator].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return part[(separator + 1)..];
            }
        }

        throw new FormatException($"The TigerBeetle connection string does not contain {name}.");
    }

    private static (string Host, int Port) ParseEndpoint(string address)
    {
        if (int.TryParse(address, NumberStyles.None, CultureInfo.InvariantCulture, out var port))
        {
            return ("127.0.0.1", port);
        }

        if (!Uri.TryCreate($"tcp://{address}", UriKind.Absolute, out var endpoint) || endpoint.Port < 1)
        {
            throw new FormatException($"'{address}' is not a valid TigerBeetle endpoint.");
        }

        return (endpoint.DnsSafeHost, endpoint.Port);
    }
}
