using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
#if !NET6_0_OR_GREATER
using System.Runtime.InteropServices;
#endif

namespace GossNet.Protocol;

/// <summary>
/// Default <see cref="IUdpClient"/> implementation, backed by <see cref="UdpClient"/>.
/// </summary>
/// <remarks>
/// Destination hostnames are resolved through a short-lived cache: the UdpClient
/// hostname overloads resolve DNS on every call, which on the send path meant one
/// lookup per neighbour per message. IP literals bypass resolution entirely.
/// </remarks>
public sealed class UdpClientAdapter(int port) : IUdpClient
{
    /// <summary>
    /// How long a resolved address is reused. Matches the discovery providers' default
    /// cache duration, so staleness is no worse than membership already is.
    /// </summary>
    private static readonly TimeSpan ResolutionLifetime = TimeSpan.FromSeconds(30);

    private readonly UdpClient _client = new(port);

    private readonly ConcurrentDictionary<string, (IPAddress Address, long ResolvedAt)> _resolved =
        new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public bool EnableBroadcast
    {
        get => _client.EnableBroadcast;
        set => _client.EnableBroadcast = value;
    }

    private async ValueTask<IPAddress> ResolveAsync(string hostname, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(hostname, out var literal))
        {
            return literal;
        }

        var now = Stopwatch.GetTimestamp();

        if (_resolved.TryGetValue(hostname, out var cached) &&
            TimeSpan.FromSeconds((double)(now - cached.ResolvedAt) / Stopwatch.Frequency) < ResolutionLifetime)
        {
            return cached.Address;
        }

#if NET6_0_OR_GREATER
        var addresses = await Dns.GetHostAddressesAsync(hostname, cancellationToken).ConfigureAwait(false);
#else
        cancellationToken.ThrowIfCancellationRequested();
        var addresses = await Dns.GetHostAddressesAsync(hostname).ConfigureAwait(false);
#endif

        var address = SelectAddress(addresses)
            ?? throw new SocketException((int)SocketError.HostNotFound);

        _resolved[hostname] = (address, now);

        return address;
    }

    /// <summary>Picks an address the socket can actually send to.</summary>
    private IPAddress? SelectAddress(IPAddress[] addresses)
    {
        var family = _client.Client.AddressFamily;

        foreach (var address in addresses)
        {
            if (address.AddressFamily == family)
            {
                return address;
            }
        }

        return addresses.Length > 0 ? addresses[0] : null;
    }

#if NET6_0_OR_GREATER
    /// <inheritdoc />
    public ValueTask<UdpReceiveResult> ReceiveAsync(CancellationToken cancellationToken) =>
        _client.ReceiveAsync(cancellationToken);

    /// <inheritdoc />
    public async ValueTask<int> SendAsync(ReadOnlyMemory<byte> datagram, string hostname, int port, CancellationToken cancellationToken)
    {
        var address = await ResolveAsync(hostname, cancellationToken).ConfigureAwait(false);

        return await _client.SendAsync(datagram, new IPEndPoint(address, port), cancellationToken).ConfigureAwait(false);
    }
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

        var address = await ResolveAsync(hostname, cancellationToken).ConfigureAwait(false);

        // Avoid a copy when the memory is already array-backed, which it always is
        // on the send path inside this library.
        var buffer = MemoryMarshal.TryGetArray(datagram, out var segment) && segment.Array is not null && segment.Offset == 0
            ? segment.Array
            : datagram.ToArray();

        var send = _client.SendAsync(buffer, datagram.Length, new IPEndPoint(address, port));
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
