namespace GossNet.Discovery.Aws;

/// <summary>
/// Settings for <see cref="Ec2TagNodeDiscovery"/>.
/// </summary>
public sealed class AwsDiscoveryOptions
{
    /// <summary>Gets the instance tag key identifying cluster members.</summary>
    /// <remarks>Required. Instances are found by a tag they already carry, so nothing registers itself.</remarks>
    public required string TagKey { get; init; }

    /// <summary>Gets the value that tag must have.</summary>
    public required string TagValue { get; init; }

    /// <summary>Gets the gossip port every instance listens on. Defaults to 9055.</summary>
    /// <remarks>
    /// EC2 describes instances, not services, so the port cannot be discovered and must be
    /// uniform across the cluster.
    /// </remarks>
    public int Port { get; init; } = 9055;

    /// <summary>
    /// Gets a value indicating whether to use each instance's private address. Defaults to true.
    /// </summary>
    /// <remarks>
    /// Private addressing is correct for nodes inside one VPC, which is the normal shape.
    /// Public addresses cost inter-AZ traffic and require the instances to have them at all.
    /// </remarks>
    public bool UsePrivateIp { get; init; } = true;

    /// <summary>Gets the AWS region. Falls back to the ambient SDK configuration when null.</summary>
    public string? Region { get; init; }

    /// <summary>Gets how long a resolved instance list is reused. Defaults to 30 seconds.</summary>
    public TimeSpan? CacheDuration { get; init; }
}
