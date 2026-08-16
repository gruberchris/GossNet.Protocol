using System.Net;

namespace GossNet.Protocol;

/// <summary>
/// Discovers neighbours from the addresses registered for the configured hostname.
/// </summary>
/// <remarks>
/// <para>
/// Every node in the network must share a hostname that resolves to all of their
/// addresses, typically via multiple A records or a headless service.
/// </para>
/// <para>
/// Results are cached for a short window. Resolution previously ran on every single
/// message, and used the blocking <c>Dns.GetHostEntry</c> inside an async method.
/// </para>
/// </remarks>
public sealed class DnsNodeDiscovery : CachingNodeDiscovery
{
    private readonly GossNetConfiguration _configuration;
    private readonly IDnsResolver _resolver;

    /// <summary>
    /// Initializes DNS-based discovery.
    /// </summary>
    /// <param name="configuration">The node configuration supplying the hostname and port.</param>
    /// <param name="cacheDuration">How long to reuse a resolved list. Defaults to 30 seconds.</param>
    /// <param name="resolver">Resolver to use; the system resolver by default.</param>
    public DnsNodeDiscovery(GossNetConfiguration configuration, TimeSpan? cacheDuration = null, IDnsResolver? resolver = null)
        : base(cacheDuration)
    {
        _configuration = configuration;
        _resolver = resolver ?? SystemDnsResolver.Instance;
    }

    /// <inheritdoc />
    protected override async ValueTask<IReadOnlyList<GossNetNodeHostEntry>> ResolveAsync(CancellationToken cancellationToken)
    {
        var addresses = await _resolver.GetHostAddressesAsync(_configuration.Hostname, cancellationToken).ConfigureAwait(false);
        var localAddresses = await _resolver.GetLocalAddressesAsync(cancellationToken).ConfigureAwait(false);

        var local = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var address in localAddresses)
        {
            local.Add(Normalize(address));
        }

        var neighbours = new List<GossNetNodeHostEntry>(addresses.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var address in addresses)
        {
            var key = Normalize(address);

            // Every entry resolved here carries this node's own port, so an address that
            // belongs to this machine is this node. Self-exclusion is address-based
            // rather than the shared hostname comparison, because DNS hands back IP
            // addresses: comparing the configured *hostname* against them never matched,
            // and the node unicast every message back to itself.
            if (local.Contains(key) || !seen.Add(key))
            {
                continue;
            }

            neighbours.Add(new GossNetNodeHostEntry { Hostname = address.ToString(), Port = _configuration.Port });
        }

        return neighbours;
    }

    private static string Normalize(IPAddress address) =>
        (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
}
