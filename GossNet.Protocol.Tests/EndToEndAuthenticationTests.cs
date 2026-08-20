using System.Net.Sockets;
using System.Text;

namespace GossNet.Protocol.Tests;

/// <summary>
/// Exercises authentication over real UDP sockets on loopback: what unit tests with a
/// mocked transport cannot prove — that the frames survive an actual socket, and that a
/// real attacker datagram is rejected without disturbing legitimate traffic.
/// </summary>
[TestClass]
public sealed class EndToEndAuthenticationTests
{
    private static readonly byte[] ClusterKey = Encoding.UTF8.GetBytes("cluster-shared-key-0123456789");

    private static GossNetConfiguration Configuration(int ownPort, int peerPort) => new()
    {
        Hostname = "127.0.0.1",
        Port = ownPort,
        NodeDiscovery = NodeDiscovery.StaticList,
        StaticNodes = [new GossNetNodeHostEntry { Hostname = "127.0.0.1", Port = peerPort }],
        DatagramProtector = new HmacDatagramProtector(ClusterKey)
    };

    [TestMethod]
    public async Task AuthenticatedNodes_ExchangeOverRealSockets_AndRejectAttackerTraffic()
    {
        // Offset by runtime version because the per-framework test runs execute in
        // parallel and must not bind the same ports.
        var portA = 19200 + (Environment.Version.Major * 10) + 1;
        var portB = portA + 1;

        await using var nodeA = new GossNetNode<TestMessage>(Configuration(portA, portB));
        await using var nodeB = new GossNetNode<TestMessage>(Configuration(portB, portA));

        using var subscription = nodeB.Subscribe();

        nodeA.Start();
        nodeB.Start();

        // An attacker who can reach the port: plaintext injection and a wrong-key forgery.
        using var attacker = new UdpClient();
        var attackerProtector = new HmacDatagramProtector(Encoding.UTF8.GetBytes("attacker-key-9876543210abcdef"));

        var plaintext = Encoding.UTF8.GetBytes(new TestMessage { Data = "plaintext-injection" }.Serialize());
        var forged = attackerProtector.Protect(Encoding.UTF8.GetBytes(new TestMessage { Data = "forged" }.Serialize()));

        await attacker.SendAsync(plaintext, plaintext.Length, "127.0.0.1", portB);
        await attacker.SendAsync(forged, forged.Length, "127.0.0.1", portB);

        // Legitimate traffic behind the attack must still arrive.
        var sent = await nodeA.SendAsync(new TestMessage { Data = "legitimate" });
        Assert.AreEqual(1, sent, "node A must reach node B");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = await subscription.Reader.ReadAsync(cts.Token);

        Assert.AreEqual("legitimate", received.Message.Data);

        // Give any injected message time to have been (wrongly) delivered.
        await Task.Delay(200);
        Assert.IsFalse(subscription.Reader.TryRead(out _), "attacker datagrams must not reach subscribers");
    }
}
