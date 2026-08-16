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
public sealed class ConsulNodeDiscovery : CachingNodeDiscovery, IDisposable
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
    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }
}
