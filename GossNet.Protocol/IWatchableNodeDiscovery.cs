namespace GossNet.Protocol;

/// <summary>
/// A discovery provider whose backend can push membership changes instead of being polled.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="INodeDiscovery"/> is pull-based, and providers that query a remote backend
/// cache their results, so a node joining or leaving takes up to the cache duration to be
/// noticed. Backends with a change feed — etcd watches, Consul blocking queries, the
/// Kubernetes watch API — can do better, and this is how they say so.
/// </para>
/// <para>
/// Implementing it is optional. A node checks for it when starting and falls back to the
/// cached poll otherwise, so no existing provider is affected.
/// </para>
/// </remarks>
public interface IWatchableNodeDiscovery : INodeDiscovery
{
    /// <summary>
    /// Yields the neighbour list each time the backend reports it has changed.
    /// </summary>
    /// <param name="cancellationToken">Ends the watch.</param>
    /// <returns>
    /// A sequence of complete neighbour lists, not deltas. The node replaces its view with
    /// each one, so a partial list would silently shrink the cluster.
    /// </returns>
    /// <remarks>
    /// The sequence should yield the current membership promptly on subscription rather than
    /// waiting for the first change, otherwise a node has no neighbours until something
    /// happens to be added or removed.
    /// </remarks>
    IAsyncEnumerable<IReadOnlyList<GossNetNodeHostEntry>> WatchAsync(CancellationToken cancellationToken);
}
