using System.Net;
using System.Net.Sockets;
using GossNet.Protocol;

namespace GossNet.Discovery.Tests;

/// <summary>
/// Transport stub for provider tests: records sends and never yields a datagram.
/// </summary>
internal sealed class NullUdpClient : IUdpClient
{
    private readonly List<(string Hostname, int Port)> _sent = [];

    public bool EnableBroadcast { get; set; }

    public IReadOnlyList<(string Hostname, int Port)> Sent
    {
        get
        {
            lock (_sent)
            {
                return _sent.ToArray();
            }
        }
    }

    public async ValueTask<UdpReceiveResult> ReceiveAsync(CancellationToken cancellationToken)
    {
        // Park until cancelled, the way an idle socket does.
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);

        return default;
    }

    public ValueTask<int> SendAsync(ReadOnlyMemory<byte> datagram, string hostname, int port, CancellationToken cancellationToken)
    {
        lock (_sent)
        {
            _sent.Add((hostname, port));
        }

        return new ValueTask<int>(datagram.Length);
    }

    public void Dispose()
    {
    }
}
