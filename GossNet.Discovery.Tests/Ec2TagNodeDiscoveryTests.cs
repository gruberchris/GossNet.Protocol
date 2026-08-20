using GossNet.Discovery.Aws;
using GossNet.Protocol;

namespace GossNet.Discovery.Tests;

[TestClass]
public sealed class Ec2TagNodeDiscoveryTests
{
    private sealed class FakeEc2Lookup : IEc2InstanceLookup
    {
        private readonly Ec2Instance[] _instances;
        private readonly Exception? _fault;

        public FakeEc2Lookup(params Ec2Instance[] instances) => _instances = instances;

        public FakeEc2Lookup(Exception fault)
        {
            _instances = [];
            _fault = fault;
        }

        public int Queries { get; private set; }
        public string? LastTagKey { get; private set; }
        public string? LastTagValue { get; private set; }
        public bool LastUsePrivateIp { get; private set; }
        public bool IsDisposed { get; private set; }

        public ValueTask<IReadOnlyList<Ec2Instance>> GetInstancesAsync(
            string tagKey, string tagValue, bool usePrivateIp, CancellationToken cancellationToken = default)
        {
            Queries++;
            LastTagKey = tagKey;
            LastTagValue = tagValue;
            LastUsePrivateIp = usePrivateIp;

            if (_fault is not null)
            {
                throw _fault;
            }

            return new ValueTask<IReadOnlyList<Ec2Instance>>(_instances);
        }

        public void Dispose() => IsDisposed = true;
    }

    private static GossNetConfiguration Configuration(string hostname = "10.0.1.1", int port = 9055) => new()
    {
        Hostname = hostname,
        Port = port
    };

    private static AwsDiscoveryOptions Options(
        TimeSpan? cache = null,
        bool usePrivateIp = true,
        int port = 9055) => new()
    {
        TagKey = "gossnet-cluster",
        TagValue = "production",
        Port = port,
        UsePrivateIp = usePrivateIp,
        CacheDuration = cache
    };

    [TestMethod]
    public async Task Resolve_MapsInstancesToNeighbours()
    {
        var lookup = new FakeEc2Lookup(
            new Ec2Instance("10.0.1.2", "i-aaa"),
            new Ec2Instance("10.0.1.3", "i-bbb"));

        using var discovery = new Ec2TagNodeDiscovery(Configuration(), Options(), lookup);

        var neighbours = await discovery.GetNeighboursAsync();

        CollectionAssert.AreEquivalent(
            new[] { "10.0.1.2:9055", "10.0.1.3:9055" },
            neighbours.Select(n => n.ToString()).ToArray());
    }

    /// <summary>This node carries the same tag as the rest of the cluster.</summary>
    [TestMethod]
    public async Task Resolve_ExcludesTheNodeItself()
    {
        var lookup = new FakeEc2Lookup(
            new Ec2Instance("10.0.1.1", "i-self"),
            new Ec2Instance("10.0.1.2", "i-other"));

        using var discovery = new Ec2TagNodeDiscovery(Configuration("10.0.1.1", 9055), Options(), lookup);

        var neighbours = await discovery.GetNeighboursAsync();

        CollectionAssert.AreEqual(new[] { "10.0.1.2:9055" }, neighbours.Select(n => n.ToString()).ToArray());
    }

    [TestMethod]
    public async Task Resolve_PassesTheTagFilterThrough()
    {
        var lookup = new FakeEc2Lookup();

        using var discovery = new Ec2TagNodeDiscovery(Configuration(), Options(), lookup);

        await discovery.GetNeighboursAsync();

        Assert.AreEqual("gossnet-cluster", lookup.LastTagKey);
        Assert.AreEqual("production", lookup.LastTagValue);
        Assert.IsTrue(lookup.LastUsePrivateIp);
    }

    [TestMethod]
    public async Task Resolve_HonoursPublicAddressSelection()
    {
        var lookup = new FakeEc2Lookup();

        using var discovery = new Ec2TagNodeDiscovery(Configuration(), Options(usePrivateIp: false), lookup);

        await discovery.GetNeighboursAsync();

        Assert.IsFalse(lookup.LastUsePrivateIp);
    }

    /// <summary>EC2 describes instances, not services, so the port comes from configuration.</summary>
    [TestMethod]
    public async Task Resolve_AppliesTheConfiguredPort()
    {
        var lookup = new FakeEc2Lookup(new Ec2Instance("10.0.1.2", "i-aaa"));

        using var discovery = new Ec2TagNodeDiscovery(Configuration(), Options(port: 9999), lookup);

        var neighbours = await discovery.GetNeighboursAsync();

        CollectionAssert.AreEqual(new[] { "10.0.1.2:9999" }, neighbours.Select(n => n.ToString()).ToArray());
    }

    [TestMethod]
    public async Task Resolve_CachesWithinTheWindow()
    {
        var lookup = new FakeEc2Lookup(new Ec2Instance("10.0.1.2", "i-aaa"));

        using var discovery = new Ec2TagNodeDiscovery(Configuration(), Options(cache: TimeSpan.FromMinutes(5)), lookup);

        await discovery.GetNeighboursAsync();
        await discovery.GetNeighboursAsync();

        Assert.AreEqual(1, lookup.Queries, "Discovery runs on the message path and must not query EC2 per message.");
    }

    [TestMethod]
    public async Task Resolve_ReQueriesAfterTheCacheExpires()
    {
        var lookup = new FakeEc2Lookup(new Ec2Instance("10.0.1.2", "i-aaa"));

        using var discovery = new Ec2TagNodeDiscovery(
            Configuration(), Options(cache: TimeSpan.FromMilliseconds(50)), lookup);

        await discovery.GetNeighboursAsync();
        await Task.Delay(120);
        await discovery.GetNeighboursAsync();

        Assert.AreEqual(2, lookup.Queries);
    }

    /// <summary>A throttled or unauthorized call must not look like a cluster of one.</summary>
    [TestMethod]
    public async Task Resolve_WrapsBackendFailures()
    {
        var lookup = new FakeEc2Lookup(new InvalidOperationException("RequestLimitExceeded"));

        using var discovery = new Ec2TagNodeDiscovery(Configuration(), Options(), lookup);

        var ex = await Assert.ThrowsExactlyAsync<NodeDiscoveryException>(
            async () => await discovery.GetNeighboursAsync());

        Assert.IsInstanceOfType<InvalidOperationException>(ex.InnerException);
        StringAssert.Contains(ex.Message, "gossnet-cluster=production");
    }

    [TestMethod]
    public async Task Resolve_PropagatesCancellation()
    {
        var lookup = new FakeEc2Lookup(new OperationCanceledException());

        using var discovery = new Ec2TagNodeDiscovery(Configuration(), Options(), lookup);
        using var cts = new CancellationTokenSource();

        await cts.CancelAsync();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            async () => await discovery.GetNeighboursAsync(cts.Token));
    }

    [TestMethod]
    public void Dispose_LeavesAnInjectedLookupAlone()
    {
        var lookup = new FakeEc2Lookup();
        var discovery = new Ec2TagNodeDiscovery(Configuration(), Options(), lookup);

        discovery.Dispose();

        Assert.IsFalse(lookup.IsDisposed, "An injected lookup may be shared and must be left alone.");
    }

    [TestMethod]
    public void MissingTagKey_IsRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new Ec2TagNodeDiscovery(
            Configuration(),
            new AwsDiscoveryOptions { TagKey = "  ", TagValue = "production" },
            new FakeEc2Lookup()));
    }

    [TestMethod]
    public void MissingTagValue_IsRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new Ec2TagNodeDiscovery(
            Configuration(),
            new AwsDiscoveryOptions { TagKey = "gossnet-cluster", TagValue = "" },
            new FakeEc2Lookup()));
    }

    [TestMethod]
    public async Task Resolve_HandlesAnEmptyCluster()
    {
        using var discovery = new Ec2TagNodeDiscovery(Configuration(), Options(), new FakeEc2Lookup());

        Assert.AreEqual(0, (await discovery.GetNeighboursAsync()).Count);
    }
}
