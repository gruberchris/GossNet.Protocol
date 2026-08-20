using System.Globalization;
using System.Runtime.CompilerServices;
using GossNet.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GossNet.Discovery.Etcd;

/// <summary>
/// Discovers neighbours from keys registered under an etcd prefix, and watches that prefix
/// for changes.
/// </summary>
/// <remarks>
/// <para>
/// The node registers itself under a lease, so etcd deletes the key by itself when the node
/// stops renewing — a crashed node disappears without anything having to detect it.
/// </para>
/// <para>
/// This is the one provider that implements <see cref="IWatchableNodeDiscovery"/>: etcd's
/// watch is a first-class change feed, so membership changes reach a node as they happen
/// rather than after the cache expires.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var etcdOptions = new EtcdDiscoveryOptions { ConnectionString = "http://localhost:2379" };
///
/// var configuration = new GossNetConfiguration
/// {
///     Hostname = "10.0.0.4",
///     Port = 9055,
///     DiscoveryProviderFactory = cfg => new EtcdNodeDiscovery(cfg, etcdOptions)
/// };
/// </code>
/// </example>
public sealed class EtcdNodeDiscovery : CachingNodeDiscovery, IWatchableNodeDiscovery, IDisposable
{
    private readonly GossNetConfiguration _configuration;
    private readonly EtcdDiscoveryOptions _options;
    private readonly IEtcdRegistry _registry;
    private readonly ILogger _logger;
    private readonly bool _ownsRegistry;
    private readonly string _member;
    private readonly CancellationTokenSource _cancellation = new();

    private int _disposed;

    /// <summary>Guards <see cref="_registration"/>.</summary>
    private readonly object _registrationGate = new();

    /// <summary>
    /// The in-flight or completed registration, shared by every caller so none proceeds
    /// while it is still being established. Cleared on failure so the next call retries.
    /// </summary>
    private Task? _registration;

    /// <summary>
    /// Initializes etcd-based discovery.
    /// </summary>
    /// <param name="configuration">The node configuration, supplying the identity to register.</param>
    /// <param name="options">The etcd settings.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="registry">Registry to use; one is created from <paramref name="options"/> when omitted.</param>
    public EtcdNodeDiscovery(
        GossNetConfiguration configuration,
        EtcdDiscoveryOptions options,
        ILogger? logger = null,
        IEtcdRegistry? registry = null)
        : base(options.CacheDuration)
    {
        if (string.IsNullOrWhiteSpace(options.Prefix))
        {
            throw new ArgumentException($"{nameof(EtcdDiscoveryOptions.Prefix)} must be provided.", nameof(options));
        }

        _configuration = configuration;
        _options = options;
        _logger = logger ?? NullLogger.Instance;

        _ownsRegistry = registry is null;
        _registry = registry ?? new EtcdRegistry(options, _logger);

        _member = $"{configuration.Hostname}:{configuration.Port.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>Gets the identity this node registers under.</summary>
    public string Member => _member;

    /// <summary>Gets the key this node's registration is written to.</summary>
    public string MemberKey => _options.Prefix + _member;

    /// <inheritdoc />
    protected override async ValueTask<IReadOnlyList<GossNetNodeHostEntry>> ResolveAsync(CancellationToken cancellationToken)
    {
        await EnsureRegisteredAsync().ConfigureAwait(false);

        IReadOnlyList<string> members;

        try
        {
            members = await _registry.GetMembersAsync(_options.Prefix, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new NodeDiscoveryException($"Failed to read GossNet members from etcd prefix '{_options.Prefix}'.", ex);
        }

        return Parse(members);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<IReadOnlyList<GossNetNodeHostEntry>> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellation.Token);
        var token = linked.Token;

        await EnsureRegisteredAsync().ConfigureAwait(false);

        // Emit the current membership before waiting for a change, otherwise the node has
        // no neighbours until something happens to join or leave.
        yield return Parse(await _registry.GetMembersAsync(_options.Prefix, token).ConfigureAwait(false));

        await foreach (var _ in _registry.WatchAsync(_options.Prefix, token).ConfigureAwait(false))
        {
            // The watch says something changed, not what the membership now is, so the
            // prefix is re-read. A complete list is what the contract requires.
            yield return Parse(await _registry.GetMembersAsync(_options.Prefix, token).ConfigureAwait(false));
        }
    }

    private IReadOnlyList<GossNetNodeHostEntry> Parse(IReadOnlyList<string> members)
    {
        var candidates = new List<GossNetNodeHostEntry>(members.Count);

        foreach (var member in members)
        {
            if (TryParseMember(member, out var entry))
            {
                candidates.Add(entry);
            }
            else
            {
                _logger.LogDebug("Ignoring unparseable etcd member '{Member}'", member);
            }
        }

        // This node registers under the same prefix, so it is in its own results.
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

    /// <summary>
    /// Registers this node once, on first use.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deferred rather than done in the constructor so that constructing a provider never
    /// performs network I/O, matching every other provider in the library.
    /// </para>
    /// <para>
    /// Deliberately takes no cancellation token. The registration is scoped to the
    /// provider's lifetime, not to whichever call happened to trigger it: cancelling one
    /// lookup must not tear down this node's lease and evict it from the cluster.
    /// </para>
    /// </remarks>
    private Task EnsureRegisteredAsync()
    {
        lock (_registrationGate)
        {
            // Concurrent callers share the one in-flight registration rather than one
            // proceeding unregistered while another is still establishing it. A failed
            // attempt is replaced on the next call, so etcd being briefly unavailable
            // at startup does not leave the node permanently unregistered.
            if (_registration is null || _registration.IsFaulted || _registration.IsCanceled)
            {
                _registration = RegisterAsync();
            }

            return _registration;
        }
    }

    private async Task RegisterAsync()
    {
        try
        {
            await _registry
                .RegisterAsync(MemberKey, _member, _options.LeaseTtl, _cancellation.Token)
                .ConfigureAwait(false);

            _logger.LogDebug("Registered {Member} in etcd at {Key}", _member, MemberKey);
        }
        catch (Exception ex)
        {
            throw new NodeDiscoveryException($"Failed to register {_member} in etcd at '{MemberKey}'.", ex);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Stops lease renewal, so etcd expires the key and peers stop seeing this node.
        _cancellation.Cancel();

        if (_ownsRegistry)
        {
            _registry.Dispose();
        }

        _cancellation.Dispose();
    }
}
