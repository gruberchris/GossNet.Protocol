using System.Runtime.CompilerServices;
using System.Threading.Channels;
using k8s;
using k8s.Models;

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
/// A <see cref="IKubernetesPodLookup"/> that also streams pod changes.
/// </summary>
/// <remarks>
/// Separate from <see cref="IKubernetesPodLookup"/> rather than added to it: that interface
/// has shipped since 0.3.0, and adding a member would break every existing implementation.
/// <see cref="KubernetesNodeDiscovery"/> watches only when its lookup implements this.
/// </remarks>
public interface IWatchablePodLookup : IKubernetesPodLookup
{
    /// <summary>
    /// Signals once for the current state, then again each time a matching pod is added,
    /// changed or removed.
    /// </summary>
    /// <param name="namespaceName">The namespace to watch.</param>
    /// <param name="labelSelector">The label selector.</param>
    /// <param name="fieldSelector">An optional field selector.</param>
    /// <param name="cancellationToken">Ends the watch.</param>
    /// <remarks>
    /// Yields a signal, not the pods. Kubernetes watch events describe one pod at a time,
    /// while a neighbour list has to be complete, so the caller re-lists on each signal.
    /// </remarks>
    IAsyncEnumerable<bool> WatchPodsAsync(
        string namespaceName,
        string labelSelector,
        string? fieldSelector,
        CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IKubernetesPodLookup"/> backed by the Kubernetes API server.
/// </summary>
public sealed class KubernetesPodLookup : IKubernetesPodLookup, IWatchablePodLookup
{
    private const string ServiceAccountNamespaceFile =
        "/var/run/secrets/kubernetes.io/serviceaccount/namespace";

    /// <summary>Pause before re-establishing a watch that ended, so a failing API server cannot spin.</summary>
    private static readonly TimeSpan WatchRestartDelay = TimeSpan.FromSeconds(1);

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

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// A Kubernetes watch is not a durable subscription. It is opened at a
    /// <c>resourceVersion</c> and ends on its own — the API server closes idle connections,
    /// and once the starting version has aged out of etcd's compaction window the server
    /// answers <c>410 Gone</c> rather than replaying from it.
    /// </para>
    /// <para>
    /// Both are handled the same way, and structurally rather than by inspecting status
    /// codes: every iteration re-lists to obtain a fresh <c>resourceVersion</c> before
    /// opening the next watch. A <c>410</c> therefore recovers by the same path as an idle
    /// disconnect, and there is no way to accidentally resume from a version the server has
    /// already rejected.
    /// </para>
    /// </remarks>
    public async IAsyncEnumerable<bool> WatchPodsAsync(
        string namespaceName,
        string labelSelector,
        string? fieldSelector,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var changes = Channel.CreateUnbounded<bool>();

        // The client's watch is callback-shaped; a channel turns it into the pull-based
        // sequence IWatchableNodeDiscovery expects.
        _ = Task.Run(
            async () =>
            {
                try
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        await WatchOnceAsync(namespaceName, labelSelector, fieldSelector, changes.Writer, cancellationToken)
                            .ConfigureAwait(false);

                        // Without this, an API server rejecting every watch immediately
                        // would spin.
                        await Task.Delay(WatchRestartDelay, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Shutdown.
                }
                finally
                {
                    changes.Writer.TryComplete();
                }
            },
            CancellationToken.None);

        await foreach (var change in changes.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return change;
        }
    }

    /// <summary>
    /// Lists to get a fresh <c>resourceVersion</c>, then watches from it until the watch ends.
    /// </summary>
    private async Task WatchOnceAsync(
        string namespaceName,
        string labelSelector,
        string? fieldSelector,
        ChannelWriter<bool> changes,
        CancellationToken cancellationToken)
    {
        string? resourceVersion;

        try
        {
            var list = await _client.CoreV1
                .ListNamespacedPodAsync(
                    namespaceName,
                    labelSelector: labelSelector,
                    fieldSelector: fieldSelector,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            resourceVersion = list.Metadata?.ResourceVersion;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The API server is unreachable. The caller's delay applies before retrying.
            return;
        }

        // Publish the current state before waiting for a change, or a node has no
        // neighbours until a pod happens to come or go.
        changes.TryWrite(true);

        try
        {
            // The typed WatchListNamespacedPodAsync rather than WatcherExt.WatchAsync: the
            // latter is obsolete in KubernetesClient 19, and this one yields the events
            // directly instead of needing the response adapted.
            var events = _client.CoreV1.WatchListNamespacedPodAsync(
                namespaceName,
                labelSelector: labelSelector,
                fieldSelector: fieldSelector,
                resourceVersion: resourceVersion,
                cancellationToken: cancellationToken);

            await foreach (var _ in events.ConfigureAwait(false))
            {
                changes.TryWrite(true);
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // 410 Gone, a dropped connection, or the server closing an idle watch. All are
            // recovered by re-listing on the next iteration.
        }
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
