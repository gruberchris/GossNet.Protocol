using Google.Cloud.Compute.V1;

namespace GossNet.Discovery.Gcp;

/// <summary>A Compute Engine instance matching the configured label.</summary>
/// <param name="Address">The address to reach the instance on.</param>
/// <param name="Name">The instance name, used for diagnostics.</param>
public sealed record GceInstance(string Address, string Name);

/// <summary>
/// The single Compute Engine query <see cref="GceLabelNodeDiscovery"/> needs.
/// </summary>
/// <remarks>Kept narrow so discovery can be tested without Google Cloud credentials or a network.</remarks>
public interface IGceInstanceLookup : IDisposable
{
    /// <summary>
    /// Lists running instances in a project carrying a label.
    /// </summary>
    /// <param name="projectId">The project to search.</param>
    /// <param name="labelKey">The label key.</param>
    /// <param name="labelValue">The label value.</param>
    /// <param name="useInternalIp">Whether to return internal rather than external addresses.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    ValueTask<IReadOnlyList<GceInstance>> GetInstancesAsync(
        string projectId,
        string labelKey,
        string labelValue,
        bool useInternalIp,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IGceInstanceLookup"/> backed by the Compute Engine API.
/// </summary>
/// <remarks>
/// Credentials come from Application Default Credentials — the instance service account on
/// a VM, <c>GOOGLE_APPLICATION_CREDENTIALS</c>, or a developer sign-in. The identity needs
/// the <c>compute.instances.list</c> permission, which <c>roles/compute.viewer</c> grants.
/// </remarks>
public sealed class GceInstanceLookup : IGceInstanceLookup
{
    private const string RunningStatus = "RUNNING";

    private readonly InstancesClient _client;

    /// <summary>Creates a client using Application Default Credentials.</summary>
    public GceInstanceLookup() => _client = InstancesClient.Create();

    /// <summary>Uses an existing client.</summary>
    /// <param name="client">A configured Compute Engine client.</param>
    public GceInstanceLookup(InstancesClient client) => _client = client;

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<GceInstance>> GetInstancesAsync(
        string projectId,
        string labelKey,
        string labelValue,
        bool useInternalIp,
        CancellationToken cancellationToken = default)
    {
        var request = new AggregatedListInstancesRequest
        {
            Project = projectId,

            // Aggregated across every zone: a gossip cluster normally spans zones, and
            // asking per-zone would mean knowing the zone list up front.
            Filter = $"labels.{labelKey}={labelValue}",

            // Without this, one unreachable zone fails the entire lookup rather than
            // returning the instances the other zones did report.
            ReturnPartialSuccess = true
        };

        var instances = new List<GceInstance>();

        // The paged enumerable fetches subsequent pages as it is consumed, so a cluster
        // larger than one page is not truncated.
        await foreach (var scope in _client.AggregatedListAsync(request).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            foreach (var instance in scope.Value.Instances)
            {
                // Terminated and stopped instances keep their labels, and their addresses
                // are either gone or reassigned. Checked here rather than in the filter
                // because a server-side status filter silently returns nothing when the
                // expression is malformed.
                if (!string.Equals(instance.Status, RunningStatus, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var address = ResolveAddress(instance, useInternalIp);

                // An instance can legitimately have no external address; skipping beats
                // emitting a neighbour that can never be reached.
                if (!string.IsNullOrEmpty(address))
                {
                    instances.Add(new GceInstance(address!, instance.Name));
                }
            }
        }

        return instances;
    }

    private static string? ResolveAddress(Instance instance, bool useInternalIp)
    {
        foreach (var nic in instance.NetworkInterfaces)
        {
            if (useInternalIp)
            {
                if (!string.IsNullOrEmpty(nic.NetworkIP))
                {
                    return nic.NetworkIP;
                }

                continue;
            }

            // An external address lives on an access config, not on the interface itself.
            foreach (var access in nic.AccessConfigs)
            {
                if (!string.IsNullOrEmpty(access.NatIP))
                {
                    return access.NatIP;
                }
            }
        }

        return null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // InstancesClient holds a pooled gRPC channel managed by the SDK and exposes no
        // Dispose; the method exists so the seam matches every other provider's lookup.
    }
}
