using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GossNet.Protocol.Tests.Mocks;

namespace GossNet.Protocol.Tests;

[TestClass]
public class GossNetNodeTests
{
    private static readonly GossNetNodeHostEntry NeighbourA = new() { Hostname = "neighbour-a", Port = 9101 };
    private static readonly GossNetNodeHostEntry NeighbourB = new() { Hostname = "neighbour-b", Port = 9102 };

    private GossNetConfiguration _configuration = null!;
    private MockUdpClient _udpClient = null!;
    private MockLogger<GossNetNode<TestMessage>> _logger = null!;
    private GossNetNode<TestMessage> _node = null!;

    [TestInitialize]
    public void Setup()
    {
        _configuration = NewConfiguration();
        _udpClient = new MockUdpClient();
        _logger = new MockLogger<GossNetNode<TestMessage>>();

        // The logger is actually passed in now; it used to be constructed and dropped.
        _node = new GossNetNode<TestMessage>(_configuration, _logger, _udpClient);
    }

    [TestCleanup]
    public void Cleanup() => _node.Dispose();

    private static GossNetConfiguration NewConfiguration() => new()
    {
        Hostname = "self",
        Port = 9100,
        // StaticList keeps neighbour resolution deterministic and off the network.
        NodeDiscovery = NodeDiscovery.StaticList,
        StaticNodes = [NeighbourA, NeighbourB]
    };

    private static byte[] Datagram(TestMessage message) => Encoding.UTF8.GetBytes(message.Serialize());

    /// <summary>Waits for a condition, failing fast rather than sleeping a fixed interval.</summary>
    private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var sw = Stopwatch.StartNew();

        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return condition();
    }

    // ---------------------------------------------------------------------------
    // Fan-out: every subscriber must receive every message.
    // ---------------------------------------------------------------------------

    [TestMethod]
    public async Task Subscribe_DeliversEveryMessageToEverySubscriber()
    {
        using var first = _node.Subscribe();
        using var second = _node.Subscribe();
        using var third = _node.Subscribe();

        _node.Start();
        _udpClient.EnqueueReceive(Datagram(new TestMessage { Data = "broadcast" }));

        // Previously all subscribers shared one reader, so exactly one of these
        // would have received the message and the others would hang.
        foreach (var subscription in new[] { first, second, third })
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var received = await subscription.Reader.ReadAsync(cts.Token);

            Assert.AreEqual("broadcast", received.Message.Data);
        }
    }

    [TestMethod]
    public async Task Subscribe_EachSubscriberReceivesAllOfManyMessages()
    {
        const int messageCount = 25;

        using var first = _node.Subscribe();
        using var second = _node.Subscribe();

        _node.Start();

        for (var i = 0; i < messageCount; i++)
        {
            _udpClient.EnqueueReceive(Datagram(new TestMessage { Data = $"message-{i}" }));
        }

        foreach (var subscription in new[] { first, second })
        {
            for (var i = 0; i < messageCount; i++)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var received = await subscription.Reader.ReadAsync(cts.Token);

                Assert.AreEqual($"message-{i}", received.Message.Data, "messages must arrive in order and none may be lost");
            }
        }
    }

    [TestMethod]
    public async Task DisposingSubscription_StopsDeliveryAndCompletesReader()
    {
        var subscription = _node.Subscribe();
        using var survivor = _node.Subscribe();

        _node.Start();
        subscription.Dispose();

        _udpClient.EnqueueReceive(Datagram(new TestMessage { Data = "after-unsubscribe" }));

        // The survivor still receives, proving the message really was processed.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = await survivor.Reader.ReadAsync(cts.Token);
        Assert.AreEqual("after-unsubscribe", received.Message.Data);

        // The disposed subscription is completed and empty. Unsubscribing used to
        // remove the reader from a list that dispatch never consulted, so the caller
        // kept receiving messages.
        Assert.IsFalse(subscription.Reader.TryRead(out _), "a disposed subscription must not receive messages");
        Assert.IsTrue(subscription.Reader.Completion.IsCompleted, "a disposed subscription's reader must complete");
    }

    [TestMethod]
    public async Task NoSubscribers_DoesNotBufferAnyMessages()
    {
        _node.Start();

        // Processed with nobody listening. These used to accumulate forever in a
        // shared unbounded channel, growing until the process ran out of memory.
        for (var i = 0; i < 50; i++)
        {
            _udpClient.EnqueueReceive(Datagram(new TestMessage { Data = $"unheard-{i}" }));
        }

        Assert.IsTrue(await WaitForAsync(() => _udpClient.SentPackets.Count >= 50 * 2),
            "all messages should have been processed and forwarded");

        // A subscriber joining afterwards must not inherit a backlog.
        using var late = _node.Subscribe();
        Assert.IsFalse(late.Reader.TryRead(out _), "a new subscriber must not receive messages buffered before it existed");
    }

    [TestMethod]
    public async Task SlowSubscriber_DropsOldestInsteadOfGrowing()
    {
        var configuration = new GossNetConfiguration
        {
            Hostname = "self",
            Port = 9100,
            NodeDiscovery = NodeDiscovery.StaticList,
            StaticNodes = [NeighbourA],
            SubscriberQueueCapacity = 4
        };

        var udpClient = new MockUdpClient();
        using var node = new GossNetNode<TestMessage>(configuration, _logger, udpClient);
        using var subscription = node.Subscribe();

        node.Start();

        for (var i = 0; i < 40; i++)
        {
            udpClient.EnqueueReceive(Datagram(new TestMessage { Data = $"flood-{i}" }));
        }

        Assert.IsTrue(await WaitForAsync(() => udpClient.SentPackets.Count >= 40),
            "the node must keep processing even though the subscriber never reads");

        var buffered = 0;
        while (subscription.Reader.TryRead(out _))
        {
            buffered++;
        }

        Assert.IsTrue(buffered <= 4, $"queue must stay within its capacity of 4 but held {buffered}");
    }

    // ---------------------------------------------------------------------------
    // Lifecycle.
    // ---------------------------------------------------------------------------

    [TestMethod]
    public async Task StopAsync_ReturnsPromptly_WhenNoDatagramEverArrives()
    {
        _node.Start();

        // Give the loop time to park inside ReceiveAsync.
        await Task.Delay(100);

        var sw = Stopwatch.StartNew();
        await _node.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        sw.Stop();

        // ReceiveAsync took no cancellation token before, so the loop stayed parked
        // until a datagram happened to arrive and StopAsync waited forever.
        Assert.IsTrue(sw.ElapsedMilliseconds < 2000, $"StopAsync took {sw.ElapsedMilliseconds}ms; it must not wait for a datagram");
    }

    [TestMethod]
    public async Task StopAsync_IsIdempotent()
    {
        _node.Start();

        await _node.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await _node.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task StopAsync_OnNodeThatWasNeverStarted_DoesNothing()
    {
        await _node.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task Node_CanBeRestartedAfterStopping()
    {
        using var subscription = _node.Subscribe();

        _node.Start();
        await _node.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

        // Stopping used to complete the shared channel permanently, so any message
        // processed after a restart threw on write.
        _node.Start();
        _udpClient.EnqueueReceive(Datagram(new TestMessage { Data = "after-restart" }));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = await subscription.Reader.ReadAsync(cts.Token);

        Assert.AreEqual("after-restart", received.Message.Data);
    }

    [TestMethod]
    public void Start_CalledTwice_DoesNotStartASecondLoop()
    {
        _node.Start();
        _node.Start();

        Assert.IsTrue(_logger.LogEntries.Any(entry => entry.Contains("already running")),
            "the second Start must be ignored rather than orphaning the first loop");
    }

    [TestMethod]
    public void Dispose_IsIdempotent()
    {
        _node.Dispose();
        _node.Dispose();
    }

    [TestMethod]
    public void Dispose_DisposesTheTransport()
    {
        _node.Dispose();

        Assert.IsTrue(_udpClient.IsDisposed);
    }

    [TestMethod]
    public async Task DisposeAsync_StopsTheNodeAndCompletesSubscriptions()
    {
        var subscription = _node.Subscribe();
        _node.Start();

        await _node.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(_udpClient.IsDisposed);
        Assert.IsTrue(subscription.Reader.Completion.IsCompleted, "disposal must complete subscriber readers so consumers can exit");
    }

    [TestMethod]
    public void Subscribe_AfterDispose_Throws()
    {
        _node.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => _node.Subscribe());
    }

    [TestMethod]
    public async Task SendAsync_AfterDispose_Throws()
    {
        _node.Dispose();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            async () => await _node.SendAsync(new TestMessage { Data = "x" }));
    }

    // ---------------------------------------------------------------------------
    // Error handling.
    // ---------------------------------------------------------------------------

    [TestMethod]
    public async Task FailingReceive_BacksOffInsteadOfSpinning()
    {
        _udpClient.ReceiveFault = () => new InvalidOperationException("socket boom");

        _node.Start();
        await Task.Delay(500);

        var attempts = _udpClient.ReceiveAttempts;

        // A persistently failing socket used to be retried with no delay at all,
        // producing a 100%-CPU loop that flooded the log. With backoff starting at
        // 50ms and doubling, 500ms allows only a handful of attempts.
        Assert.IsTrue(attempts is > 0 and < 20, $"expected a small number of backed-off retries, saw {attempts}");
    }

    [TestMethod]
    public async Task MalformedDatagram_DoesNotStopTheLoop()
    {
        using var subscription = _node.Subscribe();

        _node.Start();
        _udpClient.EnqueueReceive("this is not json"u8.ToArray());
        _udpClient.EnqueueReceive(Datagram(new TestMessage { Data = "still-working" }));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = await subscription.Reader.ReadAsync(cts.Token);

        Assert.AreEqual("still-working", received.Message.Data);
    }

    [TestMethod]
    public async Task MalformedDatagrams_DoNotTriggerTheFailureBackoff()
    {
        using var subscription = _node.Subscribe();

        _node.Start();

        // A stream of junk used to count as consecutive loop failures, so ten of
        // these accumulated over twenty seconds of exponential backoff before the
        // real message behind them was even received — a trivially cheap way for
        // stray traffic to stall a node.
        for (var i = 0; i < 10; i++)
        {
            _udpClient.EnqueueReceive("junk"u8.ToArray());
        }

        _udpClient.EnqueueReceive(Datagram(new TestMessage { Data = "behind-the-junk" }));

        var sw = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = await subscription.Reader.ReadAsync(cts.Token);
        sw.Stop();

        Assert.AreEqual("behind-the-junk", received.Message.Data);
        Assert.IsTrue(sw.ElapsedMilliseconds < 3000,
            $"junk datagrams delayed a real message by {sw.ElapsedMilliseconds}ms; they must be dropped without backoff");
    }

    [TestMethod]
    public async Task DiscoveryFailure_WhileForwarding_DoesNotStallTheReceiveLoop()
    {
        var configuration = new GossNetConfiguration
        {
            Hostname = "self",
            Port = 9100,
            DiscoveryProvider = new ThrowingDiscovery()
        };

        var udpClient = new MockUdpClient();
        using var node = new GossNetNode<TestMessage>(configuration, _logger, udpClient);
        using var subscription = node.Subscribe();

        node.Start();
        udpClient.EnqueueReceive(Datagram(new TestMessage { Data = "first" }));
        udpClient.EnqueueReceive(Datagram(new TestMessage { Data = "second" }));

        // Both messages must reach subscribers promptly even though every forward
        // fails: a discovery outage is a fan-out problem, not a receive problem.
        foreach (var expected in new[] { "first", "second" })
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var received = await subscription.Reader.ReadAsync(cts.Token);

            Assert.AreEqual(expected, received.Message.Data);
        }
    }

    private sealed class ThrowingDiscovery : INodeDiscovery
    {
        public ValueTask<IReadOnlyList<GossNetNodeHostEntry>> GetNeighboursAsync(CancellationToken cancellationToken = default) =>
            throw new NodeDiscoveryException("backend unreachable");
    }

    // ---------------------------------------------------------------------------
    // Authentication.
    // ---------------------------------------------------------------------------

    private static readonly byte[] ClusterKey = Encoding.UTF8.GetBytes("cluster-shared-key-0123456789");

    private static GossNetNode<TestMessage> AuthenticatedNode(MockUdpClient udpClient, TimeSpan? maxAge = null)
    {
        var configuration = new GossNetConfiguration
        {
            Hostname = "self",
            Port = 9100,
            NodeDiscovery = NodeDiscovery.StaticList,
            StaticNodes = [NeighbourA],
            DatagramProtector = new HmacDatagramProtector(ClusterKey),
            MessageMaxAge = maxAge
        };

        return new GossNetNode<TestMessage>(configuration, udpClient: udpClient);
    }

    [TestMethod]
    public async Task AuthenticatedNodes_ExchangeMessages()
    {
        var udpClient = new MockUdpClient();
        await using var node = AuthenticatedNode(udpClient);
        using var subscription = node.Subscribe();

        node.Start();

        // What one authenticated node sends, another accepts: feed a sent datagram
        // straight back in as if a peer had produced it.
        await node.SendAsync(new TestMessage { Data = "outbound" });
        Assert.AreEqual(1, udpClient.SentPackets.Count);

        var peerProtector = new HmacDatagramProtector(ClusterKey);
        var inbound = peerProtector.Protect(Datagram(new TestMessage { Data = "inbound" }));
        udpClient.EnqueueReceive(inbound);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = await subscription.Reader.ReadAsync(cts.Token);

        Assert.AreEqual("inbound", received.Message.Data);
    }

    [TestMethod]
    public async Task AuthenticatedNode_DropsPlaintextAndForgedDatagrams()
    {
        var udpClient = new MockUdpClient();
        await using var node = AuthenticatedNode(udpClient);
        using var subscription = node.Subscribe();

        node.Start();

        // A plaintext message, and one signed with the wrong key.
        udpClient.EnqueueReceive(Datagram(new TestMessage { Data = "plaintext-injection" }));

        var attacker = new HmacDatagramProtector(Encoding.UTF8.GetBytes("attacker-key-9876543210abcdef"));
        udpClient.EnqueueReceive(attacker.Protect(Datagram(new TestMessage { Data = "forged" })));

        // Followed by a legitimate one, proving the junk was dropped without stalling.
        var peer = new HmacDatagramProtector(ClusterKey);
        udpClient.EnqueueReceive(peer.Protect(Datagram(new TestMessage { Data = "legitimate" })));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = await subscription.Reader.ReadAsync(cts.Token);

        Assert.AreEqual("legitimate", received.Message.Data, "only the authenticated message may be delivered");
        Assert.IsFalse(subscription.Reader.TryRead(out _), "the plaintext and forged messages must not be delivered");
    }

    [TestMethod]
    public async Task AuthenticatedSend_TransmitsProtectedDatagrams()
    {
        var udpClient = new MockUdpClient();
        await using var node = AuthenticatedNode(udpClient);

        await node.SendAsync(new TestMessage { Data = "wire-check" });

        var verifier = new HmacDatagramProtector(ClusterKey);

        Assert.AreEqual(1, udpClient.SentPackets.Count);
        Assert.IsTrue(verifier.TryUnprotect(udpClient.SentPackets[0].Datagram, out _),
            "outgoing datagrams must carry a valid frame");
    }

    [TestMethod]
    public async Task StaleMessage_IsDroppedWhenAuthenticated()
    {
        var udpClient = new MockUdpClient();
        await using var node = AuthenticatedNode(udpClient, maxAge: TimeSpan.FromMinutes(5));
        using var subscription = node.Subscribe();

        node.Start();

        var peer = new HmacDatagramProtector(ClusterKey);

        // A captured datagram replayed after its id has left the dedup cache would
        // otherwise be accepted as new; the freshness window closes that gap.
        var stale = new TestMessage { Data = "replayed" };
        stale.Timestamp = DateTime.UtcNow.AddMinutes(-10);
        udpClient.EnqueueReceive(peer.Protect(Datagram(stale)));

        udpClient.EnqueueReceive(peer.Protect(Datagram(new TestMessage { Data = "fresh" })));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = await subscription.Reader.ReadAsync(cts.Token);

        Assert.AreEqual("fresh", received.Message.Data, "the stale message must be dropped");
        Assert.IsFalse(subscription.Reader.TryRead(out _));
    }

    [TestMethod]
    public async Task OversizedAuthenticatedMessage_ThrowsAClearError()
    {
        var udpClient = new MockUdpClient();
        await using var node = AuthenticatedNode(udpClient);

        // The size check runs against the protected datagram, so the frame's overhead
        // counts toward the limit and the error says so.
        var message = new TestMessage { Data = new string('x', GossNetMessageBase.MaxDatagramBytes + 1) };

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await node.SendAsync(message));

        StringAssert.Contains(exception.Message, "protection overhead");
        Assert.AreEqual(0, udpClient.SentPackets.Count);
    }

    // ---------------------------------------------------------------------------
    // Gossip behaviour.
    // ---------------------------------------------------------------------------

    [TestMethod]
    public async Task SendAsync_MarksSelfAsNotified()
    {
        var message = new TestMessage { Data = "hello" };

        await _node.SendAsync(message);

        Assert.IsTrue(message.NotifiedNodes.Any(n => n.Hostname == _configuration.Hostname && n.Port == _configuration.Port));
    }

    [TestMethod]
    public async Task SendAsync_SendsToEveryNeighbour()
    {
        var sent = await _node.SendAsync(new TestMessage { Data = "hello" });

        Assert.AreEqual(2, sent);

        var destinations = _udpClient.SentPackets.Select(p => $"{p.Hostname}:{p.Port}").OrderBy(x => x).ToArray();
        CollectionAssert.AreEqual(new[] { "neighbour-a:9101", "neighbour-b:9102" }, destinations);
    }

    [TestMethod]
    public async Task SendAsync_SkipsNeighboursAlreadyNotified()
    {
        var message = new TestMessage { Data = "hello" };
        message.NotifiedNodes = [NeighbourA];

        var sent = await _node.SendAsync(message);

        Assert.AreEqual(1, sent);
        Assert.AreEqual(1, _udpClient.SentPackets.Count);
        Assert.AreEqual("neighbour-b", _udpClient.SentPackets[0].Hostname);
    }

    [TestMethod]
    public async Task SendAsync_OversizedMessage_ThrowsAClearError()
    {
        // Bigger than a single IPv4 UDP datagram can carry. Without the guard this
        // fails at the socket with an opaque error that never mentions the message.
        var message = new TestMessage { Data = new string('x', GossNetMessageBase.MaxDatagramBytes + 1) };

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await _node.SendAsync(message));

        StringAssert.Contains(exception.Message, "exceeds the maximum UDP");
        Assert.AreEqual(0, _udpClient.SentPackets.Count, "nothing should be transmitted");
    }

    [TestMethod]
    public async Task SendAsync_MessageJustUnderTheLimit_IsSent()
    {
        // 2 KB of slack for the JSON envelope around the payload.
        var message = new TestMessage { Data = new string('x', GossNetMessageBase.MaxDatagramBytes - 2048) };

        var sent = await _node.SendAsync(message);

        Assert.AreEqual(2, sent);
    }

    [TestMethod]
    public async Task SendAsync_OneUnreachableNeighbour_DoesNotStopTheOthers()
    {
        _udpClient.SendFault = hostname => hostname == "neighbour-a" ? new SocketException() : null;

        var sent = await _node.SendAsync(new TestMessage { Data = "hello" });

        Assert.AreEqual(1, sent);
        Assert.AreEqual(1, _udpClient.SentPackets.Count);
        Assert.AreEqual("neighbour-b", _udpClient.SentPackets[0].Hostname);
    }

    [TestMethod]
    public async Task AddRecipientsToNotifiedNodes_ListsRecipientsInTheTransmittedMessage()
    {
        var configuration = new GossNetConfiguration
        {
            Hostname = "self",
            Port = 9100,
            NodeDiscovery = NodeDiscovery.StaticList,
            StaticNodes = [NeighbourA, NeighbourB],
            AddRecipientsToNotifiedNodes = true
        };

        var udpClient = new MockUdpClient();
        await using var node = new GossNetNode<TestMessage>(configuration, udpClient: udpClient);

        await node.SendAsync(new TestMessage { Data = "hello" });

        Assert.AreEqual(2, udpClient.SentPackets.Count);

        // Every transmitted copy lists both recipients, so neither echoes to the other.
        foreach (var packet in udpClient.SentPackets)
        {
            var onTheWire = new TestMessage();
            onTheWire.Deserialize(Encoding.UTF8.GetString(packet.Datagram));

            var notified = onTheWire.NotifiedNodes.Select(n => n.ToString()).ToArray();

            CollectionAssert.Contains(notified, "neighbour-a:9101");
            CollectionAssert.Contains(notified, "neighbour-b:9102");
            CollectionAssert.Contains(notified, "self:9100");
        }
    }

    [TestMethod]
    public async Task DefaultBehaviour_DoesNotListRecipients()
    {
        await _node.SendAsync(new TestMessage { Data = "hello" });

        var onTheWire = new TestMessage();
        onTheWire.Deserialize(Encoding.UTF8.GetString(_udpClient.SentPackets[0].Datagram));

        // Off by default: the echo redundancy is what delivers through datagram loss.
        CollectionAssert.AreEqual(
            new[] { "self:9100" },
            onTheWire.NotifiedNodes.Select(n => n.ToString()).ToArray());
    }

    [TestMethod]
    public async Task ReceivedMessage_IsForwardedToNeighbours()
    {
        _node.Start();
        _udpClient.EnqueueReceive(Datagram(new TestMessage { Data = "relay" }));

        Assert.IsTrue(await WaitForAsync(() => _udpClient.SentPackets.Count == 2),
            "a received message must be forwarded to both neighbours");
    }

    [TestMethod]
    public async Task DuplicateMessage_IsProcessedOnlyOnce()
    {
        using var subscription = _node.Subscribe();
        _node.Start();

        var message = new TestMessage { Data = "duplicate" };
        var datagram = Datagram(message);

        _udpClient.EnqueueReceive(datagram);
        _udpClient.EnqueueReceive(datagram);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = await subscription.Reader.ReadAsync(cts.Token);
        Assert.AreEqual("duplicate", received.Message.Data);

        // Allow the second copy to be processed and discarded.
        await Task.Delay(200);

        Assert.IsFalse(subscription.Reader.TryRead(out _), "the duplicate must not be delivered a second time");
    }
}

/// <summary>Message type used by the node tests, serialized the way a real consumer would.</summary>
public class TestMessage : GossNetMessageBase
{
    public string Data { get; set; } = string.Empty;

    public override void Deserialize(string data)
    {
        base.Deserialize(data);

        var parsed = JsonSerializer.Deserialize<TestMessage>(data);

        if (parsed is not null)
        {
            Data = parsed.Data;
        }
    }
}
