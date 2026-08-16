namespace GossNet.Protocol;

/// <summary>
/// Mechanism used to discover a node's neighbours.
/// </summary>
public enum NodeDiscovery
{
    /// <summary>Resolve neighbours from DNS records for the configured hostname.</summary>
    Dns,

    /// <summary>Resolve neighbours from a Consul service catalog.</summary>
    Consul,

    /// <summary>Resolve neighbours from the Kubernetes API.</summary>
    Kubernetes,

    /// <summary>Resolve neighbours from the Docker API.</summary>
    Docker,

    /// <summary>Use the explicitly configured <see cref="GossNetConfiguration.StaticNodes"/>.</summary>
    StaticList
}

/// <summary>
/// Settings for a <see cref="GossNetNode{T}"/>.
/// </summary>
public class GossNetConfiguration
{
    /// <summary>Gets the hostname this node identifies itself as.</summary>
    public required string Hostname { get; init; }

    /// <summary>Gets the UDP port this node listens on. Defaults to 9055.</summary>
    public int Port { get; init; } = 9055;

    /// <summary>Gets the neighbour discovery mechanism.</summary>
    /// <remarks>Ignored when <see cref="DiscoveryProvider"/> is set.</remarks>
    public NodeDiscovery NodeDiscovery { get; init; }

    /// <summary>
    /// Gets an explicit discovery provider, overriding <see cref="NodeDiscovery"/>.
    /// </summary>
    /// <remarks>
    /// This is how mechanisms outside the core package are supplied — the Consul,
    /// Kubernetes and Docker providers ship separately so the core package stays free
    /// of their dependencies. Any custom <see cref="INodeDiscovery"/> works here too.
    /// </remarks>
    public INodeDiscovery? DiscoveryProvider { get; init; }

    /// <summary>Gets the neighbours used when <see cref="NodeDiscovery"/> is <see cref="NodeDiscovery.StaticList"/>.</summary>
    public IEnumerable<GossNetNodeHostEntry> StaticNodes { get; init; } = new List<GossNetNodeHostEntry>();

    /// <summary>Gets how long a message id is remembered for de-duplication. Defaults to 600 seconds.</summary>
    public int MessageTtlSeconds { get; init; } = 600;

    /// <summary>
    /// Gets the maximum number of undelivered messages buffered per subscriber. Defaults to 1024.
    /// </summary>
    /// <remarks>
    /// Each subscriber has its own bounded buffer. When a subscriber falls behind, its
    /// oldest buffered message is dropped rather than blocking the receive loop or
    /// growing without limit — a slow subscriber degrades only itself. Gossip is
    /// eventually consistent and messages are re-delivered by other nodes, so dropping
    /// is preferable to stalling the node.
    /// </remarks>
    public int SubscriberQueueCapacity { get; init; } = 1024;
}
