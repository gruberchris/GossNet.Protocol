using GossNet.Protocol;

namespace GossNet.Discovery.Aws;

/// <summary>
/// Discovers GossNet neighbours from EC2 instances carrying a tag.
/// </summary>
/// <remarks>
/// Tag-based rather than registry-based, mirroring how Consul and Serf auto-join on AWS.
/// Nothing registers or deregisters: instances are found by a tag they already carry, so
/// an instance that disappears simply stops matching.
/// </remarks>
/// <example>
/// <code>
/// var awsOptions = new AwsDiscoveryOptions
/// {
///     TagKey = "gossnet-cluster",
///     TagValue = "production",
///     Port = 9055
/// };
///
/// var configuration = new GossNetConfiguration
/// {
///     Hostname = "10.0.1.23",
///     Port = 9055,
///     DiscoveryProviderFactory = cfg => new Ec2TagNodeDiscovery(cfg, awsOptions)
/// };
/// </code>
/// </example>
public sealed class Ec2TagNodeDiscovery : CachingNodeDiscovery, IDisposable
{
    private readonly GossNetConfiguration _configuration;
    private readonly AwsDiscoveryOptions _options;
    private readonly IEc2InstanceLookup _lookup;
    private readonly bool _ownsLookup;

    /// <summary>
    /// Initializes EC2 tag-based discovery.
    /// </summary>
    /// <param name="configuration">The node configuration, used to exclude this node from its own results.</param>
    /// <param name="options">The AWS settings.</param>
    /// <param name="lookup">Lookup to use; one is created from <paramref name="options"/> when omitted.</param>
    public Ec2TagNodeDiscovery(
        GossNetConfiguration configuration,
        AwsDiscoveryOptions options,
        IEc2InstanceLookup? lookup = null)
        : base(options.CacheDuration)
    {
        if (string.IsNullOrWhiteSpace(options.TagKey))
        {
            throw new ArgumentException($"{nameof(AwsDiscoveryOptions.TagKey)} must be provided.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.TagValue))
        {
            throw new ArgumentException($"{nameof(AwsDiscoveryOptions.TagValue)} must be provided.", nameof(options));
        }

        _configuration = configuration;
        _options = options;

        // Only dispose a lookup we created; a caller-supplied one may be shared.
        _ownsLookup = lookup is null;
        _lookup = lookup ?? new Ec2InstanceLookup(options);
    }

    /// <inheritdoc />
    protected override async ValueTask<IReadOnlyList<GossNetNodeHostEntry>> ResolveAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Ec2Instance> instances;

        try
        {
            instances = await _lookup
                .GetInstancesAsync(_options.TagKey, _options.TagValue, _options.UsePrivateIp, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Surfaced rather than swallowed: a throttled or unauthorized EC2 call must not
            // look like a cluster with no other instances in it.
            throw new NodeDiscoveryException(
                $"Failed to query EC2 for instances tagged {_options.TagKey}={_options.TagValue}.", ex);
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
