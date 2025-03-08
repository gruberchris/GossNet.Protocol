namespace GossNet.Protocol;

public abstract class GossNetMessageBase
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public IEnumerable<GossNetNodeHostEntry> NotifiedNodes { get; set; } = new List<GossNetNodeHostEntry>();
    public abstract string Serialize();
    public abstract void Deserialize(string data);
}