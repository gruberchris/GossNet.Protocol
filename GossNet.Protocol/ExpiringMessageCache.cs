using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;

namespace GossNet.Protocol;

/// <summary>
/// Remembers the ids of recently seen messages so a node can drop duplicates.
/// </summary>
/// <typeparam name="T">The gossip message type.</typeparam>
/// <remarks>
/// Only message ids are retained. The cache previously stored whole message objects,
/// which kept every payload a node had seen alive for the full TTL (10 minutes by
/// default) even though nothing ever read them back.
/// </remarks>
public sealed class ExpiringMessageCache<T> : IDisposable where T : GossNetMessageBase
{
    private readonly MemoryCache _cache;
    private readonly TimeSpan _defaultExpiration;

    /// <summary>
    /// Mirrors the live cache keys. Concurrent because eviction callbacks run on
    /// pool threads, independently of callers.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, byte> _keys = new();

    /// <summary>
    /// Makes the "is it present, if not add it" sequence atomic. Without this two
    /// threads could both observe a message id as absent and both report a first
    /// sighting, so the same gossip message would be processed and re-forwarded twice.
    /// </summary>
#if NET9_0_OR_GREATER
    private readonly Lock _gate = new();
#else
    private readonly object _gate = new();
#endif

    private bool _disposed;

    /// <summary>
    /// Initializes the cache.
    /// </summary>
    /// <param name="defaultExpiration">How long an id is remembered. Defaults to five minutes.</param>
    public ExpiringMessageCache(TimeSpan? defaultExpiration = null)
    {
        _cache = new MemoryCache(new MemoryCacheOptions
        {
            ExpirationScanFrequency = TimeSpan.FromMinutes(1)
        });

        _defaultExpiration = defaultExpiration ?? TimeSpan.FromMinutes(5);
    }

    /// <summary>
    /// Records a message id if it has not been seen recently.
    /// </summary>
    /// <param name="message">The message whose id should be recorded.</param>
    /// <returns><c>true</c> if this is the first sighting; <c>false</c> if it is a duplicate.</returns>
    public bool TryAdd(T message) => TryAdd(message.Id);

    /// <summary>
    /// Records a message id if it has not been seen recently.
    /// </summary>
    /// <param name="messageId">The id to record.</param>
    /// <returns><c>true</c> if this is the first sighting; <c>false</c> if it is a duplicate.</returns>
    public bool TryAdd(Guid messageId)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(messageId, out _))
            {
                return false;
            }

            var options = new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = _defaultExpiration
            };

            // Keeps _keys in step with the cache. Previously keys were only pruned
            // inside GetAll(), which nothing in the library called, so the key set
            // grew forever even as the entries behind it expired.
            options.RegisterPostEvictionCallback(static (key, _, _, state) =>
                ((ConcurrentDictionary<Guid, byte>)state!).TryRemove((Guid)key, out _), _keys);

            _cache.Set(messageId, true, options);
            _keys[messageId] = 0;

            return true;
        }
    }

    /// <summary>
    /// Determines whether a message id was seen recently.
    /// </summary>
    /// <param name="messageId">The id to look for.</param>
    public bool Contains(Guid messageId)
    {
        lock (_gate)
        {
            return _cache.TryGetValue(messageId, out _);
        }
    }

    /// <summary>
    /// Gets the number of ids currently remembered.
    /// </summary>
    public int Count => _keys.Count;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // MemoryCache owns a timer; without this every node leaked one.
        _cache.Dispose();
        _keys.Clear();
    }
}
