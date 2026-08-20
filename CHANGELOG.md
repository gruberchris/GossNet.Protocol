# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this
project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). While the
version is below 1.0.0, breaking changes ship in minor releases.

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
