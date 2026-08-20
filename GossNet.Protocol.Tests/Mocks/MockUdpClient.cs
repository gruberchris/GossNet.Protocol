using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace GossNet.Protocol.Tests.Mocks;

/// <summary>
/// In-memory <see cref="IUdpClient"/>.
/// </summary>
/// <remarks>
/// Receives block until a datagram is enqueued or the token is cancelled, mirroring a
/// real socket. The previous mock returned an empty result immediately when its queue
/// was empty, which meant the node's receive loop spun instead of parking — hiding
/// both the shutdown hang and the absence of any error backoff.
/// </remarks>
public sealed class MockUdpClient : IUdpClient
{
    private readonly Channel<UdpReceiveResult> _incoming = Channel.CreateUnbounded<UdpReceiveResult>();
    private readonly ConcurrentQueue<SentPacket> _sentPackets = new();
    private int _receiveAttempts;

    public bool EnableBroadcast { get; set; }

    public bool IsDisposed { get; private set; }

    /// <summary>Number of times the node asked for a datagram; used to detect hot looping.</summary>
    public int ReceiveAttempts => Volatile.Read(ref _receiveAttempts);

    /// <summary>When set, every receive throws the produced exception.</summary>
    public Func<Exception>? ReceiveFault { get; set; }

    /// <summary>When set and returning non-null for a hostname, that send throws.</summary>
    public Func<string, Exception?>? SendFault { get; set; }

    public IReadOnlyList<SentPacket> SentPackets => _sentPackets.ToArray();

    public void EnqueueReceive(byte[] datagram, IPEndPoint? remoteEndPoint = null) =>
        _incoming.Writer.TryWrite(new UdpReceiveResult(datagram, remoteEndPoint ?? new IPEndPoint(IPAddress.Loopback, 9999)));

    public async ValueTask<UdpReceiveResult> ReceiveAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _receiveAttempts);

        if (ReceiveFault is not null)
        {
            throw ReceiveFault();
        }

        return await _incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<int> SendAsync(ReadOnlyMemory<byte> datagram, string hostname, int port, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (SendFault?.Invoke(hostname) is { } fault)
        {
            throw fault;
        }

        _sentPackets.Enqueue(new SentPacket(datagram.ToArray(), hostname, port));

        return new ValueTask<int>(datagram.Length);
    }

    public void Dispose() => IsDisposed = true;

    public sealed record SentPacket(byte[] Datagram, string Hostname, int Port);
}
