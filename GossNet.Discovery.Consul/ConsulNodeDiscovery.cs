using System.Runtime.CompilerServices;
using GossNet.Protocol;

namespace GossNet.Discovery.Consul;

/// <summary>
/// Discovers GossNet neighbours from a Consul service catalog.
/// </summary>
/// <example>
/// <code>
/// var consulOptions = new ConsulDiscoveryOptions { ServiceName = "gossnet" };
///
/// var configuration = new GossNetConfiguration
/// {
///     Hostname = "10.0.0.1",
///     Port = 9055,
///     DiscoveryProviderFactory = cfg => new ConsulNodeDiscovery(cfg, consulOptions)
/// };
/// </code>
/// </example>
public sealed class ConsulNodeDiscovery : CachingNodeDiscovery, IWatchableNodeDiscovery, IDisposable
{
    private readonly GossNetConfiguration _configuration;
    private readonly ConsulDiscoveryOptions _options;
    private readonly IConsulHealthClient _client;
    private readonly bool _ownsClient;

    /// <summary>
    /// Initializes Consul-based discovery.
    /// </summary>
    /// <param name="configuration">The node configuration, used to exclude this node from its own results.</param>
    /// <param name="options">The Consul settings.</param>
    /// <param name="client">Client to use; one is created from <paramref name="options"/> when omitted.</param>
    public ConsulNodeDiscovery(
        GossNetConfiguration configuration,
        ConsulDiscoveryOptions options,
        IConsulHealthClient? client = null)
        : base(options.CacheDuration)
    {
        if (string.IsNullOrWhiteSpace(options.ServiceName))
        {
            throw new ArgumentException($"{nameof(ConsulDiscoveryOptions.ServiceName)} must be provided.", nameof(options));
        }

        _configuration = configuration;
        _options = options;

        // Only dispose a client we created; a caller-supplied one may be shared.
        _ownsClient = client is null;
        _client = client ?? new ConsulHealthClient(options);
    }

    /// <inheritdoc />
    protected override async ValueTask<IReadOnlyList<GossNetNodeHostEntry>> ResolveAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ConsulServiceInstance> instances;

        try
        {
            instances = await _client
                .GetServiceInstancesAsync(_options.ServiceName, _options.Tag, _options.PassingOnly, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Surfaced rather than swallowed: an unreachable Consul agent must not look
            // like a network with no other nodes in it.
            throw new NodeDiscoveryException(
                $"Failed to query Consul for service '{_options.ServiceName}'.", ex);
        }

        var candidates = new List<GossNetNodeHostEntry>(instances.Count);

        foreach (var instance in instances)
        {
            candidates.Add(new GossNetNodeHostEntry { Hostname = instance.Address, Port = instance.Port });
        }

        // A node registers itself in Consul, so it always appears in its own results.
        return ExcludeSelf(candidates, _configuration);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Uses Consul blocking queries: each request carries the index from the previous
    /// result, and the agent holds it open until that index moves. Three rules make this
    /// correct, and getting any of them wrong fails quietly rather than loudly:
    /// </para>
    /// <list type="bullet">
    /// <item>An index that goes <em>backwards</em> means Consul restarted or the table was
    /// re-indexed. The index must be reset to zero to re-baseline, or the query blocks
    /// forever and no further change is ever seen.</item>
    /// <item>An index below one must be treated as one, otherwise the next query is a
    /// non-blocking read and the watch becomes a hot loop.</item>
    /// <item>An <em>unchanged</em> index means the wait elapsed, not that anything changed,
    /// so nothing is yielded — republishing identical membership every wait period would be
    /// pointless churn.</item>
    /// </list>
    /// <para>
    /// Completes immediately when the client does not support blocking queries, which leaves
    /// the node on its normal cached polling.
    /// </para>
    /// </remarks>
    public async IAsyncEnumerable<IReadOnlyList<GossNetNodeHostEntry>> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_client is not IWatchableConsulHealthClient watchable)
        {
            yield break;
        }

        ulong index = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            ConsulQueryResult result;

            try
            {
                result = await watchable
                    .QueryServiceInstancesAsync(
                        _options.ServiceName, _options.Tag, _options.PassingOnly, index, _options.WatchWaitTime, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }
            catch (Exception)
            {
                // An agent that is down must not turn the watch into a hot loop against a
                // refused connection.
                try
                {
                    await Task.Delay(_options.WatchRetryDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }

                continue;
            }

            var previous = index;
            index = Normalize(result.Index, previous);

            // The very first query establishes the baseline and must publish it, or the node
            // has no neighbours until something happens to change.
            if (previous != 0 && index == previous)
            {
                continue;
            }

            var candidates = new List<GossNetNodeHostEntry>(result.Instances.Count);

            foreach (var instance in result.Instances)
            {
                candidates.Add(new GossNetNodeHostEntry { Hostname = instance.Address, Port = instance.Port });
            }

            yield return ExcludeSelf(candidates, _configuration);
        }
    }

    /// <summary>
    /// Applies Consul's blocking-query index rules.
    /// </summary>
    /// <param name="returned">The index Consul reported.</param>
    /// <param name="previous">The index sent with the request.</param>
    internal static ulong Normalize(ulong returned, ulong previous) =>
        returned < previous ? 0 : returned < 1 ? 1 : returned;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }
}
