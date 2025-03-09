using Microsoft.Extensions.Caching.Memory;

namespace GossNet.Protocol;

public class ExpiringMessageCache<T> where T : GossNetMessageBase
{
    private readonly MemoryCache _cache;
    private readonly TimeSpan _defaultExpiration;
    private readonly HashSet<string> _keys = [];

    public ExpiringMessageCache(TimeSpan? defaultExpiration = null)
    {
        var options = new MemoryCacheOptions
        {
            ExpirationScanFrequency = TimeSpan.FromMinutes(1)
        };
        _cache = new MemoryCache(options);
        _defaultExpiration = defaultExpiration ?? TimeSpan.FromMinutes(5);
    }

    public bool TryAdd(T message)
    {
        var key = message.Id.ToString();
        
        if (_cache.TryGetValue(key, out _))
        {
            // Message already exists
            return false;
        }

        _cache.Set(key, message, _defaultExpiration);
        _keys.Add(key);
        
        return true;
    }

    public bool Contains(Guid messageId)
    {
        return _cache.TryGetValue(messageId.ToString(), out _);
    }

    public bool TryGetValue(Guid messageId, out T message)
    {
        var result = _cache.TryGetValue(messageId.ToString(), out var value);
        
        message = result ? (T)value! : default!;
        
        return result;
    }

    public IEnumerable<T> GetAll()
    {
        var result = new List<T>();
        
        foreach (var key in _keys.ToList())
        {
            if (_cache.TryGetValue(key, out var value) && value is T typedValue)
            {
                result.Add(typedValue);
            }
            else
            {
                _keys.Remove(key);
            }
        }
        
        return result;
    }

    public int Count => _cache.Count;
}