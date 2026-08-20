using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using GossNet.Protocol.Tests.Mocks;

namespace GossNet.Protocol.Tests;

/// <summary>
/// Covers the wiring between <see cref="GossNetNode{T}"/> and a provider that pushes
/// membership changes.
/// </summary>
[TestClass]
public sealed class WatchableNodeDiscoveryTests
{
    private static readonly GossNetNodeHostEntry Polled = new() { Hostname = "polled", Port = 9101 };
    private static readonly GossNetNodeHostEntry Watched = new() { Hostname = "watched", Port = 9102 };

    /// <summary>A provider whose watch is driven by the test.</summary>
    private sealed class WatchingDiscovery : IWatchableNodeDiscovery
    {
        private readonly Channel<IReadOnlyList<GossNetNodeHostEntry>> _updates =
            Channel.CreateUnbounded<IReadOnlyList<GossNetNodeHostEntry>>();

        private int _polls;

        public int Polls => Volatile.Read(ref _polls);

        public int WatchSubscriptions { get; private set; }

        /// <summary>When set, the watch throws after yielding whatever was already pushed.</summary>
        public Exception? FaultAfterDrain { get; set; }

        public void Push(params GossNetNodeHostEntry[] neighbours) => _updates.Writer.TryWrite(neighbours);

        public void EndWatch() => _updates.Writer.TryComplete();

        public ValueTask<IReadOnlyList<GossNetNodeHostEntry>> GetNeighboursAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _polls);

            return new ValueTask<IReadOnlyList<GossNetNodeHostEntry>>((IReadOnlyList<GossNetNodeHostEntry>)[Polled]);
        }

        public async IAsyncEnumerable<IReadOnlyList<GossNetNodeHostEntry>> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            WatchSubscriptions++;

            await foreach (var update in _updates.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return update;
            }

            if (FaultAfterDrain is not null)
            {
                throw FaultAfterDrain;
            }
        }
    }

    /// <summary>A provider with no watch, which must keep taking the polling path.</summary>
    private sealed class PollOnlyDiscovery : INodeDiscovery
    {
        private int _polls;

        public int Polls => Volatile.Read(ref _polls);

        public ValueTask<IReadOnlyList<GossNetNodeHostEntry>> GetNeighboursAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _polls);

            return new ValueTask<IReadOnlyList<GossNetNodeHostEntry>>((IReadOnlyList<GossNetNodeHostEntry>)[Polled]);
        }
    }

    private static GossNetConfiguration Configuration(INodeDiscovery provider) => new()
    {
        Hostname = "self",
        Port = 9100,
        DiscoveryProvider = provider
    };

    private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.ElapsedMilliseconds < timeoutMs)
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
    public async Task Start_SubscribesToAWatchingProvider()
    {
        var discovery = new WatchingDiscovery();
        await using var node = new GossNetNode<TestMessage>(Configuration(discovery), udpClient: new MockUdpClient());

        node.Start();

        Assert.IsTrue(await WaitForAsync(() => discovery.WatchSubscriptions == 1));
    }

    [TestMethod]
    public async Task PushedMembershipReplacesPolling()
    {
        var discovery = new WatchingDiscovery();
        var udpClient = new MockUdpClient();
        await using var node = new GossNetNode<TestMessage>(Configuration(discovery), udpClient: udpClient);

        node.Start();
        discovery.Push(Watched);

        Assert.IsTrue(await WaitForAsync(() => discovery.WatchSubscriptions == 1));
        await WaitForAsync(() => false, 100);

        await node.SendAsync(new TestMessage { Data = "hello" });

        CollectionAssert.AreEqual(
            new[] { "watched" },
            udpClient.SentPackets.Select(packet => packet.Hostname).ToArray(),
            "The watched membership should have been used instead of the polled list.");

        Assert.AreEqual(0, discovery.Polls, "A watching provider should not also be polled per message.");
    }

    [TestMethod]
    public async Task LaterPushesReplaceEarlierOnes()
    {
        var discovery = new WatchingDiscovery();
        var udpClient = new MockUdpClient();
        await using var node = new GossNetNode<TestMessage>(Configuration(discovery), udpClient: udpClient);

        node.Start();

        discovery.Push(Watched);
        await WaitForAsync(() => false, 100);

        discovery.Push(new GossNetNodeHostEntry { Hostname = "replacement", Port = 9103 });
        await WaitForAsync(() => false, 100);

        await node.SendAsync(new TestMessage { Data = "hello" });

        CollectionAssert.AreEqual(
            new[] { "replacement" },
            udpClient.SentPackets.Select(packet => packet.Hostname).ToArray(),
            "A watch yields complete lists, so the latest one replaces the previous view.");
    }

    [TestMethod]
    public async Task ProviderWithoutAWatch_KeepsPolling()
    {
        var discovery = new PollOnlyDiscovery();
        var udpClient = new MockUdpClient();
        await using var node = new GossNetNode<TestMessage>(Configuration(discovery), udpClient: udpClient);

        node.Start();
        await node.SendAsync(new TestMessage { Data = "hello" });

        Assert.AreEqual(1, discovery.Polls);
        CollectionAssert.AreEqual(new[] { "polled" }, udpClient.SentPackets.Select(packet => packet.Hostname).ToArray());
    }

    /// <summary>
    /// A watch is an optimization. Losing it must degrade to polling rather than take the
    /// node down or leave it gossiping to a frozen membership.
    /// </summary>
    [TestMethod]
    public async Task FaultingWatch_FallsBackToPolling()
    {
        var discovery = new WatchingDiscovery { FaultAfterDrain = new InvalidOperationException("watch broke") };
        var udpClient = new MockUdpClient();
        var logger = new MockLogger<GossNetNode<TestMessage>>();

        await using var node = new GossNetNode<TestMessage>(Configuration(discovery), logger, udpClient);

        node.Start();

        discovery.Push(Watched);
        await WaitForAsync(() => false, 100);

        discovery.EndWatch();

        Assert.IsTrue(
            await WaitForAsync(() => logger.LogEntries.Any(entry => entry.Contains("watch failed", StringComparison.OrdinalIgnoreCase))),
            "The failure should have been reported.");

        await node.SendAsync(new TestMessage { Data = "hello" });

        Assert.AreEqual(1, discovery.Polls, "The node should have returned to asking the provider.");
        CollectionAssert.AreEqual(new[] { "polled" }, udpClient.SentPackets.Select(packet => packet.Hostname).ToArray());
    }

    [TestMethod]
    public async Task StopAsync_DiscardsWatchedMembership()
    {
        var discovery = new WatchingDiscovery();
        var udpClient = new MockUdpClient();
        await using var node = new GossNetNode<TestMessage>(Configuration(discovery), udpClient: udpClient);

        node.Start();
        discovery.Push(Watched);
        await WaitForAsync(() => false, 100);

        await node.StopAsync();

        await node.SendAsync(new TestMessage { Data = "hello" });

        Assert.AreEqual(1, discovery.Polls, "A stopped node must not keep using membership the watch left behind.");
    }

    [TestMethod]
    public async Task StopAsync_EndsTheWatch()
    {
        var discovery = new WatchingDiscovery();
        await using var node = new GossNetNode<TestMessage>(Configuration(discovery), udpClient: new MockUdpClient());

        node.Start();
        Assert.IsTrue(await WaitForAsync(() => discovery.WatchSubscriptions == 1));

        // Completes without hanging on the still-open watch channel.
        await node.StopAsync();
    }

    [TestMethod]
    public async Task Restart_Resubscribes()
    {
        var discovery = new WatchingDiscovery();
        await using var node = new GossNetNode<TestMessage>(Configuration(discovery), udpClient: new MockUdpClient());

        node.Start();
        Assert.IsTrue(await WaitForAsync(() => discovery.WatchSubscriptions == 1));

        await node.StopAsync();
        node.Start();

        Assert.IsTrue(await WaitForAsync(() => discovery.WatchSubscriptions == 2));
    }

    /// <summary>
    /// A watch whose first subscription yields once then breaks; later subscriptions work.
    /// Mimics a backend surfacing a dropped connection by ending the stream.
    /// </summary>
    private sealed class FlakyWatchingDiscovery : IWatchableNodeDiscovery
    {
        private readonly Channel<IReadOnlyList<GossNetNodeHostEntry>> _recovered =
            Channel.CreateUnbounded<IReadOnlyList<GossNetNodeHostEntry>>();

        private int _subscriptions;

        public int WatchSubscriptions => Volatile.Read(ref _subscriptions);

        public void PushAfterRecovery(params GossNetNodeHostEntry[] neighbours) => _recovered.Writer.TryWrite(neighbours);

        public ValueTask<IReadOnlyList<GossNetNodeHostEntry>> GetNeighboursAsync(CancellationToken cancellationToken = default) =>
            new((IReadOnlyList<GossNetNodeHostEntry>)[Polled]);

        public async IAsyncEnumerable<IReadOnlyList<GossNetNodeHostEntry>> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _subscriptions) == 1)
            {
                yield return [Watched];

                throw new InvalidOperationException("connection dropped");
            }

            await foreach (var update in _recovered.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return update;
            }
        }
    }

    /// <summary>
    /// A single dropped connection used to downgrade the node to cached polling for the
    /// rest of its life; the watch must be re-established instead.
    /// </summary>
    [TestMethod]
    public async Task BrokenWatch_IsReestablishedWithBackoff()
    {
        var discovery = new FlakyWatchingDiscovery();
        var udpClient = new MockUdpClient();
        await using var node = new GossNetNode<TestMessage>(Configuration(discovery), udpClient: udpClient);

        node.Start();

        // The retry backoff starts at one second, so allow a few.
        Assert.IsTrue(await WaitForAsync(() => discovery.WatchSubscriptions >= 2, 5000),
            "the node must re-subscribe after the watch breaks");

        discovery.PushAfterRecovery(new GossNetNodeHostEntry { Hostname = "recovered", Port = 9104 });
        await WaitForAsync(() => false, 200);

        await node.SendAsync(new TestMessage { Data = "hello" });

        CollectionAssert.AreEqual(
            new[] { "recovered" },
            udpClient.SentPackets.Select(packet => packet.Hostname).ToArray(),
            "membership pushed by the re-established watch must be used");
    }

    /// <summary>A watch that ends cleanly without yielding: the provider saying "cannot watch".</summary>
    private sealed class WatchlessDiscovery : IWatchableNodeDiscovery
    {
        private int _subscriptions;

        public int WatchSubscriptions => Volatile.Read(ref _subscriptions);

        public ValueTask<IReadOnlyList<GossNetNodeHostEntry>> GetNeighboursAsync(CancellationToken cancellationToken = default) =>
            new((IReadOnlyList<GossNetNodeHostEntry>)[Polled]);

#pragma warning disable CS1998 // The empty sequence has nothing to await.
        public async IAsyncEnumerable<IReadOnlyList<GossNetNodeHostEntry>> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _subscriptions);
            yield break;
        }
#pragma warning restore CS1998
    }

    [TestMethod]
    public async Task WatchThatNeverYields_IsNotRetried()
    {
        var discovery = new WatchlessDiscovery();
        var udpClient = new MockUdpClient();
        await using var node = new GossNetNode<TestMessage>(Configuration(discovery), udpClient: udpClient);

        node.Start();

        Assert.IsTrue(await WaitForAsync(() => discovery.WatchSubscriptions == 1));

        // Outlast the first retry window: a clean zero-yield completion means the
        // backend cannot watch, and hammering it with resubscriptions would spin.
        await WaitForAsync(() => false, 1500);

        Assert.AreEqual(1, discovery.WatchSubscriptions, "an unsupported watch must not be retried");

        await node.SendAsync(new TestMessage { Data = "hello" });
        CollectionAssert.AreEqual(new[] { "polled" }, udpClient.SentPackets.Select(packet => packet.Hostname).ToArray());
    }
}
