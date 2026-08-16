namespace GossNet.Protocol.Tests;

[TestClass]
public sealed class ExpiringMessageCacheTests
{
    [TestMethod]
    public void TryAdd_NewMessage_ReturnsTrue()
    {
        using var cache = new ExpiringMessageCache<TestMessage>();

        Assert.IsTrue(cache.TryAdd(new TestMessage()));
        Assert.AreEqual(1, cache.Count);
    }

    [TestMethod]
    public void TryAdd_DuplicateId_ReturnsFalse()
    {
        using var cache = new ExpiringMessageCache<TestMessage>();
        var id = Guid.NewGuid();

        Assert.IsTrue(cache.TryAdd(new TestMessage { Id = id }));
        Assert.IsFalse(cache.TryAdd(new TestMessage { Id = id }));
        Assert.AreEqual(1, cache.Count);
    }

    [TestMethod]
    public void Contains_ReflectsWhetherIdWasAdded()
    {
        using var cache = new ExpiringMessageCache<TestMessage>();
        var id = Guid.NewGuid();

        Assert.IsFalse(cache.Contains(id));

        cache.TryAdd(new TestMessage { Id = id });

        Assert.IsTrue(cache.Contains(id));
    }

    [TestMethod]
    public async Task Ids_ExpireAfterTheConfiguredWindow()
    {
        var expiration = TimeSpan.FromMilliseconds(100);
        using var cache = new ExpiringMessageCache<TestMessage>(expiration);
        var id = Guid.NewGuid();

        cache.TryAdd(new TestMessage { Id = id });
        Assert.IsTrue(cache.Contains(id));

        await Task.Delay(expiration + TimeSpan.FromMilliseconds(150));

        Assert.IsFalse(cache.Contains(id), "the id should no longer be remembered");
    }

    /// <summary>
    /// Regression test for the check-then-act race.
    /// </summary>
    /// <remarks>
    /// TryGetValue followed by Set was not atomic, so two threads could both observe
    /// the same id as absent and both report a first sighting — causing the same
    /// gossip message to be processed and re-forwarded more than once.
    /// </remarks>
    [TestMethod]
    public async Task TryAdd_ConcurrentCallsForOneId_SucceedExactlyOnce()
    {
        const int racers = 200;

        using var cache = new ExpiringMessageCache<TestMessage>();
        var id = Guid.NewGuid();

        // RunContinuationsAsynchronously is essential: by default SetResult runs every
        // awaiting continuation inline on the completing thread, so the racers would
        // execute one after another and never actually race.
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new CountdownEvent(racers);
        var successes = 0;

        var tasks = Enumerable.Range(0, racers).Select(_ => Task.Run(async () =>
        {
            ready.Signal();
            await start.Task;

            if (cache.TryAdd(new TestMessage { Id = id }))
            {
                Interlocked.Increment(ref successes);
            }
        })).ToArray();

        // Only release the racers once every one of them is parked on the gate.
        ready.Wait(TimeSpan.FromSeconds(30));
        start.SetResult();
        await Task.WhenAll(tasks);

        Assert.AreEqual(1, successes, "exactly one caller may be told it is the first to see the message");
        Assert.AreEqual(1, cache.Count);
    }

    [TestMethod]
    public async Task TryAdd_ConcurrentCallsForDistinctIds_AllSucceed()
    {
        const int count = 500;

        using var cache = new ExpiringMessageCache<TestMessage>();
        var ids = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();

        // The key set was a plain HashSet mutated without synchronization, which could
        // corrupt it or throw under concurrent use.
        await Task.WhenAll(ids.Select(id => Task.Run(() => cache.TryAdd(new TestMessage { Id = id }))));

        Assert.AreEqual(count, cache.Count);
        Assert.IsTrue(ids.All(cache.Contains));
    }

    /// <summary>
    /// Regression test for the key set growing forever.
    /// </summary>
    /// <remarks>
    /// Keys were only pruned inside GetAll(), which nothing in the library ever called,
    /// so the set grew without bound even as the cache entries behind it expired —
    /// defeating the point of an expiring cache.
    /// </remarks>
    [TestMethod]
    public async Task ExpiredIds_AreRemovedFromTheKeySet()
    {
        var expiration = TimeSpan.FromMilliseconds(100);
        using var cache = new ExpiringMessageCache<TestMessage>(expiration);

        var ids = Enumerable.Range(0, 50).Select(_ => Guid.NewGuid()).ToArray();

        foreach (var id in ids)
        {
            cache.TryAdd(new TestMessage { Id = id });
        }

        Assert.AreEqual(50, cache.Count);

        await Task.Delay(expiration + TimeSpan.FromMilliseconds(150));

        // Touching the entries makes MemoryCache evict them and fire the callback that
        // keeps the key set in step. Those callbacks are dispatched to the thread pool,
        // so the pruning guarantee is "eventually", not "by the time Contains returns".
        foreach (var id in ids)
        {
            cache.Contains(id);
        }

        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (cache.Count > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.AreEqual(0, cache.Count, "the key set must shrink as entries expire");
    }

    [TestMethod]
    public void Dispose_IsIdempotent()
    {
        var cache = new ExpiringMessageCache<TestMessage>();
        cache.TryAdd(new TestMessage());

        cache.Dispose();
        cache.Dispose();
    }

    private sealed class TestMessage : GossNetMessageBase;
}
