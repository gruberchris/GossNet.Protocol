using System.Threading.Channels;

namespace GossNet.Protocol;

/// <summary>
/// A participant in a GossNet network.
/// </summary>
/// <typeparam name="T">The gossip message type.</typeparam>
public interface IGossNetNode<T> : IDisposable, IAsyncDisposable where T : GossNetMessageBase, new()
{
    /// <summary>
    /// Creates a subscription that receives every message this node accepts.
    /// </summary>
    /// <returns>
    /// A subscription owning its own <see cref="ChannelReader{T}"/>. Dispose it to stop
    /// receiving messages.
    /// </returns>
    /// <remarks>
    /// Subscribing is cheap and does no I/O, so it is deliberately synchronous.
    /// Multiple subscriptions each receive every message.
    /// </remarks>
    IGossNetSubscription<T> Subscribe();

    /// <summary>
    /// Sends a message to the node's neighbours.
    /// </summary>
    /// <param name="message">The message to send.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>The number of neighbours the message was successfully sent to.</returns>
    /// <remarks>
    /// The message instance is mutated: this node (and, with
    /// <see cref="GossNetConfiguration.AddRecipientsToNotifiedNodes"/>, the recipients)
    /// are appended to <see cref="GossNetMessageBase.NotifiedNodes"/>, and its id is
    /// recorded in the de-duplication cache — so a copy of the message arriving back
    /// over the network is discarded rather than re-delivered to this node's own
    /// subscribers.
    /// </remarks>
    Task<int> SendAsync(T message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts receiving and processing messages. Calling this on a started node does nothing.
    /// </summary>
    void Start();

    /// <summary>
    /// Stops receiving and processing messages. Calling this on a stopped node does nothing.
    /// </summary>
    /// <remarks>
    /// A stopped node can be started again; existing subscriptions remain valid across a
    /// stop/start cycle.
    /// </remarks>
    Task StopAsync();
}
