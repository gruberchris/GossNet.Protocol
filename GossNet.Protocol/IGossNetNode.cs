namespace GossNet.Protocol;

public interface IGossNetNode<T> : IDisposable where T : GossNetMessageBase, new()
{
    /// <summary>
    /// Subscribes to incoming GossNet messages.
    /// </summary>
    /// <param name="handler">The event handler to be called when messages are received.</param>
    Task SubscribeAsync(EventHandler<GossNetMessageReceivedEventArgs<T>> handler);
    
    /// <summary>
    /// Unsubscribes from incoming GossNet messages.
    /// </summary>
    /// <param name="handler">The event handler to be removed.</param>
    Task UnsubscribeAsync(EventHandler<GossNetMessageReceivedEventArgs<T>> handler);
    
    /// <summary>
    /// Sends a message to other nodes in the network.
    /// </summary>
    /// <param name="message">The message to send.</param>
    /// <returns>The number of bytes sent.</returns>
    Task<int> SendAsync(T message);
    
    /// <summary>
    /// Starts the node, enabling it to receive and process messages.
    /// </summary>
    void Start();
    
    /// <summary>
    /// Stops the node from receiving and processing messages.
    /// </summary>
    Task StopAsync();
}