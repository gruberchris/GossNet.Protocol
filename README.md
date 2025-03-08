# GossNet.Protocol

GossNet (Gossip Network) is a lightweight C# library implementing the [gossip protocol](https://en.wikipedia.org/wiki/Gossip_protocol) pattern for distributed systems. This library enables efficient message propagation across a network of nodes without the need for centralized coordination.

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
            
            // Copy base properties
            Id = message.Id;
            Timestamp = message.Timestamp;
            NotifiedNodes = message.NotifiedNodes;
        }
    }
}
```

2. Create a GossNet Node

Next, create a GossNet node and configure it to send and receive messages:

```csharp
using GossNet.Protocol;

// Create configuration
var config = new GossNetConfiguration
{
    Hostname = "localhost",  // This node's hostname
    Port = 5055,             // This node's port
    Neighbours = new[]       // Other nodes in the network
    {
        new GossNetNodeHostEntry { Hostname = "localhost", Port = 5056 }
    }
};

// Create and start a node
using var node = new GossNetNode<ChatMessage>(config);

// Subscribe to incoming messages
await node.SubscribeAsync((sender, args) => 
{
    var message = args.Message;
    Console.WriteLine($"[{message.Timestamp}] {message.Username}: {message.Content}");
});

// Start the node
node.Start();

// Create configuration
var config2 = new GossNetConfiguration
{
    Hostname = "localhost",  // This node's hostname
    Port = 5056,             // This node's port
    Neighbours = new[]       // Other nodes in the network
    {
        new GossNetNodeHostEntry { Hostname = "localhost", Port = 5055 }
    }
};

// Create and start a second node
using var node2 = new GossNetNode<ChatMessage>(config2);

// Subscribe to incoming messages
await node2.SubscribeAsync((sender, args) => 
{
    var message = args.Message;
    Console.WriteLine($"[{message.Timestamp}] {message.Username}: {message.Content}");
});

// Start the node
node2.Start();

// Send a message
var msg = new ChatMessage 
{
    Username = "Alice",
    Content = "Hello, distributed world!"
};
await node.SendAsync(msg);
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
- Simple subscription model for message handling

## License

GossNet.Protocol is licensed under the MIT License. See [LICENSE](LICENSE) for more information.