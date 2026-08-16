namespace GossNet.Discovery.Docker;

/// <summary>
/// Settings for <see cref="DockerNodeDiscovery"/>.
/// </summary>
public sealed class DockerDiscoveryOptions
{
    /// <summary>
    /// Gets the container label identifying the gossip containers, for example
    /// <c>app=gossnet</c> or just <c>gossnet</c> to match on the key alone.
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    /// Gets the Docker endpoint. Defaults to the local daemon — the Unix socket on
    /// Linux and macOS, the named pipe on Windows.
    /// </summary>
    public Uri? Endpoint { get; init; }

    /// <summary>
    /// Gets the Docker network whose address should be used.
    /// </summary>
    /// <remarks>
    /// A container attached to several networks has an address on each. Without a name
    /// the first address found is used, which is ambiguous for multi-homed containers,
    /// so set this whenever containers join more than one network.
    /// </remarks>
    public string? NetworkName { get; init; }

    /// <summary>
    /// Gets the port neighbours listen on. Defaults to the node's own configured port.
    /// </summary>
    public int? Port { get; init; }

    /// <summary>
    /// Gets a value indicating whether only running containers are returned. Defaults to <c>true</c>.
    /// </summary>
    /// <remarks>
    /// A created or exited container has no listening socket, so gossiping at it only
    /// produces dropped datagrams.
    /// </remarks>
    public bool RunningOnly { get; init; } = true;

    /// <summary>Gets how long a resolved neighbour list is reused. Defaults to 30 seconds.</summary>
    public TimeSpan? CacheDuration { get; init; }
}
