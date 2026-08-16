namespace GossNet.Protocol;

/// <summary>
/// Envelope delivered to subscribers for each message a node accepts.
/// </summary>
/// <typeparam name="T">The gossip message type.</typeparam>
public class GossNetChannelMessage<T> where T : GossNetMessageBase
{
    /// <summary>Gets the received message.</summary>
    public required T Message { get; init; }
}
