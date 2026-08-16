using System.Diagnostics;
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
