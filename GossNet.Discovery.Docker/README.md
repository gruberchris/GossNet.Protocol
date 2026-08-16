# GossNet.Discovery.Docker

Docker-based node discovery for [GossNet.Protocol](https://github.com/gruberchris/GossNet.Protocol).

Ships separately so the Docker client dependency stays out of the core package.

**Target frameworks:** `netstandard2.0`, `net8.0`, `net10.0`.

## Installation

```shell
dotnet add package GossNet.Discovery.Docker
```

## Usage

Label each gossip container and put them on a shared user-defined network:

```yaml
services:
  node:
    image: my-gossnet-app
    labels:
      app: gossnet
    networks:
      - gossnet-net

networks:
  gossnet-net:
```

```csharp
using GossNet.Discovery.Docker;
using GossNet.Protocol;

var dockerOptions = new DockerDiscoveryOptions
{
    Label = "app=gossnet",
    NetworkName = "gossnet-net"
};

var configuration = new GossNetConfiguration
{
    Hostname = Environment.GetEnvironmentVariable("CONTAINER_IP")!,
    Port = 9055,
    DiscoveryProviderFactory = cfg => new DockerNodeDiscovery(cfg, dockerOptions)
};

using var node = new GossNetNode<MyMessage>(configuration);
node.Start();
```

`DiscoveryProviderFactory` is used rather than `DiscoveryProvider` because the provider
needs the node's own address and port to exclude itself — the container running the node
carries the same label as its peers.

The container needs access to the Docker socket to query the daemon:

```yaml
volumes:
  - /var/run/docker.sock:/var/run/docker.sock:ro
```

## Options

| Option | Default | Description |
|---|---|---|
| `Label` | required | Label identifying the gossip containers (`key=value` or a bare key) |
| `Endpoint` | local daemon | Docker endpoint; Unix socket on Linux/macOS, named pipe on Windows |
| `NetworkName` | first address found | Which network's address to use |
| `Port` | the node's own port | Port neighbours listen on |
| `RunningOnly` | `true` | Only return running containers |
| `CacheDuration` | 30 seconds | How long a resolved neighbour list is reused |

**Set `NetworkName` whenever containers join more than one network.** A multi-homed
container has an address on each, and without a name the first one found is used, which
is ambiguous. Containers not attached to the named network are skipped rather than
guessed at.

Containers without an address are skipped — a container can be listed before it is
attached to a network. If the daemon cannot be reached, a `NodeDiscoveryException` is
thrown rather than returning an empty neighbour list.

## License

MIT. See [LICENSE](https://github.com/gruberchris/GossNet.Protocol/blob/main/LICENSE).
