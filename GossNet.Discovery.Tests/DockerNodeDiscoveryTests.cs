using GossNet.Discovery.Docker;
using GossNet.Protocol;

namespace GossNet.Discovery.Tests;

[TestClass]
public sealed class DockerNodeDiscoveryTests
{
    private sealed class FakeDockerLookup : IDockerContainerLookup
    {
        private readonly DockerContainerInfo[] _containers;
        private readonly Exception? _fault;

        public FakeDockerLookup(params DockerContainerInfo[] containers) => _containers = containers;

        public FakeDockerLookup(Exception fault)
        {
            _containers = [];
            _fault = fault;
        }

        public int Queries { get; private set; }
        public string? LastLabel { get; private set; }
        public bool LastRunningOnly { get; private set; }
        public bool IsDisposed { get; private set; }

        public ValueTask<IReadOnlyList<DockerContainerInfo>> ListContainersAsync(
            string label, bool runningOnly, CancellationToken cancellationToken = default)
        {
            Queries++;
            LastLabel = label;
            LastRunningOnly = runningOnly;

            if (_fault is not null)
            {
                throw _fault;
            }

            return new ValueTask<IReadOnlyList<DockerContainerInfo>>(_containers);
        }

        public void Dispose() => IsDisposed = true;
    }

    private static DockerContainerInfo Container(
        string name, bool running = true, params (string Network, string Address)[] networks) =>
        new(name, running, networks.ToDictionary(n => n.Network, n => n.Address, StringComparer.OrdinalIgnoreCase));

    private static GossNetConfiguration Configuration(string hostname = "172.18.0.1", int port = 9055) => new()
    {
        Hostname = hostname,
        Port = port
    };

    private static DockerDiscoveryOptions Options(
        TimeSpan? cache = null, string? network = null, int? port = null, bool runningOnly = true) => new()
    {
        Label = "app=gossnet",
        NetworkName = network,
        Port = port,
        RunningOnly = runningOnly,
        CacheDuration = cache
    };

    [TestMethod]
    public async Task Resolve_MapsContainerAddressesToNeighbours()
    {
        var lookup = new FakeDockerLookup(
            Container("a", networks: ("gossnet-net", "172.18.0.2")),
            Container("b", networks: ("gossnet-net", "172.18.0.3")));

        using var discovery = new DockerNodeDiscovery(Configuration(), Options(), lookup);

        var neighbours = await discovery.GetNeighboursAsync();

        CollectionAssert.AreEquivalent(
            new[] { "172.18.0.2:9055", "172.18.0.3:9055" },
            neighbours.Select(n => n.ToString()).ToArray());
    }

    /// <summary>The container running this node carries the same label.</summary>
    [TestMethod]
    public async Task Resolve_ExcludesTheNodesOwnContainer()
    {
        var lookup = new FakeDockerLookup(
            Container("self", networks: ("gossnet-net", "172.18.0.1")),
            Container("peer", networks: ("gossnet-net", "172.18.0.2")));

        using var discovery = new DockerNodeDiscovery(Configuration("172.18.0.1", 9055), Options(), lookup);

        var neighbours = await discovery.GetNeighboursAsync();

        Assert.AreEqual(1, neighbours.Count);
        Assert.AreEqual("172.18.0.2:9055", neighbours[0].ToString());
    }

    [TestMethod]
    public async Task Resolve_SkipsContainersThatAreNotRunning()
    {
        var lookup = new FakeDockerLookup(
            Container("running", networks: ("gossnet-net", "172.18.0.2")),
            Container("exited", running: false, networks: ("gossnet-net", "172.18.0.3")));

        using var discovery = new DockerNodeDiscovery(Configuration(), Options(), lookup);

        var neighbours = await discovery.GetNeighboursAsync();

        Assert.AreEqual(1, neighbours.Count);
        Assert.AreEqual("172.18.0.2:9055", neighbours[0].ToString());
    }

    [TestMethod]
    public async Task Resolve_IncludesStoppedContainersWhenRunningOnlyIsDisabled()
    {
        var lookup = new FakeDockerLookup(
            Container("running", networks: ("gossnet-net", "172.18.0.2")),
            Container("exited", running: false, networks: ("gossnet-net", "172.18.0.3")));

        using var discovery = new DockerNodeDiscovery(Configuration(), Options(runningOnly: false), lookup);

        Assert.AreEqual(2, (await discovery.GetNeighboursAsync()).Count);
        Assert.IsFalse(lookup.LastRunningOnly, "the daemon must be asked for non-running containers too");
    }

    /// <summary>A container can be listed before it is attached to a network.</summary>
    [TestMethod]
    public async Task Resolve_SkipsContainersWithoutAnAddress()
    {
        var lookup = new FakeDockerLookup(
            Container("detached"),
            Container("attached", networks: ("gossnet-net", "172.18.0.2")));

        using var discovery = new DockerNodeDiscovery(Configuration(), Options(), lookup);

        var neighbours = await discovery.GetNeighboursAsync();

        Assert.AreEqual(1, neighbours.Count);
        Assert.AreEqual("172.18.0.2:9055", neighbours[0].ToString());
    }

    /// <summary>
    /// A multi-homed container has an address on every network it joins, so the right
    /// one has to be chosen rather than guessed.
    /// </summary>
    [TestMethod]
    public async Task Resolve_UsesTheConfiguredNetworkForMultiHomedContainers()
    {
        var lookup = new FakeDockerLookup(
            Container("peer", networks: [("bridge", "172.17.0.9"), ("gossnet-net", "172.18.0.2")]));

        using var discovery = new DockerNodeDiscovery(Configuration(), Options(network: "gossnet-net"), lookup);

        var neighbours = await discovery.GetNeighboursAsync();

        Assert.AreEqual("172.18.0.2:9055", neighbours[0].ToString(), "the address on the configured network must win");
    }

    [TestMethod]
    public async Task Resolve_SkipsContainersNotOnTheConfiguredNetwork()
    {
        var lookup = new FakeDockerLookup(
            Container("elsewhere", networks: ("bridge", "172.17.0.9")),
            Container("peer", networks: ("gossnet-net", "172.18.0.2")));

        using var discovery = new DockerNodeDiscovery(Configuration(), Options(network: "gossnet-net"), lookup);

        var neighbours = await discovery.GetNeighboursAsync();

        Assert.AreEqual(1, neighbours.Count, "a container off the network is not reachable at a predictable address");
        Assert.AreEqual("172.18.0.2:9055", neighbours[0].ToString());
    }

    [TestMethod]
    public async Task Resolve_PassesTheConfiguredLabel()
    {
        var lookup = new FakeDockerLookup(Container("peer", networks: ("gossnet-net", "172.18.0.2")));
        using var discovery = new DockerNodeDiscovery(Configuration(), Options(), lookup);

        await discovery.GetNeighboursAsync();

        Assert.AreEqual("app=gossnet", lookup.LastLabel);
        Assert.IsTrue(lookup.LastRunningOnly);
    }

    [TestMethod]
    public async Task Resolve_UsesTheConfiguredPortOverride()
    {
        var lookup = new FakeDockerLookup(Container("peer", networks: ("gossnet-net", "172.18.0.2")));
        using var discovery = new DockerNodeDiscovery(Configuration(port: 9055), Options(port: 7000), lookup);

        Assert.AreEqual("172.18.0.2:7000", (await discovery.GetNeighboursAsync())[0].ToString());
    }

    [TestMethod]
    public async Task Resolve_CachesWithinTheCacheWindow()
    {
        var lookup = new FakeDockerLookup(Container("peer", networks: ("gossnet-net", "172.18.0.2")));
        using var discovery = new DockerNodeDiscovery(Configuration(), Options(TimeSpan.FromSeconds(30)), lookup);

        for (var i = 0; i < 50; i++)
        {
            await discovery.GetNeighboursAsync();
        }

        Assert.AreEqual(1, lookup.Queries, "discovery runs on the message path and must not call the daemon per message");
    }

    [TestMethod]
    public async Task Resolve_RefreshesAfterTheCacheExpires()
    {
        var lookup = new FakeDockerLookup(Container("peer", networks: ("gossnet-net", "172.18.0.2")));
        using var discovery = new DockerNodeDiscovery(Configuration(), Options(TimeSpan.FromMilliseconds(50)), lookup);

        await discovery.GetNeighboursAsync();
        await Task.Delay(120);
        await discovery.GetNeighboursAsync();

        Assert.AreEqual(2, lookup.Queries, "containers come and go, so membership must be refreshed");
    }

    [TestMethod]
    public async Task Resolve_WhenTheDaemonFails_ThrowsInsteadOfReturningEmpty()
    {
        var lookup = new FakeDockerLookup(new HttpRequestException("cannot connect to the Docker daemon"));
        using var discovery = new DockerNodeDiscovery(Configuration(), Options(), lookup);

        var exception = await Assert.ThrowsExactlyAsync<NodeDiscoveryException>(
            async () => await discovery.GetNeighboursAsync());

        StringAssert.Contains(exception.Message, "app=gossnet");
        Assert.IsInstanceOfType<HttpRequestException>(exception.InnerException);
    }

    [TestMethod]
    public async Task Resolve_PropagatesCancellation()
    {
        var lookup = new FakeDockerLookup(new OperationCanceledException());
        using var discovery = new DockerNodeDiscovery(Configuration(), Options(), lookup);
        using var cts = new CancellationTokenSource();

        await cts.CancelAsync();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            async () => await discovery.GetNeighboursAsync(cts.Token));
    }

    [TestMethod]
    public void Constructor_WithoutLabel_Throws() =>
        Assert.ThrowsExactly<ArgumentException>(() =>
            new DockerNodeDiscovery(Configuration(), new DockerDiscoveryOptions { Label = " " }));

    [TestMethod]
    public void Dispose_DoesNotDisposeACallerSuppliedLookup()
    {
        var lookup = new FakeDockerLookup();

        new DockerNodeDiscovery(Configuration(), Options(), lookup).Dispose();

        Assert.IsFalse(lookup.IsDisposed, "a shared client must outlive the provider that borrowed it");
    }

    [TestMethod]
    public async Task IntegratesWithANodeViaDiscoveryProviderFactory()
    {
        var lookup = new FakeDockerLookup(Container("peer", networks: ("gossnet-net", "172.18.0.2")));

        var configuration = new GossNetConfiguration
        {
            Hostname = "172.18.0.1",
            Port = 9055,
            DiscoveryProviderFactory = cfg => new DockerNodeDiscovery(cfg, Options(), lookup)
        };

        var udpClient = new NullUdpClient();
        using var node = new GossNetNode<DockerTestMessage>(configuration, udpClient: udpClient);

        await node.SendAsync(new DockerTestMessage());

        Assert.AreEqual(1, lookup.Queries);
        Assert.AreEqual(1, udpClient.Sent.Count);
        Assert.AreEqual("172.18.0.2", udpClient.Sent[0].Hostname);
    }
}

public sealed class DockerTestMessage : GossNetMessageBase;
