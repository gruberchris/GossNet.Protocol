using System.Net;

namespace GossNet.Protocol.Tests;

[TestClass]
public sealed class NodeDiscoveryTests
{
    /// <summary>Records how often it is asked to resolve, so caching can be observed.</summary>
    private sealed class FakeDnsResolver(string[] hostAddresses, string[] localAddresses) : IDnsResolver
    {
        public int HostLookups { get; private set; }

        public ValueTask<IReadOnlyList<IPAddress>> GetHostAddressesAsync(string hostname, CancellationToken cancellationToken = default)
        {
            HostLookups++;

            return new ValueTask<IReadOnlyList<IPAddress>>(hostAddresses.Select(IPAddress.Parse).ToArray());
        }

        public ValueTask<IReadOnlyList<IPAddress>> GetLocalAddressesAsync(CancellationToken cancellationToken = default) =>
            new(localAddresses.Select(IPAddress.Parse).ToArray());
    }

    private static GossNetConfiguration Configuration(NodeDiscovery mechanism, params GossNetNodeHostEntry[] staticNodes) => new()
    {
        Hostname = "gossnet.example.com",
        Port = 9055,
        NodeDiscovery = mechanism,
        StaticNodes = staticNodes
    };

    // ---------------------------------------------------------------------------
    // Static list.
    // ---------------------------------------------------------------------------

    [TestMethod]
    public async Task StaticList_ReturnsConfiguredNeighbours()
    {
        var configuration = Configuration(
            NodeDiscovery.StaticList,
            new GossNetNodeHostEntry { Hostname = "node-b", Port = 9056 },
            new GossNetNodeHostEntry { Hostname = "node-c", Port = 9057 });

        var neighbours = await new StaticListNodeDiscovery(configuration).GetNeighboursAsync();

        Assert.AreEqual(2, neighbours.Count);
    }

    [TestMethod]
    public async Task StaticList_ExcludesTheNodeItself()
    {
        var configuration = Configuration(
            NodeDiscovery.StaticList,
            new GossNetNodeHostEntry { Hostname = "gossnet.example.com", Port = 9055 },
            new GossNetNodeHostEntry { Hostname = "node-b", Port = 9056 });

        var neighbours = await new StaticListNodeDiscovery(configuration).GetNeighboursAsync();

        Assert.AreEqual(1, neighbours.Count);
        Assert.AreEqual("node-b", neighbours[0].Hostname);
    }

    [TestMethod]
    public async Task StaticList_KeepsSameHostOnADifferentPort()
    {
        var configuration = Configuration(
            NodeDiscovery.StaticList,
            new GossNetNodeHostEntry { Hostname = "gossnet.example.com", Port = 9056 });

        var neighbours = await new StaticListNodeDiscovery(configuration).GetNeighboursAsync();

        Assert.AreEqual(1, neighbours.Count, "a different port is a different node");
    }

    // ---------------------------------------------------------------------------
    // DNS.
    // ---------------------------------------------------------------------------

    [TestMethod]
    public async Task Dns_ResolvesEveryAddressAsANeighbour()
    {
        var resolver = new FakeDnsResolver(["10.0.0.1", "10.0.0.2", "10.0.0.3"], ["127.0.0.1"]);
        var discovery = new DnsNodeDiscovery(Configuration(NodeDiscovery.Dns), resolver: resolver);

        var neighbours = await discovery.GetNeighboursAsync();

        CollectionAssert.AreEquivalent(
            new[] { "10.0.0.1:9055", "10.0.0.2:9055", "10.0.0.3:9055" },
            neighbours.Select(n => n.ToString()).ToArray());
    }

    /// <summary>
    /// Regression test for a node gossiping with itself.
    /// </summary>
    /// <remarks>
    /// DNS returns every address for the shared hostname, including this node's own.
    /// Self-exclusion compared the configured hostname (a name) against the resolved
    /// entries (IP addresses), so it never matched and the node unicast every message
    /// back to itself.
    /// </remarks>
    [TestMethod]
    public async Task Dns_ExcludesTheNodesOwnAddress()
    {
        var resolver = new FakeDnsResolver(["10.0.0.1", "10.0.0.2", "10.0.0.3"], ["127.0.0.1", "10.0.0.2"]);
        var discovery = new DnsNodeDiscovery(Configuration(NodeDiscovery.Dns), resolver: resolver);

        var neighbours = await discovery.GetNeighboursAsync();

        CollectionAssert.AreEquivalent(
            new[] { "10.0.0.1:9055", "10.0.0.3:9055" },
            neighbours.Select(n => n.ToString()).ToArray());

        Assert.IsFalse(neighbours.Any(n => n.Hostname == "10.0.0.2"), "the node must not be its own neighbour");
    }

    [TestMethod]
    public async Task Dns_DeduplicatesRepeatedAddresses()
    {
        var resolver = new FakeDnsResolver(["10.0.0.1", "10.0.0.1"], ["127.0.0.1"]);
        var discovery = new DnsNodeDiscovery(Configuration(NodeDiscovery.Dns), resolver: resolver);

        var neighbours = await discovery.GetNeighboursAsync();

        Assert.AreEqual(1, neighbours.Count);
    }

    /// <summary>
    /// Resolution used to run on every single message, using the blocking
    /// Dns.GetHostEntry inside an async method.
    /// </summary>
    [TestMethod]
    public async Task Dns_CachesResultsWithinTheCacheWindow()
    {
        var resolver = new FakeDnsResolver(["10.0.0.1"], ["127.0.0.1"]);
        var discovery = new DnsNodeDiscovery(Configuration(NodeDiscovery.Dns), TimeSpan.FromSeconds(30), resolver);

        for (var i = 0; i < 50; i++)
        {
            await discovery.GetNeighboursAsync();
        }

        Assert.AreEqual(1, resolver.HostLookups, "50 sends must not produce 50 DNS lookups");
    }

    [TestMethod]
    public async Task Dns_RefreshesAfterTheCacheExpires()
    {
        var resolver = new FakeDnsResolver(["10.0.0.1"], ["127.0.0.1"]);
        var discovery = new DnsNodeDiscovery(Configuration(NodeDiscovery.Dns), TimeSpan.FromMilliseconds(50), resolver);

        await discovery.GetNeighboursAsync();
        Assert.AreEqual(1, resolver.HostLookups);

        await Task.Delay(120);
        await discovery.GetNeighboursAsync();

        Assert.AreEqual(2, resolver.HostLookups, "membership changes must eventually be picked up");
    }

    // ---------------------------------------------------------------------------
    // Provider selection.
    // ---------------------------------------------------------------------------

    [TestMethod]
    [DataRow(NodeDiscovery.Consul, "GossNet.Discovery.Consul")]
    [DataRow(NodeDiscovery.Kubernetes, "GossNet.Discovery.Kubernetes")]
    [DataRow(NodeDiscovery.Docker, "GossNet.Discovery.Docker")]
    public void UnavailableMechanism_FailsFastNamingThePackage(NodeDiscovery mechanism, string expectedPackage)
    {
        // These used to return an empty neighbour list, so the node reported success
        // while gossiping to nobody.
        var exception = Assert.ThrowsExactly<NotSupportedException>(
            () => new GossNetNode<DiscoveryTestMessage>(Configuration(mechanism)));

        StringAssert.Contains(exception.Message, expectedPackage);
    }

    [TestMethod]
    public void CustomProvider_OverridesTheConfiguredMechanism()
    {
        var configuration = new GossNetConfiguration
        {
            Hostname = "self",
            Port = 9055,
            // Would throw on its own; the explicit provider takes precedence.
            NodeDiscovery = NodeDiscovery.Consul,
            DiscoveryProvider = new StubDiscovery()
        };

        using var node = new GossNetNode<DiscoveryTestMessage>(configuration);
    }

    [TestMethod]
    public async Task CustomProvider_IsUsedToResolveNeighbours()
    {
        var stub = new StubDiscovery();
        var configuration = new GossNetConfiguration
        {
            Hostname = "self",
            Port = 9055,
            DiscoveryProvider = stub
        };

        var udpClient = new Mocks.MockUdpClient();
        using var node = new GossNetNode<DiscoveryTestMessage>(configuration, udpClient: udpClient);

        await node.SendAsync(new DiscoveryTestMessage());

        Assert.IsTrue(stub.Calls > 0);
        Assert.AreEqual(1, udpClient.SentPackets.Count);
        Assert.AreEqual("custom-neighbour", udpClient.SentPackets[0].Hostname);
    }

    private sealed class StubDiscovery : INodeDiscovery
    {
        public int Calls { get; private set; }

        public ValueTask<IReadOnlyList<GossNetNodeHostEntry>> GetNeighboursAsync(CancellationToken cancellationToken = default)
        {
            Calls++;

            return new ValueTask<IReadOnlyList<GossNetNodeHostEntry>>(
                new[] { new GossNetNodeHostEntry { Hostname = "custom-neighbour", Port = 9056 } });
        }
    }
}

public sealed class DiscoveryTestMessage : GossNetMessageBase;
