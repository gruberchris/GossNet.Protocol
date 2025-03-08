using System.Net.Sockets;

namespace GossNet.Protocol;

public interface IUdpClient : IDisposable
{
    bool EnableBroadcast { get; set; }
    Task<UdpReceiveResult> ReceiveAsync();
    Task<int> SendAsync(byte[] datagram, int bytes, string hostname, int port);
}