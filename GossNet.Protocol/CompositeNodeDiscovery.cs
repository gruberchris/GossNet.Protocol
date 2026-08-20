namespace GossNet.Protocol;

/// <summary>
/// Combines several discovery mechanisms into one neighbour list.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="GossNetConfiguration.DiscoveryProvider"/> holds a single provider, so without
/// this there is no way to express "static seeds as well as Consul", or "DNS with a static
/// fallback". Results from every child are unioned and de-duplicated.
/// </para>
/// <para>
/// This adds no cache of its own. Providers that query a remote backend already derive from
/// <see cref="CachingNodeDiscovery"/>, and a second layer of caching would only compound how
/// stale a result can be.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var configuration = new GossNetConfiguration
/// {
///     Hostname = "10.0.0.1",
///     StaticNodes = seeds,
///     DiscoveryProviderFactory = cfg => new CompositeNodeDiscovery(
///     [
///         new StaticListNodeDiscovery(cfg),
///         new ConsulNodeDiscovery(cfg, consulOptions)
///     ])
/// };
/// </code>
/// </example>
public sealed class CompositeNodeDiscovery : INodeDiscovery, IDisposable
{
    private readonly IReadOnlyList<INodeDiscovery> _providers;
    private readonly bool _ownsProviders;

    private bool _disposed;

    /// <summary>
    /// Initializes composite discovery.
    /// </summary>
    /// <param name="providers">The providers to combine, queried in the order given.</param>
    /// <param name="ownsProviders">
    /// Whether disposing this also disposes the children. False when the caller keeps using
    /// them elsewhere.
    /// </param>
    /// <exception cref="ArgumentException">No providers were supplied, or one was null.</exception>
    public CompositeNodeDiscovery(IEnumerable<INodeDiscovery> providers, bool ownsProviders = true)
    {
        // Thrown by hand rather than with ArgumentNullException.ThrowIfNull: this package
        // still targets netstandard2.0, where that helper does not exist.
        if (providers is null)
        {
            throw new ArgumentNullException(nameof(providers));
        }

        var resolved = new List<INodeDiscovery>(providers);

        if (resolved.Count == 0)
        {
            // An empty composite would report a network of one, which is exactly the
            // silent failure GossNetDiscovery.CreateProvider exists to prevent.
            throw new ArgumentException("At least one discovery provider is required.", nameof(providers));
        }

        if (resolved.Contains(null!))
        {
            throw new ArgumentException("Discovery providers cannot be null.", nameof(providers));
        }

        _providers = resolved;
        _ownsProviders = ownsProviders;
    }

    /// <summary>Gets the number of combined providers.</summary>
    public int ProviderCount => _providers.Count;

    /// <inheritdoc />
    /// <exception cref="NodeDiscoveryException">Every provider failed.</exception>
    /// <remarks>
    /// One failing provider is tolerated: an unreachable registry must not blind a cluster
    /// that also has static seeds. All of them failing is not tolerated, because returning
    /// an empty list would look identical to a network with nobody else in it.
    /// </remarks>
    public async ValueTask<IReadOnlyList<GossNetNodeHostEntry>> GetNeighboursAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(CompositeNodeDiscovery));
        }

        var neighbours = new List<GossNetNodeHostEntry>();
        var seen = new HashSet<GossNetNodeHostEntry>();
        List<Exception>? failures = null;

        foreach (var provider in _providers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<GossNetNodeHostEntry> resolved;

            try
            {
                resolved = await provider.GetNeighboursAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures ??= [];
                failures.Add(ex);
                continue;
            }

            foreach (var neighbour in resolved)
            {
                // GossNetNodeHostEntry implements IEquatable and GetHashCode, so the same
                // host and port discovered by two providers collapses to one entry.
                if (seen.Add(neighbour))
                {
                    neighbours.Add(neighbour);
                }
            }
        }

        if (failures is not null && failures.Count == _providers.Count)
        {
            throw new NodeDiscoveryException(
                $"All {_providers.Count} discovery providers failed.",
                new AggregateException(failures));
        }

        return neighbours;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (!_ownsProviders)
        {
            return;
        }

        foreach (var provider in _providers)
        {
            (provider as IDisposable)?.Dispose();
        }
    }
}
