# GossNet.Discovery.Gcp

Google Cloud Compute Engine node discovery for
[GossNet.Protocol](https://github.com/gruberchris/GossNet.Protocol).

Instances are found by a **label** they already carry, so nothing registers or deregisters
itself — the same shape as `GossNet.Discovery.Aws` and `GossNet.Discovery.Azure`. An instance
that goes away simply stops matching.

> **Labels, not network tags.** Compute Engine has both and they are not interchangeable.
> Network tags are unkeyed strings used for firewall rules; **labels** are the key/value
> metadata that corresponds to an AWS or Azure tag, and are what this provider matches on.

## Installation

```shell
dotnet add package GossNet.Discovery.Gcp
```

## Usage

Label every cluster member, then point discovery at that label:

```shell
gcloud compute instances add-labels node-a --labels=gossnet-cluster=production
```

```csharp
using GossNet.Discovery.Gcp;
using GossNet.Protocol;

var gcpOptions = new GcpDiscoveryOptions
{
    ProjectId = "my-project",
    LabelKey = "gossnet-cluster",
    LabelValue = "production",
    Port = 9055
};

var configuration = new GossNetConfiguration
{
    Hostname = "10.128.0.4",
    Port = 9055,
    DiscoveryProviderFactory = cfg => new GceLabelNodeDiscovery(cfg, gcpOptions)
};

await using var node = new GossNetNode<MyMessage>(configuration);
node.Start();
```

`Hostname` must be the address other nodes reach this instance on — normally its internal
IP, matching `UseInternalIp`. Discovery excludes the node from its own results by comparing
against it, so a mismatch makes a node gossip to itself.

## Credentials and permissions

Credentials come from Application Default Credentials: the attached service account on a VM,
`GOOGLE_APPLICATION_CREDENTIALS`, or `gcloud auth application-default login` locally.

The identity needs `compute.instances.list`, which `roles/compute.viewer` grants:

```shell
gcloud projects add-iam-policy-binding my-project \
  --member=serviceAccount:gossnet@my-project.iam.gserviceaccount.com \
  --role=roles/compute.viewer
```

## Running on GKE?

Use [`GossNet.Discovery.Kubernetes`](https://www.nuget.org/packages/GossNet.Discovery.Kubernetes)
instead. This package is for instances on Compute Engine; on GKE the pods are what you want
to discover, not the nodes hosting them.

## Options

| Option          | Default    | Description                                                |
|-----------------|------------|------------------------------------------------------------|
| `ProjectId`     | *required* | Project to search                                           |
| `LabelKey`      | *required* | Instance label key identifying cluster members              |
| `LabelValue`    | *required* | The value that label must have                              |
| `Port`          | `9055`     | Gossip port, uniform across the cluster                     |
| `UseInternalIp` | `true`     | Use the internal address rather than an external one        |
| `CacheDuration` | 30 seconds | How long a resolved instance list is reused                 |

Compute Engine describes instances, not services, so the port cannot be discovered and must
be the same on every node.

Instances are listed **aggregated across every zone**, since a gossip cluster normally spans
zones and asking per-zone would mean knowing the zone list up front. The request sets
`ReturnPartialSuccess`, so one unreachable zone degrades the result rather than failing the
whole lookup, and paging is followed in full so large clusters are not truncated.

Only instances in the `RUNNING` state are returned — stopped and terminated ones keep their
labels, but their addresses are gone or reassigned. Instances with no address of the
requested kind are skipped rather than returned as unreachable neighbours; note that an
external address exists only when the instance has an access config.

A failed API call — throttling, missing permission, no credentials — throws
`NodeDiscoveryException` rather than returning an empty list, so an API problem is never
mistaken for a cluster of one.

## License

MIT
