// Deliberately NOT Parallelize, unlike GossNet.Protocol.Tests.
//
// These tests share one Consul agent. Even with a distinct service name per test, running
// them concurrently would have several blocking queries and registration bursts hitting the
// same agent at once, and the assertions here count updates. Pinning this explicitly means
// the convention cannot be copied in from the unit-test projects by accident.
[assembly: DoNotParallelize]
