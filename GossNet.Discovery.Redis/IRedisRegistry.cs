using StackExchange.Redis;

namespace GossNet.Discovery.Redis;

/// <summary>
/// The Redis operations <see cref="RedisNodeDiscovery"/> needs.
/// </summary>
/// <remarks>
/// Membership is one sorted set: the member is <c>host:port</c> and the score is the epoch
/// milliseconds of its last heartbeat. That makes "who is alive" a single range query and
/// needs no per-key expiry or <c>SCAN</c>, both of which scale badly.
/// </remarks>
public interface IRedisRegistry : IDisposable
{
    /// <summary>Records or refreshes a member's heartbeat.</summary>
    /// <param name="key">The membership key.</param>
    /// <param name="member">The <c>host:port</c> identity.</param>
    /// <param name="score">Epoch milliseconds of this heartbeat.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    ValueTask HeartbeatAsync(string key, string member, double score, CancellationToken cancellationToken = default);

    /// <summary>Lists members that have heartbeated recently enough.</summary>
    /// <param name="key">The membership key.</param>
    /// <param name="minScore">Oldest acceptable heartbeat, in epoch milliseconds.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    ValueTask<IReadOnlyList<string>> GetLiveMembersAsync(string key, double minScore, CancellationToken cancellationToken = default);

    /// <summary>Removes a member, used when a node shuts down cleanly.</summary>
    /// <param name="key">The membership key.</param>
    /// <param name="member">The <c>host:port</c> identity.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    ValueTask RemoveAsync(string key, string member, CancellationToken cancellationToken = default);

    /// <summary>Drops entries whose heartbeat is older than the cutoff.</summary>
    /// <param name="key">The membership key.</param>
    /// <param name="maxScore">Newest score to remove, in epoch milliseconds.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    ValueTask PruneAsync(string key, double maxScore, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IRedisRegistry"/> backed by StackExchange.Redis.
/// </summary>
public sealed class RedisRegistry : IRedisRegistry
{
    private readonly IConnectionMultiplexer _connection;
    private readonly bool _ownsConnection;

    /// <summary>Connects using the configured connection string.</summary>
    /// <param name="options">The Redis settings.</param>
    /// <exception cref="ArgumentException">No connection string was supplied.</exception>
    public RedisRegistry(RedisDiscoveryOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new ArgumentException(
                $"{nameof(RedisDiscoveryOptions.ConnectionString)} is required when no registry is supplied.",
                nameof(options));
        }

        _connection = ConnectionMultiplexer.Connect(options.ConnectionString!);
        _ownsConnection = true;
    }

    /// <summary>Uses an existing multiplexer, which the caller keeps ownership of.</summary>
    /// <param name="connection">A connected multiplexer.</param>
    /// <remarks>
    /// The recommended form in an application that already talks to Redis: the multiplexer
    /// is expensive and designed to be shared.
    /// </remarks>
    public RedisRegistry(IConnectionMultiplexer connection)
    {
        _connection = connection;
        _ownsConnection = false;
    }

    private IDatabase Database => _connection.GetDatabase();

    /// <inheritdoc />
    public async ValueTask HeartbeatAsync(string key, string member, double score, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await Database.SortedSetAddAsync(key, member, score).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<string>> GetLiveMembersAsync(string key, double minScore, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var members = await Database
            .SortedSetRangeByScoreAsync(key, minScore, double.PositiveInfinity)
            .ConfigureAwait(false);

        var result = new List<string>(members.Length);

        foreach (var member in members)
        {
            if (member.HasValue)
            {
                result.Add(member!);
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async ValueTask RemoveAsync(string key, string member, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await Database.SortedSetRemoveAsync(key, member).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask PruneAsync(string key, double maxScore, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await Database.SortedSetRemoveRangeByScoreAsync(key, double.NegativeInfinity, maxScore).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsConnection)
        {
            _connection.Dispose();
        }
    }
}
