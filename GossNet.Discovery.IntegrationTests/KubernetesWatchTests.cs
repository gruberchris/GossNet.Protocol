using GossNet.Discovery.Kubernetes;
using GossNet.Protocol;
using k8s;
using k8s.Models;
using Testcontainers.K3s;

namespace GossNet.Discovery.IntegrationTests;

/// <summary>
/// Exercises the Kubernetes pod watch against a real API server.
/// </summary>
/// <remarks>
/// Watch correctness is <c>resourceVersion</c> handling — resuming from the right version,
/// and recovering when the server ends the watch or rejects the version outright. That is
/// server behaviour, so a hand-written fake could only assert it agrees with the code calling
/// it. These tests run k3s in a container.
/// </remarks>
[TestClass]
public sealed class KubernetesWatchTests
{
    private const string LabelKey = "app";
    private const string LabelValue = "gossnet";
    private const string Selector = $"{LabelKey}={LabelValue}";

    private static K3sContainer? _k3s;
    private static string? _kubeconfigPath;
    private static string? _skipReason;

    [ClassInitialize]
    public static async Task StartClusterAsync(TestContext context)
    {
        try
        {
            // Image in the constructor, not WithImage: Testcontainers 4.14 obsoleted the
            // parameterless overload.
            _k3s = new K3sBuilder("rancher/k3s:v1.31.2-k3s1").Build();

            await _k3s.StartAsync().ConfigureAwait(false);

            // The lookup takes a kubeconfig path, so the container's config is written out.
            _kubeconfigPath = Path.Combine(Path.GetTempPath(), $"gossnet-k3s-{Guid.NewGuid():N}.yaml");

            await File.WriteAllTextAsync(_kubeconfigPath, await _k3s.GetKubeconfigAsync().ConfigureAwait(false))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A developer machine without Docker should skip rather than fail; CI has it.
            _skipReason = $"k3s container unavailable: {ex.Message}";
            _k3s = null;
        }
    }

    [ClassCleanup]
    public static async Task StopClusterAsync()
    {
        if (_kubeconfigPath is not null && File.Exists(_kubeconfigPath))
        {
            File.Delete(_kubeconfigPath);
        }

        if (_k3s is not null)
        {
            await _k3s.DisposeAsync().ConfigureAwait(false);
        }
    }

    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A namespace per test. Pod deletion is asynchronous, so a pod another test removed can
    /// still be terminating — and still firing matching watch events — while this one runs.
    /// Sharing one namespace made those events indistinguishable from the test's own.
    /// </summary>
    private string Namespace => TestContext.TestName!.Replace('_', '-').ToLowerInvariant();

    [TestInitialize]
    public async Task CreateNamespaceAsync()
    {
        if (_skipReason is not null)
        {
            Assert.Inconclusive(_skipReason);
        }

        using var client = NewClient();

        await client.CoreV1.CreateNamespaceAsync(
            new V1Namespace { Metadata = new V1ObjectMeta { Name = Namespace } }).ConfigureAwait(false);
    }

    /// <summary>Drops the whole namespace whatever the outcome, taking its pods with it.</summary>
    [TestCleanup]
    public async Task DeleteNamespaceAsync()
    {
        if (_k3s is null || _skipReason is not null)
        {
            return;
        }

        try
        {
            using var client = NewClient();

            await client.CoreV1.DeleteNamespaceAsync(Namespace).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort: the cluster is torn down with the container anyway.
        }
    }

    private static k8s.Kubernetes NewClient() =>
        new(KubernetesClientConfiguration.BuildConfigFromConfigFile(_kubeconfigPath));

    private KubernetesNodeDiscovery Discovery(string selfHost = "10.42.0.1", int selfPort = 9055) =>
        new(
            new GossNetConfiguration { Hostname = selfHost, Port = selfPort },
            new KubernetesDiscoveryOptions
            {
                LabelSelector = Selector,
                Namespace = Namespace,
                KubeConfigPath = _kubeconfigPath,
                // Pods here never become Ready — nothing pulls an image — and readiness is
                // not what these tests are about.
                ReadyOnly = false,
                CacheDuration = TimeSpan.Zero
            });

    /// <summary>
    /// Creates a pod carrying the discovery label. It is never scheduled to completion, which
    /// is fine: creating and deleting it moves the namespace's resourceVersion, which is what
    /// the watch reacts to.
    /// </summary>
    private async Task CreatePodAsync(string name)
    {
        using var client = NewClient();

        await client.CoreV1.CreateNamespacedPodAsync(
            new V1Pod
            {
                Metadata = new V1ObjectMeta
                {
                    Name = name,
                    Labels = new Dictionary<string, string> { [LabelKey] = LabelValue }
                },
                Spec = new V1PodSpec
                {
                    Containers = [new V1Container { Name = "pause", Image = "registry.k8s.io/pause:3.9" }]
                }
            },
            Namespace).ConfigureAwait(false);
    }

    private async Task DeletePodAsync(string name)
    {
        using var client = NewClient();

        await client.CoreV1.DeleteNamespacedPodAsync(name, Namespace).ConfigureAwait(false);
    }

    private static (Task Reader, List<IReadOnlyList<GossNetNodeHostEntry>> Updates) Collect(
        KubernetesNodeDiscovery discovery,
        CancellationToken cancellationToken)
    {
        var updates = new List<IReadOnlyList<GossNetNodeHostEntry>>();

        var reader = Task.Run(async () =>
        {
            try
            {
                await foreach (var update in discovery.WatchAsync(cancellationToken).ConfigureAwait(false))
                {
                    lock (updates)
                    {
                        updates.Add(update);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when the test finishes.
            }
        });

        return (reader, updates);
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs = 30_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(200).ConfigureAwait(false);
        }

        return condition();
    }

    private static int Count(List<IReadOnlyList<GossNetNodeHostEntry>> updates)
    {
        lock (updates)
        {
            return updates.Count;
        }
    }

    [TestMethod]
    public async Task Poll_ListsPodsMatchingTheSelector()
    {
        await CreatePodAsync("poll-a");

        using var discovery = Discovery();

        // Proves the label selector and namespace reach a real API server; the pod has no IP
        // yet, so the neighbour list may legitimately be empty.
        var neighbours = await discovery.GetNeighboursAsync();

        Assert.IsNotNull(neighbours);
    }

    /// <summary>The baseline must arrive without waiting for something to change.</summary>
    [TestMethod]
    public async Task Watch_PublishesCurrentStateImmediately()
    {
        using var discovery = Discovery();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var (reader, updates) = Collect(discovery, cts.Token);

        Assert.IsTrue(
            await WaitForAsync(() => Count(updates) >= 1),
            "The watch never published the current membership.");

        await cts.CancelAsync();
        await reader;
    }

    /// <summary>The point of the feature: a pod appearing must wake the watch.</summary>
    [TestMethod]
    public async Task Watch_ReactsToAPodBeingCreated()
    {
        using var discovery = Discovery();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var (reader, updates) = Collect(discovery, cts.Token);

        Assert.IsTrue(await WaitForAsync(() => Count(updates) >= 1), "No baseline was published.");

        var baseline = Count(updates);

        await CreatePodAsync("watch-created");

        Assert.IsTrue(
            await WaitForAsync(() => Count(updates) > baseline),
            "The watch did not fire when a matching pod was created.");

        await cts.CancelAsync();
        await reader;
    }

    [TestMethod]
    public async Task Watch_ReactsToAPodBeingDeleted()
    {
        await CreatePodAsync("watch-deleted");

        using var discovery = Discovery();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var (reader, updates) = Collect(discovery, cts.Token);

        Assert.IsTrue(await WaitForAsync(() => Count(updates) >= 1), "No baseline was published.");

        var baseline = Count(updates);

        await DeletePodAsync("watch-deleted");

        Assert.IsTrue(
            await WaitForAsync(() => Count(updates) > baseline),
            "The watch did not fire when a matching pod was deleted.");

        await cts.CancelAsync();
        await reader;
    }

    /// <summary>A pod outside the selector must not wake the watch.</summary>
    [TestMethod]
    public async Task Watch_IgnoresPodsOutsideTheSelector()
    {
        using var discovery = Discovery();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var (reader, updates) = Collect(discovery, cts.Token);

        Assert.IsTrue(await WaitForAsync(() => Count(updates) >= 1));

        var baseline = Count(updates);

        using (var client = NewClient())
        {
            await client.CoreV1.CreateNamespacedPodAsync(
                new V1Pod
                {
                    Metadata = new V1ObjectMeta
                    {
                        Name = "unrelated",
                        Labels = new Dictionary<string, string> { ["app"] = "something-else" }
                    },
                    Spec = new V1PodSpec
                    {
                        Containers = [new V1Container { Name = "pause", Image = "registry.k8s.io/pause:3.9" }]
                    }
                },
                Namespace).ConfigureAwait(false);
        }

        await Task.Delay(TimeSpan.FromSeconds(5));

        Assert.AreEqual(baseline, Count(updates), "A pod outside the label selector should be invisible to the watch.");

        await cts.CancelAsync();
        await reader;
    }

    [TestMethod]
    public async Task Watch_StopsPromptlyOnCancellation()
    {
        using var discovery = Discovery();
        using var cts = new CancellationTokenSource();

        var (reader, updates) = Collect(discovery, cts.Token);

        Assert.IsTrue(await WaitForAsync(() => Count(updates) >= 1));

        await cts.CancelAsync();

        // A watch parked on a long-poll must still unwind when cancelled.
        var finished = await Task.WhenAny(reader, Task.Delay(TimeSpan.FromSeconds(15)));

        Assert.AreSame(reader, finished, "The watch did not unwind when cancelled.");
    }
}
