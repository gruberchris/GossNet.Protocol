using System.Net.Sockets;

namespace GossNet.Protocol;

public class UdpClientAdapter(int port) : IUdpClient
{
    private readonly UdpClient _client = new(port);

    public bool EnableBroadcast
    {
        get => _client.EnableBroadcast;
        set => _client.EnableBroadcast = value;
    }

    public Task<UdpReceiveResult> ReceiveAsync() => _client.ReceiveAsync();

    public Task<int> SendAsync(byte[] datagram, int bytes, string hostname, int port) =>
        _client.SendAsync(datagram, bytes, hostname, port);

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing) return;
        
        _client?.Dispose();
    }
}