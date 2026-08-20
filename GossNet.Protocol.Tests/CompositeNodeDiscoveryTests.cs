namespace GossNet.Protocol.Tests;

[TestClass]
public sealed class CompositeNodeDiscoveryTests
{
    private static readonly GossNetNodeHostEntry NodeA = new() { Hostname = "10.0.0.1", Port = 9055 };
    private static readonly GossNetNodeHostEntry NodeB = new() { Hostname = "10.0.0.2", Port = 9055 };
    private static readonly GossNetNodeHostEntry NodeC = new() { Hostname = "10.0.0.3", Port = 9055 };

    /// <summary>A provider returning a fixed list, or throwing a fixed fault.</summary>
    private sealed class StubDiscovery : INodeDiscovery, IDisposable
    {
        private readonly GossNetNodeHostEntry[] _neighbours;
        private readonly Exception? _fault;

        public StubDiscovery(params GossNetNodeHostEntry[] neighbours) => _neighbours = neighbours;

        public StubDiscovery(Exception fault)
        {
            _neighbours = [];
            _fault = fault;
        }

        public int Queries { get; private set; }

        public bool IsDisposed { get; private set; }

        public ValueTask<IReadOnlyList<GossNetNodeHostEntry>> GetNeighboursAsync(CancellationToken cancellationToken = default)
        {
            Queries++;

            if (_fault is not null)
            {
                throw _fault;
            }

            return new ValueTask<IReadOnlyList<GossNetNodeHostEntry>>(_neighbours);
        }

        public void Dispose() => IsDisposed = true;
    }

    [TestMethod]
    public async Task Union_CombinesEveryProvider()
    {
        using var composite = new CompositeNodeDiscovery(
        [
            new StubDiscovery(NodeA),
            new StubDiscovery(NodeB, NodeC)
        ]);

        var neighbours = await composite.GetNeighboursAsync();

        CollectionAssert.AreEquivalent(
            new[] { "10.0.0.1:9055", "10.0.0.2:9055", "10.0.0.3:9055" },
            neighbours.Select(n => n.ToString()).ToArray());
    }

    /// <summary>The same node registered in two backends must be contacted once, not twice.</summary>
    [TestMethod]
    public async Task Union_DeduplicatesOverlappingEntries()
    {
        using var composite = new CompositeNodeDiscovery(
        [
            new StubDiscovery(NodeA, NodeB),
            new StubDiscovery(NodeB, NodeC)
        ]);

        var neighbours = await composite.GetNeighboursAsync();

        Assert.AreEqual(3, neighbours.Count);
    }

    [TestMethod]
    public async Task Union_PreservesFirstSeenOrder()
    {
        using var composite = new CompositeNodeDiscovery(
        [
            new StubDiscovery(NodeC),
            new StubDiscovery(NodeA, NodeC, NodeB)
        ]);

        var neighbours = await composite.GetNeighboursAsync();

        CollectionAssert.AreEqual(
            new[] { "10.0.0.3:9055", "10.0.0.1:9055", "10.0.0.2:9055" },
            neighbours.Select(n => n.ToString()).ToArray());
    }

    /// <summary>An unreachable registry must not blind a cluster that also has static seeds.</summary>
    [TestMethod]
    public async Task OneFailingProvider_StillReturnsTheOthers()
    {
        using var composite = new CompositeNodeDiscovery(
        [
            new StubDiscovery(new NodeDiscoveryException("consul is down")),
            new StubDiscovery(NodeA, NodeB)
        ]);

        var neighbours = await composite.GetNeighboursAsync();

        Assert.AreEqual(2, neighbours.Count);
    }

    /// <summary>
    /// Returning nothing would be indistinguishable from a network of one, which is the
    /// silent failure the discovery layer exists to prevent.
    /// </summary>
    [TestMethod]
    public async Task EveryProviderFailing_Throws()
    {
        using var composite = new CompositeNodeDiscovery(
        [
            new StubDiscovery(new NodeDiscoveryException("first")),
            new StubDiscovery(new InvalidOperationException("second"))
        ]);

        var ex = await Assert.ThrowsExactlyAsync<NodeDiscoveryException>(
            async () => await composite.GetNeighboursAsync());

        var aggregate = (AggregateException)ex.InnerException!;

        Assert.AreEqual(2, aggregate.InnerExceptions.Count, "Every underlying failure should be reported.");
        CollectionAssert.AreEquivalent(
            new[] { "first", "second" },
            aggregate.InnerExceptions.Select(e => e.Message).ToArray());
    }

    [TestMethod]
    public async Task EveryProviderQueriedEachTime()
    {
        var first = new StubDiscovery(NodeA);
        var second = new StubDiscovery(NodeB);

        using var composite = new CompositeNodeDiscovery([first, second]);

        await composite.GetNeighboursAsync();
        await composite.GetNeighboursAsync();

        // The composite adds no cache of its own; children cache themselves.
        Assert.AreEqual(2, first.Queries);
        Assert.AreEqual(2, second.Queries);
    }

    [TestMethod]
    public void EmptyProviderList_IsRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new CompositeNodeDiscovery([]));
    }

    [TestMethod]
    public void NullProviderList_IsRejected()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new CompositeNodeDiscovery(null!));
    }

    [TestMethod]
    public void NullProvider_IsRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new CompositeNodeDiscovery([new StubDiscovery(NodeA), null!]));
    }

    [TestMethod]
    public void Dispose_DisposesOwnedProviders()
    {
        var child = new StubDiscovery(NodeA);
        var composite = new CompositeNodeDiscovery([child]);

        composite.Dispose();

        Assert.IsTrue(child.IsDisposed);
    }

    /// <summary>A provider the caller still uses elsewhere must survive the composite.</summary>
    [TestMethod]
    public void Dispose_LeavesBorrowedProvidersAlone()
    {
        var child = new StubDiscovery(NodeA);
        var composite = new CompositeNodeDiscovery([child], ownsProviders: false);

        composite.Dispose();

        Assert.IsFalse(child.IsDisposed);
    }

    [TestMethod]
    public async Task UseAfterDispose_Throws()
    {
        var composite = new CompositeNodeDiscovery([new StubDiscovery(NodeA)]);
        composite.Dispose();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () => await composite.GetNeighboursAsync());
    }

    [TestMethod]
    public async Task Cancellation_Propagates()
    {
        using var composite = new CompositeNodeDiscovery([new StubDiscovery(NodeA)]);
        using var cts = new CancellationTokenSource();

        await cts.CancelAsync();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            async () => await composite.GetNeighboursAsync(cts.Token));
    }

    [TestMethod]
    public void ProviderCount_ReportsTheChildren()
    {
        using var composite = new CompositeNodeDiscovery([new StubDiscovery(NodeA), new StubDiscovery(NodeB)]);

        Assert.AreEqual(2, composite.ProviderCount);
    }
}
