using k8s;

namespace GossNet.Discovery.Kubernetes;

/// <summary>A pod matched by the discovery selector.</summary>
/// <param name="Name">The pod name, used for diagnostics.</param>
/// <param name="PodIp">The pod's cluster IP, or null when one has not been assigned yet.</param>
/// <param name="IsReady">Whether the pod is Running with a Ready condition of true.</param>
public sealed record KubernetesPodInfo(string Name, string? PodIp, bool IsReady);

/// <summary>
/// The single Kubernetes query <see cref="KubernetesNodeDiscovery"/> needs.
/// </summary>
/// <remarks>Kept narrow so discovery can be tested without a cluster.</remarks>
public interface IKubernetesPodLookup : IDisposable
{
    /// <summary>
    /// Lists the pods matching a selector.
    /// </summary>
    /// <param name="namespaceName">The namespace to search.</param>
    /// <param name="labelSelector">The label selector.</param>
    /// <param name="fieldSelector">An optional field selector.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    ValueTask<IReadOnlyList<KubernetesPodInfo>> ListPodsAsync(
        string namespaceName,
        string labelSelector,
        string? fieldSelector,
        CancellationToken cancellationToken = default);

    /// <summary>Gets the namespace the current pod runs in, when running in-cluster.</summary>
    string? CurrentNamespace { get; }
}

/// <summary>
/// <see cref="IKubernetesPodLookup"/> backed by the Kubernetes API server.
/// </summary>
public sealed class KubernetesPodLookup : IKubernetesPodLookup
{
    private const string ServiceAccountNamespaceFile =
        "/var/run/secrets/kubernetes.io/serviceaccount/namespace";

    private readonly k8s.Kubernetes _client;

    /// <summary>
    /// Creates a client, preferring in-cluster configuration.
    /// </summary>
    /// <param name="options">The discovery settings.</param>
    public KubernetesPodLookup(KubernetesDiscoveryOptions options)
    {
        var configuration = !string.IsNullOrEmpty(options.KubeConfigPath)
            ? KubernetesClientConfiguration.BuildConfigFromConfigFile(options.KubeConfigPath)
            : KubernetesClientConfiguration.IsInCluster()
                ? KubernetesClientConfiguration.InClusterConfig()
                : KubernetesClientConfiguration.BuildConfigFromConfigFile();

        _client = new k8s.Kubernetes(configuration);

        CurrentNamespace = ReadCurrentNamespace();
    }

    /// <inheritdoc />
    public string? CurrentNamespace { get; }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<KubernetesPodInfo>> ListPodsAsync(
        string namespaceName,
        string labelSelector,
        string? fieldSelector,
        CancellationToken cancellationToken = default)
    {
        var pods = await _client.CoreV1
            .ListNamespacedPodAsync(
                namespaceName,
                labelSelector: labelSelector,
                fieldSelector: fieldSelector,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var result = new List<KubernetesPodInfo>();

        foreach (var pod in pods.Items)
        {
            var isRunning = string.Equals(pod.Status?.Phase, "Running", StringComparison.OrdinalIgnoreCase);

            var isReady = isRunning && pod.Status?.Conditions is not null && pod.Status.Conditions.Any(condition =>
                string.Equals(condition.Type, "Ready", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(condition.Status, "True", StringComparison.OrdinalIgnoreCase));

            result.Add(new KubernetesPodInfo(pod.Metadata?.Name ?? "<unknown>", pod.Status?.PodIP, isReady));
        }

        return result;
    }

    private static string? ReadCurrentNamespace()
    {
        try
        {
            return File.Exists(ServiceAccountNamespaceFile)
                ? File.ReadAllText(ServiceAccountNamespaceFile).Trim()
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();
}
