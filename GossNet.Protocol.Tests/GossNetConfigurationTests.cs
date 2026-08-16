namespace GossNet.Protocol.Tests;

[TestClass]
public sealed class GossNetConfigurationTests
{
    [TestMethod]
    public void Defaults_AreAsDocumented()
    {
        var config = new GossNetConfiguration { Hostname = "localhost" };

        // The README claimed 5055 for a long time; 9055 is the real default.
        Assert.AreEqual(9055, config.Port);
        Assert.AreEqual("localhost", config.Hostname);
        Assert.AreEqual(NodeDiscovery.Dns, config.NodeDiscovery);
        Assert.AreEqual(600, config.MessageTtlSeconds);
        Assert.AreEqual(1024, config.SubscriberQueueCapacity);
        Assert.AreEqual(0, config.StaticNodes.Count());
        Assert.IsNull(config.DiscoveryProvider);
        Assert.IsNull(config.DiscoveryProviderFactory);
    }

    [TestMethod]
    public void CustomValues_AreRetained()
    {
        GossNetNodeHostEntry[] staticNodes =
        [
            new() { Hostname = "node1", Port = 8080 },
            new() { Hostname = "node2", Port = 8081 }
        ];

        var config = new GossNetConfiguration
        {
            Hostname = "test-server",
            Port = 8080,
            NodeDiscovery = NodeDiscovery.StaticList,
            StaticNodes = staticNodes,
            MessageTtlSeconds = 30,
            SubscriberQueueCapacity = 8
        };

        Assert.AreEqual("test-server", config.Hostname);
        Assert.AreEqual(8080, config.Port);
        Assert.AreEqual(NodeDiscovery.StaticList, config.NodeDiscovery);
        Assert.AreEqual(30, config.MessageTtlSeconds);
        Assert.AreEqual(8, config.SubscriberQueueCapacity);
        CollectionAssert.AreEqual(staticNodes, config.StaticNodes.ToArray());
    }

    /// <summary>
    /// `required` is a compile-time guarantee, so this asserts the contract that
    /// actually matters: a caller cannot construct the configuration without a
    /// hostname. The previous version reflected over the attribute and then asserted
    /// that an empty string round-tripped, which tested nothing.
    /// </summary>
    [TestMethod]
    public void Hostname_IsRequiredAtCompileTime()
    {
        var hostname = typeof(GossNetConfiguration).GetProperty(nameof(GossNetConfiguration.Hostname));

        Assert.IsNotNull(hostname);
        Assert.IsTrue(
            hostname.CustomAttributes.Any(attribute => attribute.AttributeType.Name == "RequiredMemberAttribute"),
            "omitting Hostname must be a compile error");
    }
}
