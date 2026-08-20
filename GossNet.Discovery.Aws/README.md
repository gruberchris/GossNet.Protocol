# GossNet.Discovery.Aws

AWS EC2 tag-based node discovery for [GossNet.Protocol](https://github.com/gruberchris/GossNet.Protocol).

Instances are found by a tag they already carry, so nothing registers or deregisters itself.
This mirrors how Consul and Serf auto-join on AWS (`retry_join provider=aws tag_key=…`): an
instance that goes away simply stops matching.

## Installation

```shell
dotnet add package GossNet.Discovery.Aws
```

## Usage

Tag every cluster member, then point discovery at that tag:

```csharp
using GossNet.Discovery.Aws;
using GossNet.Protocol;

var awsOptions = new AwsDiscoveryOptions
{
    TagKey = "gossnet-cluster",
    TagValue = "production",
    Port = 9055
};

var configuration = new GossNetConfiguration
{
    Hostname = "10.0.1.23",
    Port = 9055,
    DiscoveryProviderFactory = cfg => new Ec2TagNodeDiscovery(cfg, awsOptions)
};

await using var node = new GossNetNode<MyMessage>(configuration);
node.Start();
```

`Hostname` must be the address other nodes can reach this instance on — normally its private
IP, matching `UsePrivateIp`. Discovery excludes the node from its own results by comparing
against it, so a mismatch makes a node gossip to itself.

## IAM

The instance profile (or whichever credentials the SDK picks up) needs one action:

```json
{
  "Effect": "Allow",
  "Action": "ec2:DescribeInstances",
  "Resource": "*"
}
```

`ec2:DescribeInstances` does not support resource-level permissions, so `Resource` must be
`*`. Scope it with a condition key if your policy requires narrowing.

## Options

| Option          | Default    | Description                                                        |
|-----------------|------------|--------------------------------------------------------------------|
| `TagKey`        | *required* | Instance tag key identifying cluster members                        |
| `TagValue`      | *required* | The value that tag must have                                        |
| `Port`          | `9055`     | Gossip port, uniform across the cluster                             |
| `UsePrivateIp`  | `true`     | Use the private address rather than the public one                  |
| `Region`        | ambient    | AWS region; falls back to the SDK's own configuration               |
| `CacheDuration` | 30 seconds | How long a resolved instance list is reused                         |

EC2 describes instances, not services, so the port cannot be discovered and must be the same
on every node.

Only instances in the `running` state are returned. Stopped and terminated instances keep
their tags, and their addresses are either gone or reassigned. Instances with no address of
the requested kind are skipped rather than returned as unreachable neighbours. Results are
paginated through in full, so clusters larger than one page are not truncated.

A failed EC2 call — throttling, missing permissions, no credentials — throws
`NodeDiscoveryException` rather than returning an empty list, so an API problem is never
mistaken for a cluster of one.

## License

MIT
