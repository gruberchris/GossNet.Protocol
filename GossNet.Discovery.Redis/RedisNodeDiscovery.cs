using System.Globalization;
using GossNet.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GossNet.Discovery.Redis;

/// <summary>
/// Discovers neighbours through a shared Redis sorted set that nodes heartbeat into.
/// </summary>
/// <remarks>
/// <para>
/// Unlike the read-only providers, this one <strong>registers the node</strong>: Consul has
/// an agent and EC2 has tags, but Redis knows nothing until something writes to it. A
/// background heartbeat refreshes this node's entry, and members whose heartbeat has gone
/// stale are filtered out and pruned.
/// </para>
/// <para>
/// Dispose removes this node's entry so a clean shutdown is noticed immediately rather than
/// after <see cref="RedisDiscoveryOptions.RegistrationTimeout"/>.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var redisOptions = new RedisDiscoveryOptions { ConnectionString = "localhost:6379" };
///
/// var configuration = new GossNetConfiguration
/// {
///     Hostname = "10.0.0.4",
///     Port = 9055,
///     DiscoveryProviderFactory = cfg => new RedisNodeDiscovery(cfg, redisOptions)
/// };
/// </code>
/// </example>
public sealed class RedisNodeDiscovery : CachingNodeDiscovery, IDisposable
{
    private readonly GossNetConfiguration _configuration;
    private readonly RedisDiscoveryOptions _options;
    private readonly IRedisRegistry _registry;
    private readonly ILogger _logger;
    private readonly bool _ownsRegistry;
    private readonly string _member;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _heartbeatLoop;

    private int _disposed;

    /// <summary>
    /// Registers this node and starts heartbeating.
    /// </summary>
    /// <param name="configuration">The node configuration, supplying the identity to register.</param>
    /// <param name="options">The Redis settings.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="registry">Registry to use; one is created from <paramref name="options"/> when omitted.</param>
    public RedisNodeDiscovery(
        GossNetConfiguration configuration,
        RedisDiscoveryOptions options,
        ILogger? logger = null,
        IRedisRegistry? registry = null)
        : base(options.CacheDuration)
    {
        if (string.IsNullOrWhiteSpace(options.Key))
        {
            throw new ArgumentException($"{nameof(RedisDiscoveryOptions.Key)} must be provided.", nameof(options));
        }

        _configuration = configuration;
        _options = options;
        _logger = logger ?? NullLogger.Instance;

        // Only dispose a registry we created; a caller-supplied one wraps a multiplexer
        // that is meant to be shared across an application.
        _ownsRegistry = registry is null;
        _registry = registry ?? new RedisRegistry(options);

        _member = $"{configuration.Hostname}:{configuration.Port.ToString(CultureInfo.InvariantCulture)}";

        _heartbeatLoop = Task.Run(() => HeartbeatLoopAsync(_cancellation.Token), CancellationToken.None);
    }

    /// <summary>Gets the identity this node registers under.</summary>
    public string Member => _member;

    /// <inheritdoc />
    protected override async ValueTask<IReadOnlyList<GossNetNodeHostEntry>> ResolveAsync(CancellationToken cancellationToken)
    {
        var now = Now();
        var cutoff = now - _options.RegistrationTimeout.TotalMilliseconds;

        IReadOnlyList<string> members;

        try
        {
            members = await _registry.GetLiveMembersAsync(_options.Key, cutoff, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Surfaced rather than swallowed: an unreachable Redis must not look like a
            // cluster with no other members in it.
            throw new NodeDiscoveryException($"Failed to read GossNet members from Redis key '{_options.Key}'.", ex);
        }

        var candidates = new List<GossNetNodeHostEntry>(members.Count);

        foreach (var member in members)
        {
            if (TryParseMember(member, out var entry))
            {
                candidates.Add(entry);
            }
            else
            {
                // Another application writing to the same key, or a member from an older
                // format. Skipping beats failing the whole lookup.
                _logger.LogDebug("Ignoring unparseable Redis member '{Member}'", member);
            }
        }

        // This node heartbeats into the same set, so it is in its own results.
        return ExcludeSelf(candidates, _configuration);
    }

    /// <summary>Splits a <c>host:port</c> member.</summary>
    /// <param name="member">The stored value.</param>
    /// <param name="entry">The parsed node, when the value is well formed.</param>
    internal static bool TryParseMember(string member, out GossNetNodeHostEntry entry)
    {
        entry = null!;

        if (string.IsNullOrWhiteSpace(member))
        {
            return false;
        }

        // Split on the last colon so IPv6 literals survive.
        var separator = member.LastIndexOf(':');

        if (separator <= 0 || separator == member.Length - 1)
        {
            return false;
        }

        if (!int.TryParse(member[(separator + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ||
            port is <= 0 or > 65535)
        {
            return false;
        }

        entry = new GossNetNodeHostEntry { Hostname = member[..separator], Port = port };

        return true;
    }

    private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _registry.HeartbeatAsync(_options.Key, _member, Now(), cancellationToken).ConfigureAwait(false);

                // Pruning here rather than on read keeps the set from growing forever with
                // nodes that never came back, without making reads do write work.
                await _registry
                    .PruneAsync(_options.Key, Now() - (_options.RegistrationTimeout.TotalMilliseconds * 2), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // A missed heartbeat is survivable: the timeout is several intervals wide.
                _logger.LogDebug(ex, "Redis heartbeat failed");
            }

            try
            {
                await Task.Delay(_options.HeartbeatInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cancellation.Cancel();

        try
        {
            _heartbeatLoop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // Ends by cancellation; not a failure.
        }

        try
        {
            // Deregister so peers notice the departure now rather than after the timeout.
            // CancellationToken.None deliberately: the cancellation source is already
            // tripped, and this last write is the point of a clean shutdown.
            _registry.RemoveAsync(_options.Key, _member, CancellationToken.None).AsTask().Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to deregister {Member} from Redis", _member);
        }

        if (_ownsRegistry)
        {
            _registry.Dispose();
        }

        _cancellation.Dispose();
    }
}
