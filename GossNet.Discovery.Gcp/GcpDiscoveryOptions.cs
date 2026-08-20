namespace GossNet.Discovery.Gcp;

/// <summary>
/// Settings for <see cref="GceLabelNodeDiscovery"/>.
/// </summary>
public sealed class GcpDiscoveryOptions
{
    /// <summary>Gets the Google Cloud project containing the cluster. Required.</summary>
    public required string ProjectId { get; init; }

    /// <summary>
    /// Gets the instance <em>label</em> key identifying cluster members.
    /// </summary>
    /// <remarks>
    /// A label, not a network tag. Compute Engine has both, and they are not
    /// interchangeable: network tags are unkeyed strings used for firewall rules, while
    /// labels are the key/value metadata that corresponds to an AWS or Azure tag.
    /// </remarks>
    public required string LabelKey { get; init; }

    /// <summary>Gets the value that label must have.</summary>
    public required string LabelValue { get; init; }

    /// <summary>Gets the gossip port every instance listens on. Defaults to 9055.</summary>
    /// <remarks>
    /// Compute Engine describes instances, not services, so the port cannot be discovered
    /// and must be uniform across the cluster.
    /// </remarks>
    public int Port { get; init; } = 9055;

    /// <summary>
    /// Gets a value indicating whether to use each instance's internal address. Defaults to true.
    /// </summary>
    /// <remarks>
    /// Internal addressing is correct for nodes inside one VPC, which is the normal shape.
    /// An external address exists only when the instance has an access config, and costs
    /// egress to use.
    /// </remarks>
    public bool UseInternalIp { get; init; } = true;

    /// <summary>Gets how long a resolved instance list is reused. Defaults to 30 seconds.</summary>
    public TimeSpan? CacheDuration { get; init; }
}
