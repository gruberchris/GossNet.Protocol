namespace GossNet.Protocol;

/// <summary>
/// Settings for <see cref="MulticastNodeDiscovery"/>.
/// </summary>
public sealed class MulticastDiscoveryOptions
{
    /// <summary>
    /// Gets the multicast group to announce on. Defaults to <c>239.255.42.99</c>.
    /// </summary>
    /// <remarks>
    /// In the administratively scoped block (239.0.0.0/8), which is reserved for private
    /// use and is not forwarded off the local network by default.
    /// </remarks>
    public string GroupAddress { get; init; } = "239.255.42.99";

    /// <summary>
    /// Gets the port announcements are exchanged on. Defaults to 9056.
    /// </summary>
    /// <remarks>
    /// One higher than the default gossip port, and deliberately not the same: discovery
    /// runs on its own socket so announcements never reach the message receive loop.
    /// </remarks>
    public int Port { get; init; } = 9056;

    /// <summary>Gets how often this node announces itself. Defaults to two seconds.</summary>
    public TimeSpan AnnounceInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets how long a peer is kept after its last announcement. Defaults to ten seconds.
    /// </summary>
    /// <remarks>
    /// Should be a small multiple of <see cref="AnnounceInterval"/> so a peer survives a
    /// few lost announcements. Multicast is unreliable; treating one miss as a departure
    /// would make the neighbour list flap.
    /// </remarks>
    public TimeSpan PeerTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets the multicast TTL, the number of routers an announcement may cross. Defaults to 1.
    /// </summary>
    /// <remarks>A TTL of 1 keeps announcements on the local subnet.</remarks>
    public int TimeToLive { get; init; } = 1;

    /// <summary>
    /// Gets a value indicating whether announcements loop back to the sending host.
    /// Defaults to true.
    /// </summary>
    /// <remarks>
    /// Required for nodes sharing a machine to find each other, which is how this is
    /// usually first tried. A node ignores its own announcement regardless.
    /// </remarks>
    public bool EnableLoopback { get; init; } = true;
}
