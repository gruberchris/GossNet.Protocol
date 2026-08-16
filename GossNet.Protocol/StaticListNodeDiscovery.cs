namespace GossNet.Protocol;

/// <summary>
/// Discovers neighbours from an explicitly configured list.
/// </summary>
public sealed class StaticListNodeDiscovery : INodeDiscovery
{
    private readonly IReadOnlyList<GossNetNodeHostEntry> _neighbours;

    /// <summary>
    /// Initializes discovery from <see cref="GossNetConfiguration.StaticNodes"/>.
    /// </summary>
    /// <param name="configuration">The node configuration.</param>
    public StaticListNodeDiscovery(GossNetConfiguration configuration)
    {
        var neighbours = new List<GossNetNodeHostEntry>();

        foreach (var node in configuration.StaticNodes)
        {
            // Listing yourself is a common configuration slip; sending to yourself is
            // pure waste since the message is discarded as a duplicate on arrival.
            if (node.Hostname == configuration.Hostname && node.Port == configuration.Port)
            {
                continue;
            }

            neighbours.Add(node);
        }

        _neighbours = neighbours;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<GossNetNodeHostEntry>> GetNeighboursAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return new ValueTask<IReadOnlyList<GossNetNodeHostEntry>>(_neighbours);
    }
}
