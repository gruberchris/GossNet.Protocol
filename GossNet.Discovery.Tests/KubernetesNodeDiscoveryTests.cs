using GossNet.Discovery.Kubernetes;
using GossNet.Protocol;

namespace GossNet.Discovery.Tests;

[TestClass]
public sealed class KubernetesNodeDiscoveryTests
{
    private sealed class FakePodLookup : IKubernetesPodLookup
    {
        private readonly KubernetesPodInfo[] _pods;
        private readonly Exception? _fault;

        public FakePodLookup(params KubernetesPodInfo[] pods) => _pods = pods;

        public FakePodLookup(Exception fault)
        {
            _pods = [];
            _fault = fault;
        }

        public int Queries { get; private set; }
        public string? LastNamespace { get; private set; }
        public string? LastLabelSelector { get; private set; }
        public string? LastFieldSelector { get; private set; }
        public bool IsDisposed { get; private set; }
        public string? CurrentNamespace { get; set; }

        public ValueTask<IReadOnlyList<KubernetesPodInfo>> ListPodsAsync(
            string namespaceName, string labelSelector, string? fieldSelector, CancellationToken cancellationToken = default)
        {
            Queries++;
            LastNamespace = namespaceName;
            LastLabelSelector = labelSelector;
            LastFieldSelector = fieldSelector;

            if (_fault is not null)
            {
                throw _fault;
            }

            return new ValueTask<IReadOnlyList<KubernetesPodInfo>>(_pods);
        }

        public void Dispose() => IsDisposed = true;
    }

    private static KubernetesPodInfo Pod(string name, string? ip, bool ready = true) => new(name, ip, ready);

    private static GossNetConfiguration Configuration(string hostname = "10.1.0.1", int port = 9055) => new()
    {
        Hostname = hostname,
        Port = port
    };

    private static KubernetesDiscoveryOptions Options(
        TimeSpan? cache = null, string? ns = null, int? port = null, bool readyOnly = true, string? fieldSelector = null) => new()
    {
        LabelSelector = "app=gossnet",
        Namespace = ns,
        Port = port,
        ReadyOnly = readyOnly,
        FieldSelector = fieldSelector,
        CacheDuration = cache
    };

    [TestMethod]
    public async Task Resolve_MapsPodIpsToNeighbours()
    {
        var lookup = new FakePodLookup(Pod("a", "10.1.0.2"), Pod("b", "10.1.0.3"));
        using var discovery = new KubernetesNodeDiscovery(Configuration(), Options(), lookup);

        var neighbours = await discovery.GetNeighboursAsync();

        CollectionAssert.AreEquivalent(
            new[] { "10.1.0.2:9055", "10.1.0.3:9055" },
            neighbours.Select(n => n.ToString()).ToArray());
    }

    /// <summary>The pod running this node matches its own label selector.</summary>
    [TestMethod]
    public async Task Resolve_ExcludesTheNodesOwnPod()
    {
        var lookup = new FakePodLookup(Pod("self", "10.1.0.1"), Pod("peer", "10.1.0.2"));
        using var discovery = new KubernetesNodeDiscovery(Configuration("10.1.0.1", 9055), Options(), lookup);

        var neighbours = await discovery.GetNeighboursAsync();

        Assert.AreEqual(1, neighbours.Count);
        Assert.AreEqual("10.1.0.2:9055", neighbours[0].ToString());
    }

    /// <summary>A pod that is scheduled but not Ready has no listening socket yet.</summary>
    [TestMethod]
    public async Task Resolve_SkipsPodsThatAreNotReady()
    {
        var lookup = new FakePodLookup(Pod("ready", "10.1.0.2"), Pod("starting", "10.1.0.3", ready: false));
        using var discovery = new KubernetesNodeDiscovery(Configuration(), Options(), lookup);

        var neighbours = await discovery.GetNeighboursAsync();

        Assert.AreEqual(1, neighbours.Count);
        Assert.AreEqual("10.1.0.2:9055", neighbours[0].ToString());
    }

    [TestMethod]
    public async Task Resolve_IncludesUnreadyPodsWhenReadyOnlyIsDisabled()
    {
        var lookup = new FakePodLookup(Pod("ready", "10.1.0.2"), Pod("starting", "10.1.0.3", ready: false));
        using var discovery = new KubernetesNodeDiscovery(Configuration(), Options(readyOnly: false), lookup);

        Assert.AreEqual(2, (await discovery.GetNeighboursAsync()).Count);
    }

    /// <summary>A pod can exist before the scheduler has assigned it an address.</summary>
    [TestMethod]
    public async Task Resolve_SkipsPodsWithoutAnAssignedIp()
    {
        var lookup = new FakePodLookup(Pod("pending", null), Pod("running", "10.1.0.2"));
        using var discovery = new KubernetesNodeDiscovery(Configuration(), Options(), lookup);

        var neighbours = await discovery.GetNeighboursAsync();

        Assert.AreEqual(1, neighbours.Count);
        Assert.AreEqual("10.1.0.2:9055", neighbours[0].ToString());
    }

    [TestMethod]
    public async Task Resolve_UsesThePodsOwnNamespaceByDefault()
    {
        var lookup = new FakePodLookup(Pod("a", "10.1.0.2")) { CurrentNamespace = "gossip-system" };
        using var discovery = new KubernetesNodeDiscovery(Configuration(), Options(), lookup);

        await discovery.GetNeighboursAsync();

        Assert.AreEqual("gossip-system", lookup.LastNamespace, "a deployment should not have to be told where it runs");
    }

    [TestMethod]
    public async Task Resolve_FallsBackToDefaultNamespaceOutsideACluster()
    {
        var lookup = new FakePodLookup(Pod("a", "10.1.0.2"));
        using var discovery = new KubernetesNodeDiscovery(Configuration(), Options(), lookup);

        await discovery.GetNeighboursAsync();

        Assert.AreEqual("default", lookup.LastNamespace);
    }

    [TestMethod]
    public async Task Resolve_PrefersAnExplicitNamespace()
    {
        var lookup = new FakePodLookup(Pod("a", "10.1.0.2")) { CurrentNamespace = "gossip-system" };
        using var discovery = new KubernetesNodeDiscovery(Configuration(), Options(ns: "explicit"), lookup);

        await discovery.GetNeighboursAsync();

        Assert.AreEqual("explicit", lookup.LastNamespace);
    }

    [TestMethod]
    public async Task Resolve_PassesTheConfiguredSelectors()
    {
        var lookup = new FakePodLookup(Pod("a", "10.1.0.2"));
        using var discovery = new KubernetesNodeDiscovery(Configuration(), Options(fieldSelector: "status.phase=Running"), lookup);

        await discovery.GetNeighboursAsync();

        Assert.AreEqual("app=gossnet", lookup.LastLabelSelector);
        Assert.AreEqual("status.phase=Running", lookup.LastFieldSelector);
    }

    [TestMethod]
    public async Task Resolve_UsesTheConfiguredPortOverride()
    {
        var lookup = new FakePodLookup(Pod("a", "10.1.0.2"));
        using var discovery = new KubernetesNodeDiscovery(Configuration(port: 9055), Options(port: 7000), lookup);

        var neighbours = await discovery.GetNeighboursAsync();

        Assert.AreEqual("10.1.0.2:7000", neighbours[0].ToString());
    }

    [TestMethod]
    public async Task Resolve_CachesWithinTheCacheWindow()
    {
        var lookup = new FakePodLookup(Pod("a", "10.1.0.2"));
        using var discovery = new KubernetesNodeDiscovery(Configuration(), Options(TimeSpan.FromSeconds(30)), lookup);

        for (var i = 0; i < 50; i++)
        {
            await discovery.GetNeighboursAsync();
        }

        Assert.AreEqual(1, lookup.Queries, "discovery runs on the message path and must not call the API server per message");
    }

    [TestMethod]
    public async Task Resolve_RefreshesAfterTheCacheExpires()
    {
        var lookup = new FakePodLookup(Pod("a", "10.1.0.2"));
        using var discovery = new KubernetesNodeDiscovery(Configuration(), Options(TimeSpan.FromMilliseconds(50)), lookup);

        await discovery.GetNeighboursAsync();
        await Task.Delay(120);
        await discovery.GetNeighboursAsync();

        Assert.AreEqual(2, lookup.Queries, "pods come and go, so membership must be refreshed");
    }

    [TestMethod]
    public async Task Resolve_WhenTheApiServerFails_ThrowsInsteadOfReturningEmpty()
    {
        var lookup = new FakePodLookup(new HttpRequestException("connection refused"));
        using var discovery = new KubernetesNodeDiscovery(Configuration(), Options(), lookup);

        var exception = await Assert.ThrowsExactlyAsync<NodeDiscoveryException>(
            async () => await discovery.GetNeighboursAsync());

        StringAssert.Contains(exception.Message, "app=gossnet");
        Assert.IsInstanceOfType<HttpRequestException>(exception.InnerException);
    }

    [TestMethod]
    public async Task Resolve_PropagatesCancellation()
    {
        var lookup = new FakePodLookup(new OperationCanceledException());
        using var discovery = new KubernetesNodeDiscovery(Configuration(), Options(), lookup);
        using var cts = new CancellationTokenSource();

        await cts.CancelAsync();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            async () => await discovery.GetNeighboursAsync(cts.Token));
    }

    [TestMethod]
    public void Constructor_WithoutLabelSelector_Throws() =>
        Assert.ThrowsExactly<ArgumentException>(() =>
            new KubernetesNodeDiscovery(Configuration(), new KubernetesDiscoveryOptions { LabelSelector = " " }));

    [TestMethod]
    public void Dispose_DoesNotDisposeACallerSuppliedLookup()
    {
        var lookup = new FakePodLookup();

        new KubernetesNodeDiscovery(Configuration(), Options(), lookup).Dispose();

        Assert.IsFalse(lookup.IsDisposed, "a shared client must outlive the provider that borrowed it");
    }

    [TestMethod]
    public async Task IntegratesWithANodeViaDiscoveryProviderFactory()
    {
        var lookup = new FakePodLookup(Pod("peer", "10.1.0.2"));

        var configuration = new GossNetConfiguration
        {
            Hostname = "10.1.0.1",
            Port = 9055,
            DiscoveryProviderFactory = cfg => new KubernetesNodeDiscovery(cfg, Options(), lookup)
        };

        var udpClient = new NullUdpClient();
        using var node = new GossNetNode<KubernetesTestMessage>(configuration, udpClient: udpClient);

        await node.SendAsync(new KubernetesTestMessage());

        Assert.AreEqual(1, lookup.Queries);
        Assert.AreEqual(1, udpClient.Sent.Count);
        Assert.AreEqual("10.1.0.2", udpClient.Sent[0].Hostname);
    }
}

public sealed class KubernetesTestMessage : GossNetMessageBase;
