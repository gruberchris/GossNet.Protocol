# GossNet.Discovery.Redis

Redis-based node discovery for [GossNet.Protocol](https://github.com/gruberchris/GossNet.Protocol).

Unlike the read-only providers, this one **registers the node**. Consul has an agent and EC2
has instance tags, but Redis knows nothing until something writes to it — so each node
heartbeats itself into a shared sorted set and reads the others out of it.

## Installation

```shell
dotnet add package GossNet.Discovery.Redis
```

## Usage

```csharp
using GossNet.Discovery.Redis;
using GossNet.Protocol;

var redisOptions = new RedisDiscoveryOptions
{
    ConnectionString = "localhost:6379",
    Key = "gossnet:members"
};

var configuration = new GossNetConfiguration
{
    Hostname = "10.0.0.4",
    Port = 9055,
    DiscoveryProviderFactory = cfg => new RedisNodeDiscovery(cfg, redisOptions)
};

await using var node = new GossNetNode<MyMessage>(configuration);
node.Start();
```

`Hostname` and `Port` are what this node registers, so they must be reachable by the others.

If your application already talks to Redis, share the multiplexer rather than opening a
second connection — it is expensive and designed to be shared:

```csharp
DiscoveryProviderFactory = cfg => new RedisNodeDiscovery(
    cfg, redisOptions, registry: new RedisRegistry(existingMultiplexer))
```

A registry you supply is left open when discovery is disposed; one built from
`ConnectionString` is closed.

## How membership is stored

One sorted set. The member is `host:port`, the score is the epoch milliseconds of its last
heartbeat:

```
ZADD gossnet:members <now-ms> "10.0.0.4:9055"      # heartbeat, every HeartbeatInterval
ZRANGEBYSCORE gossnet:members <cutoff> +inf        # who is alive
ZREM gossnet:members "10.0.0.4:9055"               # clean shutdown
```

A single key and a single range query, with no per-key expiry and no `SCAN` — both of which
scale badly on a shared instance. Entries older than twice the timeout are pruned during
heartbeats, so a node that never comes back does not accumulate forever.

Disposing the provider removes this node's entry, so a clean shutdown is noticed immediately
rather than after `RegistrationTimeout`.

## Options

| Option                | Default            | Description                                          |
|-----------------------|--------------------|------------------------------------------------------|
| `ConnectionString`    | *required*¹        | Redis connection string                              |
| `Key`                 | `gossnet:members`  | Sorted set holding membership                        |
| `HeartbeatInterval`   | 5 seconds          | How often this node refreshes its registration       |
| `RegistrationTimeout` | 20 seconds         | How long a node stays live after its last heartbeat  |
| `CacheDuration`       | 5 seconds          | How long a resolved member list is reused            |

¹ Not required when you pass a `RedisRegistry` built from an existing multiplexer.

Keep `RegistrationTimeout` at several heartbeat intervals. A node that misses one because of
a GC pause or a slow round trip has not left the cluster.

Use a different `Key` per cluster sharing a Redis instance, the way you would a different
Consul service name.

A failed read throws `NodeDiscoveryException` rather than returning an empty list, so an
unreachable Redis is never mistaken for a cluster of one. A failed *heartbeat* is only
logged — the timeout is several intervals wide, so one miss is survivable.

## License

MIT
