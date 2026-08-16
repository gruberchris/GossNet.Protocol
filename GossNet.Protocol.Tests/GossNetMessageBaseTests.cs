using System.Text;
using System.Text.Json;

namespace GossNet.Protocol.Tests;

[TestClass]
public sealed class GossNetMessageBaseTests
{
    // Internal setters are reachable via InternalsVisibleTo, so these tests no longer
    // poke at properties through reflection.
    private sealed class ChatMessage : GossNetMessageBase
    {
        public string Content { get; set; } = string.Empty;

        public override void Deserialize(string data)
        {
            base.Deserialize(data);

            var parsed = JsonSerializer.Deserialize<ChatMessage>(data);

            if (parsed is not null)
            {
                Content = parsed.Content;
            }
        }
    }

    [TestMethod]
    public void Constructor_SetsDefaults()
    {
        var message = new ChatMessage();

        Assert.AreNotEqual(Guid.Empty, message.Id);
        Assert.IsTrue((DateTime.UtcNow - message.Timestamp).TotalSeconds < 5);
        Assert.AreEqual(0, message.NotifiedNodes.Count);
    }

    [TestMethod]
    public void NotifiedNodes_CanBeAssigned()
    {
        var message = new ChatMessage();
        GossNetNodeHostEntry[] nodes =
        [
            new() { Hostname = "node1", Port = 8080 },
            new() { Hostname = "node2", Port = 8081 }
        ];

        message.NotifiedNodes = nodes;

        CollectionAssert.AreEqual(nodes, message.NotifiedNodes.ToArray());
    }

    [TestMethod]
    public void SerializeThenDeserialize_RoundTripsEveryBaseProperty()
    {
        var original = new ChatMessage { Content = "hello" };
        original.NotifiedNodes = [new GossNetNodeHostEntry { Hostname = "node1", Port = 8080 }];

        var restored = new ChatMessage();
        restored.Deserialize(original.Serialize());

        Assert.AreEqual(original.Id, restored.Id);
        Assert.AreEqual(original.Timestamp, restored.Timestamp);
        Assert.AreEqual("hello", restored.Content);
        Assert.AreEqual(1, restored.NotifiedNodes.Count);
        Assert.AreEqual(original.NotifiedNodes.First(), restored.NotifiedNodes.First());
    }

    /// <summary>
    /// The wire format used WriteIndented, padding every datagram with whitespace.
    /// </summary>
    [TestMethod]
    public void Serialize_ProducesCompactJson()
    {
        var message = new ChatMessage { Content = "hello" };

        var json = message.Serialize();

        Assert.IsFalse(json.Contains('\n'), "the wire format must not contain newlines");
        Assert.IsFalse(json.Contains("  "), "the wire format must not contain indentation");
    }

    [TestMethod]
    public void Serialize_IsSmallerWithoutIndentation()
    {
        var message = new ChatMessage { Content = "hello" };
        message.NotifiedNodes =
        [
            new GossNetNodeHostEntry { Hostname = "node1", Port = 8080 },
            new GossNetNodeHostEntry { Hostname = "node2", Port = 8081 }
        ];

        var compact = Encoding.UTF8.GetByteCount(message.Serialize());
        var indented = Encoding.UTF8.GetByteCount(
            JsonSerializer.Serialize(message, message.GetType(), new JsonSerializerOptions { WriteIndented = true }));

        Assert.IsTrue(compact < indented, $"compact ({compact} bytes) should be smaller than indented ({indented} bytes)");
    }

    [TestMethod]
    public void Deserialize_NullPayload_ThrowsWithTheActualTypeName()
    {
        var message = new ChatMessage();

        // The message used to name "TestMessage" regardless of the real type.
        var exception = Assert.ThrowsExactly<JsonException>(() => message.Deserialize("null"));

        StringAssert.Contains(exception.Message, nameof(ChatMessage));
    }

    [TestMethod]
    public void Deserialize_MalformedPayload_Throws()
    {
        var message = new ChatMessage();

        Assert.ThrowsExactly<JsonException>(() => message.Deserialize("{ not json"));
    }
}
