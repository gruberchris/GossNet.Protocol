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
    /// <remarks>Only consulted when the watch is not running.</remarks>
    public TimeSpan? CacheDuration { get; init; }

    /// <summary>
    /// Gets how long a blocking query waits for a change before returning unchanged.
    /// Defaults to five minutes.
    /// </summary>
    /// <remarks>
    /// This is the Consul blocking-query <c>wait</c> parameter, not a timeout on discovery:
    /// a query that returns unchanged is simply re-issued. Consul caps it at ten minutes and
    /// adds its own jitter, so a whole cluster does not re-query in lockstep.
    /// </remarks>
    public TimeSpan WatchWaitTime { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets how long to wait before re-establishing a blocking query that failed.
    /// Defaults to two seconds.
    /// </summary>
    /// <remarks>
    /// Without a pause, an agent that is down turns the watch into a hot loop against a
    /// refused connection.
    /// </remarks>
    public TimeSpan WatchRetryDelay { get; init; } = TimeSpan.FromSeconds(2);
}
