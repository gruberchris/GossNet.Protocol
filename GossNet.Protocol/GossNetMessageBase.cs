using System.Text.Json;

namespace GossNet.Protocol;

/// <summary>
/// Base class for messages exchanged over a GossNet network.
/// </summary>
public class GossNetMessageBase
{
    /// <summary>
    /// Largest payload that fits in a single IPv4 UDP datagram: 65535 bytes minus the
    /// 20-byte IP header and 8-byte UDP header.
    /// </summary>
    public const int MaxDatagramBytes = 65507;

    /// <summary>Gets the unique id used to de-duplicate this message across the network.</summary>
    public Guid Id { get; internal set; } = Guid.NewGuid();

    /// <summary>Gets the UTC time the message was created.</summary>
    public DateTime Timestamp { get; internal set; } = DateTime.UtcNow;

    /// <summary>Gets the nodes already known to have seen this message.</summary>
    public IReadOnlyCollection<GossNetNodeHostEntry> NotifiedNodes
    {
        get;

        // Normalizes to a List so callers cannot mutate the message through a
        // reference they kept, while avoiding a copy when one is not needed.
        internal set => field = value as List<GossNetNodeHostEntry> ?? [.. value];
    } = new List<GossNetNodeHostEntry>();

    /// <summary>
    /// Serializes the message for transmission.
    /// </summary>
    public virtual string Serialize() => JsonSerializer.Serialize(this, GetType(), SerializeOptions);

    /// <summary>
    /// Restores the base properties from a serialized message.
    /// </summary>
    /// <param name="data">The serialized message.</param>
    /// <exception cref="JsonException">The payload could not be read.</exception>
    /// <remarks>
    /// <strong>Restores only the base properties</strong> — id, timestamp and notified
    /// nodes — while <see cref="Serialize"/> writes the whole derived object. A derived
    /// type MUST override this, call <c>base.Deserialize(data)</c>, and then restore its
    /// own properties; otherwise they are silently lost on every received message.
    /// </remarks>
    public virtual void Deserialize(string data)
    {
        var deserialized = JsonSerializer.Deserialize<BaseProperties>(data, DeserializeOptions)
            ?? throw new JsonException($"Failed to deserialize {GetType().Name}: the payload was null.");

        Id = deserialized.Id;
        Timestamp = deserialized.Timestamp;
        NotifiedNodes = deserialized.NotifiedNodes;
    }

    /// <inheritdoc />
    public override string ToString() => $"Id: {Id}, Timestamp: {Timestamp}, NotifiedNodes: {string.Join(", ", NotifiedNodes)}";

    // WriteIndented was true, which padded every datagram with whitespace purely to
    // make the wire format pretty. Indentation roughly doubled small payloads.
    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        WriteIndented = false
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
