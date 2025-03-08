using System.Net.Sockets;

namespace GossNet.Protocol.Tests.Mocks;

public class MockUdpClient : IUdpClient
{
    public bool EnableBroadcast { get; set; }
    public bool IsDisposed { get; private set; }
    public Queue<UdpReceiveResult> ReceiveQueue { get; } = new();
    public List<(byte[] datagram, int bytes, string hostname, int port)> SentPackets { get; } = new();

    public Task<UdpReceiveResult> ReceiveAsync()
    {
        if (ReceiveQueue.TryDequeue(out var result))
            return Task.FromResult(result);
        
        return Task.FromResult(new UdpReceiveResult([], new System.Net.IPEndPoint(0, 0)));
    }

    public Task<int> SendAsync(byte[] datagram, int bytes, string hostname, int port)
    {
        SentPackets.Add((datagram.ToArray(), bytes, hostname, port));
        return Task.FromResult(bytes);
    }

    public void Dispose()
    {
        IsDisposed = true;
    }
}