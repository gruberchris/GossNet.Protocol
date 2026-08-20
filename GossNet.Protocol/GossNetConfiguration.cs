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
    StaticList,

    /// <summary>
    /// Start from <see cref="GossNetConfiguration.StaticNodes"/> as seeds and learn the rest
    /// of the network from the messages themselves.
    /// </summary>
    PeerExchange,

    /// <summary>
    /// Announce to, and listen on, a multicast group. Zero configuration, but local network
    /// only.
    /// </summary>
    Multicast
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

    /// <summary>
    /// Gets a factory that builds the discovery provider from this configuration.
    /// </summary>
    /// <remarks>
    /// Use this when a provider needs the node's own identity — to exclude the node
    /// from its own results, for example — which is otherwise circular, since the
    /// provider would have to be constructed before the configuration that describes it.
    /// Takes precedence over <see cref="NodeDiscovery"/>, but not over
    /// <see cref="DiscoveryProvider"/>.
    /// </remarks>
    /// <example>
    /// <code>
    /// var configuration = new GossNetConfiguration
    /// {
    ///     Hostname = "10.0.0.1",
    ///     DiscoveryProviderFactory = cfg => new ConsulNodeDiscovery(cfg, consulOptions)
    /// };
    /// </code>
    /// </example>
    public Func<GossNetConfiguration, INodeDiscovery>? DiscoveryProviderFactory { get; init; }

    /// <summary>Gets the neighbours used when <see cref="NodeDiscovery"/> is <see cref="NodeDiscovery.StaticList"/>.</summary>
    public IEnumerable<GossNetNodeHostEntry> StaticNodes { get; init; } = new List<GossNetNodeHostEntry>();

    /// <summary>Gets how long a message id is remembered for de-duplication. Defaults to 600 seconds.</summary>
    public int MessageTtlSeconds { get; init; } = 600;

    /// <summary>
    /// Gets the protector that authenticates datagrams, or null for plaintext.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When set, every outgoing gossip message and multicast discovery announcement is
    /// wrapped by the protector, and every received datagram that fails verification is
    /// dropped before it is parsed. Anything that can reach the UDP port can otherwise
    /// inject messages into subscribers and, with peer exchange, poison the peer table.
    /// </para>
    /// <para>
    /// Use <see cref="HmacDatagramProtector"/> with the same key on every node. All nodes
    /// in a cluster must agree: a node without the protector cannot talk to nodes with it.
    /// </para>
    /// </remarks>
    public IDatagramProtector? DatagramProtector { get; init; }

    /// <summary>
    /// Gets how old a received message may be before it is dropped. Defaults to
    /// <see cref="MessageTtlSeconds"/>. Only applied when <see cref="DatagramProtector"/>
    /// is set.
    /// </summary>
    /// <remarks>
    /// The de-duplication cache already blocks replays within <see cref="MessageTtlSeconds"/>;
    /// this window closes the gap after it, where a captured datagram could otherwise be
    /// replayed verbatim once its id has been forgotten. It assumes reasonably synchronized
    /// clocks across nodes — only messages <em>older</em> than the window are rejected, so
    /// a node with a fast clock is tolerated.
    /// </remarks>
    public TimeSpan? MessageMaxAge { get; init; }

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
