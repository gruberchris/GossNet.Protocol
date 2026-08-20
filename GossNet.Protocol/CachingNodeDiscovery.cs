using System.Diagnostics;

namespace GossNet.Protocol;

/// <summary>
/// Base class for discovery providers that query a remote backend, adding a short-lived
/// result cache.
/// </summary>
/// <remarks>
/// <para>
/// Discovery runs on the message path, so an uncached provider issues a backend call for
/// every message sent. Deriving providers implement <see cref="ResolveAsync"/> and get
/// caching for free.
/// </para>
/// <para>
/// Refreshes are single-flight: when the cache expires under concurrent senders, one
/// caller queries the backend and the rest reuse its result rather than each issuing
/// their own query.
/// </para>
/// <para>
/// When a refresh fails and a previous result exists, the previous result is served and
/// the failure is not surfaced: membership that is slightly stale keeps the cluster
/// gossiping through a backend outage, which is strictly better than every message
/// failing. The failure is surfaced only when there has never been a successful resolve,
/// because then an unreachable backend would be indistinguishable from a network with
/// nobody else in it.
/// </para>
/// </remarks>
public abstract class CachingNodeDiscovery : INodeDiscovery
{
    /// <summary>Default lifetime of a resolved neighbour list.</summary>
    public static readonly TimeSpan DefaultCacheDuration = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _cacheDuration;

    /// <summary>Makes cache refreshes single-flight.</summary>
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

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
        cancellationToken.ThrowIfCancellationRequested();

        if (TryGetFresh(out var cached))
        {
            return cached;
        }

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Whoever held the gate first may have already refreshed the cache.
            if (TryGetFresh(out cached))
            {
                return cached;
            }

            IReadOnlyList<GossNetNodeHostEntry> neighbours;

            try
            {
                neighbours = await ResolveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                lock (_gate)
                {
                    // Serve the previous list rather than failing every message for the
                    // duration of a backend outage. _cachedAt is deliberately not
                    // refreshed, so the next call tries the backend again.
                    if (_cachedAt != long.MinValue)
                    {
                        return _cached;
                    }
                }

                throw;
            }

            lock (_gate)
            {
                _cached = neighbours;
                _cachedAt = Stopwatch.GetTimestamp();
            }

            return neighbours;
        }
        finally
        {
            _refreshGate.Release();
        }
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
