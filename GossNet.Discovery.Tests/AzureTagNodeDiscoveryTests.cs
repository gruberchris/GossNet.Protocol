using GossNet.Discovery.Azure;
using GossNet.Protocol;

namespace GossNet.Discovery.Tests;

[TestClass]
public sealed class AzureTagNodeDiscoveryTests
{
    private sealed class FakeAzureLookup : IAzureInstanceLookup
    {
        private readonly AzureInstance[] _instances;
        private readonly Exception? _fault;

        public FakeAzureLookup(params AzureInstance[] instances) => _instances = instances;

        public FakeAzureLookup(Exception fault)
        {
            _instances = [];
            _fault = fault;
        }

        public int Queries { get; private set; }
        public string? LastResourceGroup { get; private set; }
        public string? LastTagKey { get; private set; }
        public string? LastTagValue { get; private set; }
        public bool IsDisposed { get; private set; }

        public ValueTask<IReadOnlyList<AzureInstance>> GetInstancesAsync(
            string resourceGroup, string tagKey, string tagValue, CancellationToken cancellationToken = default)
        {
            Queries++;
            LastResourceGroup = resourceGroup;
            LastTagKey = tagKey;
            LastTagValue = tagValue;

            if (_fault is not null)
            {
                throw _fault;
            }

            return new ValueTask<IReadOnlyList<AzureInstance>>(_instances);
        }

        public void Dispose() => IsDisposed = true;
    }

    private static GossNetConfiguration Configuration(string hostname = "10.0.1.1", int port = 9055) => new()
    {
        Hostname = hostname,
        Port = port
    };

    private static AzureDiscoveryOptions Options(TimeSpan? cache = null, int port = 9055) => new()
    {
        ResourceGroup = "gossnet-rg",
        TagKey = "gossnet-cluster",
        TagValue = "production",
        Port = port,
        CacheDuration = cache
    };

    [TestMethod]
    public async Task Resolve_MapsInstancesToNeighbours()
    {
        var lookup = new FakeAzureLookup(
            new AzureInstance("10.0.1.2", "vm-a"),
            new AzureInstance("10.0.1.3", "vm-b"));

        using var discovery = new AzureTagNodeDiscovery(Configuration(), Options(), lookup);

        var neighbours = await discovery.GetNeighboursAsync();

        CollectionAssert.AreEquivalent(
            new[] { "10.0.1.2:9055", "10.0.1.3:9055" },
            neighbours.Select(n => n.ToString()).ToArray());
    }

    [TestMethod]
    public async Task Resolve_ExcludesTheNodeItself()
    {
        var lookup = new FakeAzureLookup(
            new AzureInstance("10.0.1.1", "vm-self"),
            new AzureInstance("10.0.1.2", "vm-other"));

        using var discovery = new AzureTagNodeDiscovery(Configuration("10.0.1.1", 9055), Options(), lookup);

        var neighbours = await discovery.GetNeighboursAsync();

        CollectionAssert.AreEqual(new[] { "10.0.1.2:9055" }, neighbours.Select(n => n.ToString()).ToArray());
    }

    [TestMethod]
    public async Task Resolve_PassesTheScopeAndTagThrough()
    {
        var lookup = new FakeAzureLookup();

        using var discovery = new AzureTagNodeDiscovery(Configuration(), Options(), lookup);

        await discovery.GetNeighboursAsync();

        Assert.AreEqual("gossnet-rg", lookup.LastResourceGroup);
        Assert.AreEqual("gossnet-cluster", lookup.LastTagKey);
        Assert.AreEqual("production", lookup.LastTagValue);
    }

    /// <summary>Azure describes machines, not services, so the port comes from configuration.</summary>
    [TestMethod]
    public async Task Resolve_AppliesTheConfiguredPort()
    {
        var lookup = new FakeAzureLookup(new AzureInstance("10.0.1.2", "vm-a"));

        using var discovery = new AzureTagNodeDiscovery(Configuration(), Options(port: 9999), lookup);

        var neighbours = await discovery.GetNeighboursAsync();

        CollectionAssert.AreEqual(new[] { "10.0.1.2:9999" }, neighbours.Select(n => n.ToString()).ToArray());
    }

    [TestMethod]
    public async Task Resolve_CachesWithinTheWindow()
    {
        var lookup = new FakeAzureLookup(new AzureInstance("10.0.1.2", "vm-a"));

        using var discovery = new AzureTagNodeDiscovery(Configuration(), Options(cache: TimeSpan.FromMinutes(5)), lookup);

        await discovery.GetNeighboursAsync();
        await discovery.GetNeighboursAsync();

        Assert.AreEqual(1, lookup.Queries, "Discovery runs on the message path and must not query ARM per message.");
    }

    [TestMethod]
    public async Task Resolve_ReQueriesAfterTheCacheExpires()
    {
        var lookup = new FakeAzureLookup(new AzureInstance("10.0.1.2", "vm-a"));

        using var discovery = new AzureTagNodeDiscovery(
            Configuration(), Options(cache: TimeSpan.FromMilliseconds(50)), lookup);

        await discovery.GetNeighboursAsync();
        await Task.Delay(120);
        await discovery.GetNeighboursAsync();

        Assert.AreEqual(2, lookup.Queries);
    }

    [TestMethod]
    public async Task Resolve_WrapsBackendFailures()
    {
        var lookup = new FakeAzureLookup(new InvalidOperationException("AuthorizationFailed"));

        using var discovery = new AzureTagNodeDiscovery(Configuration(), Options(), lookup);

        var ex = await Assert.ThrowsExactlyAsync<NodeDiscoveryException>(
            async () => await discovery.GetNeighboursAsync());

        Assert.IsInstanceOfType<InvalidOperationException>(ex.InnerException);
        StringAssert.Contains(ex.Message, "gossnet-rg");
    }

    [TestMethod]
    public async Task Resolve_PropagatesCancellation()
    {
        var lookup = new FakeAzureLookup(new OperationCanceledException());

        using var discovery = new AzureTagNodeDiscovery(Configuration(), Options(), lookup);
        using var cts = new CancellationTokenSource();

        await cts.CancelAsync();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            async () => await discovery.GetNeighboursAsync(cts.Token));
    }

    [TestMethod]
    public void Dispose_LeavesAnInjectedLookupAlone()
    {
        var lookup = new FakeAzureLookup();
        var discovery = new AzureTagNodeDiscovery(Configuration(), Options(), lookup);

        discovery.Dispose();

        Assert.IsFalse(lookup.IsDisposed);
    }

    [TestMethod]
    [DataRow("", "key", "value")]
    [DataRow("rg", "", "value")]
    [DataRow("rg", "key", "")]
    public void MissingRequiredOptions_AreRejected(string resourceGroup, string tagKey, string tagValue)
    {
        Assert.ThrowsExactly<ArgumentException>(() => new AzureTagNodeDiscovery(
            Configuration(),
            new AzureDiscoveryOptions { ResourceGroup = resourceGroup, TagKey = tagKey, TagValue = tagValue },
            new FakeAzureLookup()));
    }

    [TestMethod]
    public async Task Resolve_HandlesAnEmptyResourceGroup()
    {
        using var discovery = new AzureTagNodeDiscovery(Configuration(), Options(), new FakeAzureLookup());

        Assert.AreEqual(0, (await discovery.GetNeighboursAsync()).Count);
    }
}
