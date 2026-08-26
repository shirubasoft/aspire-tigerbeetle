using System.Globalization;
using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.TigerBeetle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

/// <summary>
/// Adds and configures TigerBeetle change data capture resources.
/// </summary>
public static class TigerBeetleChangeDataCaptureResourceBuilderExtensions
{
    internal const string TigerBeetleAddressesEnvironmentVariable = "TIGERBEETLE_CDC_ADDRESSES";
    internal const string AmqpUriEnvironmentVariable = "TIGERBEETLE_CDC_AMQP_URI";

    /// <summary>Adds a child change data capture resource that publishes TigerBeetle events to RabbitMQ.</summary>
    /// <param name="tigerBeetle">The TigerBeetle resource builder.</param>
    /// <param name="name">The change data capture resource name.</param>
    /// <param name="rabbitMq">The RabbitMQ server to which events are published.</param>
    /// <param name="publishExchange">The pre-existing RabbitMQ exchange to which events are published.</param>
    /// <returns>The TigerBeetle change data capture resource builder.</returns>
    /// <remarks>
    /// This overload waits for RabbitMQ before starting. The exchange must exist before the change data capture resource starts.
    /// </remarks>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="publishExchange" /> is empty or contains only white-space characters.</exception>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the exported connection-resource union overload.")]
    public static IResourceBuilder<TigerBeetleChangeDataCaptureResource> AddChangeDataCapture(
        this IResourceBuilder<TigerBeetleResource> tigerBeetle,
        [ResourceName] string name,
        IResourceBuilder<RabbitMQServerResource> rabbitMq,
        string publishExchange)
    {
        ArgumentNullException.ThrowIfNull(rabbitMq);

        return AddChangeDataCapture(
                tigerBeetle,
                name,
                (IResourceBuilder<IResourceWithConnectionString>)rabbitMq,
                publishExchange)
            .WaitFor(rabbitMq);
    }

    /// <summary>Adds a child change data capture resource that publishes TigerBeetle events to an AMQP 0.9.1 connection.</summary>
    /// <param name="tigerBeetle">The TigerBeetle resource builder.</param>
    /// <param name="name">The change data capture resource name.</param>
    /// <param name="amqpConnection">A resource that supplies an <c>amqp://</c> or <c>amqps://</c> connection string.</param>
    /// <param name="publishExchange">The pre-existing AMQP exchange to which events are published.</param>
    /// <returns>The TigerBeetle change data capture resource builder.</returns>
    /// <remarks>
    /// This overload adds a reference relationship to <paramref name="amqpConnection" /> but does not wait for it.
    /// Add <c>WaitFor</c> at the call site when the connection resource has a lifecycle managed by the AppHost.
    /// </remarks>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="publishExchange" /> is empty or contains only white-space characters.</exception>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the exported connection-resource union overload.")]
    public static IResourceBuilder<TigerBeetleChangeDataCaptureResource> AddChangeDataCapture(
        this IResourceBuilder<TigerBeetleResource> tigerBeetle,
        [ResourceName] string name,
        IResourceBuilder<IResourceWithConnectionString> amqpConnection,
        string publishExchange)
    {
        ArgumentNullException.ThrowIfNull(tigerBeetle);
        ArgumentNullException.ThrowIfNull(amqpConnection);
        ArgumentException.ThrowIfNullOrWhiteSpace(publishExchange);

        var resource = new TigerBeetleChangeDataCaptureResource(
            name,
            tigerBeetle.Resource,
            amqpConnection.Resource,
            publishExchange);

        tigerBeetle.ApplicationBuilder.Eventing.Subscribe<BeforeResourceStartedEvent>(
            resource,
            async (@event, cancellationToken) =>
            {
                var connectionString = await amqpConnection.Resource
                    .GetConnectionStringAsync(cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new DistributedApplicationException(
                        $"The AMQP connection string for the '{amqpConnection.Resource.Name}' resource is unavailable.");
                var connection = ParseAmqpConnectionString(connectionString);

                if (connection.UsesTls)
                {
                    @event.Services.GetRequiredService<ResourceLoggerService>().GetLogger(resource).LogWarning(
                        "TigerBeetle CDC does not support native AMQP TLS. Resource {ResourceName} will connect to {Host}:{Port} without TLS. Configure a TLS tunnel for amqps:// connections.",
                        resource.Name,
                        connection.Host,
                        connection.Port);
                }
            });

        return tigerBeetle.ApplicationBuilder.AddResource(resource)
            .WithImage(TigerBeetleContainerImageTags.Image)
            .WithImageRegistry(TigerBeetleContainerImageTags.Registry)
            .WithImageTag(TigerBeetleContainerImageTags.Tag)
            .WithIconName("ArrowSync")
            .WithEntrypoint("/bin/sh")
            .WithArgs(context =>
            {
                context.Args.Add("-ec");
                context.Args.Add(BuildStartupScript(resource));
            })
            .WithEnvironment(TigerBeetleAddressesEnvironmentVariable, tigerBeetle.Resource.ClientAddressesExpression)
            .WithEnvironment(AmqpUriEnvironmentVariable, amqpConnection.Resource.ConnectionStringExpression)
            .WithContainerRuntimeArgs(context =>
            {
                context.Args.Add("--security-opt");
                context.Args.Add("seccomp=unconfined");
            })
            .WithParentRelationship(tigerBeetle)
            .WithReferenceRelationship(amqpConnection)
            .WaitFor(tigerBeetle);
    }

    /// <summary>Adds a child change data capture resource for a polyglot AppHost.</summary>
    [AspireExport]
    internal static IResourceBuilder<TigerBeetleChangeDataCaptureResource> AddChangeDataCapture(
        this IResourceBuilder<TigerBeetleResource> tigerBeetle,
        [ResourceName] string name,
        [AspireUnion(
            typeof(IResourceBuilder<RabbitMQServerResource>),
            typeof(IResourceBuilder<IResourceWithConnectionString>))]
        object amqpConnection,
        string publishExchange)
    {
        return amqpConnection switch
        {
            IResourceBuilder<RabbitMQServerResource> rabbitMq =>
                AddChangeDataCapture(tigerBeetle, name, rabbitMq, publishExchange),
            IResourceBuilder<IResourceWithConnectionString> connection =>
                AddChangeDataCapture(tigerBeetle, name, connection, publishExchange),
            _ => throw new ArgumentException(
                "The AMQP connection must be a RabbitMQ resource or a resource with a connection string.",
                nameof(amqpConnection)),
        };
    }

    /// <summary>Overrides the RabbitMQ virtual host parsed from the connection string.</summary>
    /// <param name="builder">The TigerBeetle change data capture resource builder.</param>
    /// <param name="virtualHost">The RabbitMQ virtual host.</param>
    /// <returns>The TigerBeetle change data capture resource builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="virtualHost" /> is empty or contains only white-space characters.</exception>
    [AspireExport]
    public static IResourceBuilder<TigerBeetleChangeDataCaptureResource> WithVirtualHost(
        this IResourceBuilder<TigerBeetleChangeDataCaptureResource> builder,
        string virtualHost)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualHost);

        builder.Resource.VirtualHost = virtualHost;
        return builder;
    }

    /// <summary>Sets the routing key used when publishing change data capture events.</summary>
    /// <param name="builder">The TigerBeetle change data capture resource builder.</param>
    /// <param name="publishRoutingKey">The AMQP routing key.</param>
    /// <returns>The TigerBeetle change data capture resource builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="publishRoutingKey" /> is empty or contains only white-space characters.</exception>
    [AspireExport]
    public static IResourceBuilder<TigerBeetleChangeDataCaptureResource> WithPublishRoutingKey(
        this IResourceBuilder<TigerBeetleChangeDataCaptureResource> builder,
        string publishRoutingKey)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(publishRoutingKey);

        builder.Resource.PublishRoutingKey = publishRoutingKey;
        return builder;
    }

    /// <summary>Overrides the last published timestamp from which the change data capture job resumes.</summary>
    /// <param name="builder">The TigerBeetle change data capture resource builder.</param>
    /// <param name="timestampLast">A TigerBeetle timestamp in unsigned 64-bit decimal form. Use <c>0</c> to replay all events.</param>
    /// <returns>The TigerBeetle change data capture resource builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="timestampLast" /> is not an unsigned 64-bit decimal value.</exception>
    [AspireExport]
    public static IResourceBuilder<TigerBeetleChangeDataCaptureResource> WithTimestampLast(
        this IResourceBuilder<TigerBeetleChangeDataCaptureResource> builder,
        string timestampLast)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!ulong.TryParse(timestampLast, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            throw new ArgumentException("The last timestamp must be an unsigned 64-bit decimal value.", nameof(timestampLast));
        }

        builder.Resource.TimestampLast = timestampLast;
        return builder;
    }

    /// <summary>Overrides the last published timestamp from which the change data capture job resumes.</summary>
    /// <param name="builder">The TigerBeetle change data capture resource builder.</param>
    /// <param name="timestampLast">A TigerBeetle timestamp. Use <c>0</c> to replay all events.</param>
    /// <returns>The TigerBeetle change data capture resource builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder" /> is <see langword="null" />.</exception>
    [AspireExportIgnore(Reason = "System.UInt64 has no portable JavaScript representation. Use the decimal string overload in polyglot AppHosts.")]
    public static IResourceBuilder<TigerBeetleChangeDataCaptureResource> WithTimestampLast(
        this IResourceBuilder<TigerBeetleChangeDataCaptureResource> builder,
        ulong timestampLast)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithTimestampLast(timestampLast.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Adds raw arguments to the end of the <c>tigerbeetle amqp</c> command.</summary>
    /// <param name="builder">The TigerBeetle change data capture resource builder.</param>
    /// <param name="args">The arguments to append.</param>
    /// <returns>The TigerBeetle change data capture resource builder.</returns>
    /// <remarks>
    /// Arguments are appended after integration-owned options. TigerBeetle performs final validation when arguments conflict.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="builder" /> or <paramref name="args" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">An argument is empty or contains only white-space characters.</exception>
    [AspireExport]
    public static IResourceBuilder<TigerBeetleChangeDataCaptureResource> WithCdcArgs(
        this IResourceBuilder<TigerBeetleChangeDataCaptureResource> builder,
        params string[] args)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(args);

        foreach (var argument in args)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(argument);
            builder.Resource.AdditionalArguments.Add(argument);
        }

        return builder;
    }

    internal static AmqpConnectionInfo ParseAmqpConnectionString(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        if (!Uri.TryCreate(connectionString, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("amqp" or "amqps") ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new DistributedApplicationException(
                "The AMQP connection string must be an absolute amqp:// or amqps:// URI with a host.");
        }

        var userInfo = uri.GetComponents(UriComponents.UserInfo, UriFormat.UriEscaped);
        var userInfoParts = userInfo.Split(':', 2);
        var userName = userInfoParts.Length > 0 && userInfoParts[0].Length > 0
            ? Uri.UnescapeDataString(userInfoParts[0])
            : "guest";
        var password = userInfoParts.Length > 1 && userInfoParts[1].Length > 0
            ? Uri.UnescapeDataString(userInfoParts[1])
            : "guest";
        var encodedVirtualHost = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        var virtualHost = encodedVirtualHost.Length > 0
            ? Uri.UnescapeDataString(encodedVirtualHost)
            : "/";
        var usesTls = string.Equals(uri.Scheme, "amqps", StringComparison.Ordinal);
        var port = uri.Port is > 0
            ? uri.Port
            : usesTls ? 5671 : 5672;

        return new AmqpConnectionInfo(uri.Scheme, uri.Host, port, userName, password, virtualHost, usesTls);
    }

    internal static string BuildStartupScript(TigerBeetleChangeDataCaptureResource resource)
    {
        var script = new StringBuilder()
            .AppendLine("set -eu")
            .AppendLine("uri_decode() {")
            .AppendLine("  printf '%s\\n' \"$1\" | awk '")
            .AppendLine("    function hex_digit(value) {")
            .AppendLine("      if (value >= \"0\" && value <= \"9\") return value + 0")
            .AppendLine("      value = tolower(value)")
            .AppendLine("      return index(\"abcdef\", value) + 9")
            .AppendLine("    }")
            .AppendLine("    {")
            .AppendLine("      output = \"\"")
            .AppendLine("      for (position = 1; position <= length($0); position++) {")
            .AppendLine("        character = substr($0, position, 1)")
            .AppendLine("        if (character == \"%\" && position + 2 <= length($0)) {")
            .AppendLine("          first = substr($0, position + 1, 1)")
            .AppendLine("          second = substr($0, position + 2, 1)")
            .AppendLine("          if (first ~ /^[0-9A-Fa-f]$/ && second ~ /^[0-9A-Fa-f]$/) {")
            .AppendLine("            output = output sprintf(\"%c\", hex_digit(first) * 16 + hex_digit(second))")
            .AppendLine("            position += 2")
            .AppendLine("            continue")
            .AppendLine("          }")
            .AppendLine("        }")
            .AppendLine("        output = output character")
            .AppendLine("      }")
            .AppendLine("      printf \"%s\", output")
            .AppendLine("    }'")
            .AppendLine("}")
            .AppendLine("resolve_endpoint() {")
            .AppendLine("  endpoint=\"$(printf '%s' \"$1\" | awk '{$1=$1};1')\"")
            .AppendLine("  case \"$endpoint\" in")
            .AppendLine("    \\[*\\]:*) host=\"${endpoint#\\[}\"; host=\"${host%%\\]*}\"; port=\"${endpoint##*:}\" ;;")
            .AppendLine("    *:*:*) printf '%s\\n' \"$endpoint\"; return ;;")
            .AppendLine("    *:*) host=\"${endpoint%:*}\"; port=\"${endpoint##*:}\" ;;")
            .AppendLine("    *) host=\"$endpoint\"; port=\"\" ;;")
            .AppendLine("  esac")
            .AppendLine("  resolved=\"$(getent ahostsv4 \"$host\" | awk 'NR == 1 { print $1 }')\"")
            .AppendLine("  if [ -z \"$resolved\" ]; then")
            .AppendLine("    resolved=\"$(getent ahosts \"$host\" | awk 'NR == 1 { print $1 }')\"")
            .AppendLine("  fi")
            .AppendLine("  if [ -z \"$resolved\" ]; then")
            .AppendLine("    echo \"Could not resolve CDC endpoint host '$host'.\" >&2")
            .AppendLine("    return 1")
            .AppendLine("  fi")
            .AppendLine("  if [ -z \"$port\" ]; then")
            .AppendLine("    printf '%s\\n' \"$resolved\"")
            .AppendLine("  elif printf '%s' \"$resolved\" | grep -q ':'; then")
            .AppendLine("    printf '[%s]:%s\\n' \"$resolved\" \"$port\"")
            .AppendLine("  else")
            .AppendLine("    printf '%s:%s\\n' \"$resolved\" \"$port\"")
            .AppendLine("  fi")
            .AppendLine("}")
            .AppendLine($"amqp_uri=\"${{{AmqpUriEnvironmentVariable}%%#*}}\"")
            .AppendLine("amqp_uri=\"${amqp_uri%%\\?*}\"")
            .AppendLine("case \"$amqp_uri\" in")
            .AppendLine("  amqp://*) scheme=\"amqp\"; default_port=\"5672\" ;;")
            .AppendLine("  amqps://*)")
            .AppendLine("    scheme=\"amqps\"")
            .AppendLine("    default_port=\"5671\"")
            .AppendLine("    echo \"WARNING: TigerBeetle CDC does not support native AMQP TLS. Configure a TLS tunnel; this connection will be attempted without TLS.\" >&2")
            .AppendLine("    ;;")
            .AppendLine("  *) echo \"The CDC AMQP connection string must use amqp:// or amqps://.\" >&2; exit 1 ;;")
            .AppendLine("esac")
            .AppendLine("authority_and_path=\"${amqp_uri#*://}\"")
            .AppendLine("authority=\"${authority_and_path%%/*}\"")
            .AppendLine("if [ \"$authority\" = \"$authority_and_path\" ]; then")
            .AppendLine("  encoded_vhost=\"\"")
            .AppendLine("else")
            .AppendLine("  encoded_vhost=\"${authority_and_path#*/}\"")
            .AppendLine("fi")
            .AppendLine("case \"$authority\" in")
            .AppendLine("  *@*) user_info=\"${authority%@*}\"; host_port=\"${authority##*@}\" ;;")
            .AppendLine("  *) user_info=\"\"; host_port=\"$authority\" ;;")
            .AppendLine("esac")
            .AppendLine("case \"$user_info\" in")
            .AppendLine("  *:*) encoded_user=\"${user_info%%:*}\"; encoded_password=\"${user_info#*:}\" ;;")
            .AppendLine("  *) encoded_user=\"$user_info\"; encoded_password=\"\" ;;")
            .AppendLine("esac")
            .AppendLine("if [ -n \"$encoded_user\" ]; then amqp_user=\"$(uri_decode \"$encoded_user\")\"; else amqp_user=\"guest\"; fi")
            .AppendLine("if [ -n \"$encoded_password\" ]; then amqp_password=\"$(uri_decode \"$encoded_password\")\"; else amqp_password=\"guest\"; fi")
            .AppendLine("if [ -n \"$encoded_vhost\" ]; then amqp_vhost=\"$(uri_decode \"$encoded_vhost\")\"; else amqp_vhost=\"/\"; fi")
            .AppendLine("case \"$host_port\" in")
            .AppendLine("  \\[*\\]:*) amqp_host_name=\"${host_port#\\[}\"; amqp_host_name=\"${amqp_host_name%%\\]*}\"; amqp_port=\"${host_port##*:}\" ;;")
            .AppendLine("  \\[*\\]) amqp_host_name=\"${host_port#\\[}\"; amqp_host_name=\"${amqp_host_name%\\]}\"; amqp_port=\"$default_port\" ;;")
            .AppendLine("  *:*:*) echo \"An IPv6 AMQP host must be enclosed in brackets.\" >&2; exit 1 ;;")
            .AppendLine("  *:*) amqp_host_name=\"${host_port%:*}\"; amqp_port=\"${host_port##*:}\" ;;")
            .AppendLine("  *) amqp_host_name=\"$host_port\"; amqp_port=\"$default_port\" ;;")
            .AppendLine("esac")
            .AppendLine("amqp_host_name=\"$(uri_decode \"$amqp_host_name\")\"")
            .AppendLine("case \"$amqp_port\" in ''|*[!0-9]*) echo \"The CDC AMQP port is invalid.\" >&2; exit 1 ;; esac")
            .AppendLine("if [ \"$amqp_port\" -lt 1 ] || [ \"$amqp_port\" -gt 65535 ]; then echo \"The CDC AMQP port is invalid.\" >&2; exit 1; fi")
            .AppendLine("case \"$amqp_host_name\" in *:*) amqp_endpoint=\"[$amqp_host_name]:$amqp_port\" ;; *) amqp_endpoint=\"$amqp_host_name:$amqp_port\" ;; esac")
            .AppendLine("tigerbeetle_addresses=\"\"")
            .AppendLine("old_ifs=\"$IFS\"")
            .AppendLine("IFS=,")
            .AppendLine($"for endpoint in ${{{TigerBeetleAddressesEnvironmentVariable}}}; do")
            .AppendLine("  resolved=\"$(resolve_endpoint \"$endpoint\")\"")
            .AppendLine("  if [ -z \"$tigerbeetle_addresses\" ]; then")
            .AppendLine("    tigerbeetle_addresses=\"$resolved\"")
            .AppendLine("  else")
            .AppendLine("    tigerbeetle_addresses=\"$tigerbeetle_addresses,$resolved\"")
            .AppendLine("  fi")
            .AppendLine("done")
            .AppendLine("IFS=\"$old_ifs\"")
            .AppendLine("amqp_host=\"$(resolve_endpoint \"$amqp_endpoint\")\"");

        if (resource.VirtualHost is not null)
        {
            script.Append("amqp_vhost=").AppendLine(ShellQuote(resource.VirtualHost));
        }

        script.Append("exec /sbin/tini -- /tigerbeetle amqp")
            .Append(" --addresses=\"$tigerbeetle_addresses\"")
            .Append(" --cluster=").Append(ShellQuote(resource.TigerBeetle.ClusterId))
            .Append(" --host=\"$amqp_host\"")
            .Append(" --vhost=\"$amqp_vhost\"")
            .Append(" --user=\"$amqp_user\"")
            .Append(" --password=\"$amqp_password\"")
            .Append(" --publish-exchange=").Append(ShellQuote(resource.PublishExchange));

        AppendOption(script, "--publish-routing-key", resource.PublishRoutingKey);
        AppendOption(script, "--timestamp-last", resource.TimestampLast);

        foreach (var argument in resource.AdditionalArguments)
        {
            script.Append(' ').Append(ShellQuote(argument));
        }

        return script.ToString();
    }

    private static void AppendOption(StringBuilder target, string name, string? value)
    {
        if (value is { Length: > 0 })
        {
            target.Append(' ').Append(name).Append('=').Append(ShellQuote(value));
        }
    }

    private static string ShellQuote(string value) => $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

    internal sealed record AmqpConnectionInfo(
        string Scheme,
        string Host,
        int Port,
        string UserName,
        string Password,
        string VirtualHost,
        bool UsesTls);
}
