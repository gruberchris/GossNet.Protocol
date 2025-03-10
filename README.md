# GossNet.Protocol

[![NuGet](https://img.shields.io/nuget/v/GossNet.Protocol.svg)](https://www.nuget.org/packages/GossNet.Protocol)

GossNet (Gossip Network) is a lightweight C# library implementing the [gossip protocol](https://en.wikipedia.org/wiki/Gossip_protocol) pattern for distributed systems. This library enables efficient message propagation across a network of nodes without the need for centralized coordination.

GossNet.Protocol uses UDP for message communication, allowing for fast and lightweight message passing. It is designed to be simple to use and integrate into existing applications, providing a scalable and resilient communication mechanism for distributed systems. By default, GossNet.Protocol uses UDP port 5055 but this is configurable.

## What is GossNet?

GossNet.Protocol is an implementation of the gossip protocol, a method for information dissemination in distributed systems. It allows messages to propagate through a network by having each node pass information to a subset of its neighbors, creating an epidemic-style spread of data that's both efficient and resilient to failures.

## Problems It Solves

- Decentralized Communication: Eliminates the need for central servers or message brokers
- Network Resilience: Continues functioning even if some nodes fail or become unreachable
- Scalability: Efficiently distributes messages across large networks with minimal overhead
- Eventually Consistent: Ensures all nodes eventually receive all messages
- Self-Organizing: Requires minimal configuration and adapts to network changes

## Installation

GossNet.Protocol is available as a NuGet package. You can install it using the following command:

```shell
dotnet add package GossNet.Protocol
```

## Usage Example

1. Define Your Message Type

First, create a message class that extends GossNetMessageBase:

```csharp
using GossNet.Protocol;
using System.Text.Json;

public class ChatMessage : GossNetMessageBase
{
    public string Username { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    
    public override string Serialize()
    {
        return JsonSerializer.Serialize(this);
    }
    
    public override void Deserialize(string data)
    {
        var message = JsonSerializer.Deserialize<ChatMessage>(data);
        if (message != null)
        {
            Username = message.Username;
            Content = message.Content;
            
            base.Deserialize(data);
        }
    }
}
```

2. Create a GossNet Node

Next, create a GossNet node and configure it to send and receive messages. You can subscribe to incoming messages using a channel and process them in a background task:

```csharp
using GossNet;
using GossNet.Protocol;
using Microsoft.Extensions.Logging;
using Serilog;

// Create logger
var serilogLogger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateLogger();

// Create Microsoft Extensions Logging factory from Serilog
var loggerFactory = new LoggerFactory().AddSerilog(serilogLogger);

// Create typed logger for GossNetNode<TestMessage>
var logger = loggerFactory.CreateLogger<GossNetNode<ChatMessage>>();

// Create and start nodes
var node1 = new GossNetNode<ChatMessage>(new GossNetConfiguration
{
    Hostname = "localhost",
    NodeDiscovery = NodeDiscovery.StaticList,
    StaticNodes = new List<GossNetNodeHostEntry> 
    { 
        new() { Hostname = "localhost", Port = 9056 }
    }
}, logger);

var node2 = new GossNetNode<ChatMessage>(new GossNetConfiguration
{
    Hostname = "localhost",
    Port = 9056,
    NodeDiscovery = NodeDiscovery.StaticList,
    StaticNodes = new List<GossNetNodeHostEntry> 
    { 
        new() { Hostname = "localhost", Port = 9057 } 
    }
}, logger);

var node3 = new GossNetNode<ChatMessage>(new GossNetConfiguration
{
    Hostname = "localhost",
    Port = 9057,
    NodeDiscovery = NodeDiscovery.StaticList,
    StaticNodes = new List<GossNetNodeHostEntry> 
    { 
        new() { Hostname = "localhost", Port = 9055 }
    }
}, logger);

// Start all nodes first
node1.Start();
node2.Start();
node3.Start();

// Setup subscriptions
_ = Task.Run(async () =>
{
    var reader1 = await node1.SubscribeAsync();
    await foreach (var messageItem in reader1.ReadAllAsync())
    {
        Console.WriteLine($"[{messageItem.Message.Timestamp} on Node 1] {messageItem.Message.Username} : {messageItem.Message.Content}");
    }
});

_ = Task.Run(async () =>
{
    var reader2 = await node2.SubscribeAsync();
    await foreach (var messageItem in reader2.ReadAllAsync())
    {
        Console.WriteLine($"[{messageItem.Message.Timestamp} on Node 2] {messageItem.Message.Username} : {messageItem.Message.Content}");
    }
});

_ = Task.Run(async () =>
{
    var reader3 = await node3.SubscribeAsync();
    await foreach (var messageItem in reader3.ReadAllAsync())
    {
        Console.WriteLine($"[{messageItem.Message.Timestamp} on Node 3] {messageItem.Message.Username} : {messageItem.Message.Content}");
    }
});

// Give some time for nodes to start up and listen for messages
await Task.Delay(1000);

// Send a message from node 1
var message = new ChatMessage
{
    Username = "Alice",
    Content = "Hello, world!"
};

await node1.SendAsync(message);

// Keep the program running to observe messages
Console.WriteLine("Press any key to exit...");
Console.ReadKey();
```

When you run this code, you should see the message "Hello, world!" propagated from node 1 to nodes 2 and 3, with each node printing the message to the console.

```text
[20:40:15 INF] Starting GossNetNode: localhost:9055
[20:40:15 INF] Starting GossNetNode: localhost:9056
[20:40:15 INF] Starting GossNetNode: localhost:9057
[3/10/2025 12:40:16 AM on Node 2] Alice : Hello, world!
[3/10/2025 12:40:16 AM on Node 3] Alice : Hello, world!
Press any key to exit...
```

3. Manage Node Lifecycle

Finally, manage the node's lifecycle by starting and stopping it as needed:

```csharp
await node.StopAsync();
```

## How GossNet.Protocol Works

GossNet nodes use UDP for message communication. When a node receives or sends a message:

1. It marks itself as "notified" in the message metadata
2. It processes the message (invokes subscribers)
3. It forwards the message to all neighbors that haven't been notified yet

This epidemic spreading ensures the message reaches all nodes in the network efficiently, even in case of partial network failures.

## Features

- UDP-based communication for lightweight, fast message passing
- Thread-safe design with proper synchronization
- Automatic handling of duplicate messages
- Custom message types through generic implementation
- Simple subscription model for message handling using .NET channels

## Service Discovery

| Method      | Description                     | Status  |
|-------------|---------------------------------|---------|
| DNS         | Discover nodes using DNS        | Done    |
| Consul      | Discover nodes using Consul     | Planned |
| Kubernetes  | Discover nodes using Kubernetes | Planned |
| Docker      | Discover nodes using Docker     | Planned |
| Static List | Manually configure node list    | Done    |


## License

GossNet.Protocol is licensed under the MIT License. See [LICENSE](LICENSE) for more information.