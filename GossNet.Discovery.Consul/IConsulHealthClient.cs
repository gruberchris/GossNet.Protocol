using Consul;

namespace GossNet.Discovery.Consul;

/// <summary>A service instance registered in Consul.</summary>
/// <param name="Address">The address to reach the instance on.</param>
/// <param name="Port">The port the instance listens on.</param>
public sealed record ConsulServiceInstance(string Address, int Port);

/// <summary>
/// The single Consul query <see cref="ConsulNodeDiscovery"/> needs.
/// </summary>
/// <remarks>Kept narrow so discovery can be tested without a running Consul agent.</remarks>
public interface IConsulHealthClient : IDisposable
{
    /// <summary>
    /// Lists the instances registered for a service.
    /// </summary>
    /// <param name="serviceName">The service name.</param>
    /// <param name="tag">An optional tag instances must carry.</param>
    /// <param name="passingOnly">Whether to return only instances whose checks pass.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    ValueTask<IReadOnlyList<ConsulServiceInstance>> GetServiceInstancesAsync(
        string serviceName,
        string? tag,
        bool passingOnly,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IConsulHealthClient"/> backed by the Consul HTTP API.
/// </summary>
public sealed class ConsulHealthClient : IConsulHealthClient
{
    private readonly ConsulClient _client;
    private readonly string? _datacenter;

    /// <summary>
    /// Creates a client from discovery options.
    /// </summary>
    /// <param name="options">The Consul settings.</param>
    public ConsulHealthClient(ConsulDiscoveryOptions options)
    {
        _datacenter = options.Datacenter;

        _client = new ConsulClient(configuration =>
        {
            if (options.Address is not null)
            {
                configuration.Address = options.Address;
            }

            if (!string.IsNullOrEmpty(options.Token))
            {
                configuration.Token = options.Token;
            }

            if (!string.IsNullOrEmpty(options.Datacenter))
            {
                configuration.Datacenter = options.Datacenter;
            }
        });
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ConsulServiceInstance>> GetServiceInstancesAsync(
        string serviceName,
        string? tag,
        bool passingOnly,
        CancellationToken cancellationToken = default)
    {
        var queryOptions = new QueryOptions();

        if (!string.IsNullOrEmpty(_datacenter))
        {
            queryOptions.Datacenter = _datacenter;
        }

        // The Consul client treats an empty tag as "no tag filter".
        var result = await _client.Health
            .Service(serviceName, tag ?? string.Empty, passingOnly, queryOptions, cancellationToken)
            .ConfigureAwait(false);

        var instances = new List<ConsulServiceInstance>();

        foreach (var entry in result.Response ?? [])
        {
            // Consul convention: a service registered without its own address is
            // reachable at the address of the node hosting it.
            var address = !string.IsNullOrEmpty(entry.Service?.Address)
                ? entry.Service!.Address
                : entry.Node?.Address;

            if (string.IsNullOrEmpty(address) || entry.Service is null)
            {
                continue;
            }

            instances.Add(new ConsulServiceInstance(address!, entry.Service.Port));
        }

        return instances;
    }

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();
}
