using HttpWorker.Options;
using Microsoft.Extensions.Options;
using Milvasoft.Milvaion.Sdk.Worker.Exceptions;
using System.Net;
using System.Net.Sockets;

namespace HttpWorker.Security;

/// <summary>
/// Thrown when a connection is refused by <see cref="HttpTargetPolicy"/>.
/// </summary>
public sealed class HttpTargetBlockedException(string message) : Exception(message);

/// <summary>
/// Enforces <see cref="HttpWorkerSecurityOptions"/>. URLs are checked before a request is built, and every new
/// connection is checked again against the addresses it actually dials, which is what covers redirects and DNS rebinding.
/// </summary>
public sealed class HttpTargetPolicy
{
    // Link-local covers the 169.254.169.254 cloud metadata endpoint; multicast and "this network" are never valid targets.
    private static readonly IPNetwork[] _privateNetworks =
    [
        IPNetwork.Parse("0.0.0.0/8"),
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("100.64.0.0/10"),
        IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("169.254.0.0/16"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("224.0.0.0/4"),
        IPNetwork.Parse("::/128"),
        IPNetwork.Parse("::1/128"),
        IPNetwork.Parse("fc00::/7"),
        IPNetwork.Parse("fe80::/10"),
        IPNetwork.Parse("ff00::/8")
    ];

    private readonly HttpWorkerSecurityOptions _security;
    private readonly IPNetwork[] _blockedNetworks;
    private readonly IPNetwork[] _allowedNetworks;

    /// <summary>
    /// Creates the policy. Throws if a configured CIDR is invalid.
    /// </summary>
    public HttpTargetPolicy(IOptions<HttpWorkerOptions> options)
    {
        _security = options.Value.Security ?? new HttpWorkerSecurityOptions();
        _blockedNetworks = ParseNetworks(_security.BlockedCidrs, nameof(_security.BlockedCidrs));
        _allowedNetworks = ParseNetworks(_security.AllowedCidrs, nameof(_security.AllowedCidrs));
    }

    /// <summary>
    /// Whether any rule limits where requests can go.
    /// </summary>
    public bool RestrictsTargets => _security.AllowedHosts?.Count > 0 || _security.BlockPrivateNetworks || _blockedNetworks.Length > 0;

    /// <summary>
    /// Refuses features the job data asks for that this worker does not allow.
    /// </summary>
    public void EnsureJobSettingsAllowed(bool ignoreSslErrors, bool usesProxy, bool usesThumbprintCertificate)
    {
        if (ignoreSslErrors && !_security.AllowIgnoreSslErrors)
            throw new PermanentJobException("IgnoreSslErrors is not allowed on this worker (HttpWorker:Security:AllowIgnoreSslErrors).");

        if (usesProxy && !_security.AllowProxy)
            throw new PermanentJobException("Proxy is not allowed on this worker (HttpWorker:Security:AllowProxy).");

        if (usesThumbprintCertificate && !_security.AllowThumbprintCertificates)
            throw new PermanentJobException("Client certificates by thumbprint are not allowed on this worker (HttpWorker:Security:AllowThumbprintCertificates).");
    }

    /// <summary>
    /// Refuses a URL whose host is not allowed. Literal IP addresses are checked here; host names are resolved at connect time.
    /// </summary>
    public void EnsureUrlAllowed(string url)
    {
        if (!RestrictsTargets)
            return;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new PermanentJobException($"'{url}' is not an absolute URL.");

        try
        {
            EnsureHostAllowed(uri.IdnHost);

            if (IPAddress.TryParse(uri.IdnHost, out var address) && !IsAddressAllowed(address))
                throw new HttpTargetBlockedException($"Address {address} is not allowed on this worker.");
        }
        catch (HttpTargetBlockedException ex)
        {
            throw new PermanentJobException(ex.Message, ex);
        }
    }

    /// <summary>
    /// Whether a connection to this address is allowed.
    /// </summary>
    public bool IsAddressAllowed(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (_allowedNetworks.Any(n => n.Contains(address)))
            return true;

        if (_blockedNetworks.Any(n => n.Contains(address)))
            return false;

        return !_security.BlockPrivateNetworks || !_privateNetworks.Any(n => n.Contains(address));
    }

    /// <summary>
    /// Makes the handler check every connection it opens. Does nothing when no rule is configured.
    /// </summary>
    public void Apply(SocketsHttpHandler handler)
    {
        if (RestrictsTargets)
            handler.ConnectCallback = ConnectAsync;
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;

        EnsureHostAllowed(host);

        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, cancellationToken);

        // Dial the validated addresses themselves: resolving again at connect time would reopen the rebinding window
        var allowed = addresses.Where(IsAddressAllowed).ToArray();

        if (allowed.Length == 0)
            throw new HttpTargetBlockedException($"Host '{host}' resolves only to addresses that are not allowed on this worker.");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, cancellationToken);

            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private void EnsureHostAllowed(string host)
    {
        if (_security.AllowedHosts is not { Count: > 0 })
            return;

        if (!_security.AllowedHosts.Any(pattern => HostMatches(pattern, host)))
            throw new HttpTargetBlockedException($"Host '{host}' is not in the allowed hosts of this worker.");
    }

    private static bool HostMatches(string pattern, string host) => pattern.StartsWith("*.", StringComparison.Ordinal)
        ? host.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase) && host.Length > pattern.Length - 1
        : host.Equals(pattern, StringComparison.OrdinalIgnoreCase);

    private static IPNetwork[] ParseNetworks(List<string> cidrs, string settingName)
    {
        if (cidrs is not { Count: > 0 })
            return [];

        return [.. cidrs.Select(cidr => IPNetwork.TryParse(cidr, out var network)
            ? network
            : throw new InvalidOperationException($"HttpWorker:Security:{settingName} contains an invalid CIDR: '{cidr}'"))];
    }
}
