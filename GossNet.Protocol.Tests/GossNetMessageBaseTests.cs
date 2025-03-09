using System.Globalization;

namespace GossNet.Protocol.Tests;

[TestClass]
public sealed class GossNetMessageBaseTests
{
    // Concrete implementation for testing
    private class TestMessage : GossNetMessageBase
    {
        public string Content { get; set; } = string.Empty;
        
        // Helper methods for testing to work around property access restrictions
        public void SetId(Guid id) => 
            typeof(GossNetMessageBase).GetProperty(nameof(Id))?.SetValue(this, id);
        
        public void SetTimestamp(DateTime timestamp) => 
            typeof(GossNetMessageBase).GetProperty(nameof(Timestamp))?.SetValue(this, timestamp);
        
        public void SetNotifiedNodes(IEnumerable<GossNetNodeHostEntry> nodes) => 
            typeof(GossNetMessageBase).GetProperty(nameof(NotifiedNodes))?.SetValue(this, nodes);
        
        public override string Serialize()
        {
            return $"{Id}|{Timestamp:o}|{Content}";
        }
        
        public override void Deserialize(string data)
        {
            var parts = data.Split('|');

            if (parts.Length < 3) return;
            
            SetId(Guid.Parse(parts[0]));
            SetTimestamp(DateTime.Parse(parts[1], CultureInfo.InvariantCulture).ToUniversalTime());
            Content = parts[2];
        }
    }
    
    [TestMethod]
    public void Constructor_SetsDefaultProperties()
    {
        // Arrange & Act
        var message = new TestMessage();
        
        // Assert
        Assert.AreNotEqual(Guid.Empty, message.Id);
        Assert.IsTrue((DateTime.UtcNow - message.Timestamp).TotalSeconds < 5); // Within 5 seconds
        Assert.AreEqual(0, message.NotifiedNodes.Count());
    }
    
    [TestMethod]
    public void Id_SetAndGet_ReturnsExpectedValue()
    {
        // Arrange
        var message = new TestMessage();
        var newId = Guid.NewGuid();
        
        // Act
        message.SetId(newId);
        
        // Assert
        Assert.AreEqual(newId, message.Id);
    }
    
    [TestMethod]
    public void Timestamp_SetAndGet_ReturnsExpectedValue()
    {
        // Arrange
        var message = new TestMessage();
        var newTimestamp = new DateTime(2023, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        
        // Act
        message.SetTimestamp(newTimestamp);
        
        // Assert
        Assert.AreEqual(newTimestamp, message.Timestamp);
    }
    
    [TestMethod]
    public void NotifiedNodes_SetAndGet_ReturnsExpectedValue()
    {
        // Arrange
        var message = new TestMessage();
        var nodes = new List<GossNetNodeHostEntry>
        {
            new() { Hostname = "node1", Port = 8080 },
            new() { Hostname = "node2", Port = 8081 }
        };
        
        // Act
        message.SetNotifiedNodes(nodes);
        
        // Assert
        Assert.AreEqual(2, message.NotifiedNodes.Count());
        CollectionAssert.AreEqual(nodes.ToList(), message.NotifiedNodes.ToList());
    }
    
    [TestMethod]
    public void Serialize_WithDefaultValues_ReturnsExpectedString()
    {
        // Arrange
        var message = new TestMessage();
        message.SetId(Guid.Parse("12345678-1234-1234-1234-123456789012"));
        message.SetTimestamp(new DateTime(2023, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        message.Content = "Test Content";

        // Act
        var result = message.Serialize();

        // Assert
        Assert.AreEqual("12345678-1234-1234-1234-123456789012|2023-01-01T12:00:00.0000000Z|Test Content", result);
    }
    
    [TestMethod]
    public void Deserialize_WithValidString_SetsProperties()
    {
        // Arrange
        var message = new TestMessage();
        var testId = Guid.Parse("12345678-1234-1234-1234-123456789012");
        var testTime = new DateTime(2023, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var formattedDate = testTime.ToString("o"); // ISO 8601 format includes timezone info

        // Act
        message.Deserialize($"{testId}|{formattedDate}|Test Content");

        // Assert
        Assert.AreEqual(testId, message.Id);
        Assert.AreEqual(testTime.ToUniversalTime(), message.Timestamp.ToUniversalTime());
        Assert.AreEqual("Test Content", message.Content);
    }
}