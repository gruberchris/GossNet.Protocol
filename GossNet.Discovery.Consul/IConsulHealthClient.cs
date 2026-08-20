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

/// <summary>The result of a blocking query.</summary>
/// <param name="Instances">The service instances as of <paramref name="Index"/>.</param>
/// <param name="Index">
/// Consul's <c>X-Consul-Index</c> for this result, to be passed to the next query.
/// </param>
public sealed record ConsulQueryResult(IReadOnlyList<ConsulServiceInstance> Instances, ulong Index);

/// <summary>
/// A <see cref="IConsulHealthClient"/> that also supports blocking queries.
/// </summary>
/// <remarks>
/// Separate from <see cref="IConsulHealthClient"/> rather than added to it: that interface
/// has shipped since 0.3.0, and adding a member would break every existing implementation.
/// <see cref="ConsulNodeDiscovery"/> watches only when its client implements this.
/// </remarks>
public interface IWatchableConsulHealthClient : IConsulHealthClient
{
    /// <summary>
    /// Issues a blocking query, returning when the result changes or the wait elapses.
    /// </summary>
    /// <param name="serviceName">The service name.</param>
    /// <param name="tag">An optional tag instances must carry.</param>
    /// <param name="passingOnly">Whether to return only instances whose checks pass.</param>
    /// <param name="waitIndex">
    /// The index from the previous result, or zero to establish a baseline. Consul holds the
    /// request open until its index for the service moves past this.
    /// </param>
    /// <param name="waitTime">How long Consul may hold the request open.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    ValueTask<ConsulQueryResult> QueryServiceInstancesAsync(
        string serviceName,
        string? tag,
        bool passingOnly,
        ulong waitIndex,
        TimeSpan waitTime,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IConsulHealthClient"/> backed by the Consul HTTP API.
/// </summary>
public sealed class ConsulHealthClient : IConsulHealthClient, IWatchableConsulHealthClient
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

        return Map(result.Response);
    }

    /// <inheritdoc />
    public async ValueTask<ConsulQueryResult> QueryServiceInstancesAsync(
        string serviceName,
        string? tag,
        bool passingOnly,
        ulong waitIndex,
        TimeSpan waitTime,
        CancellationToken cancellationToken = default)
    {
        var queryOptions = new QueryOptions
        {
            WaitIndex = waitIndex,
            WaitTime = waitTime
        };

        if (!string.IsNullOrEmpty(_datacenter))
        {
            queryOptions.Datacenter = _datacenter;
        }

        var result = await _client.Health
            .Service(serviceName, tag ?? string.Empty, passingOnly, queryOptions, cancellationToken)
            .ConfigureAwait(false);

        return new ConsulQueryResult(Map(result.Response), result.LastIndex);
    }

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();

    private static List<ConsulServiceInstance> Map(IEnumerable<ServiceEntry>? entries)
    {
        var instances = new List<ConsulServiceInstance>();

        foreach (var entry in entries ?? [])
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
}
