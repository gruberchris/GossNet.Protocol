# GossNet.Protocol

[![NuGet](https://img.shields.io/nuget/v/GossNet.Protocol.svg)](https://www.nuget.org/packages/GossNet.Protocol)

GossNet (Gossip Network) is a lightweight C# library implementing the [gossip protocol](https://en.wikipedia.org/wiki/Gossip_protocol) pattern for distributed systems. This library enables efficient message propagation across a network of nodes without the need for centralized coordination.

GossNet.Protocol uses UDP for message communication, allowing for fast and lightweight message passing. It is designed to be simple to use and integrate into existing applications, providing a scalable and resilient communication mechanism for distributed systems. By default, GossNet.Protocol uses UDP port 9055 but this is configurable.

## What is GossNet?

GossNet.Protocol is an implementation of the gossip protocol, a method for information dissemination in distributed systems. It allows messages to propagate through a network by having each node pass information to a subset of its neighbors, creating an epidemic-style spread of data that's both efficient and resilient to failures.

## Problems It Solves

- Decentralized Communication: Eliminates the need for central servers or message brokers
- Network Resilience: Continues functioning even if some nodes fail or become unreachable
- Scalability: Efficiently distributes messages across large networks with minimal overhead
- Eventually Consistent: Ensures all nodes eventually receive all messages
- Self-Organizing: Requires minimal configuration and adapts to network changes

## Supported frameworks

`GossNet.Protocol` targets `netstandard2.0`, `net8.0` and `net10.0`, so it runs on
.NET Framework 4.6.2+, .NET 8 (LTS) and .NET 10 (LTS). The library is built with the
.NET 10 SDK, but consumers are not required to use it.

## Installation

```shell
dotnet add package GossNet.Protocol
```

## Usage Example

### 1. Define your message type

Create a message class that extends `GossNetMessageBase`. Call `base.Deserialize` so the
protocol's own metadata — message id, timestamp and the list of already-notified nodes —
is restored along with your fields:

```csharp
using GossNet.Protocol;
using System.Text.Json;

public class ChatMessage : GossNetMessageBase
{
    public string Username { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;

    public override void Deserialize(string data)
    {
        base.Deserialize(data);

        var message = JsonSerializer.Deserialize<ChatMessage>(data);

        if (message is not null)
        {
            Username = message.Username;
            Content = message.Content;
        }
    }
}
```

The default `Serialize` already includes your properties, so overriding it is optional.

### 2. Create and start nodes

```csharp
using GossNet.Protocol;
using Microsoft.Extensions.Logging;
using Serilog;

var serilogLogger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateLogger();

var loggerFactory = new LoggerFactory().AddSerilog(serilogLogger);
var logger = loggerFactory.CreateLogger<GossNetNode<ChatMessage>>();

var node1 = new GossNetNode<ChatMessage>(new GossNetConfiguration
{
    Hostname = "localhost",
    Port = 9055,
    NodeDiscovery = NodeDiscovery.StaticList,
    StaticNodes = [new GossNetNodeHostEntry { Hostname = "localhost", Port = 9056 }]
}, logger);

var node2 = new GossNetNode<ChatMessage>(new GossNetConfiguration
{
    Hostname = "localhost",
    Port = 9056,
    NodeDiscovery = NodeDiscovery.StaticList,
    StaticNodes = [new GossNetNodeHostEntry { Hostname = "localhost", Port = 9057 }]
}, logger);

var node3 = new GossNetNode<ChatMessage>(new GossNetConfiguration
{
    Hostname = "localhost",
    Port = 9057,
    NodeDiscovery = NodeDiscovery.StaticList,
    StaticNodes = [new GossNetNodeHostEntry { Hostname = "localhost", Port = 9055 }]
}, logger);

node1.Start();
node2.Start();
node3.Start();
```

### 3. Subscribe

Each call to `Subscribe()` returns an independent subscription with its own queue, so
every subscriber receives every message. Dispose the subscription to stop receiving;
that also completes the reader, ending any `await foreach` loop over it:

```csharp
void Listen(GossNetNode<ChatMessage> node, string label) => _ = Task.Run(async () =>
{
    using var subscription = node.Subscribe();

    await foreach (var item in subscription.Reader.ReadAllAsync())
    {
        Console.WriteLine($"[{item.Message.Timestamp} on {label}] {item.Message.Username} : {item.Message.Content}");
    }
});

Listen(node1, "Node 1");
Listen(node2, "Node 2");
Listen(node3, "Node 3");
```

### 4. Send a message

```csharp
await node1.SendAsync(new ChatMessage { Username = "Alice", Content = "Hello, world!" });

Console.WriteLine("Press any key to exit...");
Console.ReadKey();
```

You should see the message propagate from node 1 to nodes 2 and 3:

```text
[20:40:15 INF] [localhost:9055] Starting GossNetNode
[20:40:15 INF] [localhost:9056] Starting GossNetNode
[20:40:15 INF] [localhost:9057] Starting GossNetNode
[3/10/2025 12:40:16 AM on Node 2] Alice : Hello, world!
[3/10/2025 12:40:16 AM on Node 3] Alice : Hello, world!
Press any key to exit...
```

### 5. Manage node lifecycle

`StopAsync` returns promptly and can be called more than once; a stopped node can be
started again, and existing subscriptions survive the cycle. Prefer `DisposeAsync` over
`Dispose`, since stopping is inherently asynchronous:

```csharp
await node.StopAsync();
await node.DisposeAsync();
```

## Subscriber backpressure

Each subscription has its own bounded queue, sized by
`GossNetConfiguration.SubscriberQueueCapacity` (default 1024). When a subscriber falls
behind, **its own oldest message is dropped** rather than blocking the node's receive
loop or growing without limit — a slow subscriber degrades only itself. Gossip is
eventually consistent and messages are re-delivered by other nodes, so dropping is
preferable to stalling.

A node with no subscribers buffers nothing at all.

## Message size

Messages travel in a single UDP datagram, so a serialized message must fit within
`GossNetMessageBase.MaxDatagramBytes` (65507 bytes). Exceeding it throws
`InvalidOperationException` when sending, rather than failing opaquely at the socket.

## How GossNet.Protocol Works

GossNet nodes use UDP for message communication. When a node receives or sends a message:

1. It marks itself as "notified" in the message metadata
2. It processes the message (delivering it to every subscriber)
3. It forwards the message to all neighbors that haven't been notified yet

Duplicate messages are recognised by id and discarded, so a message never loops. This
epidemic spreading ensures the message reaches all nodes in the network efficiently,
even in case of partial network failures.

## Features

- UDP-based communication for lightweight, fast message passing
- Thread-safe design with proper synchronization
- Automatic handling of duplicate messages
- Custom message types through generic implementation
- Broadcast subscription model built on .NET channels, with per-subscriber backpressure
- Pluggable node discovery

## Service Discovery

| Method      | Description                     | Status | Package                        |
|-------------|---------------------------------|--------|--------------------------------|
| DNS         | Discover nodes using DNS        | Done   | built in                       |
| Static List | Manually configure node list    | Done   | built in                       |
| Consul      | Discover nodes using Consul     | Done   | `GossNet.Discovery.Consul`     |
| Kubernetes  | Discover nodes using Kubernetes | Done   | `GossNet.Discovery.Kubernetes` |
| Docker      | Discover nodes using Docker     | Done   | `GossNet.Discovery.Docker`     |

DNS and static-list discovery are built in. The other three ship as separate packages so
their client dependencies stay out of the core package:

```shell
dotnet add package GossNet.Discovery.Consul
dotnet add package GossNet.Discovery.Kubernetes
dotnet add package GossNet.Discovery.Docker
```

Each provider package has its own README with configuration and deployment details.
Selecting a mechanism whose provider has not been supplied throws at construction rather
than silently resolving no neighbours.

Providers are supplied through `DiscoveryProviderFactory`, which receives the
configuration so the provider can exclude the node from its own results:

```csharp
var configuration = new GossNetConfiguration
{
    Hostname = "10.0.0.1",
    Port = 9055,
    DiscoveryProviderFactory = cfg => new ConsulNodeDiscovery(cfg, consulOptions)
};
```

### Custom discovery

Implement `INodeDiscovery` to plug in your own:

```csharp
public sealed class MyDiscovery : INodeDiscovery
{
    public ValueTask<IReadOnlyList<GossNetNodeHostEntry>> GetNeighboursAsync(CancellationToken ct = default) =>
        new([new GossNetNodeHostEntry { Hostname = "10.0.0.2", Port = 9055 }]);
}

var configuration = new GossNetConfiguration
{
    Hostname = "10.0.0.1",
    DiscoveryProvider = new MyDiscovery()
};
```

Discovery runs on the message path, so providers should cache. Deriving from
`CachingNodeDiscovery` gives you a short-lived result cache and self-exclusion. The
built-in DNS provider caches for 30 seconds and excludes the node's own addresses.

## Upgrading to 0.3.0

0.3.0 fixes several defects that could not be corrected without changing the API. The
library is pre-1.0, so breaking changes ship in a minor release. Upgrading from 0.1.16 —
the previous release on a feed, since no 0.2.x was ever published — means taking all of
the changes below at once.

### Subscriptions

Previously every subscriber received the *same* `ChannelReader`, so N subscribers
**competed** for each message rather than all receiving it, and unsubscribing did not
actually stop delivery.

```diff
- var reader = await node.SubscribeAsync();
- await foreach (var item in reader.ReadAllAsync()) { ... }
- await node.UnsubscribeAsync(reader);
+ using var subscription = node.Subscribe();
+ await foreach (var item in subscription.Reader.ReadAllAsync()) { ... }
+ // disposing the subscription unsubscribes
```

### Transport

`IUdpClient` now takes a `CancellationToken` on both operations, and sends use
`ReadOnlyMemory<byte>`. Without a token a receive parks until a datagram happens to
arrive, which made `StopAsync` hang indefinitely against a real socket. Custom
implementations need updating:

```diff
- Task<UdpReceiveResult> ReceiveAsync();
- Task<int> SendAsync(byte[] datagram, int bytes, string hostname, int port);
+ ValueTask<UdpReceiveResult> ReceiveAsync(CancellationToken cancellationToken);
+ ValueTask<int> SendAsync(ReadOnlyMemory<byte> datagram, string hostname, int port, CancellationToken cancellationToken);
```

### Discovery

`GossNetDiscovery` is replaced by `INodeDiscovery`. Configuring Consul, Kubernetes or
Docker discovery without installing the matching package now throws at construction;
previously those mechanisms silently returned an empty neighbour list, so the node
reported success while gossiping to nobody.

### Message cache

`ExpiringMessageCache<T>.TryGetValue` and `GetAll` were removed. The cache stores only
message ids now, rather than retaining whole payloads for the full TTL.

### Other

- `SendAsync` accepts an optional `CancellationToken`.
- `NullLoggerFactory` was removed; use `NullLogger<T>.Instance`.
- Serialized messages are no longer indented, shrinking every datagram.

## License

GossNet.Protocol is licensed under the MIT License. See [LICENSE](LICENSE) for more information.
