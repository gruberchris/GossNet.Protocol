using System.Net.Sockets;

namespace GossNet.Protocol;

/// <summary>
/// Abstraction over the UDP socket used to exchange gossip messages, so the
/// transport can be substituted in tests.
/// </summary>
public interface IUdpClient : IDisposable
{
    /// <summary>
    /// Gets or sets a value indicating whether the socket may send broadcast datagrams.
    /// </summary>
    bool EnableBroadcast { get; set; }

    /// <summary>
    /// Waits for the next datagram.
    /// </summary>
    /// <param name="cancellationToken">Cancels the pending receive.</param>
    /// <remarks>
    /// The token is required rather than optional: without it a receive parks until a
    /// datagram happens to arrive, which is what previously made stopping a node hang
    /// indefinitely against a real socket.
    /// </remarks>
    ValueTask<UdpReceiveResult> ReceiveAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Sends a datagram to the given host.
    /// </summary>
    /// <param name="datagram">The payload to send.</param>
    /// <param name="hostname">The destination host.</param>
    /// <param name="port">The destination port.</param>
    /// <param name="cancellationToken">Cancels the pending send.</param>
    /// <returns>The number of bytes sent.</returns>
    ValueTask<int> SendAsync(ReadOnlyMemory<byte> datagram, string hostname, int port, CancellationToken cancellationToken);
}
