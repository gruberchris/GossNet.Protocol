namespace GossNet.Protocol.Tests;

[TestClass]
public sealed class UdpClientAdapterTests
{
    [TestMethod]
    public void ReceiveBufferSize_IsAppliedToTheSocket()
    {
        // Port 0 binds an ephemeral port so parallel test runs cannot collide.
        using var adapter = new UdpClientAdapter(0, receiveBufferSize: 256 * 1024);

        // At least the requested size: Linux reports double the requested value, and
        // any OS may round, so equality would be brittle.
        Assert.IsTrue(adapter.ReceiveBufferSize >= 256 * 1024,
            $"requested 262144 bytes but the socket reports {adapter.ReceiveBufferSize}");
    }

    [TestMethod]
    public void ReceiveBufferSize_OmittedLeavesTheOsDefault()
    {
        using var adapter = new UdpClientAdapter(0);

        Assert.IsTrue(adapter.ReceiveBufferSize > 0, "the OS default must be reported");
    }
}
