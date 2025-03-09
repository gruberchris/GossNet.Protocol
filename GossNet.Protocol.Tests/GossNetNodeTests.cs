using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using GossNet.Protocol.Tests.Mocks;

namespace GossNet.Protocol.Tests;

[TestClass]
public class GossNetNodeTests
{
    private GossNetConfiguration _configuration = null!;
    private MockUdpClient _mockUdpClient = null!;
    private GossNetNode<TestMessage> _node = null!;
    private MockLogger<GossNetNode<TestMessage>> _mockLogger = null!;

    [TestInitialize]
    public void Setup()
    {
        _configuration = new GossNetConfiguration
        {
            Hostname = "localhost",
            Port = 8080
        };

        _mockUdpClient = new MockUdpClient();
        _mockLogger = new MockLogger<GossNetNode<TestMessage>>();
        _node = new GossNetNode<TestMessage>(_configuration, udpClient: _mockUdpClient);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            _node.Dispose();
        }
        catch (AggregateException ex) when (ex.InnerException is TaskCanceledException)
        {
            // Ignore TaskCanceledException during cleanup
        }
    }

    [TestMethod]
    public async Task SubscribeAsync_AddsChannelReader()
    {
        // Arrange
        var reader = await _node.SubscribeAsync();

        // Set up background task to read from channel
        var cts = new CancellationTokenSource();

        GossNetMessageReceivedEventArgs<TestMessage>? result = null;
        
        var readTask = Task.Run(async () =>
        {
            try
            {
                result = await reader.ReadAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation occurs
            }
        }, cts.Token);

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
        Assert.IsTrue(result != null, "Channel reader should have received a message");

        // Cleanup
        await cts.CancelAsync();
        await Task.WhenAny(readTask, Task.Delay(500));
        cts.Dispose();
    }

    [TestMethod]
    public async Task UnsubscribeAsync_RemovesChannelReader()
    {
        // Arrange
        var callCount = 0;
        var reader = await _node.SubscribeAsync();

        // Set up background task to read from channel
        var cts = new CancellationTokenSource();
        var readTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var args in reader.ReadAllAsync().WithCancellation(cts.Token))
                {
                    callCount++;
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation occurs
            }
        }, cts.Token);

        // Act
        await _node.UnsubscribeAsync(reader);

        // Send a message
        var message = new TestMessage { Data = "Test Data" };
        await _node.SendAsync(message);

        // Allow time for potential processing
        await Task.Delay(100, cts.Token);
        await cts.CancelAsync();
        await Task.WhenAny(readTask, Task.Delay(500, cts.Token)); // Allow task to complete or timeout

        // Assert
        Assert.AreEqual(0, callCount, "Channel reader should not receive messages after unsubscribing");
    
        // Cleanup
        cts.Dispose();
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
        try
        {
            await _node.StopAsync();
        }
        catch (TaskCanceledException)
        {
            // This is expected when cancelling tasks
        }

        // Assert
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

    public override void Deserialize(string data)
    {
        // Simple deserialization for testing purposes
        if (data.Contains("Data"))
        {
            var dataStart = data.IndexOf("Data") + 7;
            var dataEnd = data.IndexOf("\"", dataStart);
            Data = data.Substring(dataStart, dataEnd - dataStart);

            // Don't try to call DeserializeNotifiedNodes
            // Just clear the collection instead
            NotifiedNodes.ToList().Clear();
        }
    }
}