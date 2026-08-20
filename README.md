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

| Method        | Description                                  | Watches | Package                        |
|---------------|----------------------------------------------|---------|--------------------------------|
| DNS           | Discover nodes using DNS                     |         | built in                       |
| Static List   | Manually configure node list                 |         | built in                       |
| Peer Exchange | Learn nodes from the gossip traffic itself   |         | built in                       |
| Multicast     | Announce on a multicast group, LAN only      |         | built in                       |
| Composite     | Combine several mechanisms into one          |         | built in                       |
| AWS EC2       | Discover instances by tag                    |         | `GossNet.Discovery.Aws`        |
| Azure         | Discover virtual machines by tag             |         | `GossNet.Discovery.Azure`      |
| Consul        | Discover nodes using Consul                  | ✅      | `GossNet.Discovery.Consul`     |
| Docker        | Discover nodes using Docker                  |         | `GossNet.Discovery.Docker`     |
| Google Cloud  | Discover Compute Engine instances by label   |         | `GossNet.Discovery.Gcp`        |
| etcd          | Register under a lease, discover by prefix   | ✅      | `GossNet.Discovery.Etcd`       |
| Kubernetes    | Discover nodes using Kubernetes              |         | `GossNet.Discovery.Kubernetes` |
| Redis         | Heartbeat into a shared sorted set           |         | `GossNet.Discovery.Redis`      |

> **Docker Swarm and Kubernetes headless Services need no provider.** DNS discovery already
> covers both: Swarm's `tasks.<service>` and a headless Service's DNS name each resolve to
> every instance's address. Point `Hostname` at that name and use `NodeDiscovery.Dns`.

The first five are built in. The rest ship as separate packages so their client dependencies
stay out of the core package:

```shell
dotnet add package GossNet.Discovery.Aws
dotnet add package GossNet.Discovery.Azure
dotnet add package GossNet.Discovery.Consul
dotnet add package GossNet.Discovery.Docker
dotnet add package GossNet.Discovery.Etcd
dotnet add package GossNet.Discovery.Gcp
dotnet add package GossNet.Discovery.Kubernetes
dotnet add package GossNet.Discovery.Redis
```

`GossNet.Discovery.Kubernetes` and `GossNet.Discovery.Etcd` target `net8.0` and `net10.0`
only — neither `KubernetesClient` nor `dotnet-etcd` ships a `netstandard2.0` asset. Every
other package matches the core library's full framework set.

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

### Peer exchange

Every other mechanism asks something external who the peers are. This one does not have to.
Each message already carries `NotifiedNodes` — the nodes known to have seen it — so a node
that receives one learns the identity of everyone on that message's path. Give it a seed to
reach the network through and the rest of the membership arrives on its own:

```csharp
var configuration = new GossNetConfiguration
{
    Hostname = "10.0.0.4",
    Port = 9055,
    NodeDiscovery = NodeDiscovery.PeerExchange,
    // Seeds only. Everything else is learned.
    StaticNodes = [new GossNetNodeHostEntry { Hostname = "10.0.0.1", Port = 9055 }]
};
```

Seeds are never aged out: after a partition long enough to expire every learned peer, they
are the only way back in. Learned peers are dropped after `PeerTimeout` without a sighting
and capped at `MaxPeers`, evicting the least recently seen:

```csharp
DiscoveryProviderFactory = cfg => new PeerExchangeNodeDiscovery(cfg, new PeerExchangeOptions
{
    PeerTimeout = TimeSpan.FromMinutes(5),
    MaxPeers = 256
})
```

Set `PeerTimeout` comfortably longer than the interval at which your application sends
messages, or live peers will be forgotten and re-learned in a cycle.

> **Every node's `Hostname` must be reachable by the others.** A peer is learned exactly as
> it advertises itself, so a node behind NAT, or one in a container advertising an internal
> address, teaches its peers an address they cannot reach.

Any provider can learn this way by implementing `IObservingNodeDiscovery`; the node feeds it
each message's notified list and ignores providers that do not implement it.

### Multicast

The least ceremony of any mechanism: nodes announce themselves to a multicast group and
listen for everyone else doing the same. No registry, no seeds, no addresses to configure.

```csharp
var configuration = new GossNetConfiguration
{
    Hostname = "10.0.0.4",
    Port = 9055,
    NodeDiscovery = NodeDiscovery.Multicast
};
```

Announcements go out every `AnnounceInterval` (2s) on their own socket, separate from the
one carrying messages, and a peer is forgotten after `PeerTimeout` (10s) of silence. Keep
the timeout a small multiple of the interval: multicast is unreliable, and treating one lost
announcement as a departure would make the neighbour list flap.

```csharp
DiscoveryProviderFactory = cfg => new MulticastNodeDiscovery(cfg, new MulticastDiscoveryOptions
{
    GroupAddress = "239.255.42.99",
    Port = 9056,
    AnnounceInterval = TimeSpan.FromSeconds(2),
    PeerTimeout = TimeSpan.FromSeconds(10),
    TimeToLive = 1
})
```

> **Local network only.** The default TTL of 1 keeps announcements on the local link, and
> most cloud networks drop multicast outright. Use a registry-backed provider or peer
> exchange anywhere routed.

Announcements carry the node's configured `Hostname` and `Port`, so — as with peer exchange
— those must be reachable by the other nodes. A node ignores its own announcement, so
loopback can stay enabled for nodes sharing a host.

### Combining mechanisms

`DiscoveryProvider` holds one provider, so `CompositeNodeDiscovery` exists to union several.
The obvious use is seeds plus a registry, so a cluster can still form when the registry is
unreachable:

```csharp
DiscoveryProviderFactory = cfg => new CompositeNodeDiscovery(
[
    new StaticListNodeDiscovery(cfg),
    new ConsulNodeDiscovery(cfg, consulOptions)
])
```

Results are unioned and de-duplicated. One provider failing is tolerated and the others are
still used; **all** of them failing throws `NodeDiscoveryException` with every underlying
error attached, because returning an empty list would be indistinguishable from a network
with nobody else in it.

Pass `ownsProviders: false` when the children are used elsewhere and should outlive the
composite.

### Watching for changes

`INodeDiscovery` is pull-based and remote-backed providers cache, so a node joining or
leaving takes up to `CacheDuration` to be noticed. Backends with a change feed can do better,
and `IWatchableNodeDiscovery` is how they say so:

```csharp
public interface IWatchableNodeDiscovery : INodeDiscovery
{
    IAsyncEnumerable<IReadOnlyList<GossNetNodeHostEntry>> WatchAsync(CancellationToken ct);
}
```

Nothing needs enabling. `GossNetNode.Start()` subscribes when the provider implements it and
uses each pushed list in place of polling. `GossNet.Discovery.Etcd` and
`GossNet.Discovery.Consul` implement it today.

Two rules for implementors:

- **Yield complete lists, not deltas.** The node replaces its whole view with each one, so a
  partial list silently shrinks the cluster.
- **Yield the current membership on subscription**, before waiting for a change, or a node has
  no neighbours until something happens to join or leave.

A watch is an optimization, never a requirement. If it faults, the node logs it, drops the
pushed view and returns to polling — slower to notice changes, still correct.

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
