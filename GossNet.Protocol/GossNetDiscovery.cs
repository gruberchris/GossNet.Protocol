namespace GossNet.Protocol;

/// <summary>
/// Builds the <see cref="INodeDiscovery"/> implementation a configuration asks for.
/// </summary>
internal static class GossNetDiscovery
{
    /// <summary>
    /// Resolves the discovery provider for a configuration.
    /// </summary>
    /// <param name="configuration">The node configuration.</param>
    /// <returns>
    /// The provider, and whether the node owns it. Only providers built here from
    /// <see cref="GossNetConfiguration.NodeDiscovery"/> are owned: a caller-supplied
    /// instance or factory result may be shared between nodes, so disposing it would be
    /// the node reaching outside its own lifetime.
    /// </returns>
    /// <exception cref="NotSupportedException">
    /// The configured mechanism is provided by a separate package that has not been supplied.
    /// </exception>
    /// <remarks>
    /// Called during node construction so a misconfigured node fails immediately.
    /// Consul, Kubernetes and Docker used to return an empty neighbour list, so such a
    /// node reported success while gossiping to nobody.
    /// </remarks>
    internal static (INodeDiscovery Provider, bool Owned) CreateProvider(GossNetConfiguration configuration)
    {
        if (configuration.DiscoveryProvider is not null)
        {
            return (configuration.DiscoveryProvider, false);
        }

        if (configuration.DiscoveryProviderFactory is not null)
        {
            var created = configuration.DiscoveryProviderFactory(configuration)
                ?? throw new NodeDiscoveryException($"{nameof(GossNetConfiguration.DiscoveryProviderFactory)} returned null.");

            return (created, false);
        }

        INodeDiscovery provider = configuration.NodeDiscovery switch
        {
            NodeDiscovery.Dns => new DnsNodeDiscovery(configuration),
            NodeDiscovery.StaticList => new StaticListNodeDiscovery(configuration),
            NodeDiscovery.PeerExchange => new PeerExchangeNodeDiscovery(configuration),
            NodeDiscovery.Multicast => new MulticastNodeDiscovery(configuration),
            NodeDiscovery.Consul => throw RequiresPackage("Consul", "GossNet.Discovery.Consul", "ConsulNodeDiscovery"),
            NodeDiscovery.Kubernetes => throw RequiresPackage("Kubernetes", "GossNet.Discovery.Kubernetes", "KubernetesNodeDiscovery"),
            NodeDiscovery.Docker => throw RequiresPackage("Docker", "GossNet.Discovery.Docker", "DockerNodeDiscovery"),

            // The paramName/value arguments were previously passed the enum value as the
            // parameter name.
            _ => throw new ArgumentOutOfRangeException(
                nameof(configuration),
                configuration.NodeDiscovery,
                $"Unknown {nameof(NodeDiscovery)} value.")
        };

        return (provider, true);
    }

    private static NotSupportedException RequiresPackage(string mechanism, string package, string type) =>
        new($"{mechanism} discovery is not built into GossNet.Protocol. Install the {package} package and assign " +
            $"a {type} to {nameof(GossNetConfiguration)}.{nameof(GossNetConfiguration.DiscoveryProvider)}.");
}
