using System.Net.Sockets;
#if !NET6_0_OR_GREATER
using System.Runtime.InteropServices;
#endif

namespace GossNet.Protocol;

/// <summary>
/// Default <see cref="IUdpClient"/> implementation, backed by <see cref="UdpClient"/>.
/// </summary>
public sealed class UdpClientAdapter(int port) : IUdpClient
{
    private readonly UdpClient _client = new(port);

    /// <inheritdoc />
    public bool EnableBroadcast
    {
        get => _client.EnableBroadcast;
        set => _client.EnableBroadcast = value;
    }

#if NET6_0_OR_GREATER
    /// <inheritdoc />
    public ValueTask<UdpReceiveResult> ReceiveAsync(CancellationToken cancellationToken) =>
        _client.ReceiveAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask<int> SendAsync(ReadOnlyMemory<byte> datagram, string hostname, int port, CancellationToken cancellationToken) =>
        _client.SendAsync(datagram, hostname, port, cancellationToken);
#else
    // netstandard2.0 has no cancellable UdpClient overloads, so cancellation is
    // layered on top with Task.WhenAny.
    //
    // Cancelling does NOT dispose the socket: that would make the client unusable
    // and defeat stop/restart. The underlying receive stays pending and completes
    // when a datagram finally arrives or when the client is disposed, so an
    // orphaned receive is bounded by the lifetime of this adapter.

    /// <inheritdoc />
    public async ValueTask<UdpReceiveResult> ReceiveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var receive = _client.ReceiveAsync();
        await AwaitWithCancellationAsync(receive, cancellationToken).ConfigureAwait(false);
        return await receive.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<int> SendAsync(ReadOnlyMemory<byte> datagram, string hostname, int port, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Avoid a copy when the memory is already array-backed, which it always is
        // on the send path inside this library.
        var buffer = MemoryMarshal.TryGetArray(datagram, out var segment) && segment.Array is not null && segment.Offset == 0
            ? segment.Array
            : datagram.ToArray();

        var send = _client.SendAsync(buffer, datagram.Length, hostname, port);
        await AwaitWithCancellationAsync(send, cancellationToken).ConfigureAwait(false);
        return await send.ConfigureAwait(false);
    }

    private static async Task AwaitWithCancellationAsync(Task task, CancellationToken cancellationToken)
    {
        if (task.IsCompleted)
        {
            return;
        }

        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using (cancellationToken.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), cancelled))
        {
            var completed = await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false);

            if (completed != task)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }
    }
#endif

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();
}
