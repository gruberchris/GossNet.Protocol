using System.Threading.Channels;

namespace GossNet.Protocol;

/// <summary>
/// A single subscriber's view of the messages a node receives.
/// </summary>
/// <remarks>
/// <para>
/// Each subscription owns a private channel. Every message a node accepts is written
/// to every live subscription, so subscribers do not compete for messages.
/// </para>
/// <para>
/// Disposing the subscription detaches it from the node and completes
/// <see cref="Reader"/>, which ends any <c>await foreach</c> loop reading from it.
/// </para>
/// </remarks>
/// <typeparam name="T">The gossip message type.</typeparam>
public interface IGossNetSubscription<T> : IDisposable where T : GossNetMessageBase, new()
{
    /// <summary>
    /// Gets the reader delivering messages to this subscriber.
    /// </summary>
    ChannelReader<GossNetChannelMessage<T>> Reader { get; }
}
