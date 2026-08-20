using GossNet.Discovery.Consul;
using GossNet.Protocol;

namespace GossNet.Discovery.Tests;

[TestClass]
public sealed class ConsulNodeDiscoveryTests
{
    private sealed class FakeConsulClient : IConsulHealthClient
    {
        private readonly ConsulServiceInstance[] _instances;
        private readonly Exception? _fault;

        public FakeConsulClient(params ConsulServiceInstance[] instances) => _instances = instances;

        public FakeConsulClient(Exception fault)
        {
            _instances = [];
            _fault = fault;
        }

        public int Queries { get; private set; }
        public string? LastServiceName { get; private set; }
        public string? LastTag { get; private set; }
        public bool LastPassingOnly { get; private set; }
        public bool IsDisposed { get; private set; }

        public ValueTask<IReadOnlyList<ConsulServiceInstance>> GetServiceInstancesAsync(
            string serviceName, string? tag, bool passingOnly, CancellationToken cancellationToken = default)
        {
            Queries++;
            LastServiceName = serviceName;
            LastTag = tag;
            LastPassingOnly = passingOnly;

            if (_fault is not null)
            {
                throw _fault;
            }

            return new ValueTask<IReadOnlyList<ConsulServiceInstance>>(_instances);
        }

        public void Dispose() => IsDisposed = true;
    }

    private static GossNetConfiguration Configuration(string hostname = "10.0.0.1", int port = 9055) => new()
    {
        Hostname = hostname,
        Port = port
    };

    private static ConsulDiscoveryOptions Options(TimeSpan? cache = null, string? tag = null, bool passingOnly = true) => new()
    {
        ServiceName = "gossnet",
        Tag = tag,
        PassingOnly = passingOnly,
        CacheDuration = cache
    };

    [TestMethod]
    public async Task Resolve_MapsInstancesToNeighbours()
    {
        var client = new FakeConsulClient(
            new ConsulServiceInstance("10.0.0.2", 9055),
            new ConsulServiceInstance("10.0.0.3", 9056));

        using var discovery = new ConsulNodeDiscovery(Configuration(), Options(), client);

        var neighbours = await discovery.GetNeighboursAsync();

        CollectionAssert.AreEquivalent(
            new[] { "10.0.0.2:9055", "10.0.0.3:9056" },
            neighbours.Select(n => n.ToString()).ToArray());
    }

    /// <summary>A node registers itself in Consul, so it appears in its own service query.</summary>
    [TestMethod]
    public async Task Resolve_ExcludesTheNodeItself()
    {
        var client = new FakeConsulClient(
            new ConsulServiceInstance("10.0.0.1", 9055),
            new ConsulServiceInstance("10.0.0.2", 9055));

        using var discovery = new ConsulNodeDiscovery(Configuration("10.0.0.1", 9055), Options(), client);

        var neighbours = await discovery.GetNeighboursAsync();

        Assert.AreEqual(1, neighbours.Count);
        Assert.AreEqual("10.0.0.2:9055", neighbours[0].ToString());
    }

    [TestMethod]
    public async Task Resolve_KeepsSameAddressOnADifferentPort()
    {
        var client = new FakeConsulClient(new ConsulServiceInstance("10.0.0.1", 9056));
        using var discovery = new ConsulNodeDiscovery(Configuration("10.0.0.1", 9055), Options(), client);

        var neighbours = await discovery.GetNeighboursAsync();

        Assert.AreEqual(1, neighbours.Count, "a different port is a different node");
    }

    [TestMethod]
    public async Task Resolve_DeduplicatesRepeatedInstances()
    {
        var client = new FakeConsulClient(
            new ConsulServiceInstance("10.0.0.2", 9055),
            new ConsulServiceInstance("10.0.0.2", 9055));

        using var discovery = new ConsulNodeDiscovery(Configuration(), Options(), client);

        Assert.AreEqual(1, (await discovery.GetNeighboursAsync()).Count);
    }

    [TestMethod]
    public async Task Resolve_PassesTheConfiguredQueryParameters()
    {
        var client = new FakeConsulClient(new ConsulServiceInstance("10.0.0.2", 9055));
        using var discovery = new ConsulNodeDiscovery(Configuration(), Options(tag: "gossip", passingOnly: true), client);

        await discovery.GetNeighboursAsync();

        Assert.AreEqual("gossnet", client.LastServiceName);
        Assert.AreEqual("gossip", client.LastTag);
        Assert.IsTrue(client.LastPassingOnly, "unhealthy instances should be filtered out by default");
    }

    [TestMethod]
    public async Task Resolve_CachesWithinTheCacheWindow()
    {
        var client = new FakeConsulClient(new ConsulServiceInstance("10.0.0.2", 9055));
        using var discovery = new ConsulNodeDiscovery(Configuration(), Options(TimeSpan.FromSeconds(30)), client);

        for (var i = 0; i < 50; i++)
        {
            await discovery.GetNeighboursAsync();
        }

        Assert.AreEqual(1, client.Queries, "discovery runs on the message path and must not query Consul per message");
    }

    [TestMethod]
    public async Task Resolve_RefreshesAfterTheCacheExpires()
    {
        var client = new FakeConsulClient(new ConsulServiceInstance("10.0.0.2", 9055));
        using var discovery = new ConsulNodeDiscovery(Configuration(), Options(TimeSpan.FromMilliseconds(50)), client);

        await discovery.GetNeighboursAsync();
        await Task.Delay(120);
        await discovery.GetNeighboursAsync();

        Assert.AreEqual(2, client.Queries, "membership changes must eventually be picked up");
    }

    /// <summary>An unreachable agent must not look like a network with no other nodes.</summary>
    [TestMethod]
    public async Task Resolve_WhenConsulFails_ThrowsInsteadOfReturningEmpty()
    {
        var client = new FakeConsulClient(new HttpRequestException("connection refused"));
        using var discovery = new ConsulNodeDiscovery(Configuration(), Options(), client);

        var exception = await Assert.ThrowsExactlyAsync<NodeDiscoveryException>(
            async () => await discovery.GetNeighboursAsync());

        StringAssert.Contains(exception.Message, "gossnet");
        Assert.IsInstanceOfType<HttpRequestException>(exception.InnerException);
    }

    [TestMethod]
    public async Task Resolve_PropagatesCancellation()
    {
        var client = new FakeConsulClient(new OperationCanceledException());
        using var discovery = new ConsulNodeDiscovery(Configuration(), Options(), client);
        using var cts = new CancellationTokenSource();

        await cts.CancelAsync();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            async () => await discovery.GetNeighboursAsync(cts.Token));
    }

    [TestMethod]
    public void Constructor_WithoutServiceName_Throws() =>
        Assert.ThrowsExactly<ArgumentException>(() =>
            new ConsulNodeDiscovery(Configuration(), new ConsulDiscoveryOptions { ServiceName = "  " }));

    [TestMethod]
    public void Dispose_DoesNotDisposeACallerSuppliedClient()
    {
        var client = new FakeConsulClient();

        new ConsulNodeDiscovery(Configuration(), Options(), client).Dispose();

        Assert.IsFalse(client.IsDisposed, "a shared client must outlive the provider that borrowed it");
    }

    [TestMethod]
    public async Task IntegratesWithANodeViaDiscoveryProviderFactory()
    {
        var client = new FakeConsulClient(new ConsulServiceInstance("10.0.0.2", 9055));

        var configuration = new GossNetConfiguration
        {
            Hostname = "10.0.0.1",
            Port = 9055,
            DiscoveryProviderFactory = cfg => new ConsulNodeDiscovery(cfg, Options(), client)
        };

        // The factory exists because the provider needs the very configuration it is
        // being assigned to, which is otherwise circular.
        using var node = new GossNetNode<ConsulTestMessage>(configuration, udpClient: new NullUdpClient());

        await node.SendAsync(new ConsulTestMessage());

        Assert.AreEqual(1, client.Queries);
    }

    /// <summary>
    /// Consul's blocking-query index rules. Each of these fails silently rather than loudly
    /// when it is wrong, which is why they are pinned here as well as exercised against a
    /// real agent in GossNet.Discovery.IntegrationTests.
    /// </summary>
    [TestMethod]
    [DataRow(5UL, 0UL, 5UL, "a normal first result is taken as-is")]
    [DataRow(9UL, 5UL, 9UL, "a forward move is taken as-is")]
    [DataRow(5UL, 5UL, 5UL, "an unchanged index stays put, and the caller yields nothing")]
    [DataRow(3UL, 9UL, 0UL, "a backwards index means Consul reset, so re-baseline from zero")]
    [DataRow(0UL, 0UL, 1UL, "an index below one would make the next query non-blocking")]
    public void Normalize_AppliesTheBlockingQueryRules(ulong returned, ulong previous, ulong expected, string because)
    {
        Assert.AreEqual(expected, ConsulNodeDiscovery.Normalize(returned, previous), because);
    }

    /// <summary>A client without blocking-query support leaves the node on cached polling.</summary>
    [TestMethod]
    public async Task Watch_CompletesImmediatelyWhenTheClientCannotBlock()
    {
        using var discovery = new ConsulNodeDiscovery(Configuration(), Options(), new FakeConsulClient());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var updates = 0;

        await foreach (var _ in discovery.WatchAsync(cts.Token))
        {
            updates++;
        }

        Assert.AreEqual(0, updates);
    }
}

public sealed class ConsulTestMessage : GossNetMessageBase;
