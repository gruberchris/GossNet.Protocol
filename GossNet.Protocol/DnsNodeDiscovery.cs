using System.Diagnostics;
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
public sealed class DnsNodeDiscovery : INodeDiscovery
{
    /// <summary>Default lifetime of a resolved neighbour list.</summary>
    public static readonly TimeSpan DefaultCacheDuration = TimeSpan.FromSeconds(30);

    private readonly GossNetConfiguration _configuration;
    private readonly IDnsResolver _resolver;
    private readonly TimeSpan _cacheDuration;

#if NET9_0_OR_GREATER
    private readonly Lock _gate = new();
#else
    private readonly object _gate = new();
#endif

    private IReadOnlyList<GossNetNodeHostEntry> _cached = [];
    private long _cachedAt = long.MinValue;

    /// <summary>
    /// Initializes DNS-based discovery.
    /// </summary>
    /// <param name="configuration">The node configuration supplying the hostname and port.</param>
    /// <param name="cacheDuration">How long to reuse a resolved list. Defaults to 30 seconds.</param>
    /// <param name="resolver">Resolver to use; the system resolver by default.</param>
    public DnsNodeDiscovery(GossNetConfiguration configuration, TimeSpan? cacheDuration = null, IDnsResolver? resolver = null)
    {
        _configuration = configuration;
        _resolver = resolver ?? SystemDnsResolver.Instance;
        _cacheDuration = cacheDuration ?? DefaultCacheDuration;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<GossNetNodeHostEntry>> GetNeighboursAsync(CancellationToken cancellationToken = default)
    {
        if (TryGetFresh(out var cached))
        {
            return cached;
        }

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
            // belongs to this machine is this node. Without this the node unicasts every
            // message back to itself: self-exclusion used to compare the configured
            // *hostname* against resolved *IP addresses*, which never matched.
            if (local.Contains(key) || !seen.Add(key))
            {
                continue;
            }

            neighbours.Add(new GossNetNodeHostEntry { Hostname = address.ToString(), Port = _configuration.Port });
        }

        lock (_gate)
        {
            _cached = neighbours;
            _cachedAt = Stopwatch.GetTimestamp();
        }

        return neighbours;
    }

    private bool TryGetFresh(out IReadOnlyList<GossNetNodeHostEntry> neighbours)
    {
        lock (_gate)
        {
            neighbours = _cached;

            if (_cachedAt == long.MinValue)
            {
                return false;
            }

            var age = TimeSpan.FromSeconds((double)(Stopwatch.GetTimestamp() - _cachedAt) / Stopwatch.Frequency);

            return age < _cacheDuration;
        }
    }

    private static string Normalize(IPAddress address) =>
        (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
}
