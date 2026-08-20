# GossNet.Discovery.Etcd

etcd-based node discovery for [GossNet.Protocol](https://github.com/gruberchris/GossNet.Protocol),
with **watch** support.

Nodes register themselves under a key prefix, holding their key with a lease. A node that
crashes stops renewing, etcd expires the lease and deletes the key — so departures need
nothing to detect them.

## Supported frameworks

`net8.0` and `net10.0` only — **not** `netstandard2.0`, unlike most packages in this repo.
The `dotnet-etcd` client ships no `netstandard2.0` asset, so there is no way to support
.NET Framework without dropping the client or pinning an ancient version of it.

## Installation

```shell
dotnet add package GossNet.Discovery.Etcd
```

## Usage

```csharp
using GossNet.Discovery.Etcd;
using GossNet.Protocol;

var etcdOptions = new EtcdDiscoveryOptions
{
    ConnectionString = "http://localhost:2379",
    Prefix = "/gossnet/members/"
};

var configuration = new GossNetConfiguration
{
    Hostname = "10.0.0.4",
    Port = 9055,
    DiscoveryProviderFactory = cfg => new EtcdNodeDiscovery(cfg, etcdOptions)
};

await using var node = new GossNetNode<MyMessage>(configuration);
node.Start();
```

`Hostname` and `Port` are what this node registers, so they must be reachable by the others.

## Watching

This is the only provider that implements `IWatchableNodeDiscovery`. When a node starts, it
subscribes to the prefix and membership changes arrive **as they happen** rather than after
the cache expires — cutting failover from `CacheDuration` to roughly a round trip.

Nothing needs enabling. `GossNetNode` checks for the interface on `Start()` and subscribes
if present. If the watch fails, the node logs it and falls back to the cached poll: a broken
change feed makes discovery slower, never broken.

## How membership is stored

One key per node under the prefix, value `host:port`, held by a lease:

```
PUT /gossnet/members/10.0.0.4:9055  "10.0.0.4:9055"  --lease=<id>
```

The lease is renewed in the background for the life of the provider. Disposing the provider
stops renewal, so the key expires and peers stop seeing this node.

## Options

| Option             | Default             | Description                                        |
|--------------------|---------------------|----------------------------------------------------|
| `ConnectionString` | *required*¹         | etcd endpoint, e.g. `http://localhost:2379`         |
| `Prefix`           | `/gossnet/members/` | Key prefix members register under                   |
| `LeaseTtl`         | 15 seconds          | Lease TTL for this node's registration              |
| `CacheDuration`    | 5 seconds           | Reuse window, only used when the watch is not running |
| `Username`         | none                | For authenticated clusters                          |
| `Password`         | none                | For authenticated clusters                          |

¹ Not required when you pass your own `IEtcdRegistry`.

Use a different `Prefix` per cluster sharing an etcd instance, the way you would a different
Consul service name.

Registration happens on first use rather than in the constructor, so constructing a provider
never performs network I/O. A failed read or registration throws `NodeDiscoveryException`
rather than returning an empty list, so an unreachable etcd is never mistaken for a cluster
of one.

## License

MIT
