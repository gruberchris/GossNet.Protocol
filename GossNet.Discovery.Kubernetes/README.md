# GossNet.Discovery.Kubernetes

Kubernetes-based node discovery for [GossNet.Protocol](https://github.com/gruberchris/GossNet.Protocol).

Ships separately so the Kubernetes client dependency stays out of the core package.
This is not merely a preference: `KubernetesClient` publishes **no `netstandard2.0`
asset**, so folding it into `GossNet.Protocol` would force every consumer of the core
package off `netstandard2.0`.

**Target frameworks:** `net8.0`, `net10.0`.

## Installation

```shell
dotnet add package GossNet.Discovery.Kubernetes
```

## Usage

Give each gossip pod a shared label and expose its own IP through the downward API:

```yaml
env:
  - name: POD_IP
    valueFrom:
      fieldRef:
        fieldPath: status.podIP
```

```csharp
using GossNet.Discovery.Kubernetes;
using GossNet.Protocol;

var k8sOptions = new KubernetesDiscoveryOptions
{
    LabelSelector = "app=gossnet",
    ReadyOnly = true
};

var configuration = new GossNetConfiguration
{
    // The address peers reach this node on, so it can exclude itself.
    Hostname = Environment.GetEnvironmentVariable("POD_IP")!,
    Port = 9055,
    DiscoveryProviderFactory = cfg => new KubernetesNodeDiscovery(cfg, k8sOptions)
};

using var node = new GossNetNode<MyMessage>(configuration);
node.Start();
```

`DiscoveryProviderFactory` is used rather than `DiscoveryProvider` because the provider
needs the node's own address and port to exclude itself — the pod running the node
matches its own label selector.

## Options

| Option | Default | Description |
|---|---|---|
| `LabelSelector` | required | Selector identifying the gossip pods |
| `Namespace` | the pod's own | Namespace to search |
| `Port` | the node's own port | Port neighbours listen on |
| `FieldSelector` | none | Extra field selector |
| `ReadyOnly` | `true` | Only return pods that are Running and Ready |
| `KubeConfigPath` | none | Explicit kubeconfig; otherwise in-cluster config with kubeconfig fallback |
| `CacheDuration` | 30 seconds | How long a resolved neighbour list is reused |

Pods without an assigned IP are skipped. If the API server cannot be reached, a
`NodeDiscoveryException` is thrown rather than returning an empty neighbour list.

## RBAC

The service account needs permission to list pods in the namespace:

```yaml
apiVersion: rbac.authorization.k8s.io/v1
kind: Role
metadata:
  name: gossnet-discovery
rules:
  - apiGroups: [""]
    resources: ["pods"]
    verbs: ["list"]
```

## License

MIT. See [LICENSE](https://github.com/gruberchris/GossNet.Protocol/blob/main/LICENSE).
