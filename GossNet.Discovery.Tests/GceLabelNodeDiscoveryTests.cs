using GossNet.Discovery.Gcp;
using GossNet.Protocol;

namespace GossNet.Discovery.Tests;

[TestClass]
public sealed class GceLabelNodeDiscoveryTests
{
    private sealed class FakeGceLookup : IGceInstanceLookup
    {
        private readonly GceInstance[] _instances;
        private readonly Exception? _fault;

        public FakeGceLookup(params GceInstance[] instances) => _instances = instances;

        public FakeGceLookup(Exception fault)
        {
            _instances = [];
            _fault = fault;
        }

        public int Queries { get; private set; }
        public string? LastProjectId { get; private set; }
        public string? LastLabelKey { get; private set; }
        public string? LastLabelValue { get; private set; }
        public bool LastUseInternalIp { get; private set; }
        public bool IsDisposed { get; private set; }

        public ValueTask<IReadOnlyList<GceInstance>> GetInstancesAsync(
            string projectId, string labelKey, string labelValue, bool useInternalIp, CancellationToken cancellationToken = default)
        {
            Queries++;
            LastProjectId = projectId;
            LastLabelKey = labelKey;
            LastLabelValue = labelValue;
            LastUseInternalIp = useInternalIp;

            if (_fault is not null)
            {
                throw _fault;
            }

            return new ValueTask<IReadOnlyList<GceInstance>>(_instances);
        }

        public void Dispose() => IsDisposed = true;
    }

    private static GossNetConfiguration Configuration(string hostname = "10.128.0.1", int port = 9055) => new()
    {
        Hostname = hostname,
        Port = port
    };

    private static GcpDiscoveryOptions Options(
        TimeSpan? cache = null,
        bool useInternalIp = true,
        int port = 9055) => new()
    {
        ProjectId = "my-project",
        LabelKey = "gossnet-cluster",
        LabelValue = "production",
        Port = port,
        UseInternalIp = useInternalIp,
        CacheDuration = cache
    };

    [TestMethod]
    public async Task Resolve_MapsInstancesToNeighbours()
    {
        var lookup = new FakeGceLookup(
            new GceInstance("10.128.0.2", "node-a"),
            new GceInstance("10.128.0.3", "node-b"));

        using var discovery = new GceLabelNodeDiscovery(Configuration(), Options(), lookup);

        var neighbours = await discovery.GetNeighboursAsync();

        CollectionAssert.AreEquivalent(
            new[] { "10.128.0.2:9055", "10.128.0.3:9055" },
            neighbours.Select(n => n.ToString()).ToArray());
    }

    /// <summary>This node carries the same label as the rest of the cluster.</summary>
    [TestMethod]
    public async Task Resolve_ExcludesTheNodeItself()
    {
        var lookup = new FakeGceLookup(
            new GceInstance("10.128.0.1", "node-self"),
            new GceInstance("10.128.0.2", "node-other"));

        using var discovery = new GceLabelNodeDiscovery(Configuration("10.128.0.1", 9055), Options(), lookup);

        var neighbours = await discovery.GetNeighboursAsync();

        CollectionAssert.AreEqual(new[] { "10.128.0.2:9055" }, neighbours.Select(n => n.ToString()).ToArray());
    }

    [TestMethod]
    public async Task Resolve_PassesTheProjectAndLabelThrough()
    {
        var lookup = new FakeGceLookup();

        using var discovery = new GceLabelNodeDiscovery(Configuration(), Options(), lookup);

        await discovery.GetNeighboursAsync();

        Assert.AreEqual("my-project", lookup.LastProjectId);
        Assert.AreEqual("gossnet-cluster", lookup.LastLabelKey);
        Assert.AreEqual("production", lookup.LastLabelValue);
        Assert.IsTrue(lookup.LastUseInternalIp);
    }

    [TestMethod]
    public async Task Resolve_HonoursExternalAddressSelection()
    {
        var lookup = new FakeGceLookup();

        using var discovery = new GceLabelNodeDiscovery(Configuration(), Options(useInternalIp: false), lookup);

        await discovery.GetNeighboursAsync();

        Assert.IsFalse(lookup.LastUseInternalIp);
    }

    /// <summary>Compute Engine describes instances, not services, so the port comes from configuration.</summary>
    [TestMethod]
    public async Task Resolve_AppliesTheConfiguredPort()
    {
        var lookup = new FakeGceLookup(new GceInstance("10.128.0.2", "node-a"));

        using var discovery = new GceLabelNodeDiscovery(Configuration(), Options(port: 9999), lookup);

        var neighbours = await discovery.GetNeighboursAsync();

        CollectionAssert.AreEqual(new[] { "10.128.0.2:9999" }, neighbours.Select(n => n.ToString()).ToArray());
    }

    [TestMethod]
    public async Task Resolve_CachesWithinTheWindow()
    {
        var lookup = new FakeGceLookup(new GceInstance("10.128.0.2", "node-a"));

        using var discovery = new GceLabelNodeDiscovery(Configuration(), Options(cache: TimeSpan.FromMinutes(5)), lookup);

        await discovery.GetNeighboursAsync();
        await discovery.GetNeighboursAsync();

        Assert.AreEqual(1, lookup.Queries, "Discovery runs on the message path and must not query the API per message.");
    }

    [TestMethod]
    public async Task Resolve_ReQueriesAfterTheCacheExpires()
    {
        var lookup = new FakeGceLookup(new GceInstance("10.128.0.2", "node-a"));

        using var discovery = new GceLabelNodeDiscovery(
            Configuration(), Options(cache: TimeSpan.FromMilliseconds(50)), lookup);

        await discovery.GetNeighboursAsync();
        await Task.Delay(120);
        await discovery.GetNeighboursAsync();

        Assert.AreEqual(2, lookup.Queries);
    }

    [TestMethod]
    public async Task Resolve_WrapsBackendFailures()
    {
        var lookup = new FakeGceLookup(new InvalidOperationException("PERMISSION_DENIED"));

        using var discovery = new GceLabelNodeDiscovery(Configuration(), Options(), lookup);

        var ex = await Assert.ThrowsExactlyAsync<NodeDiscoveryException>(
            async () => await discovery.GetNeighboursAsync());

        Assert.IsInstanceOfType<InvalidOperationException>(ex.InnerException);
        StringAssert.Contains(ex.Message, "my-project");
        StringAssert.Contains(ex.Message, "gossnet-cluster=production");
    }

    [TestMethod]
    public async Task Resolve_PropagatesCancellation()
    {
        var lookup = new FakeGceLookup(new OperationCanceledException());

        using var discovery = new GceLabelNodeDiscovery(Configuration(), Options(), lookup);
        using var cts = new CancellationTokenSource();

        await cts.CancelAsync();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            async () => await discovery.GetNeighboursAsync(cts.Token));
    }

    [TestMethod]
    public void Dispose_LeavesAnInjectedLookupAlone()
    {
        var lookup = new FakeGceLookup();
        var discovery = new GceLabelNodeDiscovery(Configuration(), Options(), lookup);

        discovery.Dispose();

        Assert.IsFalse(lookup.IsDisposed, "An injected lookup may be shared and must be left alone.");
    }

    [TestMethod]
    [DataRow("", "key", "value")]
    [DataRow("project", "", "value")]
    [DataRow("project", "key", "")]
    public void MissingRequiredOptions_AreRejected(string projectId, string labelKey, string labelValue)
    {
        Assert.ThrowsExactly<ArgumentException>(() => new GceLabelNodeDiscovery(
            Configuration(),
            new GcpDiscoveryOptions { ProjectId = projectId, LabelKey = labelKey, LabelValue = labelValue },
            new FakeGceLookup()));
    }

    [TestMethod]
    public async Task Resolve_HandlesAnEmptyProject()
    {
        using var discovery = new GceLabelNodeDiscovery(Configuration(), Options(), new FakeGceLookup());

        Assert.AreEqual(0, (await discovery.GetNeighboursAsync()).Count);
    }
}
