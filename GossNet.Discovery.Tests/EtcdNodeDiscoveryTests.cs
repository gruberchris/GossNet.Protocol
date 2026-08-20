using System.Threading.Channels;
using GossNet.Discovery.Etcd;
using GossNet.Protocol;

namespace GossNet.Discovery.Tests;

[TestClass]
public sealed class EtcdNodeDiscoveryTests
{
    /// <summary>An in-memory prefix store with a driveable watch.</summary>
    private sealed class FakeEtcdRegistry : IEtcdRegistry
    {
        private readonly List<string> _members;
        private readonly Exception? _readFault;
        private readonly Channel<bool> _changes = Channel.CreateUnbounded<bool>();

        public FakeEtcdRegistry(params string[] members) => _members = [.. members];

        public FakeEtcdRegistry(Exception readFault)
        {
            _members = [];
            _readFault = readFault;
        }

        public int Registrations { get; private set; }
        public string? RegisteredKey { get; private set; }
        public string? RegisteredValue { get; private set; }
        public TimeSpan RegisteredTtl { get; private set; }
        public bool IsDisposed { get; private set; }
        public bool RegistrationCancelled { get; private set; }
        public Exception? RegisterFault { get; set; }

        public void Add(string member)
        {
            lock (_members)
            {
                _members.Add(member);
            }

            _changes.Writer.TryWrite(true);
        }

        public void EndWatch() => _changes.Writer.TryComplete();

        public ValueTask RegisterAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken = default)
        {
            if (RegisterFault is not null)
            {
                throw RegisterFault;
            }

            Registrations++;
            RegisteredKey = key;
            RegisteredValue = value;
            RegisteredTtl = ttl;

            cancellationToken.Register(() => RegistrationCancelled = true);

            lock (_members)
            {
                _members.Add(value);
            }

            return default;
        }

        public ValueTask<IReadOnlyList<string>> GetMembersAsync(string prefix, CancellationToken cancellationToken = default)
        {
            if (_readFault is not null)
            {
                throw _readFault;
            }

            lock (_members)
            {
                return new ValueTask<IReadOnlyList<string>>((IReadOnlyList<string>)[.. _members]);
            }
        }

        public async IAsyncEnumerable<bool> WatchAsync(
            string prefix,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var change in _changes.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return change;
            }
        }

        public void Dispose() => IsDisposed = true;
    }

    private static GossNetConfiguration Configuration(string hostname = "10.0.0.1", int port = 9055) => new()
    {
        Hostname = hostname,
        Port = port
    };

    private static EtcdDiscoveryOptions Options() => new()
    {
        Prefix = "/gossnet/members/",
        LeaseTtl = TimeSpan.FromSeconds(15),
        CacheDuration = TimeSpan.Zero
    };

    [TestMethod]
    [DataRow("10.0.0.2:9055", "10.0.0.2", 9055)]
    [DataRow("fe80::1:9055", "fe80::1", 9055)]
    public void ParseMember_HandlesHostAndPort(string member, string host, int port)
    {
        Assert.IsTrue(EtcdNodeDiscovery.TryParseMember(member, out var entry));
        Assert.AreEqual(host, entry.Hostname);
        Assert.AreEqual(port, entry.Port);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("noport")]
    [DataRow("10.0.0.2:")]
    [DataRow("10.0.0.2:notaport")]
    [DataRow("10.0.0.2:70000")]
    public void ParseMember_RejectsMalformedValues(string member)
    {
        Assert.IsFalse(EtcdNodeDiscovery.TryParseMember(member, out _));
    }

    [TestMethod]
    public async Task Resolve_MapsMembersToNeighbours()
    {
        var registry = new FakeEtcdRegistry("10.0.0.2:9055", "10.0.0.3:9056");

        using var discovery = new EtcdNodeDiscovery(Configuration(), Options(), registry: registry);

        var neighbours = await discovery.GetNeighboursAsync();

        CollectionAssert.AreEquivalent(
            new[] { "10.0.0.2:9055", "10.0.0.3:9056" },
            neighbours.Select(n => n.ToString()).ToArray());
    }

    [TestMethod]
    public async Task Resolve_RegistersThisNodeOnFirstUse()
    {
        var registry = new FakeEtcdRegistry();

        using var discovery = new EtcdNodeDiscovery(Configuration("10.0.0.1", 9055), Options(), registry: registry);

        Assert.AreEqual(0, registry.Registrations, "Constructing a provider must not perform network I/O.");

        await discovery.GetNeighboursAsync();

        Assert.AreEqual(1, registry.Registrations);
        Assert.AreEqual("/gossnet/members/10.0.0.1:9055", registry.RegisteredKey);
        Assert.AreEqual("10.0.0.1:9055", registry.RegisteredValue);
        Assert.AreEqual(TimeSpan.FromSeconds(15), registry.RegisteredTtl);
    }

    [TestMethod]
    public async Task Resolve_RegistersOnlyOnce()
    {
        var registry = new FakeEtcdRegistry();

        using var discovery = new EtcdNodeDiscovery(Configuration(), Options(), registry: registry);

        await discovery.GetNeighboursAsync();
        await discovery.GetNeighboursAsync();

        Assert.AreEqual(1, registry.Registrations);
    }

    /// <summary>This node registers under the same prefix, so it is in its own results.</summary>
    [TestMethod]
    public async Task Resolve_ExcludesTheNodeItself()
    {
        var registry = new FakeEtcdRegistry("10.0.0.2:9055");

        using var discovery = new EtcdNodeDiscovery(Configuration("10.0.0.1", 9055), Options(), registry: registry);

        var neighbours = await discovery.GetNeighboursAsync();

        CollectionAssert.AreEqual(new[] { "10.0.0.2:9055" }, neighbours.Select(n => n.ToString()).ToArray());
    }

    [TestMethod]
    public async Task Resolve_SkipsUnparseableMembers()
    {
        var registry = new FakeEtcdRegistry("garbage", "10.0.0.2:9055");

        using var discovery = new EtcdNodeDiscovery(Configuration(), Options(), registry: registry);

        var neighbours = await discovery.GetNeighboursAsync();

        // Registering also writes this node's own value, which ExcludeSelf then drops, so
        // the single real member is all that remains.
        CollectionAssert.AreEqual(new[] { "10.0.0.2:9055" }, neighbours.Select(n => n.ToString()).ToArray());
    }

    [TestMethod]
    public async Task Resolve_WrapsBackendFailures()
    {
        var registry = new FakeEtcdRegistry(new InvalidOperationException("etcdserver: request timed out"));

        using var discovery = new EtcdNodeDiscovery(Configuration(), Options(), registry: registry);

        var ex = await Assert.ThrowsExactlyAsync<NodeDiscoveryException>(
            async () => await discovery.GetNeighboursAsync());

        Assert.IsInstanceOfType<InvalidOperationException>(ex.InnerException);
    }

    /// <summary>A brief outage at startup must not leave the node permanently unregistered.</summary>
    [TestMethod]
    public async Task Resolve_RetriesRegistrationAfterAFailure()
    {
        var registry = new FakeEtcdRegistry { RegisterFault = new InvalidOperationException("etcd unavailable") };

        using var discovery = new EtcdNodeDiscovery(Configuration(), Options(), registry: registry);

        await Assert.ThrowsExactlyAsync<NodeDiscoveryException>(async () => await discovery.GetNeighboursAsync());

        registry.RegisterFault = null;
        await discovery.GetNeighboursAsync();

        Assert.AreEqual(1, registry.Registrations);
    }

    [TestMethod]
    public async Task Watch_YieldsCurrentMembershipImmediately()
    {
        var registry = new FakeEtcdRegistry("10.0.0.2:9055");

        using var discovery = new EtcdNodeDiscovery(Configuration(), Options(), registry: registry);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await foreach (var neighbours in discovery.WatchAsync(cts.Token))
        {
            // Without this a node would have no neighbours until something happened to
            // join or leave.
            CollectionAssert.Contains(neighbours.Select(n => n.ToString()).ToArray(), "10.0.0.2:9055");
            break;
        }
    }

    [TestMethod]
    public async Task Watch_YieldsAgainWhenTheBackendChanges()
    {
        var registry = new FakeEtcdRegistry("10.0.0.2:9055");

        using var discovery = new EtcdNodeDiscovery(Configuration(), Options(), registry: registry);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var updates = new List<int>();

        var reader = Task.Run(async () =>
        {
            await foreach (var neighbours in discovery.WatchAsync(cts.Token))
            {
                updates.Add(neighbours.Count);

                if (updates.Count == 2)
                {
                    break;
                }
            }
        });

        await Task.Delay(100);
        registry.Add("10.0.0.3:9055");

        await reader;

        Assert.AreEqual(2, updates.Count);
        Assert.IsGreaterThan(updates[0], updates[1], "The second update should include the added member.");
    }

    [TestMethod]
    public async Task Watch_EndsWhenTheFeedCompletes()
    {
        var registry = new FakeEtcdRegistry("10.0.0.2:9055");

        using var discovery = new EtcdNodeDiscovery(Configuration(), Options(), registry: registry);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        registry.EndWatch();

        var count = 0;

        await foreach (var _ in discovery.WatchAsync(cts.Token))
        {
            count++;
        }

        Assert.AreEqual(1, count, "Only the initial membership should have been yielded.");
    }

    /// <summary>Stopping renewal is what makes etcd expire the key and drop this node.</summary>
    [TestMethod]
    public async Task Dispose_StopsLeaseRenewal()
    {
        var registry = new FakeEtcdRegistry();
        var discovery = new EtcdNodeDiscovery(Configuration(), Options(), registry: registry);

        await discovery.GetNeighboursAsync();
        discovery.Dispose();

        Assert.IsTrue(registry.RegistrationCancelled);
    }

    [TestMethod]
    public void Dispose_LeavesAnInjectedRegistryAlone()
    {
        var registry = new FakeEtcdRegistry();
        var discovery = new EtcdNodeDiscovery(Configuration(), Options(), registry: registry);

        discovery.Dispose();

        Assert.IsFalse(registry.IsDisposed);
    }

    [TestMethod]
    public void MissingPrefix_IsRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new EtcdNodeDiscovery(
            Configuration(),
            new EtcdDiscoveryOptions { Prefix = " " },
            registry: new FakeEtcdRegistry()));
    }

    [TestMethod]
    public void MissingConnectionString_IsRejectedWhenBuildingARegistry()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new EtcdRegistry(new EtcdDiscoveryOptions()));
    }
}
