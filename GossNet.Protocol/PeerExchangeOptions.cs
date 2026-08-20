namespace GossNet.Protocol;

/// <summary>
/// Settings for <see cref="PeerExchangeNodeDiscovery"/>.
/// </summary>
public sealed class PeerExchangeOptions
{
    /// <summary>
    /// Gets how long a learned peer is kept after it was last seen in a message.
    /// Defaults to five minutes.
    /// </summary>
    /// <remarks>
    /// This is a liveness guess, not a failure detector: a peer that stops appearing in
    /// notified lists has either gone away or simply not been on any recent path. Set it
    /// comfortably longer than the interval at which the application sends messages, or
    /// live peers will be forgotten and re-learned in a cycle.
    /// </remarks>
    public TimeSpan PeerTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets the maximum number of learned peers retained. Defaults to 256.
    /// </summary>
    /// <remarks>
    /// Bounded because the peer set grows with everything the node has ever heard from.
    /// When full, the least recently seen peer is evicted. Seeds do not count towards this
    /// limit and are never evicted.
    /// </remarks>
    public int MaxPeers { get; init; } = 256;
}
