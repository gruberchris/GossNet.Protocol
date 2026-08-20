# GossNet.Discovery.Azure

Azure tag-based node discovery for [GossNet.Protocol](https://github.com/gruberchris/GossNet.Protocol).

Virtual machines are found by a tag they already carry, so nothing registers or deregisters
itself — the same shape as `GossNet.Discovery.Aws`. A machine that goes away simply stops
matching.

## Installation

```shell
dotnet add package GossNet.Discovery.Azure
```

## Usage

Tag every cluster member, then point discovery at that tag:

```csharp
using GossNet.Discovery.Azure;
using GossNet.Protocol;

var azureOptions = new AzureDiscoveryOptions
{
    ResourceGroup = "gossnet-rg",
    TagKey = "gossnet-cluster",
    TagValue = "production",
    Port = 9055
};

var configuration = new GossNetConfiguration
{
    Hostname = "10.0.1.23",
    Port = 9055,
    DiscoveryProviderFactory = cfg => new AzureTagNodeDiscovery(cfg, azureOptions)
};

await using var node = new GossNetNode<MyMessage>(configuration);
node.Start();
```

`Hostname` must be the address other nodes reach this machine on — normally its private IP.
Discovery excludes the node from its own results by comparing against it, so a mismatch makes
a node gossip to itself.

## Credentials and permissions

Credentials come from `DefaultAzureCredential`: managed identity on a VM, environment
variables, or a developer sign-in. The identity needs **Reader** on the resource group —
enough to list machines and read their network interfaces.

Discovery is scoped to a resource group rather than a whole subscription, which keeps the
lookup cheap and the role assignment narrow.

## Options

| Option           | Default    | Description                                                   |
|------------------|------------|---------------------------------------------------------------|
| `ResourceGroup`  | *required* | Resource group to search                                       |
| `TagKey`         | *required* | Tag key identifying cluster members                            |
| `TagValue`       | *required* | The value that tag must have                                   |
| `Port`           | `9055`     | Gossip port, uniform across the cluster                        |
| `SubscriptionId` | ambient    | Falls back to the credential's default subscription            |
| `CacheDuration`  | 30 seconds | How long a resolved instance list is reused                    |

Azure describes machines, not services, so the port cannot be discovered and must be the same
on every node.

A machine's address lives on its network interface rather than the machine resource, so
resolving it takes a second lookup per machine. Machines with no usable private address — one
still provisioning, say — are skipped rather than returned as unreachable neighbours.

A failed ARM call — throttling, missing role assignment, no credentials — throws
`NodeDiscoveryException` rather than returning an empty list, so an API problem is never
mistaken for a cluster of one.

## License

MIT
