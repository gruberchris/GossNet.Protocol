using GossNet.Protocol;

namespace GossNet.Discovery.Gcp;

/// <summary>
/// Discovers GossNet neighbours from Compute Engine instances carrying a label.
/// </summary>
/// <remarks>
/// Label-based rather than registry-based, the same shape as the AWS and Azure providers.
/// Nothing registers or deregisters: instances are found by a label they already carry, so
/// one that goes away simply stops matching.
/// </remarks>
/// <example>
/// <code>
/// var gcpOptions = new GcpDiscoveryOptions
/// {
///     ProjectId = "my-project",
///     LabelKey = "gossnet-cluster",
///     LabelValue = "production"
/// };
///
/// var configuration = new GossNetConfiguration
/// {
///     Hostname = "10.128.0.4",
///     Port = 9055,
///     DiscoveryProviderFactory = cfg => new GceLabelNodeDiscovery(cfg, gcpOptions)
/// };
/// </code>
/// </example>
public sealed class GceLabelNodeDiscovery : CachingNodeDiscovery, IDisposable
{
    private readonly GossNetConfiguration _configuration;
    private readonly GcpDiscoveryOptions _options;
    private readonly IGceInstanceLookup _lookup;
    private readonly bool _ownsLookup;

    /// <summary>
    /// Initializes Compute Engine label-based discovery.
    /// </summary>
    /// <param name="configuration">The node configuration, used to exclude this node from its own results.</param>
    /// <param name="options">The Google Cloud settings.</param>
    /// <param name="lookup">Lookup to use; one is created from Application Default Credentials when omitted.</param>
    public GceLabelNodeDiscovery(
        GossNetConfiguration configuration,
        GcpDiscoveryOptions options,
        IGceInstanceLookup? lookup = null)
        : base(options.CacheDuration)
    {
        if (string.IsNullOrWhiteSpace(options.ProjectId))
        {
            throw new ArgumentException($"{nameof(GcpDiscoveryOptions.ProjectId)} must be provided.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.LabelKey))
        {
            throw new ArgumentException($"{nameof(GcpDiscoveryOptions.LabelKey)} must be provided.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.LabelValue))
        {
            throw new ArgumentException($"{nameof(GcpDiscoveryOptions.LabelValue)} must be provided.", nameof(options));
        }

        _configuration = configuration;
        _options = options;

        // Only dispose a lookup we created; a caller-supplied one may be shared.
        _ownsLookup = lookup is null;
        _lookup = lookup ?? new GceInstanceLookup();
    }

    /// <inheritdoc />
    protected override async ValueTask<IReadOnlyList<GossNetNodeHostEntry>> ResolveAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<GceInstance> instances;

        try
        {
            instances = await _lookup
                .GetInstancesAsync(_options.ProjectId, _options.LabelKey, _options.LabelValue, _options.UseInternalIp, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Surfaced rather than swallowed: a throttled or unauthorized API call must not
            // look like a cluster with no other instances in it.
            throw new NodeDiscoveryException(
                $"Failed to query Google Cloud project '{_options.ProjectId}' for instances labelled " +
                $"{_options.LabelKey}={_options.LabelValue}.", ex);
        }

        var candidates = new List<GossNetNodeHostEntry>(instances.Count);

        foreach (var instance in instances)
        {
            candidates.Add(new GossNetNodeHostEntry { Hostname = instance.Address, Port = _options.Port });
        }

        // This node carries the same label as the rest of the cluster, so it is in its own results.
        return ExcludeSelf(candidates, _configuration);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsLookup)
        {
            _lookup.Dispose();
        }
    }
}
