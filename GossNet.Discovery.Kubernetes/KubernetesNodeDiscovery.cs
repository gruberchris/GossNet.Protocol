using System.Runtime.CompilerServices;
using GossNet.Protocol;

namespace GossNet.Discovery.Kubernetes;

/// <summary>
/// Discovers GossNet neighbours by listing pods in a Kubernetes namespace.
/// </summary>
/// <example>
/// <code>
/// var k8sOptions = new KubernetesDiscoveryOptions { LabelSelector = "app=gossnet" };
///
/// var configuration = new GossNetConfiguration
/// {
///     // Supplied by the downward API so the pod knows the address its peers see.
///     Hostname = Environment.GetEnvironmentVariable("POD_IP")!,
///     Port = 9055,
///     DiscoveryProviderFactory = cfg => new KubernetesNodeDiscovery(cfg, k8sOptions)
/// };
/// </code>
/// </example>
public sealed class KubernetesNodeDiscovery : CachingNodeDiscovery, IWatchableNodeDiscovery, IDisposable
{
    private const string FallbackNamespace = "default";

    private readonly GossNetConfiguration _configuration;
    private readonly KubernetesDiscoveryOptions _options;
    private readonly IKubernetesPodLookup _lookup;
    private readonly bool _ownsLookup;

    /// <summary>
    /// Initializes Kubernetes-based discovery.
    /// </summary>
    /// <param name="configuration">The node configuration, used to exclude this node from its own results.</param>
    /// <param name="options">The Kubernetes settings.</param>
    /// <param name="lookup">Lookup to use; one is created from <paramref name="options"/> when omitted.</param>
    public KubernetesNodeDiscovery(
        GossNetConfiguration configuration,
        KubernetesDiscoveryOptions options,
        IKubernetesPodLookup? lookup = null)
        : base(options.CacheDuration)
    {
        if (string.IsNullOrWhiteSpace(options.LabelSelector))
        {
            throw new ArgumentException(
                $"{nameof(KubernetesDiscoveryOptions.LabelSelector)} must be provided so discovery can identify the gossip pods.",
                nameof(options));
        }

        _configuration = configuration;
        _options = options;

        // Only dispose a lookup we created; a caller-supplied one may be shared.
        _ownsLookup = lookup is null;
        _lookup = lookup ?? new KubernetesPodLookup(options);
    }

    /// <inheritdoc />
    protected override async ValueTask<IReadOnlyList<GossNetNodeHostEntry>> ResolveAsync(CancellationToken cancellationToken)
    {
        // Prefer the pod's own namespace so a deployment does not have to be told where
        // it is running.
        var namespaceName = _options.Namespace ?? _lookup.CurrentNamespace ?? FallbackNamespace;
        var port = _options.Port ?? _configuration.Port;

        IReadOnlyList<KubernetesPodInfo> pods;

        try
        {
            pods = await _lookup
                .ListPodsAsync(namespaceName, _options.LabelSelector, _options.FieldSelector, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Surfaced rather than swallowed: an unreachable API server must not look
            // like a network with no other nodes in it.
            throw new NodeDiscoveryException(
                $"Failed to list pods matching '{_options.LabelSelector}' in namespace '{namespaceName}'.", ex);
        }

        return ToNeighbours(pods, port);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Watches pods in the namespace, re-listing on each change. The watch reports one pod
    /// at a time while a neighbour list has to be complete, so the list is what gets
    /// published; the watch only says when to look again.
    /// </para>
    /// <para>
    /// Completes immediately when the lookup does not support watching, which leaves the
    /// node on its normal cached polling.
    /// </para>
    /// </remarks>
    public async IAsyncEnumerable<IReadOnlyList<GossNetNodeHostEntry>> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_lookup is not IWatchablePodLookup watchable)
        {
            yield break;
        }

        var namespaceName = _options.Namespace ?? _lookup.CurrentNamespace ?? FallbackNamespace;
        var port = _options.Port ?? _configuration.Port;

        await foreach (var _ in watchable
            .WatchPodsAsync(namespaceName, _options.LabelSelector, _options.FieldSelector, cancellationToken)
            .ConfigureAwait(false))
        {
            IReadOnlyList<KubernetesPodInfo> pods;

            try
            {
                pods = await _lookup
                    .ListPodsAsync(namespaceName, _options.LabelSelector, _options.FieldSelector, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }
            catch (Exception)
            {
                // The watch signalled but the follow-up list failed. Skipping this signal
                // keeps the last known membership rather than blanking it, and the next
                // change will prompt another attempt.
                continue;
            }

            yield return ToNeighbours(pods, port);
        }
    }

    /// <summary>Maps pods to neighbours, dropping any that cannot be gossiped at.</summary>
    private IReadOnlyList<GossNetNodeHostEntry> ToNeighbours(IReadOnlyList<KubernetesPodInfo> pods, int port)
    {
        var candidates = new List<GossNetNodeHostEntry>(pods.Count);

        foreach (var pod in pods)
        {
            // A pod that is scheduled but has no IP yet, or is not yet Ready, has no
            // listening socket; gossiping at it only produces dropped datagrams.
            if (string.IsNullOrEmpty(pod.PodIp) || (_options.ReadyOnly && !pod.IsReady))
            {
                continue;
            }

            candidates.Add(new GossNetNodeHostEntry { Hostname = pod.PodIp!, Port = port });
        }

        // The pod running this node matches its own label selector.
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
