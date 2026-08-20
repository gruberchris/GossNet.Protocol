# GossNet.Discovery.Consul

Consul-based node discovery for [GossNet.Protocol](https://github.com/gruberchris/GossNet.Protocol).

Ships separately so the core package stays free of the Consul client dependency.

## Installation

```shell
dotnet add package GossNet.Discovery.Consul
```

## Usage

Register each gossip node with Consul under a shared service name, then point discovery
at it:

```csharp
using GossNet.Discovery.Consul;
using GossNet.Protocol;

var consulOptions = new ConsulDiscoveryOptions
{
    ServiceName = "gossnet",
    Address = new Uri("http://consul.internal:8500"),
    PassingOnly = true
};

var configuration = new GossNetConfiguration
{
    Hostname = "10.0.0.1",
    Port = 9055,
    DiscoveryProviderFactory = cfg => new ConsulNodeDiscovery(cfg, consulOptions)
};

using var node = new GossNetNode<MyMessage>(configuration);
node.Start();
```

`DiscoveryProviderFactory` is used rather than `DiscoveryProvider` because the provider
needs the node's own hostname and port to exclude itself from its own results — a node
registered in Consul always appears in its own service query.

## Watching

`ConsulNodeDiscovery` implements `IWatchableNodeDiscovery` using **blocking queries**, so a
service registering or deregistering reaches a node in about a round trip rather than after
`CacheDuration`.

Nothing needs enabling — `GossNetNode.Start()` subscribes automatically. If the watch fails,
the node logs it and falls back to cached polling.

Each request carries the `X-Consul-Index` from the previous result and the agent holds it
open until that index moves. Three rules make that correct, and each fails *quietly* rather
than loudly when it is wrong:

- An index that goes **backwards** means Consul restarted or the table was re-indexed. It
  must be reset to zero to re-baseline, or the query blocks forever and no further change is
  ever seen.
- An index **below one** must be treated as one, or the next query is a non-blocking read
  and the watch becomes a hot loop.
- An **unchanged** index means the wait elapsed, not that anything changed, so nothing is
  published.

Because those are server behaviours, they are verified against a real Consul container in
`GossNet.Discovery.IntegrationTests` rather than against a fake.

A custom `IConsulHealthClient` keeps working unchanged; it simply will not watch. Implement
`IWatchableConsulHealthClient` as well to opt in.

## Options

| Option | Default | Description |
|---|---|---|
| `ServiceName` | required | Consul service name the nodes register under |
| `Address` | `http://localhost:8500` | Consul agent address |
| `Token` | none | ACL token |
| `Datacenter` | agent's own | Datacenter to query |
| `Tag` | none | Only return instances carrying this tag |
| `PassingOnly` | `true` | Only return instances whose health checks pass |
| `WatchWaitTime` | 5 minutes | How long a blocking query may be held open by the agent |
| `WatchRetryDelay` | 2 seconds | Pause before re-establishing a failed blocking query |
| `CacheDuration` | 30 seconds | How long a resolved neighbour list is reused |

Discovery runs on the message path, so results are cached. If Consul cannot be reached,
a `NodeDiscoveryException` is thrown rather than returning an empty neighbour list —
an unreachable agent must never be mistaken for a network of one.

## License

MIT. See [LICENSE](https://github.com/gruberchris/GossNet.Protocol/blob/main/LICENSE).
