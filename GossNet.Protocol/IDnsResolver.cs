using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace GossNet.Protocol;

/// <summary>
/// DNS and local-address lookups used by <see cref="DnsNodeDiscovery"/>.
/// </summary>
/// <remarks>Exists so discovery can be tested without touching the network.</remarks>
public interface IDnsResolver
{
    /// <summary>Resolves every address registered for a hostname.</summary>
    ValueTask<IReadOnlyList<IPAddress>> GetHostAddressesAsync(string hostname, CancellationToken cancellationToken = default);

    /// <summary>Gets the addresses belonging to the local machine.</summary>
    ValueTask<IReadOnlyList<IPAddress>> GetLocalAddressesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IDnsResolver"/> backed by the system resolver.
/// </summary>
public sealed class SystemDnsResolver : IDnsResolver
{
    /// <summary>Gets the shared instance.</summary>
    public static SystemDnsResolver Instance { get; } = new();

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<IPAddress>> GetHostAddressesAsync(string hostname, CancellationToken cancellationToken = default)
    {
#if NET6_0_OR_GREATER
        return await Dns.GetHostAddressesAsync(hostname, cancellationToken).ConfigureAwait(false);
#else
        cancellationToken.ThrowIfCancellationRequested();
        return await Dns.GetHostAddressesAsync(hostname).ConfigureAwait(false);
#endif
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<IPAddress>> GetLocalAddressesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var addresses = new List<IPAddress> { IPAddress.Loopback, IPAddress.IPv6Loopback };

        // Enumerating interfaces catches addresses that a reverse lookup on the machine
        // name would miss, which matters because anything missed here is treated as a
        // neighbour and the node ends up gossiping with itself.
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                addresses.Add(unicast.Address);
            }
        }

        return new ValueTask<IReadOnlyList<IPAddress>>(addresses);
    }

    internal static bool IsRoutable(IPAddress address) =>
        address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6;
}
