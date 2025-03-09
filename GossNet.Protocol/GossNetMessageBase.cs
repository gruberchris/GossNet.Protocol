using System.Text.Json;

namespace GossNet.Protocol;

public class GossNetMessageBase
{
    private Guid _id = Guid.NewGuid();
    private DateTime _timestamp = DateTime.UtcNow;
    private List<GossNetNodeHostEntry> _notifiedNodes = [];

    public Guid Id 
    { 
        get => _id; 
        internal set => _id = value; 
    }
    
    public DateTime Timestamp 
    { 
        get => _timestamp; 
        internal set => _timestamp = value; 
    }
    
    public IReadOnlyCollection<GossNetNodeHostEntry> NotifiedNodes 
    { 
        get => _notifiedNodes; 
        internal set => _notifiedNodes = value as List<GossNetNodeHostEntry> ?? value.ToList(); 
    }

    public virtual string Serialize()
    {
        return JsonSerializer.Serialize(this, GetType(), SerializeOptions);
    }

    public virtual void Deserialize(string data)
    {
        var deserializedMsg = JsonSerializer.Deserialize<BaseProperties>(data, DeserializeOptions);
    
        if (deserializedMsg == null) 
            throw new JsonException("Failed to deserialize TestMessage");
        
        Id = deserializedMsg.Id;
        Timestamp = deserializedMsg.Timestamp;
        NotifiedNodes = deserializedMsg.NotifiedNodes;
    }
    
    public override string ToString()
    {
        var notifiedNodes = string.Join(", ", NotifiedNodes);
        return $"Id: {Id}, Timestamp: {Timestamp}, NotifiedNodes: { notifiedNodes }";
    }
    
    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        WriteIndented = true
    };
    
    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    
    private sealed class BaseProperties
    {
        public Guid Id { get; set; }
        public DateTime Timestamp { get; set; }
        public List<GossNetNodeHostEntry> NotifiedNodes { get; set; } = [];
    }
}
