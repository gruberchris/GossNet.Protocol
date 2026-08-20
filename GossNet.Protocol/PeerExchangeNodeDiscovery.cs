using System.Diagnostics;

namespace GossNet.Protocol;

/// <summary>
/// Learns neighbours from the gossip traffic itself, starting from a few seeds.
/// </summary>
/// <remarks>
/// <para>
/// This is the only mechanism that needs no external system. Every message carries
/// <see cref="GossNetMessageBase.NotifiedNodes"/>, so a node that receives one learns the
/// identity of every node already on that message's path. Given a seed to reach the network
/// through, the rest of the membership arrives on its own.
/// </para>
/// <para>
/// Seeds come from <see cref="GossNetConfiguration.StaticNodes"/> and are never evicted or
/// aged out: after a total partition, when every learned peer has expired, they are the only
/// way back in.
/// </para>
/// <para>
/// <strong>Peers are learned as they advertise themselves.</strong> An entry is whatever a
/// node put in its own <see cref="GossNetConfiguration.Hostname"/>, so a node behind NAT, or
/// one in a container advertising an internal address, teaches its peers an address they
/// cannot reach. Every node's configured hostname must be routable by the others.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var configuration = new GossNetConfiguration
/// {
///     Hostname = "10.0.0.4",
///     Port = 9055,
///     NodeDiscovery = NodeDiscovery.PeerExchange,
///     StaticNodes = [new GossNetNodeHostEntry { Hostname = "10.0.0.1", Port = 9055 }]
/// };
/// </code>
/// </example>
public sealed class PeerExchangeNodeDiscovery : IObservingNodeDiscovery
{
    /// <summary>
    /// Longest a built neighbour list is reused, so the send path does not rebuild it for
    /// every message.
    /// </summary>
    /// <remarks>
    /// Capped by <see cref="PeerExchangeOptions.PeerTimeout"/> in the constructor: reusing a
    /// snapshot for longer than the timeout would keep serving a peer well past the point it
    /// was supposed to have been forgotten.
    /// </remarks>
    private static readonly TimeSpan MaxSnapshotLifetime = TimeSpan.FromSeconds(1);

    private readonly TimeSpan _snapshotLifetime;

#if NET9_0_OR_GREATER
    private readonly Lock _gate = new();
#else
    private readonly object _gate = new();
#endif

    private readonly GossNetConfiguration _configuration;
    private readonly PeerExchangeOptions _options;
    private readonly List<GossNetNodeHostEntry> _seeds = [];
    private readonly Dictionary<GossNetNodeHostEntry, long> _lastSeen = [];

    private IReadOnlyList<GossNetNodeHostEntry> _snapshot = [];
    private long _snapshotAt = long.MinValue;
    private int _version;
    private int _snapshotVersion = -1;

    /// <summary>
    /// Initializes peer exchange.
    /// </summary>
    /// <param name="configuration">The node configuration supplying the seeds and this node's identity.</param>
    /// <param name="options">Peer retention settings. Defaults are used when omitted.</param>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="PeerExchangeOptions.MaxPeers"/> is not positive.</exception>
    public PeerExchangeNodeDiscovery(GossNetConfiguration configuration, PeerExchangeOptions? options = null)
    {
        _configuration = configuration;
        _options = options ?? new PeerExchangeOptions();

        if (_options.MaxPeers <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), _options.MaxPeers, $"{nameof(PeerExchangeOptions.MaxPeers)} must be positive.");
        }

        _snapshotLifetime = _options.PeerTimeout < MaxSnapshotLifetime ? _options.PeerTimeout : MaxSnapshotLifetime;

        foreach (var seed in configuration.StaticNodes)
        {
            if (!IsSelf(seed) && !_seeds.Contains(seed))
            {
                _seeds.Add(seed);
            }
        }
    }

    /// <summary>Gets the number of peers learned from traffic, excluding seeds.</summary>
    public int LearnedPeerCount
    {
        get
        {
            lock (_gate)
            {
                return _lastSeen.Count;
            }
        }
    }

    /// <inheritdoc />
    public void Observe(IReadOnlyCollection<GossNetNodeHostEntry> seen)
    {
        if (seen is null || seen.Count == 0)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();

        lock (_gate)
        {
            foreach (var peer in seen)
            {
                if (peer is null || IsSelf(peer) || _seeds.Contains(peer))
                {
                    continue;
                }

                // Re-seeing a known peer refreshes it rather than adding a duplicate,
                // which is what keeps an active peer from ageing out.
                if (!_lastSeen.ContainsKey(peer))
                {
                    _version++;
                }

                _lastSeen[peer] = now;
            }

            EvictExcess();
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<GossNetNodeHostEntry>> GetNeighboursAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();

            if (_snapshotVersion == _version && Age(_snapshotAt, now) < _snapshotLifetime)
            {
                return new ValueTask<IReadOnlyList<GossNetNodeHostEntry>>(_snapshot);
            }

            var neighbours = new List<GossNetNodeHostEntry>(_seeds.Count + _lastSeen.Count);
            neighbours.AddRange(_seeds);

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
                }

                _version++;
            }

            _snapshot = neighbours;
            _snapshotAt = now;
            _snapshotVersion = _version;

            return new ValueTask<IReadOnlyList<GossNetNodeHostEntry>>(neighbours);
        }
    }

    /// <summary>Drops the least recently seen peers until the limit is respected.</summary>
    private void EvictExcess()
    {
        while (_lastSeen.Count > _options.MaxPeers)
        {
            GossNetNodeHostEntry? oldest = null;
            var oldestSeen = long.MaxValue;

            foreach (var pair in _lastSeen)
            {
                if (pair.Value < oldestSeen)
                {
                    oldestSeen = pair.Value;
                    oldest = pair.Key;
                }
            }

            if (oldest is null)
            {
                return;
            }

            _lastSeen.Remove(oldest);
            _version++;
        }
    }

    private bool IsSelf(GossNetNodeHostEntry entry) =>
        entry.Hostname == _configuration.Hostname && entry.Port == _configuration.Port;

    /// <summary>
    /// Elapsed time between two <see cref="Stopwatch"/> timestamps.
    /// </summary>
    /// <remarks>Computed by hand because <c>Stopwatch.GetElapsedTime</c> is .NET 7 and later.</remarks>
    private static TimeSpan Age(long from, long to) =>
        from == long.MinValue ? TimeSpan.MaxValue : TimeSpan.FromSeconds((double)(to - from) / Stopwatch.Frequency);
}
