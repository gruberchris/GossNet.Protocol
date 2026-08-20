using System.Net;
using System.Net.Sockets;
#if !NET6_0_OR_GREATER
using System.Runtime.InteropServices;
#endif

namespace GossNet.Protocol;

/// <summary>
/// The multicast group <see cref="MulticastNodeDiscovery"/> announces itself on.
/// </summary>
/// <remarks>Abstracted so discovery can be tested without touching a real socket.</remarks>
public interface IMulticastChannel : IDisposable
{
    /// <summary>Sends a datagram to the group.</summary>
    /// <param name="datagram">The payload.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    ValueTask SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken);

    /// <summary>Waits for the next datagram sent to the group.</summary>
    /// <param name="cancellationToken">Cancels the pending receive.</param>
    ValueTask<byte[]> ReceiveAsync(CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IMulticastChannel"/> backed by a UDP socket joined to a multicast group.
/// </summary>
/// <remarks>
/// This is a second socket, deliberately separate from the one carrying gossip messages.
/// Joining a multicast group on the message socket would deliver discovery announcements
/// into the node's receive loop, where they would fail to deserialize as messages.
/// </remarks>
public sealed class UdpMulticastChannel : IMulticastChannel
{
    private readonly UdpClient _client;
    private readonly IPEndPoint _group;

    /// <summary>
    /// Joins the group.
    /// </summary>
    /// <param name="options">Group address, port and socket settings.</param>
    /// <exception cref="NodeDiscoveryException">The group could not be joined.</exception>
    public UdpMulticastChannel(MulticastDiscoveryOptions options)
    {
        var address = IPAddress.Parse(options.GroupAddress);
        _group = new IPEndPoint(address, options.Port);

        try
        {
            _client = new UdpClient();

            // Every node on a host binds the same discovery port, so the address must be
            // shareable. Without this the second node on a machine fails to start.
            _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _client.Client.Bind(new IPEndPoint(IPAddress.Any, options.Port));
            _client.JoinMulticastGroup(address);

            // Loopback on by default: without it, nodes sharing a host never see each
            // other, which is the single most common way this gets tried out.
            _client.MulticastLoopback = options.EnableLoopback;
            _client.Ttl = (short)options.TimeToLive;
        }
        catch (Exception ex)
        {
            _client?.Dispose();

            throw new NodeDiscoveryException(
                $"Failed to join multicast group {options.GroupAddress}:{options.Port}.", ex);
        }
    }

    /// <inheritdoc />
    public async ValueTask SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken)
    {
#if NET6_0_OR_GREATER
        await _client.SendAsync(datagram, _group, cancellationToken).ConfigureAwait(false);
#else
        var buffer = MemoryMarshal.TryGetArray(datagram, out var segment) && segment.Array is not null && segment.Offset == 0
            ? segment.Array
            : datagram.ToArray();

        cancellationToken.ThrowIfCancellationRequested();
        await _client.SendAsync(buffer, datagram.Length, _group).ConfigureAwait(false);
#endif
    }

    /// <inheritdoc />
    public async ValueTask<byte[]> ReceiveAsync(CancellationToken cancellationToken)
    {
#if NET6_0_OR_GREATER
        var result = await _client.ReceiveAsync(cancellationToken).ConfigureAwait(false);

        return result.Buffer;
#else
        // netstandard2.0 has no cancellable overload, so cancellation is layered on top.
        // The underlying receive stays pending until a datagram arrives or the client is
        // disposed, so an orphaned receive is bounded by the lifetime of this channel.
        cancellationToken.ThrowIfCancellationRequested();

        var receive = _client.ReceiveAsync();

        if (!receive.IsCompleted)
        {
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            using (cancellationToken.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), cancelled))
            {
                if (await Task.WhenAny(receive, cancelled.Task).ConfigureAwait(false) != receive)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
            }
        }

        return (await receive.ConfigureAwait(false)).Buffer;
#endif
    }

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();
}
