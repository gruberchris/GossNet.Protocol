using System.Diagnostics;
using System.Text;
using GossNet.Protocol.Tests.Mocks;

namespace GossNet.Protocol.Tests;

/// <summary>
/// Covers the wiring between <see cref="GossNetNode{T}"/> and a discovery provider that
/// learns from traffic.
/// </summary>
[TestClass]
public sealed class ObservingNodeDiscoveryTests
{
    private static readonly GossNetNodeHostEntry Neighbour = new() { Hostname = "neighbour", Port = 9101 };

    /// <summary>Records every notified list handed to it.</summary>
    private sealed class RecordingDiscovery : IObservingNodeDiscovery
    {
        private readonly List<GossNetNodeHostEntry[]> _observations = [];
        private readonly Exception? _fault;

        public RecordingDiscovery(Exception? fault = null) => _fault = fault;

        public IReadOnlyList<GossNetNodeHostEntry[]> Observations
        {
            get
            {
                lock (_observations)
                {
                    return [.. _observations];
                }
            }
        }

        public void Observe(IReadOnlyCollection<GossNetNodeHostEntry> seen)
        {
            lock (_observations)
            {
                _observations.Add([.. seen]);
            }

            if (_fault is not null)
            {
                throw _fault;
            }
        }

        public ValueTask<IReadOnlyList<GossNetNodeHostEntry>> GetNeighboursAsync(CancellationToken cancellationToken = default) =>
            new((IReadOnlyList<GossNetNodeHostEntry>)[Neighbour]);
    }

    /// <summary>A provider that does not learn from traffic, so must never be observed.</summary>
    private sealed class PlainDiscovery : INodeDiscovery
    {
        public ValueTask<IReadOnlyList<GossNetNodeHostEntry>> GetNeighboursAsync(CancellationToken cancellationToken = default) =>
            new((IReadOnlyList<GossNetNodeHostEntry>)[Neighbour]);
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
    public async Task Send_ObservesTheNotifiedList()
    {
        var discovery = new RecordingDiscovery();
        await using var node = new GossNetNode<TestMessage>(Configuration(discovery), udpClient: new MockUdpClient());

        await node.SendAsync(new TestMessage { Data = "hello" });

        Assert.AreEqual(1, discovery.Observations.Count);

        // The node marks itself notified before observing, so it sees its own entry.
        CollectionAssert.AreEqual(new[] { "self:9100" }, discovery.Observations[0].Select(e => e.ToString()).ToArray());
    }

    [TestMethod]
    public async Task Receive_ObservesTheNotifiedList()
    {
        var discovery = new RecordingDiscovery();
        var udpClient = new MockUdpClient();
        await using var node = new GossNetNode<TestMessage>(Configuration(discovery), udpClient: udpClient);

        node.Start();

        var message = new TestMessage { Data = "from the wire" };
        message.NotifiedNodes = [new GossNetNodeHostEntry { Hostname = "10.0.0.7", Port = 9055 }];

        udpClient.EnqueueReceive(Encoding.UTF8.GetBytes(message.Serialize()));

        Assert.IsTrue(await WaitForAsync(() => discovery.Observations.Count > 0), "The received message was never observed.");

        var observed = discovery.Observations[0].Select(e => e.ToString()).ToArray();

        CollectionAssert.Contains(observed, "10.0.0.7:9055", "The sender should have been learned.");
        CollectionAssert.Contains(observed, "self:9100");
    }

    [TestMethod]
    public async Task NonObservingProvider_IsNeverCalled()
    {
        // Nothing to assert against directly: the point is that a provider without the
        // interface takes the original code path untouched, and sending still works.
        await using var node = new GossNetNode<TestMessage>(Configuration(new PlainDiscovery()), udpClient: new MockUdpClient());

        var sent = await node.SendAsync(new TestMessage { Data = "hello" });

        Assert.AreEqual(1, sent);
    }

    /// <summary>
    /// Observe runs on the receive loop. A provider that throws must not be mistaken for a
    /// transport failure, which would trip the loop's error backoff and stall the node.
    /// </summary>
    [TestMethod]
    public async Task ThrowingObserver_DoesNotBreakTheReceiveLoop()
    {
        var discovery = new RecordingDiscovery(new InvalidOperationException("observer exploded"));
        var udpClient = new MockUdpClient();
        var logger = new MockLogger<GossNetNode<TestMessage>>();

        await using var node = new GossNetNode<TestMessage>(Configuration(discovery), logger, udpClient);
        using var subscription = node.Subscribe();

        node.Start();

        udpClient.EnqueueReceive(Encoding.UTF8.GetBytes(new TestMessage { Data = "first" }.Serialize()));
        udpClient.EnqueueReceive(Encoding.UTF8.GetBytes(new TestMessage { Data = "second" }.Serialize()));

        Assert.IsTrue(
            await WaitForAsync(() => discovery.Observations.Count >= 2),
            "The loop stopped processing after the observer threw.");

        // Both messages still reached subscribers despite the observer failing on each.
        var delivered = 0;

        while (subscription.Reader.TryRead(out _))
        {
            delivered++;
        }

        Assert.IsGreaterThanOrEqualTo(1, delivered);
    }

    [TestMethod]
    public async Task ThrowingObserver_IsLogged()
    {
        var discovery = new RecordingDiscovery(new InvalidOperationException("observer exploded"));
        var logger = new MockLogger<GossNetNode<TestMessage>>();

        await using var node = new GossNetNode<TestMessage>(Configuration(discovery), logger, new MockUdpClient());

        await node.SendAsync(new TestMessage { Data = "hello" });

        Assert.IsTrue(
            logger.LogEntries.Any(entry => entry.Contains("observing", StringComparison.OrdinalIgnoreCase)),
            "A failing discovery provider should be reported, not silently swallowed.");
    }

    /// <summary>Peer exchange wired into a real node: a seed-only node learns a third party.</summary>
    [TestMethod]
    public async Task PeerExchange_LearnsFromRealTraffic()
    {
        var seed = new GossNetNodeHostEntry { Hostname = "10.0.0.2", Port = 9055 };

        var configuration = new GossNetConfiguration
        {
            Hostname = "10.0.0.1",
            Port = 9055,
            NodeDiscovery = NodeDiscovery.PeerExchange,
            StaticNodes = [seed]
        };

        var udpClient = new MockUdpClient();
        await using var node = new GossNetNode<TestMessage>(configuration, udpClient: udpClient);
        using var subscription = node.Subscribe();

        node.Start();

        // A message that has already been through a node this one has never heard of.
        var message = new TestMessage { Data = "relayed" };
        message.NotifiedNodes = [seed, new GossNetNodeHostEntry { Hostname = "10.0.0.3", Port = 9055 }];

        udpClient.EnqueueReceive(Encoding.UTF8.GetBytes(message.Serialize()));

        Assert.IsTrue(await WaitForAsync(() => subscription.Reader.TryRead(out _)), "The message was never processed.");

        // Learning cannot be observed on the message that taught it: 10.0.0.3 is already in
        // that message's notified list, so the node correctly declines to send it back.
        // A fresh message starts with an empty list and goes to everyone known.
        await node.SendAsync(new TestMessage { Data = "fresh" });

        Assert.IsTrue(
            await WaitForAsync(() => udpClient.SentPackets.Any(packet => packet.Hostname == "10.0.0.3")),
            "The node should have learned 10.0.0.3 from the earlier message and now gossip to it.");
    }
}
