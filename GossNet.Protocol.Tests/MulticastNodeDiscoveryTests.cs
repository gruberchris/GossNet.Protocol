using System.Text;
using System.Threading.Channels;

namespace GossNet.Protocol.Tests;

[TestClass]
public sealed class MulticastNodeDiscoveryTests
{
    /// <summary>An in-memory multicast group.</summary>
    private sealed class FakeChannel : IMulticastChannel
    {
        private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
        private readonly List<byte[]> _sent = [];

        private int _receiveAttempts;

        public bool IsDisposed { get; private set; }

        public int ReceiveAttempts => Volatile.Read(ref _receiveAttempts);

        /// <summary>When set, the next receive throws instead of returning.</summary>
        public Func<Exception>? ReceiveFault { get; set; }

        public IReadOnlyList<string> Sent
        {
            get
            {
                lock (_sent)
                {
                    return [.. _sent.Select(Encoding.UTF8.GetString)];
                }
            }
        }

        /// <summary>Raw sent datagrams, for asserting binary frames a string would mangle.</summary>
        public IReadOnlyList<byte[]> SentRaw
        {
            get
            {
                lock (_sent)
                {
                    return [.. _sent];
                }
            }
        }

        public void Deliver(string payload) => _incoming.Writer.TryWrite(Encoding.UTF8.GetBytes(payload));

        public void Deliver(byte[] payload) => _incoming.Writer.TryWrite(payload);

        public ValueTask SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken)
        {
            lock (_sent)
            {
                _sent.Add(datagram.ToArray());
            }

            return default;
        }

        public async ValueTask<byte[]> ReceiveAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _receiveAttempts);

            if (ReceiveFault is not null)
            {
                var fault = ReceiveFault();
                ReceiveFault = null;
                throw fault;
            }

            return await _incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }

        public void Dispose()
        {
            IsDisposed = true;
            _incoming.Writer.TryComplete();
        }
    }

    private static GossNetConfiguration Configuration(string hostname = "10.0.0.1", int port = 9055) => new()
    {
        Hostname = hostname,
        Port = port
    };

    private static MulticastDiscoveryOptions Options(TimeSpan? announce = null, TimeSpan? timeout = null) => new()
    {
        AnnounceInterval = announce ?? TimeSpan.FromMilliseconds(20),
        PeerTimeout = timeout ?? TimeSpan.FromSeconds(30)
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
    public void Encode_ProducesTheWireFormat()
    {
        Assert.AreEqual("gossnet/1 10.0.0.4 9055", MulticastNodeDiscovery.Encode("10.0.0.4", 9055));
    }

    [TestMethod]
    public void Decode_RoundTripsAnAnnouncement()
    {
        var payload = Encoding.UTF8.GetBytes(MulticastNodeDiscovery.Encode("10.0.0.4", 9055));

        Assert.IsTrue(MulticastNodeDiscovery.TryDecode(payload, out var entry));
        Assert.AreEqual("10.0.0.4:9055", entry.ToString());
    }

    /// <summary>A multicast group is shared space and carries traffic that is not ours.</summary>
    [TestMethod]
    [DataRow("")]
    [DataRow("garbage")]
    [DataRow("gossnet/2 10.0.0.4 9055")]
    [DataRow("gossnet/1 10.0.0.4")]
    [DataRow("gossnet/1 10.0.0.4 notaport")]
    [DataRow("gossnet/1 10.0.0.4 0")]
    [DataRow("gossnet/1 10.0.0.4 70000")]
    [DataRow("gossnet/1  9055")]
    public void Decode_RejectsAnythingMalformed(string payload)
    {
        Assert.IsFalse(MulticastNodeDiscovery.TryDecode(Encoding.UTF8.GetBytes(payload), out _));
    }

    [TestMethod]
    public void Decode_RejectsOversizedDatagrams()
    {
        Assert.IsFalse(MulticastNodeDiscovery.TryDecode(new byte[1024], out _));
    }

    [TestMethod]
    public async Task Announce_AdvertisesTheConfiguredAddress()
    {
        var channel = new FakeChannel();
        using var discovery = new MulticastNodeDiscovery(Configuration("10.0.0.4", 9055), Options(), channel: channel);

        Assert.IsTrue(await WaitForAsync(() => channel.Sent.Count > 0), "The node never announced itself.");
        Assert.AreEqual("gossnet/1 10.0.0.4 9055", channel.Sent[0]);
    }

    [TestMethod]
    public async Task Announce_Repeats()
    {
        var channel = new FakeChannel();
        using var discovery = new MulticastNodeDiscovery(Configuration(), Options(announce: TimeSpan.FromMilliseconds(20)), channel: channel);

        Assert.IsTrue(await WaitForAsync(() => channel.Sent.Count >= 3), "Announcements should repeat on an interval.");
    }

    [TestMethod]
    public async Task Receive_LearnsAPeer()
    {
        var channel = new FakeChannel();
        using var discovery = new MulticastNodeDiscovery(Configuration(), Options(), channel: channel);

        channel.Deliver(MulticastNodeDiscovery.Encode("10.0.0.9", 9055));

        Assert.IsTrue(await WaitForAsync(() => discovery.KnownPeerCount == 1));

        var neighbours = await discovery.GetNeighboursAsync();

        CollectionAssert.AreEqual(new[] { "10.0.0.9:9055" }, neighbours.Select(n => n.ToString()).ToArray());
    }

    /// <summary>Loopback is on by default, so a node sees its own announcements.</summary>
    [TestMethod]
    public async Task Receive_IgnoresItsOwnAnnouncement()
    {
        var channel = new FakeChannel();
        using var discovery = new MulticastNodeDiscovery(Configuration("10.0.0.1", 9055), Options(), channel: channel);

        channel.Deliver(MulticastNodeDiscovery.Encode("10.0.0.1", 9055));
        channel.Deliver(MulticastNodeDiscovery.Encode("10.0.0.2", 9055));

        Assert.IsTrue(await WaitForAsync(() => discovery.KnownPeerCount == 1));

        var neighbours = await discovery.GetNeighboursAsync();

        CollectionAssert.AreEqual(new[] { "10.0.0.2:9055" }, neighbours.Select(n => n.ToString()).ToArray());
    }

    [TestMethod]
    public async Task Receive_MalformedDatagramIsNotFatal()
    {
        var channel = new FakeChannel();
        using var discovery = new MulticastNodeDiscovery(Configuration(), Options(), channel: channel);

        channel.Deliver("this is not an announcement");
        channel.Deliver(MulticastNodeDiscovery.Encode("10.0.0.9", 9055));

        Assert.IsTrue(
            await WaitForAsync(() => discovery.KnownPeerCount == 1),
            "The loop should have skipped the junk and kept processing.");
    }

    [TestMethod]
    public async Task Receive_SurvivesATransientFailure()
    {
        var channel = new FakeChannel { ReceiveFault = () => new SocketFault() };
        using var discovery = new MulticastNodeDiscovery(Configuration(), Options(), channel: channel);

        Assert.IsTrue(await WaitForAsync(() => channel.ReceiveAttempts >= 2, 4000), "The loop stopped after one failure.");

        channel.Deliver(MulticastNodeDiscovery.Encode("10.0.0.9", 9055));

        Assert.IsTrue(
            await WaitForAsync(() => discovery.KnownPeerCount == 1, 4000),
            "The loop should have recovered and kept learning peers.");
    }

    [TestMethod]
    public async Task Peers_AgeOutAfterTheTimeout()
    {
        var channel = new FakeChannel();

        using var discovery = new MulticastNodeDiscovery(
            Configuration(),
            Options(timeout: TimeSpan.FromMilliseconds(100)),
            channel: channel);

        channel.Deliver(MulticastNodeDiscovery.Encode("10.0.0.9", 9055));

        Assert.IsTrue(await WaitForAsync(() => discovery.KnownPeerCount == 1));
        Assert.AreEqual(1, (await discovery.GetNeighboursAsync()).Count);

        await Task.Delay(250);

        Assert.AreEqual(0, (await discovery.GetNeighboursAsync()).Count, "The peer should have expired.");
        Assert.AreEqual(0, discovery.KnownPeerCount, "An expired peer should be dropped, not merely hidden.");
    }

    [TestMethod]
    public async Task RepeatedAnnouncements_KeepAPeerAlive()
    {
        var channel = new FakeChannel();

        using var discovery = new MulticastNodeDiscovery(
            Configuration(),
            Options(timeout: TimeSpan.FromMilliseconds(200)),
            channel: channel);

        for (var i = 0; i < 5; i++)
        {
            channel.Deliver(MulticastNodeDiscovery.Encode("10.0.0.9", 9055));
            await Task.Delay(60);
        }

        Assert.AreEqual(1, (await discovery.GetNeighboursAsync()).Count);
    }

    /// <summary>
    /// An injected channel may be shared, so the provider must not close it. The owned case
    /// cannot be asserted with a fake — owning one means it constructed a real socket.
    /// </summary>
    [TestMethod]
    public void Dispose_LeavesAnInjectedChannelAlone()
    {
        var channel = new FakeChannel();
        var discovery = new MulticastNodeDiscovery(Configuration(), Options(), channel: channel);

        discovery.Dispose();

        Assert.IsFalse(channel.IsDisposed);
    }

    [TestMethod]
    public async Task Dispose_StopsAnnouncing()
    {
        var channel = new FakeChannel();
        var discovery = new MulticastNodeDiscovery(Configuration(), Options(announce: TimeSpan.FromMilliseconds(20)), channel: channel);

        Assert.IsTrue(await WaitForAsync(() => channel.Sent.Count > 0));

        discovery.Dispose();

        var after = channel.Sent.Count;
        await Task.Delay(150);

        Assert.AreEqual(after, channel.Sent.Count, "Announcements continued after disposal.");
    }

    [TestMethod]
    public void Dispose_IsIdempotent()
    {
        var discovery = new MulticastNodeDiscovery(Configuration(), Options(), channel: new FakeChannel());

        discovery.Dispose();
        discovery.Dispose();
    }

    [TestMethod]
    public async Task Cancellation_Propagates()
    {
        using var discovery = new MulticastNodeDiscovery(Configuration(), Options(), channel: new FakeChannel());
        using var cts = new CancellationTokenSource();

        await cts.CancelAsync();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            async () => await discovery.GetNeighboursAsync(cts.Token));
    }

    // ---------------------------------------------------------------------------
    // Authentication.
    // ---------------------------------------------------------------------------

    private static readonly byte[] ClusterKey = Encoding.UTF8.GetBytes("cluster-shared-key-0123456789");

    private static GossNetConfiguration AuthenticatedConfiguration(string hostname = "10.0.0.1", int port = 9055) => new()
    {
        Hostname = hostname,
        Port = port,
        DatagramProtector = new HmacDatagramProtector(ClusterKey)
    };

    [TestMethod]
    public async Task AuthenticatedAnnouncements_AreProtectedOnTheWire()
    {
        var channel = new FakeChannel();

        using var discovery = new MulticastNodeDiscovery(
            AuthenticatedConfiguration(),
            Options(announce: TimeSpan.FromMilliseconds(20)),
            channel: channel);

        Assert.IsTrue(await WaitForAsync(() => channel.SentRaw.Count > 0));

        var verifier = new HmacDatagramProtector(ClusterKey);

        Assert.IsTrue(verifier.TryUnprotect(channel.SentRaw[0], out var payload));
        Assert.AreEqual(MulticastNodeDiscovery.Encode("10.0.0.1", 9055), Encoding.UTF8.GetString(payload));
    }

    [TestMethod]
    public async Task AuthenticatedAnnouncement_FromAPeer_IsAccepted()
    {
        var channel = new FakeChannel();

        using var discovery = new MulticastNodeDiscovery(AuthenticatedConfiguration(), Options(), channel: channel);

        var peer = new HmacDatagramProtector(ClusterKey);
        channel.Deliver(peer.Protect(Encoding.UTF8.GetBytes(MulticastNodeDiscovery.Encode("10.0.0.9", 9055))));

        Assert.IsTrue(await WaitForAsync(() => discovery.KnownPeerCount == 1));
        Assert.AreEqual("10.0.0.9:9055", (await discovery.GetNeighboursAsync())[0].ToString());
    }

    [TestMethod]
    public async Task UnauthenticatedAnnouncements_AreRejectedWhenAKeyIsSet()
    {
        var channel = new FakeChannel();

        using var discovery = new MulticastNodeDiscovery(AuthenticatedConfiguration(), Options(), channel: channel);

        // A plaintext announcement, and one signed with the wrong key: either would
        // let anything on the LAN insert a fake peer.
        channel.Deliver(MulticastNodeDiscovery.Encode("10.6.6.6", 9055));

        var attacker = new HmacDatagramProtector(Encoding.UTF8.GetBytes("attacker-key-9876543210abcdef"));
        channel.Deliver(attacker.Protect(Encoding.UTF8.GetBytes(MulticastNodeDiscovery.Encode("10.6.6.7", 9055))));

        // A legitimate one behind them, proving the forgeries were dropped in place.
        var peer = new HmacDatagramProtector(ClusterKey);
        channel.Deliver(peer.Protect(Encoding.UTF8.GetBytes(MulticastNodeDiscovery.Encode("10.0.0.9", 9055))));

        Assert.IsTrue(await WaitForAsync(() => discovery.KnownPeerCount > 0));

        var neighbours = await discovery.GetNeighboursAsync();

        Assert.AreEqual(1, neighbours.Count, "only the authenticated peer may be learned");
        Assert.AreEqual("10.0.0.9:9055", neighbours[0].ToString());
    }

    private sealed class SocketFault : Exception;
}
