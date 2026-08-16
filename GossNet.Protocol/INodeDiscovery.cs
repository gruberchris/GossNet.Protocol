namespace GossNet.Protocol;

/// <summary>
/// Resolves the neighbours a node gossips with.
/// </summary>
/// <remarks>
/// Implement this to plug in a discovery mechanism the core package does not ship,
/// and supply it through <see cref="GossNetConfiguration.DiscoveryProvider"/>.
/// Implementations are called on the message path, so they should cache their results.
/// </remarks>
public interface INodeDiscovery
{
    /// <summary>
    /// Gets the current neighbours, excluding the node itself.
    /// </summary>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    ValueTask<IReadOnlyList<GossNetNodeHostEntry>> GetNeighboursAsync(CancellationToken cancellationToken = default);
}
