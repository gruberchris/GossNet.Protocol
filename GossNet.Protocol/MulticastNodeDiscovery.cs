using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GossNet.Protocol;

/// <summary>
/// Discovers neighbours on the local network with no registry at all: each node announces
/// itself to a multicast group and listens for everyone else doing the same.
/// </summary>
/// <remarks>
/// <para>
/// Zero configuration — nodes need nothing but the same group address to find each other,
/// which makes this the least ceremony for development, homelabs and appliances on one LAN.
/// </para>
/// <para>
/// <strong>It does not cross subnets.</strong> The default TTL of 1 keeps announcements on
/// the local link, and most cloud networks drop multicast entirely. Use a registry-backed
/// provider, or <see cref="PeerExchangeNodeDiscovery"/>, anywhere routed.
/// </para>
/// <para>
/// A node announces the <see cref="GossNetConfiguration.Hostname"/> and
/// <see cref="GossNetConfiguration.Port"/> it was configured with, so those must be
/// reachable by the other nodes — the same requirement peer exchange has.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var configuration = new GossNetConfiguration
/// {
///     Hostname = "10.0.0.4",
///     Port = 9055,
///     NodeDiscovery = NodeDiscovery.Multicast
/// };
/// </code>
/// </example>
public sealed class MulticastNodeDiscovery : INodeDiscovery, IDisposable
{
    /// <summary>Prefix identifying an announcement, so unrelated traffic on the group is ignored.</summary>
    private const string Protocol = "gossnet/1";

    private static readonly TimeSpan ReceiveRetryDelay = TimeSpan.FromSeconds(1);

#if NET9_0_OR_GREATER
    private readonly Lock _gate = new();
#else
    private readonly object _gate = new();
#endif

    private readonly GossNetConfiguration _configuration;
    private readonly MulticastDiscoveryOptions _options;
    private readonly IMulticastChannel _channel;
    private readonly ILogger _logger;
    private readonly bool _ownsChannel;
    private readonly byte[] _announcement;

    /// <summary>
    /// The node's protector, applied to announcements too. Without it, anything on the
    /// LAN could forge an announcement and insert a fake peer into the neighbour list.
    /// </summary>
    private readonly IDatagramProtector? _protector;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Dictionary<GossNetNodeHostEntry, long> _lastSeen = [];

    private readonly Task _announceLoop;
    private readonly Task _receiveLoop;

    private int _disposed;

    /// <summary>
    /// Starts announcing and listening.
    /// </summary>
    /// <param name="configuration">The node configuration supplying what to advertise.</param>
    /// <param name="options">Group settings. Defaults are used when omitted.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="channel">Channel to use; a real multicast socket is created when omitted.</param>
    public MulticastNodeDiscovery(
        GossNetConfiguration configuration,
        MulticastDiscoveryOptions? options = null,
        ILogger? logger = null,
        IMulticastChannel? channel = null)
    {
        _configuration = configuration;
        _options = options ?? new MulticastDiscoveryOptions();
        _logger = logger ?? NullLogger.Instance;

        // Only dispose a channel we created; a caller-supplied one may be shared.
        _ownsChannel = channel is null;
        _channel = channel ?? new UdpMulticastChannel(_options);

        _protector = configuration.DatagramProtector;

        var announcement = Encoding.UTF8.GetBytes(Encode(configuration.Hostname, configuration.Port));
        _announcement = _protector is null ? announcement : _protector.Protect(announcement);

        var token = _cancellation.Token;

        _announceLoop = Task.Run(() => AnnounceLoopAsync(token), CancellationToken.None);
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(token), CancellationToken.None);
    }

    /// <summary>Gets the number of peers currently known, before expiry is applied.</summary>
    public int KnownPeerCount
    {
        get
        {
            lock (_gate)
            {
                return _lastSeen.Count;
            }
        }
    }

    /// <summary>Formats an announcement.</summary>
    internal static string Encode(string hostname, int port) =>
        $"{Protocol} {hostname} {port.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Parses an announcement.
    /// </summary>
    /// <param name="datagram">The received payload.</param>
    /// <param name="entry">The announced node, when the payload is valid.</param>
    /// <returns><c>true</c> when the payload is a well-formed announcement.</returns>
    /// <remarks>
    /// Anything else on the group — another application, a truncated datagram, a future
    /// protocol version — is rejected rather than throwing. A multicast group is shared
    /// space and cannot be assumed to carry only our traffic.
    /// </remarks>
    internal static bool TryDecode(byte[] datagram, out GossNetNodeHostEntry entry)
    {
        entry = null!;

        if (datagram is null || datagram.Length == 0 || datagram.Length > 512)
        {
            return false;
        }

        // GetString never throws for invalid sequences — it substitutes U+FFFD, which
        // simply fails the protocol match below.
        var text = Encoding.UTF8.GetString(datagram);

        var parts = text.Split(' ');

        if (parts.Length != 3 ||
            !string.Equals(parts[0], Protocol, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(parts[1]) ||
            !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ||
            port is <= 0 or > 65535)
        {
            return false;
        }

        entry = new GossNetNodeHostEntry { Hostname = parts[1], Port = port };

        return true;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<GossNetNodeHostEntry>> GetNeighboursAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var now = Stopwatch.GetTimestamp();
        var neighbours = new List<GossNetNodeHostEntry>();

        lock (_gate)
        {
            List<GossNetNodeHostEntry>? expired = null;

            foreach (var pair in _lastSeen)
            {
                if (Age(pair.Value, now) >= _options.PeerTimeout)
                {
                    expired ??= [];
                    expired.Add(pair.Key);
                    continue;
                }

                neighbours.Add(pair.Key);
            }

            if (expired is not null)
            {
                foreach (var peer in expired)
                {
                    _lastSeen.Remove(peer);
                    _logger.LogDebug("Multicast peer {Peer} timed out", peer);
                }
            }
        }

        return new ValueTask<IReadOnlyList<GossNetNodeHostEntry>>(neighbours);
    }

    private async Task AnnounceLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _channel.SendAsync(_announcement, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failed announcement is one missed heartbeat, not a fatal condition:
                // peers tolerate several before ageing this node out.
                _logger.LogDebug(ex, "Failed to send multicast announcement");
            }

            try
            {
                await Task.Delay(_options.AnnounceInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            byte[] datagram;

            try
            {
                datagram = await _channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Multicast receive failed; retrying");

                // Without the delay a persistently failing socket spins a core.
                try
                {
                    await Task.Delay(ReceiveRetryDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            // With a protector, unauthenticated announcements are rejected outright:
            // otherwise anything on the LAN could insert a fake peer.
            if (_protector is not null)
            {
                if (!_protector.TryUnprotect(datagram, out var payload))
                {
                    _logger.LogDebug("Dropping unauthenticated {Bytes}-byte multicast announcement", datagram.Length);
                    continue;
                }

                datagram = payload;
            }

            if (!TryDecode(datagram, out var peer) || IsSelf(peer))
            {
                continue;
            }

            var now = Stopwatch.GetTimestamp();

            lock (_gate)
            {
                if (!_lastSeen.ContainsKey(peer))
                {
                    _logger.LogDebug("Discovered multicast peer {Peer}", peer);
                }

                _lastSeen[peer] = now;
            }
        }
    }

    private bool IsSelf(GossNetNodeHostEntry entry) =>
        entry.Hostname == _configuration.Hostname && entry.Port == _configuration.Port;

    /// <remarks>Computed by hand because <c>Stopwatch.GetElapsedTime</c> is .NET 7 and later.</remarks>
    private static TimeSpan Age(long from, long to) =>
        TimeSpan.FromSeconds((double)(to - from) / Stopwatch.Frequency);

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cancellation.Cancel();

        // Disposing the channel unblocks a receive parked on a real socket, which
        // cancellation alone cannot do on every framework.
        if (_ownsChannel)
        {
            _channel.Dispose();
        }

        try
        {
            Task.WaitAll([_announceLoop, _receiveLoop], TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // Both loops end by cancellation or a disposed channel; neither is a failure.
        }

        _cancellation.Dispose();
    }
}
