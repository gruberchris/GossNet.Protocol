using Consul;
using GossNet.Discovery.Consul;
using GossNet.Protocol;
using Testcontainers.Consul;

namespace GossNet.Discovery.IntegrationTests;

/// <summary>
/// Exercises Consul blocking queries against a real agent.
/// </summary>
/// <remarks>
/// The correctness of a blocking-query watch lives entirely in how the <c>X-Consul-Index</c>
/// is handled across requests, and that is server behaviour. A hand-written fake would return
/// whatever it was told to and pass whether or not the real rules were respected, so these
/// tests run Consul in a container instead.
/// </remarks>
[TestClass]
public sealed class ConsulWatchTests
{
    private const string ServiceName = "gossnet";

    private static ConsulContainer? _consul;
    private static string? _skipReason;

    [ClassInitialize]
    public static async Task StartConsulAsync(TestContext context)
    {
        try
        {
            _consul = new ConsulBuilder().WithImage("hashicorp/consul:1.20").Build();

            await _consul.StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A developer machine without Docker should skip rather than fail; CI has it.
            _skipReason = $"Consul container unavailable: {ex.Message}";
            _consul = null;
        }
    }

    [ClassCleanup]
    public static async Task StopConsulAsync()
    {
        if (_consul is not null)
        {
            await _consul.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static Uri AgentAddress => new(_consul!.GetBaseAddress());

    [TestInitialize]
    public void SkipWithoutDocker()
    {
        if (_skipReason is not null)
        {
            Assert.Inconclusive(_skipReason);
        }
    }

    private static ConsulNodeDiscovery Discovery(string selfHost = "10.0.0.1", int selfPort = 9055) =>
        new(
            new GossNetConfiguration { Hostname = selfHost, Port = selfPort },
            new ConsulDiscoveryOptions
            {
                ServiceName = ServiceName,
                Address = AgentAddress,
                // Short so an unchanged result comes back promptly inside a test.
                WatchWaitTime = TimeSpan.FromSeconds(2),
                WatchRetryDelay = TimeSpan.FromMilliseconds(200)
            });

    private static async Task RegisterAsync(string id, string address, int port)
    {
        using var client = new ConsulClient(configuration => configuration.Address = AgentAddress);

        await client.Agent.ServiceRegister(new AgentServiceRegistration
        {
            ID = id,
            Name = ServiceName,
            Address = address,
            Port = port
        }).ConfigureAwait(false);
    }

    private static async Task DeregisterAsync(string id)
    {
        using var client = new ConsulClient(configuration => configuration.Address = AgentAddress);

        await client.Agent.ServiceDeregister(id).ConfigureAwait(false);
    }

    /// <summary>Collects watch updates in the background so the test can act between them.</summary>
    private static (Task Reader, List<IReadOnlyList<GossNetNodeHostEntry>> Updates) Collect(
        ConsulNodeDiscovery discovery,
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

    private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs = 15_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(100).ConfigureAwait(false);
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
    public async Task Poll_ReadsRegisteredInstances()
    {
        await RegisterAsync("poll-a", "10.0.0.2", 9055);

        using var discovery = Discovery();

        var neighbours = await discovery.GetNeighboursAsync();

        CollectionAssert.Contains(neighbours.Select(n => n.ToString()).ToArray(), "10.0.0.2:9055");

        await DeregisterAsync("poll-a");
    }

    /// <summary>
    /// The point of the whole feature: a registration must reach the watcher in about a
    /// round trip, not after the cache expires.
    /// </summary>
    [TestMethod]
    public async Task Watch_PublishesBaselineThenReactsToARegistration()
    {
        await RegisterAsync("watch-a", "10.0.1.2", 9055);

        using var discovery = Discovery();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));

        var (reader, updates) = Collect(discovery, cts.Token);

        Assert.IsTrue(await WaitForAsync(() => Count(updates) >= 1), "The baseline membership was never published.");

        lock (updates)
        {
            CollectionAssert.Contains(updates[0].Select(n => n.ToString()).ToArray(), "10.0.1.2:9055");
        }

        await RegisterAsync("watch-b", "10.0.1.3", 9055);

        Assert.IsTrue(
            await WaitForAsync(() => Count(updates) >= 2),
            "The blocking query did not wake up when a service was registered.");

        lock (updates)
        {
            var latest = updates[^1].Select(n => n.ToString()).ToArray();

            CollectionAssert.Contains(latest, "10.0.1.2:9055");
            CollectionAssert.Contains(latest, "10.0.1.3:9055");
        }

        await cts.CancelAsync();
        await reader;

        await DeregisterAsync("watch-a");
        await DeregisterAsync("watch-b");
    }

    [TestMethod]
    public async Task Watch_ReactsToADeregistration()
    {
        await RegisterAsync("drop-a", "10.0.2.2", 9055);
        await RegisterAsync("drop-b", "10.0.2.3", 9055);

        using var discovery = Discovery();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));

        var (reader, updates) = Collect(discovery, cts.Token);

        Assert.IsTrue(await WaitForAsync(() => Count(updates) >= 1));

        await DeregisterAsync("drop-b");

        Assert.IsTrue(
            await WaitForAsync(() =>
            {
                lock (updates)
                {
                    return updates.Any(u => !u.Select(n => n.ToString()).Contains("10.0.2.3:9055"));
                }
            }),
            "The watch never reported the deregistration.");

        await cts.CancelAsync();
        await reader;

        await DeregisterAsync("drop-a");
    }

    /// <summary>
    /// An expired wait returns the same index, which means "nothing changed" — not a change.
    /// Yielding on it would republish identical membership every wait period.
    /// </summary>
    [TestMethod]
    public async Task Watch_DoesNotRepublishWhenTheWaitElapses()
    {
        await RegisterAsync("quiet-a", "10.0.3.2", 9055);

        using var discovery = Discovery();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var (reader, updates) = Collect(discovery, cts.Token);

        Assert.IsTrue(await WaitForAsync(() => Count(updates) >= 1));

        // WatchWaitTime is 2s, so several waits elapse here with nothing changing.
        await Task.Delay(TimeSpan.FromSeconds(8));

        Assert.AreEqual(1, Count(updates), "An elapsed wait must not be mistaken for a change.");

        await cts.CancelAsync();
        await reader;

        await DeregisterAsync("quiet-a");
    }

    [TestMethod]
    public async Task Watch_ExcludesTheNodeItself()
    {
        await RegisterAsync("self", "10.0.4.1", 9055);
        await RegisterAsync("other", "10.0.4.2", 9055);

        using var discovery = Discovery("10.0.4.1", 9055);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var (reader, updates) = Collect(discovery, cts.Token);

        Assert.IsTrue(await WaitForAsync(() => Count(updates) >= 1));

        lock (updates)
        {
            var names = updates[0].Select(n => n.ToString()).ToArray();

            CollectionAssert.DoesNotContain(names, "10.0.4.1:9055");
            CollectionAssert.Contains(names, "10.0.4.2:9055");
        }

        await cts.CancelAsync();
        await reader;

        await DeregisterAsync("self");
        await DeregisterAsync("other");
    }

    [TestMethod]
    public async Task Watch_StopsPromptlyOnCancellation()
    {
        using var discovery = Discovery();
        using var cts = new CancellationTokenSource();

        var (reader, _) = Collect(discovery, cts.Token);

        await Task.Delay(500);
        await cts.CancelAsync();

        // A blocking query parked for WatchWaitTime must still unwind on cancellation.
        var finished = await Task.WhenAny(reader, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.AreSame(reader, finished, "The watch did not unwind when cancelled.");
    }
}
