using System.Globalization;
using System.Net.Sockets;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Hosting.TigerBeetle;

internal sealed class TigerBeetleTcpHealthCheck(Func<string?> clientAddressesProvider) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var clientAddresses = clientAddressesProvider();
        if (string.IsNullOrWhiteSpace(clientAddresses))
        {
            return HealthCheckResult.Unhealthy("The TigerBeetle client addresses are not available yet.");
        }

        try
        {
            foreach (var address in clientAddresses.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var (host, port) = ParseEndpoint(address);
                using var client = new TcpClient();
                await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
                return HealthCheckResult.Healthy("The TigerBeetle TCP endpoint is accepting connections.");
            }

            return HealthCheckResult.Unhealthy("The TigerBeetle resource has no client addresses.");
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
