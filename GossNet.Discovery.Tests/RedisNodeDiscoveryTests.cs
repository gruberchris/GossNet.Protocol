using GossNet.Discovery.Redis;
using GossNet.Protocol;

namespace GossNet.Discovery.Tests;

[TestClass]
public sealed class RedisNodeDiscoveryTests
{
    /// <summary>An in-memory sorted set behaving the way the Redis one does.</summary>
    private sealed class FakeRedisRegistry : IRedisRegistry
    {
        private readonly Dictionary<string, double> _members = [];
        private readonly Exception? _readFault;

        public FakeRedisRegistry(params (string Member, double Score)[] seeded)
        {
            foreach (var (member, score) in seeded)
            {
                _members[member] = score;
            }
        }

        public FakeRedisRegistry(Exception readFault) => _readFault = readFault;

        public int Heartbeats { get; private set; }
        public int Prunes { get; private set; }
        public bool IsDisposed { get; private set; }
        public List<string> Removed { get; } = [];

        public IReadOnlyDictionary<string, double> Members
        {
            get
            {
                lock (_members)
                {
                    return new Dictionary<string, double>(_members);
                }
            }
        }

        public ValueTask HeartbeatAsync(string key, string member, double score, CancellationToken cancellationToken = default)
        {
            lock (_members)
            {
                Heartbeats++;
                _members[member] = score;
            }

            return default;
        }

        public ValueTask<IReadOnlyList<string>> GetLiveMembersAsync(string key, double minScore, CancellationToken cancellationToken = default)
        {
            if (_readFault is not null)
            {
                throw _readFault;
            }

            lock (_members)
            {
                IReadOnlyList<string> live = [.. _members.Where(pair => pair.Value >= minScore).Select(pair => pair.Key)];

                return new ValueTask<IReadOnlyList<string>>(live);
            }
        }

        public ValueTask RemoveAsync(string key, string member, CancellationToken cancellationToken = default)
        {
            lock (_members)
            {
                Removed.Add(member);
                _members.Remove(member);
            }

            return default;
        }

        public ValueTask PruneAsync(string key, double maxScore, CancellationToken cancellationToken = default)
        {
            lock (_members)
            {
                Prunes++;

                foreach (var stale in _members.Where(pair => pair.Value <= maxScore).Select(pair => pair.Key).ToArray())
                {
                    _members.Remove(stale);
                }
            }

            return default;
        }

        public void Dispose() => IsDisposed = true;
    }

    private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static GossNetConfiguration Configuration(string hostname = "10.0.0.1", int port = 9055) => new()
    {
        Hostname = hostname,
        Port = port
    };

    private static RedisDiscoveryOptions Options(TimeSpan? cache = null, TimeSpan? timeout = null) => new()
    {
        Key = "gossnet:members",
        HeartbeatInterval = TimeSpan.FromMilliseconds(30),
        RegistrationTimeout = timeout ?? TimeSpan.FromSeconds(20),
        CacheDuration = cache ?? TimeSpan.Zero
    };

    private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return condition();
    }

    [TestMethod]
    [DataRow("10.0.0.2:9055", "10.0.0.2", 9055)]
    [DataRow("host.example:9100", "host.example", 9100)]
    [DataRow("fe80::1:9055", "fe80::1", 9055)]
    public void ParseMember_HandlesHostAndPort(string member, string host, int port)
    {
        Assert.IsTrue(RedisNodeDiscovery.TryParseMember(member, out var entry));
        Assert.AreEqual(host, entry.Hostname);
        Assert.AreEqual(port, entry.Port);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("noport")]
    [DataRow("10.0.0.2:")]
    [DataRow(":9055")]
    [DataRow("10.0.0.2:notaport")]
    [DataRow("10.0.0.2:0")]
    [DataRow("10.0.0.2:70000")]
    public void ParseMember_RejectsMalformedValues(string member)
    {
        Assert.IsFalse(RedisNodeDiscovery.TryParseMember(member, out _));
    }

    [TestMethod]
    public async Task Resolve_MapsLiveMembersToNeighbours()
    {
        var registry = new FakeRedisRegistry(("10.0.0.2:9055", Now()), ("10.0.0.3:9056", Now()));

        using var discovery = new RedisNodeDiscovery(Configuration(), Options(), registry: registry);

        var neighbours = await discovery.GetNeighboursAsync();

        CollectionAssert.AreEquivalent(
            new[] { "10.0.0.2:9055", "10.0.0.3:9056" },
            neighbours.Select(n => n.ToString()).ToArray());
    }

    /// <summary>This node heartbeats into the same set, so it is in its own results.</summary>
    [TestMethod]
    public async Task Resolve_ExcludesTheNodeItself()
    {
        var registry = new FakeRedisRegistry(("10.0.0.1:9055", Now()), ("10.0.0.2:9055", Now()));

        using var discovery = new RedisNodeDiscovery(Configuration("10.0.0.1", 9055), Options(), registry: registry);

        var neighbours = await discovery.GetNeighboursAsync();

        CollectionAssert.AreEqual(new[] { "10.0.0.2:9055" }, neighbours.Select(n => n.ToString()).ToArray());
    }

    [TestMethod]
    public async Task Resolve_ExcludesMembersWhoseHeartbeatIsStale()
    {
        var registry = new FakeRedisRegistry(
            ("10.0.0.2:9055", Now()),
            ("10.0.0.9:9055", Now() - 60_000));

        using var discovery = new RedisNodeDiscovery(
            Configuration(), Options(timeout: TimeSpan.FromSeconds(20)), registry: registry);

        var neighbours = await discovery.GetNeighboursAsync();

        CollectionAssert.AreEqual(new[] { "10.0.0.2:9055" }, neighbours.Select(n => n.ToString()).ToArray());
    }

    /// <summary>Another application writing to the same key must not fail the lookup.</summary>
    [TestMethod]
    public async Task Resolve_SkipsUnparseableMembers()
    {
        var registry = new FakeRedisRegistry(("garbage", Now()), ("10.0.0.2:9055", Now()));

        using var discovery = new RedisNodeDiscovery(Configuration(), Options(), registry: registry);

        var neighbours = await discovery.GetNeighboursAsync();

        CollectionAssert.AreEqual(new[] { "10.0.0.2:9055" }, neighbours.Select(n => n.ToString()).ToArray());
    }

    [TestMethod]
    public async Task Resolve_WrapsBackendFailures()
    {
        var registry = new FakeRedisRegistry(new InvalidOperationException("connection refused"));

        using var discovery = new RedisNodeDiscovery(Configuration(), Options(), registry: registry);

        var ex = await Assert.ThrowsExactlyAsync<NodeDiscoveryException>(
            async () => await discovery.GetNeighboursAsync());

        Assert.IsInstanceOfType<InvalidOperationException>(ex.InnerException);
    }

    [TestMethod]
    public async Task Heartbeat_RegistersThisNode()
    {
        var registry = new FakeRedisRegistry();

        using var discovery = new RedisNodeDiscovery(Configuration("10.0.0.1", 9055), Options(), registry: registry);

        Assert.IsTrue(await WaitForAsync(() => registry.Members.ContainsKey("10.0.0.1:9055")));
        Assert.AreEqual("10.0.0.1:9055", discovery.Member);
    }

    [TestMethod]
    public async Task Heartbeat_Repeats()
    {
        var registry = new FakeRedisRegistry();

        using var discovery = new RedisNodeDiscovery(Configuration(), Options(), registry: registry);

        Assert.IsTrue(await WaitForAsync(() => registry.Heartbeats >= 3), "Registration should be refreshed on an interval.");
    }

    /// <summary>Otherwise nodes that never come back accumulate in the set forever.</summary>
    [TestMethod]
    public async Task Heartbeat_PrunesLongDeadMembers()
    {
        var registry = new FakeRedisRegistry();

        using var discovery = new RedisNodeDiscovery(Configuration(), Options(), registry: registry);

        Assert.IsTrue(await WaitForAsync(() => registry.Prunes >= 1));
    }

    [TestMethod]
    public async Task Dispose_DeregistersThisNode()
    {
        var registry = new FakeRedisRegistry();
        var discovery = new RedisNodeDiscovery(Configuration("10.0.0.1", 9055), Options(), registry: registry);

        Assert.IsTrue(await WaitForAsync(() => registry.Members.ContainsKey("10.0.0.1:9055")));

        discovery.Dispose();

        // A clean shutdown should be noticed at once, not after the timeout.
        CollectionAssert.Contains(registry.Removed, "10.0.0.1:9055");
    }

    [TestMethod]
    public void Dispose_LeavesAnInjectedRegistryAlone()
    {
        var registry = new FakeRedisRegistry();
        var discovery = new RedisNodeDiscovery(Configuration(), Options(), registry: registry);

        discovery.Dispose();

        Assert.IsFalse(registry.IsDisposed, "A caller-supplied registry wraps a shared multiplexer.");
    }

    [TestMethod]
    public void Dispose_IsIdempotent()
    {
        var discovery = new RedisNodeDiscovery(Configuration(), Options(), registry: new FakeRedisRegistry());

        discovery.Dispose();
        discovery.Dispose();
    }

    [TestMethod]
    public void MissingKey_IsRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new RedisNodeDiscovery(
            Configuration(),
            new RedisDiscoveryOptions { Key = "  " },
            registry: new FakeRedisRegistry()));
    }

    [TestMethod]
    public void MissingConnectionString_IsRejectedWhenBuildingARegistry()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new RedisRegistry(new RedisDiscoveryOptions()));
    }
}
