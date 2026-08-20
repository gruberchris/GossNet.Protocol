namespace GossNet.Protocol;

/// <summary>
/// A discovery provider that learns about neighbours from the gossip traffic itself.
/// </summary>
/// <remarks>
/// <para>
/// Every other mechanism asks something external who the peers are. This one does not need
/// to: each message carries <see cref="GossNetMessageBase.NotifiedNodes"/>, the set of nodes
/// already known to have seen it, so the network is continuously describing its own
/// membership.
/// </para>
/// <para>
/// A node calls <see cref="Observe"/> only when its provider implements this interface, so
/// existing providers are unaffected.
/// </para>
/// </remarks>
public interface IObservingNodeDiscovery : INodeDiscovery
{
    /// <summary>
    /// Reports nodes seen in a message's notified list.
    /// </summary>
    /// <param name="seen">The nodes named in the message.</param>
    /// <remarks>
    /// Called on the receive loop and on the send path, so implementations must return
    /// promptly and must not throw: a slow or failing observer would stall message
    /// processing for the whole node.
    /// </remarks>
    void Observe(IReadOnlyCollection<GossNetNodeHostEntry> seen);
}
