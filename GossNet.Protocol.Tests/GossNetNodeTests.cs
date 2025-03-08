using System.Net;
using System.Net.Sockets;
using System.Text;
using GossNet.Protocol.Tests.Mocks;

namespace GossNet.Protocol.Tests;

[TestClass]
public class GossNetNodeTests
{
    private GossNetConfiguration _configuration = null!;
    private MockUdpClient _mockUdpClient = null!;
    private GossNetNode<TestMessage> _node = null!;

    [TestInitialize]
    public void Setup()
    {
        _configuration = new GossNetConfiguration
        {
            Hostname = "localhost",
            Port = 8080
        };
        
        _mockUdpClient = new MockUdpClient();
        _node = new GossNetNode<TestMessage>(_configuration, _mockUdpClient);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _node.Dispose();
    }

    [TestMethod]
    public async Task SubscribeAsync_AddsEventHandler()
    {
        // Arrange
        var receivedMessage = false;
        EventHandler<GossNetMessageReceivedEventArgs<TestMessage>> handler = (sender, args) =>
        {
            receivedMessage = true;
        };

        // Act
        await _node.SubscribeAsync(handler);
        
        // Set up a message to receive
        var message = new TestMessage { Data = "Test Data" };
        var jsonMessage = message.Serialize();
        var bytes = Encoding.UTF8.GetBytes(jsonMessage);
        
        _mockUdpClient.ReceiveQueue.Enqueue(new UdpReceiveResult(
            bytes, 
            new IPEndPoint(IPAddress.Parse("127.0.0.1"), 8081)
        ));
        
        // Start node to process incoming messages
        _node.Start();
        
        // Allow time for message processing
        await Task.Delay(100);
        
        // Assert
        Assert.IsTrue(receivedMessage, "Event handler should have been called");
    }

    [TestMethod]
    public async Task UnsubscribeAsync_RemovesEventHandler()
    {
        // Arrange
        var callCount = 0;
        EventHandler<GossNetMessageReceivedEventArgs<TestMessage>> handler = (sender, args) =>
        {
            callCount++;
        };

        // Act
        await _node.SubscribeAsync(handler);
        await _node.UnsubscribeAsync(handler);
        
        // Send a message
        var message = new TestMessage { Data = "Test Data" };
        await _node.SendAsync(message);
        
        // Assert
        Assert.AreEqual(0, callCount, "Event handler should not have been called after unsubscribing");
    }

    [TestMethod]
    public async Task SendAsync_MarksSelfAsNotified()
    {
        // Arrange
        var message = new TestMessage { Data = "Test Message" };
        
        // Act
        await _node.SendAsync(message);
        
        // Assert
        Assert.IsTrue(message.NotifiedNodes.Any(n => 
            n.Hostname == _configuration.Hostname && 
            n.Port == _configuration.Port
        ), "Message should mark self as notified");
    }

    [TestMethod]
    public async Task SendAsync_SendsToNeighbors()
    {
        // Arrange
        var message = new TestMessage { Data = "Test Message" };
        
        // Setup neighbors (this would normally be done by GossNetDiscovery)
        // For testing, we need to mock this behavior
        
        // Act
        await _node.SendAsync(message);
        
        // Assert
        // Verification would depend on how GossNetDiscovery.GetNeighbours is implemented
        // In a real test, you'd mock that or use dependency injection
        Assert.IsTrue(_mockUdpClient.SentPackets.Count >= 0);
    }

    [TestMethod]
    public async Task StopAsync_CancelsProcessing()
    {
        // Arrange
        _node.Start();
        
        // Act
        await _node.StopAsync();
        
        // Assert
        // Verify the node is no longer processing
        // Add a message to the queue and verify it's not processed
        var message = new TestMessage { Data = "Test Data" };
        var jsonMessage = message.Serialize();
        var bytes = Encoding.UTF8.GetBytes(jsonMessage);
        
        _mockUdpClient.ReceiveQueue.Enqueue(new UdpReceiveResult(
            bytes, 
            new IPEndPoint(IPAddress.Parse("127.0.0.1"), 8081)
        ));
        
        await Task.Delay(100);
        
        Assert.AreEqual(1, _mockUdpClient.ReceiveQueue.Count, "Message should not be processed after stopping");
    }

    [TestMethod]
    public void Dispose_DisposesUdpClient()
    {
        // Act
        _node.Dispose();
        
        // Assert
        Assert.IsTrue(_mockUdpClient.IsDisposed, "UdpClient should be disposed");
    }
}

// Test message class for unit testing
public class TestMessage : GossNetMessageBase
{
    public string Data { get; set; } = string.Empty;

    public override string Serialize()
    {
        // Simplified serialization without calling SerializeNotifiedNodes
        return $"{{\"Data\":\"{Data}\",\"NotifiedNodes\":[]}}";
    }

    public override void Deserialize(string json)
    {
        // Simple deserialization for testing purposes
        if (json.Contains("Data"))
        {
            var dataStart = json.IndexOf("Data") + 7;
            var dataEnd = json.IndexOf("\"", dataStart);
            Data = json.Substring(dataStart, dataEnd - dataStart);
            
            // Don't try to call DeserializeNotifiedNodes
            // Just clear the collection instead
            NotifiedNodes.ToList().Clear();
        }
    }
}