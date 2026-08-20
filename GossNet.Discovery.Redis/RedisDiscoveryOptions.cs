namespace GossNet.Discovery.Redis;

/// <summary>
/// Settings for <see cref="RedisNodeDiscovery"/>.
/// </summary>
public sealed class RedisDiscoveryOptions
{
    /// <summary>Gets the Redis connection string, e.g. <c>localhost:6379</c>.</summary>
    /// <remarks>Ignored when a registry is supplied directly.</remarks>
    public string? ConnectionString { get; init; }

    /// <summary>Gets the sorted-set key holding cluster membership. Defaults to <c>gossnet:members</c>.</summary>
    /// <remarks>
    /// One key for the whole cluster. Use a different key per cluster sharing a Redis
    /// instance, the way you would a different Consul service name.
    /// </remarks>
    public string Key { get; init; } = "gossnet:members";

    /// <summary>Gets how often this node refreshes its registration. Defaults to five seconds.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets how long a node is considered live after its last heartbeat. Defaults to
    /// twenty seconds.
    /// </summary>
    /// <remarks>
    /// Should be several heartbeat intervals: a node that misses one because of a GC pause
    /// or a slow round trip has not left the cluster.
    /// </remarks>
    public TimeSpan RegistrationTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Gets how long a resolved member list is reused. Defaults to five seconds.</summary>
    /// <remarks>
    /// Shorter than the other providers' 30 seconds because membership here changes at
    /// heartbeat speed, and a round trip to Redis is cheap.
    /// </remarks>
    public TimeSpan? CacheDuration { get; init; } = TimeSpan.FromSeconds(5);
}
