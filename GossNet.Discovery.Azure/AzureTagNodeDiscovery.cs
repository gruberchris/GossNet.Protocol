using GossNet.Protocol;

namespace GossNet.Discovery.Azure;

/// <summary>
/// Discovers GossNet neighbours from Azure virtual machines carrying a tag.
/// </summary>
/// <remarks>
/// Tag-based rather than registry-based, the same shape as the AWS provider. Nothing
/// registers or deregisters: machines are found by a tag they already carry, so one that
/// disappears simply stops matching.
/// </remarks>
/// <example>
/// <code>
/// var azureOptions = new AzureDiscoveryOptions
/// {
///     ResourceGroup = "gossnet-rg",
///     TagKey = "gossnet-cluster",
///     TagValue = "production"
/// };
///
/// var configuration = new GossNetConfiguration
/// {
///     Hostname = "10.0.1.23",
///     Port = 9055,
///     DiscoveryProviderFactory = cfg => new AzureTagNodeDiscovery(cfg, azureOptions)
/// };
/// </code>
/// </example>
public sealed class AzureTagNodeDiscovery : CachingNodeDiscovery, IDisposable
{
    private readonly GossNetConfiguration _configuration;
    private readonly AzureDiscoveryOptions _options;
    private readonly IAzureInstanceLookup _lookup;
    private readonly bool _ownsLookup;

    /// <summary>
    /// Initializes Azure tag-based discovery.
    /// </summary>
    /// <param name="configuration">The node configuration, used to exclude this node from its own results.</param>
    /// <param name="options">The Azure settings.</param>
    /// <param name="lookup">Lookup to use; one is created from <paramref name="options"/> when omitted.</param>
    public AzureTagNodeDiscovery(
        GossNetConfiguration configuration,
        AzureDiscoveryOptions options,
        IAzureInstanceLookup? lookup = null)
        : base(options.CacheDuration)
    {
        if (string.IsNullOrWhiteSpace(options.ResourceGroup))
        {
            throw new ArgumentException($"{nameof(AzureDiscoveryOptions.ResourceGroup)} must be provided.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.TagKey))
        {
            throw new ArgumentException($"{nameof(AzureDiscoveryOptions.TagKey)} must be provided.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.TagValue))
        {
            throw new ArgumentException($"{nameof(AzureDiscoveryOptions.TagValue)} must be provided.", nameof(options));
        }

        _configuration = configuration;
        _options = options;

        // Only dispose a lookup we created; a caller-supplied one may be shared.
        _ownsLookup = lookup is null;
        _lookup = lookup ?? new AzureInstanceLookup(options);
    }

    /// <inheritdoc />
    protected override async ValueTask<IReadOnlyList<GossNetNodeHostEntry>> ResolveAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<AzureInstance> instances;

        try
        {
            instances = await _lookup
                .GetInstancesAsync(_options.ResourceGroup, _options.TagKey, _options.TagValue, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Surfaced rather than swallowed: a throttled or unauthorized ARM call must not
            // look like a cluster with no other machines in it.
            throw new NodeDiscoveryException(
                $"Failed to query Azure resource group '{_options.ResourceGroup}' for machines tagged " +
                $"{_options.TagKey}={_options.TagValue}.", ex);
        }

        var candidates = new List<GossNetNodeHostEntry>(instances.Count);

        foreach (var instance in instances)
        {
            candidates.Add(new GossNetNodeHostEntry { Hostname = instance.Address, Port = _options.Port });
        }

        // This node carries the same tag as the rest of the cluster, so it is in its own results.
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
