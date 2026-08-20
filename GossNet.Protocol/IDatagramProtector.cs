namespace GossNet.Protocol;

/// <summary>
/// Wraps outgoing datagrams and verifies incoming ones, so a node only accepts traffic
/// from senders holding the cluster's credentials.
/// </summary>
/// <remarks>
/// <para>
/// Supplied through <see cref="GossNetConfiguration.DatagramProtector"/>. When configured,
/// every gossip message and every multicast discovery announcement is passed through
/// <see cref="Protect"/> before transmission, and every received datagram through
/// <see cref="TryUnprotect"/> before any parsing — a datagram that fails verification is
/// dropped without further processing.
/// </para>
/// <para>
/// The built-in implementation is <see cref="HmacDatagramProtector"/>, which authenticates
/// with a shared key. Implement this interface instead to plug in something stronger, such
/// as authenticated encryption.
/// </para>
/// <para>
/// Implementations must be thread-safe: they are called concurrently from the send path
/// and the receive loop.
/// </para>
/// </remarks>
public interface IDatagramProtector
{
    /// <summary>
    /// Gets the number of bytes <see cref="Protect"/> adds to a payload, used to budget
    /// against the maximum datagram size.
    /// </summary>
    int Overhead { get; }

    /// <summary>
    /// Wraps a payload for transmission.
    /// </summary>
    /// <param name="payload">The plaintext payload.</param>
    /// <returns>The datagram to transmit.</returns>
    byte[] Protect(byte[] payload);

    /// <summary>
    /// Verifies a received datagram and recovers its payload.
    /// </summary>
    /// <param name="datagram">The received datagram.</param>
    /// <param name="payload">The verified payload, when the datagram is authentic.</param>
    /// <returns>
    /// <c>true</c> when the datagram was produced by a holder of the cluster's
    /// credentials; <c>false</c> for anything else, which the caller drops.
    /// </returns>
    bool TryUnprotect(byte[] datagram, out byte[] payload);
}
