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

## Options

| Option | Default | Description |
|---|---|---|
| `ServiceName` | required | Consul service name the nodes register under |
| `Address` | `http://localhost:8500` | Consul agent address |
| `Token` | none | ACL token |
| `Datacenter` | agent's own | Datacenter to query |
| `Tag` | none | Only return instances carrying this tag |
| `PassingOnly` | `true` | Only return instances whose health checks pass |
| `CacheDuration` | 30 seconds | How long a resolved neighbour list is reused |

Discovery runs on the message path, so results are cached. If Consul cannot be reached,
a `NodeDiscoveryException` is thrown rather than returning an empty neighbour list —
an unreachable agent must never be mistaken for a network of one.

## License

MIT. See [LICENSE](https://github.com/gruberchris/GossNet.Protocol/blob/main/LICENSE).
