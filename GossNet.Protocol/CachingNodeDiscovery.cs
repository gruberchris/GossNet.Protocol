using System.Diagnostics;

namespace GossNet.Protocol;

/// <summary>
/// Base class for discovery providers that query a remote backend, adding a short-lived
/// result cache.
/// </summary>
/// <remarks>
/// Discovery runs on the message path, so an uncached provider issues a backend call for
/// every message sent. Deriving providers implement <see cref="ResolveAsync"/> and get
/// caching for free.
/// </remarks>
public abstract class CachingNodeDiscovery : INodeDiscovery
{
    /// <summary>Default lifetime of a resolved neighbour list.</summary>
    public static readonly TimeSpan DefaultCacheDuration = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _cacheDuration;

#if NET9_0_OR_GREATER
    private readonly Lock _gate = new();
#else
    private readonly object _gate = new();
#endif

    private IReadOnlyList<GossNetNodeHostEntry> _cached = [];
    private long _cachedAt = long.MinValue;

    /// <summary>
    /// Initializes the cache.
    /// </summary>
    /// <param name="cacheDuration">How long to reuse a resolved list. Defaults to 30 seconds.</param>
    protected CachingNodeDiscovery(TimeSpan? cacheDuration = null) =>
        _cacheDuration = cacheDuration ?? DefaultCacheDuration;

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<GossNetNodeHostEntry>> GetNeighboursAsync(CancellationToken cancellationToken = default)
    {
        if (TryGetFresh(out var cached))
        {
            return cached;
        }

        var neighbours = await ResolveAsync(cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            _cached = neighbours;
            _cachedAt = Stopwatch.GetTimestamp();
        }

        return neighbours;
    }

    /// <summary>
    /// Queries the backend for the current neighbours, excluding this node.
    /// </summary>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    protected abstract ValueTask<IReadOnlyList<GossNetNodeHostEntry>> ResolveAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Drops the entry matching a node's own hostname and port.
    /// </summary>
    /// <param name="candidates">Discovered entries.</param>
    /// <param name="configuration">The configuration identifying this node.</param>
    /// <remarks>
    /// A node normally registers itself with the discovery backend, so it appears in
    /// its own results. Sending to yourself is pure waste: the message is discarded as
    /// a duplicate on arrival.
    /// </remarks>
    protected static IReadOnlyList<GossNetNodeHostEntry> ExcludeSelf(
        IEnumerable<GossNetNodeHostEntry> candidates,
        GossNetConfiguration configuration)
    {
        var neighbours = new List<GossNetNodeHostEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            if (candidate.Hostname == configuration.Hostname && candidate.Port == configuration.Port)
            {
                continue;
            }

            if (seen.Add(candidate.ToString()))
            {
                neighbours.Add(candidate);
            }
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
}
