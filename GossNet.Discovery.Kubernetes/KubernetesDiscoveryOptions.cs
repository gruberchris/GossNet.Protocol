namespace GossNet.Discovery.Kubernetes;

/// <summary>
/// Settings for <see cref="KubernetesNodeDiscovery"/>.
/// </summary>
public sealed class KubernetesDiscoveryOptions
{
    /// <summary>
    /// Gets the label selector identifying the gossip pods, for example <c>app=gossnet</c>.
    /// </summary>
    public required string LabelSelector { get; init; }

    /// <summary>
    /// Gets the namespace to search. Defaults to the pod's own namespace when running
    /// in-cluster, otherwise <c>default</c>.
    /// </summary>
    public string? Namespace { get; init; }

    /// <summary>
    /// Gets the port neighbours listen on. Defaults to the node's own configured port.
    /// </summary>
    public int? Port { get; init; }

    /// <summary>Gets an optional field selector applied alongside the label selector.</summary>
    public string? FieldSelector { get; init; }

    /// <summary>
    /// Gets a value indicating whether only pods that are Running and Ready are
    /// returned. Defaults to <c>true</c>.
    /// </summary>
    /// <remarks>
    /// A pod that is scheduled but not yet ready has no listening socket, so gossiping
    /// at it just produces dropped datagrams.
    /// </remarks>
    public bool ReadyOnly { get; init; } = true;

    /// <summary>
    /// Gets an explicit kubeconfig path. When unset, in-cluster configuration is used if
    /// available, falling back to the default kubeconfig.
    /// </summary>
    public string? KubeConfigPath { get; init; }

    /// <summary>Gets how long a resolved neighbour list is reused. Defaults to 30 seconds.</summary>
    public TimeSpan? CacheDuration { get; init; }
}
