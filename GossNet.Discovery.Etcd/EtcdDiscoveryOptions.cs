namespace GossNet.Discovery.Etcd;

/// <summary>
/// Settings for <see cref="EtcdNodeDiscovery"/>.
/// </summary>
public sealed class EtcdDiscoveryOptions
{
    /// <summary>Gets the etcd endpoint, e.g. <c>http://localhost:2379</c>.</summary>
    /// <remarks>Ignored when a registry is supplied directly.</remarks>
    public string? ConnectionString { get; init; }

    /// <summary>Gets the key prefix cluster members register under. Defaults to <c>/gossnet/members/</c>.</summary>
    /// <remarks>Use a different prefix per cluster sharing an etcd instance.</remarks>
    public string Prefix { get; init; } = "/gossnet/members/";

    /// <summary>
    /// Gets the lease time-to-live for this node's registration. Defaults to fifteen seconds.
    /// </summary>
    /// <remarks>
    /// etcd removes the key automatically when the lease is not kept alive, so a node that
    /// crashes disappears on its own without anything having to notice.
    /// </remarks>
    public TimeSpan LeaseTtl { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Gets how long a resolved member list is reused. Defaults to five seconds.</summary>
    /// <remarks>
    /// Only consulted when the watch is not running. With <see cref="IWatchableNodeDiscovery"/>
    /// active, membership arrives as it changes and this is not on the path.
    /// </remarks>
    public TimeSpan? CacheDuration { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets the username for authenticated clusters.</summary>
    public string? Username { get; init; }

    /// <summary>Gets the password for authenticated clusters.</summary>
    public string? Password { get; init; }
}
