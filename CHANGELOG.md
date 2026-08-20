# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this
project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). While the
version is below 1.0.0, breaking changes ship in minor releases.

## [0.9.0]

No functional change from 0.8.2. The Kubernetes watch below was released as 0.8.2 by
mistake — that commit was missing its `+semver: minor` marker, so GitVersion took a patch
bump and a feature shipped under a fix number. 0.9.0 re-releases the same code under the
version it should have had.

### Added

- `KubernetesNodeDiscovery` now implements `IWatchableNodeDiscovery`, so a pod joining or
  leaving reaches a node as it happens instead of after `CacheDuration`.
- `IWatchablePodLookup`, the opt-in seam a lookup implements to stream pod changes. Separate
  from `IKubernetesPodLookup` rather than added to it, so existing implementations are
  unaffected; `KubernetesPodLookup` implements both. A lookup without it leaves the node on
  cached polling.
- k3s integration tests covering the pod watch, alongside the existing Consul ones.

## [0.8.2]

The 0.9.0 additions above, released under a patch number in error. Functionally identical to
0.9.0; prefer 0.9.0 so the version reflects that it carries a feature.

## [0.8.1]

Documentation only. The README was audited against the code: it had claimed that a node
delivers its own sent messages to its own subscribers, which it does not — only received
messages reach subscribers. The Features list, the upgrade section, the logging example and
the custom-discovery notes were also brought up to date.

## [0.8.0]

### Added

- `ConsulNodeDiscovery` now implements `IWatchableNodeDiscovery`, using Consul blocking
  queries. Membership changes reach a node in about a round trip instead of after
  `CacheDuration`. Configurable through `ConsulDiscoveryOptions.WatchWaitTime` and
  `WatchRetryDelay`.
- `IWatchableConsulHealthClient`, the opt-in seam a client implements to support blocking
  queries. Separate from `IConsulHealthClient` rather than added to it, so existing
  implementations are unaffected; `ConsulHealthClient` implements both. A client without it
  leaves the node on its normal cached polling.
- `GossNet.Discovery.IntegrationTests`, which runs the real backends in containers. Watch
  correctness lives in the backend's index handling, so a hand-written fake could only
  assert that it agrees with the code calling it.

### Changed

- The release workflow now verifies package contents — every expected framework asset is
  present, and no provider dependency has leaked into the core package — before pushing to
  nuget.org. That check previously ran only in PR validation, so nothing stood between a
  broken package and a publish that cannot be undone.

## [0.7.0]

### Added

- `GossNet.Discovery.Gcp` with `GceLabelNodeDiscovery`: finds cluster members by a Compute
  Engine instance **label**, completing the three major clouds alongside the AWS and Azure
  providers. Nothing registers itself. Instances are listed aggregated across every zone
  with `ReturnPartialSuccess`, so one unreachable zone degrades the result rather than
  failing the lookup. Running instances only. Needs `compute.instances.list`, granted by
  `roles/compute.viewer`.

  Note that Compute Engine labels are not network tags: network tags are unkeyed strings for
  firewall rules, while labels are the key/value metadata equivalent to an AWS or Azure tag.
  On GKE, use `GossNet.Discovery.Kubernetes` instead — the pods are what you want to
  discover, not the nodes hosting them.

## [0.6.0]

Push-based discovery, and the last three backends. Purely additive — no existing API
changed.

### Added

- `IWatchableNodeDiscovery`, an opt-in interface for providers whose backend has a change
  feed. `GossNetNode.Start()` subscribes when a provider implements it and uses each pushed
  list in place of polling, cutting the time to notice a membership change from
  `CacheDuration` to roughly a round trip. A watch that faults is logged and the node falls
  back to polling, because a broken change feed should make discovery slower, never broken.
- `GossNet.Discovery.Etcd` with `EtcdNodeDiscovery`: registers under a lease so a crashed
  node's key expires on its own, discovers by key prefix, and is the first provider to
  implement `IWatchableNodeDiscovery`. Targets `net8.0` and `net10.0` only — `dotnet-etcd`
  ships no `netstandard2.0` asset.
- `GossNet.Discovery.Redis` with `RedisNodeDiscovery`: nodes heartbeat into a shared sorted
  set scored by timestamp, so liveness is one range query with no per-key expiry and no
  `SCAN`. Deregisters on dispose so a clean shutdown is noticed immediately. The first
  provider that writes as well as reads.
- `GossNet.Discovery.Azure` with `AzureTagNodeDiscovery`: finds virtual machines in a
  resource group by tag, mirroring the AWS provider. Needs only the `Reader` role.

## [0.5.0]

Two more ways to find neighbours: one that needs no configuration at all, and one for AWS.

### Added

- `MulticastNodeDiscovery` and `NodeDiscovery.Multicast`: nodes announce themselves to a
  multicast group and listen for each other, with no registry, seeds or addresses to
  configure. Announcements use their own socket, kept separate from the message socket so
  discovery traffic never reaches the node's receive loop. Local network only — the default
  TTL of 1 keeps announcements on the link and most cloud networks drop multicast.
  Configurable through `MulticastDiscoveryOptions`; `IMulticastChannel` is the seam for
  testing without a socket.
- `GossNet.Discovery.Aws` with `Ec2TagNodeDiscovery`: finds cluster members by an EC2
  instance tag, the way Consul and Serf auto-join on AWS. Nothing registers itself. Requires
  the `ec2:DescribeInstances` IAM action. Running instances only, paginated in full.

### Fixed

- A node now disposes a discovery provider it created itself. Previously nothing did, so any
  provider holding resources leaked them for the lifetime of the process — which multicast
  discovery, with a socket and two background loops, would have done every time. Providers
  supplied through `DiscoveryProvider` or `DiscoveryProviderFactory` are still left to the
  caller, since they may be shared between nodes.

## [0.4.0]

Discovery no longer has to depend on something outside the network. Purely additive — no
existing API changed, and every existing provider keeps working untouched.

### Added

- `PeerExchangeNodeDiscovery` and `NodeDiscovery.PeerExchange`: seeds from `StaticNodes`,
  then learns the rest of the membership from the `NotifiedNodes` list every message already
  carries. Seeds are never evicted; learned peers age out after `PeerTimeout` and are capped
  at `MaxPeers`, both settable through `PeerExchangeOptions`.
- `IObservingNodeDiscovery`, the opt-in interface a provider implements to be fed each
  message's notified list. Providers that do not implement it are unaffected — the node
  resolves this once at construction, not per message.
- `CompositeNodeDiscovery`, unioning several providers so seeds and a registry can be used
  together. One provider failing is tolerated; all of them failing throws
  `NodeDiscoveryException` with every underlying error attached.

### Documentation

- Noted that Docker Swarm and Kubernetes headless Services need no dedicated provider: DNS
  discovery already resolves `tasks.<service>` and headless Service names to every instance.

## [0.3.0]

A correctness and modernization release. Several of the fixes could not be made without
changing the public API; see [Upgrading to 0.3.0](README.md#upgrading-to-030).

There is no 0.2.x release. This work was briefly numbered 0.2.0 before it shipped, but
the publishing pipeline was broken throughout that period, so nothing under 0.2 ever
reached a feed. 0.1.16 is the previous release consumers can install.

### Added

- `GossNet.Discovery.Consul`, `GossNet.Discovery.Kubernetes` and
  `GossNet.Discovery.Docker` packages, completing the three discovery mechanisms that
  were previously unimplemented stubs. They ship separately so their client
  dependencies stay out of the core package.
- `INodeDiscovery` for pluggable discovery, with `DnsNodeDiscovery` and
  `StaticListNodeDiscovery` built in, and `CachingNodeDiscovery` as a base for providers
  that query a remote backend.
- `GossNetConfiguration.DiscoveryProvider` and `DiscoveryProviderFactory`.
- `GossNetConfiguration.SubscriberQueueCapacity` for per-subscriber backpressure.
- `GossNetMessageBase.MaxDatagramBytes`, enforced when sending.
- `NodeDiscoveryException`, the shared failure contract for discovery providers.
- `IGossNetNode<T>` implements `IAsyncDisposable`.

### Fixed

- **Unbounded memory growth.** Every received message was written into a shared
  unbounded channel whether or not anyone was subscribed, so a node running without a
  subscriber grew until it ran out of memory.
- **Subscribers competed instead of receiving a broadcast.** Every subscriber was handed
  the same reader, so each message went to exactly one of them, and unsubscribing did
  not stop delivery.
- **`StopAsync` hung indefinitely** against a real socket, because the receive operation
  took no cancellation token and the loop only checked for cancellation between
  receives.
- **A failing socket produced a hot loop**, retried with no delay, burning a core and
  flooding the log. Retries now back off exponentially.
- **A stopped node could not be restarted**, because stopping permanently completed
  shared state.
- **The message cache was not thread-safe.** Its check-then-act sequence let two threads
  both claim the same message as newly seen, so a message could be processed and
  re-forwarded more than once, and its key set was mutated without synchronization.
- **The message cache leaked a timer per node** and its key set grew without bound, as
  keys were only pruned by a method nothing called.
- **Nodes gossiped with themselves** under DNS discovery: self-exclusion compared the
  configured hostname against resolved IP addresses, which never matched.
- **DNS resolution ran on every message**, using a blocking call inside an async method.
- **Consul, Kubernetes and Docker discovery silently returned no neighbours**, so a node
  configured for them reported success while gossiping to nobody.
- Serialized messages were indented, padding every datagram with whitespace.
- Oversized messages failed opaquely at the socket instead of reporting the problem.
- A failed deserialize reported the wrong type name.
- `GossNetNodeHostEntry.CompareTo` allocated two strings per comparison and ordered
  ports lexically, so `host:10` sorted before `host:9`.
- `SendAsync` was documented as returning a byte count; it returns a neighbour count.

### Changed

- **Target frameworks are now `netstandard2.0`, `net8.0` and `net10.0`.** The library
  previously required .NET 9, which is out of support. The build SDK is pinned
  separately in `global.json`, so consumers are not tied to it.
- `SubscribeAsync`/`UnsubscribeAsync` replaced by `Subscribe()` returning
  `IGossNetSubscription<T>`.
- `IUdpClient` takes a `CancellationToken` on both operations and sends
  `ReadOnlyMemory<byte>`.
- `SendAsync` accepts an optional `CancellationToken`.
- `ExpiringMessageCache<T>` stores only message ids, is `IDisposable`, and no longer
  exposes `TryGetValue` or `GetAll`.
- Dependencies updated to 10.0.11; MSTest to 4.x on Microsoft.Testing.Platform.

### Removed

- `NullLoggerFactory` — use `NullLogger<T>.Instance`.
- `GossNetDiscovery` as a public discovery entry point — use `INodeDiscovery`.

## [0.1.16] and earlier

See the [release history](https://github.com/gruberchris/GossNet.Protocol/releases).
