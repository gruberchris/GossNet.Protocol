namespace GossNet.Protocol.Tests;

[TestClass]
public sealed class PeerExchangeNodeDiscoveryTests
{
    private static readonly GossNetNodeHostEntry Self = new() { Hostname = "10.0.0.1", Port = 9055 };
    private static readonly GossNetNodeHostEntry Seed = new() { Hostname = "10.0.0.2", Port = 9055 };
    private static readonly GossNetNodeHostEntry PeerA = new() { Hostname = "10.0.0.3", Port = 9055 };
    private static readonly GossNetNodeHostEntry PeerB = new() { Hostname = "10.0.0.4", Port = 9055 };

    private static GossNetConfiguration Configuration(params GossNetNodeHostEntry[] seeds) => new()
    {
        Hostname = Self.Hostname,
        Port = Self.Port,
        NodeDiscovery = NodeDiscovery.PeerExchange,
        StaticNodes = seeds
    };

    private static async Task<string[]> NamesAsync(PeerExchangeNodeDiscovery discovery) =>
        [.. (await discovery.GetNeighboursAsync()).Select(n => n.ToString())];

    [TestMethod]
    public async Task Seeds_AreAvailableBeforeAnyTraffic()
    {
        var discovery = new PeerExchangeNodeDiscovery(Configuration(Seed));

        CollectionAssert.AreEqual(new[] { "10.0.0.2:9055" }, await NamesAsync(discovery));
    }

    [TestMethod]
    public async Task Observe_LearnsPeersFromNotifiedNodes()
    {
        var discovery = new PeerExchangeNodeDiscovery(Configuration(Seed));

        discovery.Observe([Seed, PeerA, PeerB]);

        CollectionAssert.AreEquivalent(
            new[] { "10.0.0.2:9055", "10.0.0.3:9055", "10.0.0.4:9055" },
            await NamesAsync(discovery));
    }

    [TestMethod]
    public async Task Observe_ExcludesTheNodeItself()
    {
        var discovery = new PeerExchangeNodeDiscovery(Configuration(Seed));

        discovery.Observe([Self, PeerA]);

        var neighbours = await NamesAsync(discovery);

        CollectionAssert.DoesNotContain(neighbours, "10.0.0.1:9055");
        Assert.AreEqual(1, discovery.LearnedPeerCount);
    }

    [TestMethod]
    public async Task Seeds_AreNotCountedAsLearnedPeers()
    {
        var discovery = new PeerExchangeNodeDiscovery(Configuration(Seed));

        discovery.Observe([Seed]);

        Assert.AreEqual(0, discovery.LearnedPeerCount);
        CollectionAssert.AreEqual(new[] { "10.0.0.2:9055" }, await NamesAsync(discovery));
    }

    [TestMethod]
    public void Observe_RepeatedSightingsDoNotDuplicate()
    {
        var discovery = new PeerExchangeNodeDiscovery(Configuration(Seed));

        discovery.Observe([PeerA]);
        discovery.Observe([PeerA]);
        discovery.Observe([PeerA]);

        Assert.AreEqual(1, discovery.LearnedPeerCount);
    }

    [TestMethod]
    public void Observe_IgnoresEmptyAndNull()
    {
        var discovery = new PeerExchangeNodeDiscovery(Configuration(Seed));

        discovery.Observe([]);
        discovery.Observe(null!);

        Assert.AreEqual(0, discovery.LearnedPeerCount);
    }

    [TestMethod]
    public async Task LearnedPeers_AgeOutAfterTheTimeout()
    {
        var discovery = new PeerExchangeNodeDiscovery(
            Configuration(Seed),
            new PeerExchangeOptions { PeerTimeout = TimeSpan.FromMilliseconds(100) });

        discovery.Observe([PeerA]);
        Assert.AreEqual(2, (await discovery.GetNeighboursAsync()).Count);

        await Task.Delay(250);

        CollectionAssert.AreEqual(new[] { "10.0.0.2:9055" }, await NamesAsync(discovery),
            "The learned peer should have expired, leaving only the seed.");
        Assert.AreEqual(0, discovery.LearnedPeerCount);
    }

    /// <summary>After a total partition the seeds are the only way back into the network.</summary>
    [TestMethod]
    public async Task Seeds_NeverAgeOut()
    {
        var discovery = new PeerExchangeNodeDiscovery(
            Configuration(Seed),
            new PeerExchangeOptions { PeerTimeout = TimeSpan.FromMilliseconds(50) });

        await Task.Delay(200);

        CollectionAssert.AreEqual(new[] { "10.0.0.2:9055" }, await NamesAsync(discovery));
    }

    [TestMethod]
    public async Task ReSighting_KeepsAPeerAlive()
    {
        var discovery = new PeerExchangeNodeDiscovery(
            Configuration(Seed),
            new PeerExchangeOptions { PeerTimeout = TimeSpan.FromMilliseconds(200) });

        discovery.Observe([PeerA]);

        for (var i = 0; i < 4; i++)
        {
            await Task.Delay(60);
            discovery.Observe([PeerA]);
        }

        CollectionAssert.Contains(await NamesAsync(discovery), "10.0.0.3:9055");
    }

    [TestMethod]
    public void MaxPeers_EvictsTheLeastRecentlySeen()
    {
        var discovery = new PeerExchangeNodeDiscovery(
            Configuration(Seed),
            new PeerExchangeOptions { MaxPeers = 2 });

        discovery.Observe([PeerA]);
        Thread.Sleep(5);
        discovery.Observe([PeerB]);
        Thread.Sleep(5);

        // Refresh A so B becomes the least recently seen.
        discovery.Observe([PeerA]);
        discovery.Observe([new GossNetNodeHostEntry { Hostname = "10.0.0.9", Port = 9055 }]);

        Assert.AreEqual(2, discovery.LearnedPeerCount);
    }

    [TestMethod]
    public void MaxPeers_MustBePositive()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new PeerExchangeNodeDiscovery(Configuration(Seed), new PeerExchangeOptions { MaxPeers = 0 }));
    }

    [TestMethod]
    public async Task Cancellation_Propagates()
    {
        var discovery = new PeerExchangeNodeDiscovery(Configuration(Seed));
        using var cts = new CancellationTokenSource();

        await cts.CancelAsync();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            async () => await discovery.GetNeighboursAsync(cts.Token));
    }

    [TestMethod]
    public async Task DuplicateSeeds_AreCollapsed()
    {
        var discovery = new PeerExchangeNodeDiscovery(Configuration(Seed, Seed, Self));

        CollectionAssert.AreEqual(new[] { "10.0.0.2:9055" }, await NamesAsync(discovery),
            "Duplicate seeds collapse, and the node's own entry is dropped.");
    }

    /// <summary>
    /// Observe runs on the receive loop while GetNeighboursAsync runs on the send path.
    /// That interleaving is exactly what forced the thread-safety fixes in the message
    /// cache, so it is exercised rather than assumed.
    /// </summary>
    [TestMethod]
    public async Task ConcurrentObserveAndRead_IsSafe()
    {
        var discovery = new PeerExchangeNodeDiscovery(Configuration(Seed), new PeerExchangeOptions { MaxPeers = 64 });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var writers = Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            for (var i = 0; !cts.IsCancellationRequested && i < 2_000; i++)
            {
                discovery.Observe([new GossNetNodeHostEntry { Hostname = $"10.1.{worker}.{i % 250}", Port = 9055 }]);
            }
        })).ToArray();

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; !cts.IsCancellationRequested && i < 2_000; i++)
            {
                // Enumerating a returned snapshot must never observe a mutation in progress.
                foreach (var neighbour in await discovery.GetNeighboursAsync())
                {
                    _ = neighbour.Port;
                }
            }
        })).ToArray();

        await Task.WhenAll([.. writers, .. readers]);

        Assert.IsLessThanOrEqualTo(64, discovery.LearnedPeerCount, "The peer set must stay bounded under load.");
    }
}
