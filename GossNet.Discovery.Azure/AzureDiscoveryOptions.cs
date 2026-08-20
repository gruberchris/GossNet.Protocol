namespace GossNet.Discovery.Azure;

/// <summary>
/// Settings for <see cref="AzureTagNodeDiscovery"/>.
/// </summary>
public sealed class AzureDiscoveryOptions
{
    /// <summary>Gets the subscription containing the cluster.</summary>
    /// <remarks>Falls back to the ambient default subscription when null.</remarks>
    public string? SubscriptionId { get; init; }

    /// <summary>Gets the resource group to search. Required.</summary>
    /// <remarks>
    /// Scoping to a resource group rather than a whole subscription keeps the lookup cheap
    /// and the required role assignment narrow.
    /// </remarks>
    public required string ResourceGroup { get; init; }

    /// <summary>Gets the tag identifying cluster members.</summary>
    public required string TagKey { get; init; }

    /// <summary>Gets the value that tag must have.</summary>
    public required string TagValue { get; init; }

    /// <summary>Gets the gossip port every instance listens on. Defaults to 9055.</summary>
    /// <remarks>
    /// Azure describes machines, not services, so the port cannot be discovered and must be
    /// uniform across the cluster.
    /// </remarks>
    public int Port { get; init; } = 9055;

    /// <summary>Gets how long a resolved instance list is reused. Defaults to 30 seconds.</summary>
    public TimeSpan? CacheDuration { get; init; }
}
