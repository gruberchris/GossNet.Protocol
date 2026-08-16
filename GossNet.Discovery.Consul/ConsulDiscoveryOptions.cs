namespace GossNet.Discovery.Consul;

/// <summary>
/// Settings for <see cref="ConsulNodeDiscovery"/>.
/// </summary>
public sealed class ConsulDiscoveryOptions
{
    /// <summary>Gets the Consul service name the gossip nodes register under.</summary>
    public required string ServiceName { get; init; }

    /// <summary>Gets the Consul agent address. Defaults to the client's own default (http://localhost:8500).</summary>
    public Uri? Address { get; init; }

    /// <summary>Gets an optional ACL token.</summary>
    public string? Token { get; init; }

    /// <summary>Gets an optional datacenter to query instead of the agent's own.</summary>
    public string? Datacenter { get; init; }

    /// <summary>Gets an optional tag that instances must carry.</summary>
    public string? Tag { get; init; }

    /// <summary>
    /// Gets a value indicating whether only instances whose health checks are passing
    /// are returned. Defaults to <c>true</c>.
    /// </summary>
    /// <remarks>
    /// Gossiping at an instance Consul already knows is unhealthy wastes datagrams, so
    /// unhealthy instances are filtered out by default.
    /// </remarks>
    public bool PassingOnly { get; init; } = true;

    /// <summary>Gets how long a resolved neighbour list is reused. Defaults to 30 seconds.</summary>
    public TimeSpan? CacheDuration { get; init; }
}
