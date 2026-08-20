namespace GossNet.Protocol.Tests;

[TestClass]
public sealed class CachingNodeDiscoveryTests
{
    private static readonly GossNetNodeHostEntry Neighbour = new() { Hostname = "node-b", Port = 9056 };

    /// <summary>Scriptable provider: each call takes the next step in the sequence.</summary>
    private sealed class ScriptedDiscovery(TimeSpan cacheDuration) : CachingNodeDiscovery(cacheDuration)
    {
        private readonly Queue<Func<ValueTask<IReadOnlyList<GossNetNodeHostEntry>>>> _steps = new();

        public int Resolves { get; private set; }

        public ScriptedDiscovery Returns(params GossNetNodeHostEntry[] neighbours)
        {
            _steps.Enqueue(() => new ValueTask<IReadOnlyList<GossNetNodeHostEntry>>(neighbours));
            return this;
        }

        public ScriptedDiscovery Throws()
        {
            _steps.Enqueue(() => throw new NodeDiscoveryException("backend unreachable"));
            return this;
        }

        public ScriptedDiscovery Awaits(TaskCompletionSource<IReadOnlyList<GossNetNodeHostEntry>> gate)
        {
            _steps.Enqueue(async () => await gate.Task);
            return this;
        }

        protected override ValueTask<IReadOnlyList<GossNetNodeHostEntry>> ResolveAsync(CancellationToken cancellationToken)
        {
            Resolves++;

            return _steps.Count > 0
                ? _steps.Dequeue()()
                : new ValueTask<IReadOnlyList<GossNetNodeHostEntry>>([Neighbour]);
        }
    }

    // ---------------------------------------------------------------------------
    // Stale-on-error.
    // ---------------------------------------------------------------------------

    [TestMethod]
    public async Task ResolveFailure_ServesThePreviousResult()
    {
        // Zero cache duration so every call reaches the backend.
        var discovery = new ScriptedDiscovery(TimeSpan.Zero).Returns(Neighbour).Throws();

        var first = await discovery.GetNeighboursAsync();
        var second = await discovery.GetNeighboursAsync();

        // A backend outage must not blind a cluster that already knows its members.
        Assert.AreEqual(1, second.Count);
        Assert.AreSame(first[0], second[0]);
    }

    [TestMethod]
    public async Task ResolveFailure_WithNoPreviousResult_Throws()
    {
        var discovery = new ScriptedDiscovery(TimeSpan.Zero).Throws();

        // Nothing to fall back on: surfacing the failure is what keeps an
        // unreachable backend from looking like a network of one.
        await Assert.ThrowsExactlyAsync<NodeDiscoveryException>(async () => await discovery.GetNeighboursAsync());
    }

    [TestMethod]
    public async Task ResolveFailure_IsRetriedOnTheNextCall()
    {
        var replacement = new GossNetNodeHostEntry { Hostname = "node-c", Port = 9057 };
        var discovery = new ScriptedDiscovery(TimeSpan.Zero).Returns(Neighbour).Throws().Returns(replacement);

        await discovery.GetNeighboursAsync();
        await discovery.GetNeighboursAsync();
        var recovered = await discovery.GetNeighboursAsync();

        // Serving stale must not pin the cache: the recovered backend's answer wins.
        Assert.AreEqual("node-c", recovered[0].Hostname);
        Assert.AreEqual(3, discovery.Resolves);
    }

    // ---------------------------------------------------------------------------
    // Single-flight refresh.
    // ---------------------------------------------------------------------------

    [TestMethod]
    public async Task ConcurrentCallersOnAnExpiredCache_ShareOneResolve()
    {
        var gate = new TaskCompletionSource<IReadOnlyList<GossNetNodeHostEntry>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var discovery = new ScriptedDiscovery(TimeSpan.FromMinutes(1)).Awaits(gate);

        var callers = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(async () => await discovery.GetNeighboursAsync()))
            .ToArray();

        // Give every caller time to reach the refresh gate while the resolve is
        // parked, then let it finish.
        await Task.Delay(100);
        gate.SetResult([Neighbour]);

        var results = await Task.WhenAll(callers);

        Assert.AreEqual(1, discovery.Resolves, "an expired cache under concurrency must trigger exactly one backend query");
        Assert.IsTrue(results.All(r => r.Count == 1));
    }
}
