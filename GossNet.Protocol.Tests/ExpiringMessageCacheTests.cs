namespace GossNet.Protocol.Tests;

[TestClass]
public class ExpiringMessageCacheTests
{
    [TestMethod]
    public void TryAdd_NewMessage_ReturnsTrue()
    {
        // Arrange
        var cache = new ExpiringMessageCache<TestMessage>();
        var id = Guid.NewGuid();
        var message = new TestMessage(id) { Content = "Test" };

        // Act
        var result = cache.TryAdd(message);

        // Assert
        Assert.IsTrue(result);
        Assert.AreEqual(1, cache.Count);
    }

    [TestMethod]
    public void TryAdd_DuplicateMessage_ReturnsFalse()
    {
        // Arrange
        var cache = new ExpiringMessageCache<TestMessage>();
        var messageId = Guid.NewGuid();
        var message1 = new TestMessage(messageId) { Content = "Test 1" };
        var message2 = new TestMessage(messageId) { Content = "Test 2" };

        // Act
        var result1 = cache.TryAdd(message1);
        var result2 = cache.TryAdd(message2);

        // Assert
        Assert.IsTrue(result1);
        Assert.IsFalse(result2);
        Assert.AreEqual(1, cache.Count);
    }

    [TestMethod]
    public void Contains_ExistingMessage_ReturnsTrue()
    {
        // Arrange
        var cache = new ExpiringMessageCache<TestMessage>();
        var messageId = Guid.NewGuid();
        var message = new TestMessage(messageId) { Content = "Test" };
        cache.TryAdd(message);

        // Act
        var result = cache.Contains(messageId);

        // Assert
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void Contains_NonExistingMessage_ReturnsFalse()
    {
        // Arrange
        var cache = new ExpiringMessageCache<TestMessage>();
        var messageId = Guid.NewGuid();

        // Act
        var result = cache.Contains(messageId);

        // Assert
        Assert.IsFalse(result);
    }

    [TestMethod]
    public void TryGetValue_ExistingMessage_ReturnsMessageAndTrue()
    {
        // Arrange
        var cache = new ExpiringMessageCache<TestMessage>();
        var messageId = Guid.NewGuid();
        var message = new TestMessage(messageId) { Content = "Test" };
        cache.TryAdd(message);

        // Act
        var result = cache.TryGetValue(messageId, out var retrievedMessage);

        // Assert
        Assert.IsTrue(result);
        Assert.IsNotNull(retrievedMessage);
        Assert.AreEqual(messageId, retrievedMessage.Id);
        Assert.AreEqual("Test", retrievedMessage.Content);
    }

    [TestMethod]
    public void TryGetValue_NonExistingMessage_ReturnsFalse()
    {
        // Arrange
        var cache = new ExpiringMessageCache<TestMessage>();
        var messageId = Guid.NewGuid();

        // Act
        var result = cache.TryGetValue(messageId, out var retrievedMessage);

        // Assert
        Assert.IsFalse(result);
        Assert.AreEqual(default, retrievedMessage);
    }

    [TestMethod]
    public void GetAll_ReturnsAllMessages()
    {
        // Arrange
        var cache = new ExpiringMessageCache<TestMessage>();
        var message1 = new TestMessage(Guid.NewGuid()) { Content = "Test 1" };
        var message2 = new TestMessage(Guid.NewGuid()) { Content = "Test 2" };
        var message3 = new TestMessage(Guid.NewGuid()) { Content = "Test 3" };

        cache.TryAdd(message1);
        cache.TryAdd(message2);
        cache.TryAdd(message3);

        // Act
        var messages = cache.GetAll().ToList();

        // Assert
        Assert.AreEqual(3, messages.Count);
        CollectionAssert.Contains(messages, message1);
        CollectionAssert.Contains(messages, message2);
        CollectionAssert.Contains(messages, message3);
    }

    [TestMethod]
    public async Task Messages_ExpireAfterTimespan()
    {
        // Arrange
        var expiration = TimeSpan.FromMilliseconds(100);
        var cache = new ExpiringMessageCache<TestMessage>(expiration);
        var messageId = Guid.NewGuid();
        var message = new TestMessage(messageId) { Content = "Test" };

        // Act
        cache.TryAdd(message);
        Assert.IsTrue(cache.Contains(messageId), "Message should be in cache initially");

        // Wait for expiration
        await Task.Delay(expiration + TimeSpan.FromMilliseconds(50));

        // Assert
        Assert.IsFalse(cache.Contains(messageId), "Message should have expired");
        Assert.AreEqual(0, cache.GetAll().Count(), "GetAll should return empty collection after expiration");
    }

    private class TestMessage : GossNetMessageBase
    {
        public string Content { get; set; } = string.Empty;
        
        // Add constructor that accepts an ID
        public TestMessage(Guid? id = null)
        {
            if (id.HasValue)
            {
                SetId(id.Value);
            }
        }
        
        // Add method to set ID using reflection (for testing only)
        private void SetId(Guid id)
        {
            var property = typeof(GossNetMessageBase).GetProperty("Id");
            property?.SetValue(this, id);
        }

        public override void Deserialize(string data)
        {
            // Not needed for tests
        }

        public override string Serialize()
        {
            // Not needed for tests
            return Content;
        }
    }
}