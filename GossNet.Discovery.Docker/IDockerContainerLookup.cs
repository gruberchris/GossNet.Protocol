using Docker.DotNet;
using Docker.DotNet.Models;

namespace GossNet.Discovery.Docker;

/// <summary>A container matched by the discovery label.</summary>
/// <param name="Name">The container name, used for diagnostics.</param>
/// <param name="IsRunning">Whether the container is in the running state.</param>
/// <param name="NetworkAddresses">Address per Docker network the container is attached to.</param>
public sealed record DockerContainerInfo(
    string Name,
    bool IsRunning,
    IReadOnlyDictionary<string, string> NetworkAddresses);

/// <summary>
/// The single Docker query <see cref="DockerNodeDiscovery"/> needs.
/// </summary>
/// <remarks>Kept narrow so discovery can be tested without a Docker daemon.</remarks>
public interface IDockerContainerLookup : IDisposable
{
    /// <summary>
    /// Lists the containers carrying a label.
    /// </summary>
    /// <param name="label">The label filter, either <c>key=value</c> or a bare key.</param>
    /// <param name="runningOnly">Whether to restrict the query to running containers.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    ValueTask<IReadOnlyList<DockerContainerInfo>> ListContainersAsync(
        string label,
        bool runningOnly,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IDockerContainerLookup"/> backed by the Docker Engine API.
/// </summary>
public sealed class DockerContainerLookup : IDockerContainerLookup
{
    private readonly DockerClient _client;

    /// <summary>
    /// Creates a client for the configured endpoint, or the local daemon by default.
    /// </summary>
    /// <param name="options">The discovery settings.</param>
    public DockerContainerLookup(DockerDiscoveryOptions options)
    {
        var configuration = options.Endpoint is not null
            ? new DockerClientConfiguration(options.Endpoint)
            : new DockerClientConfiguration();

        _client = configuration.CreateClient();
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<DockerContainerInfo>> ListContainersAsync(
        string label,
        bool runningOnly,
        CancellationToken cancellationToken = default)
    {
        var parameters = new ContainersListParameters
        {
            // The daemon lists only running containers unless All is set, so ask for
            // everything when the caller wants non-running ones too.
            All = !runningOnly,
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["label"] = new Dictionary<string, bool> { [label] = true }
            }
        };

        var containers = await _client.Containers
            .ListContainersAsync(parameters, cancellationToken)
            .ConfigureAwait(false);

        var result = new List<DockerContainerInfo>();

        foreach (var container in containers)
        {
            var addresses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (container.NetworkSettings?.Networks is not null)
            {
                foreach (var network in container.NetworkSettings.Networks)
                {
                    if (!string.IsNullOrEmpty(network.Value?.IPAddress))
                    {
                        addresses[network.Key] = network.Value!.IPAddress;
                    }
                }
            }

            var name = container.Names is { Count: > 0 }
                ? container.Names[0].TrimStart('/')
                : container.ID ?? "<unknown>";

            var isRunning = string.Equals(container.State, "running", StringComparison.OrdinalIgnoreCase);

            result.Add(new DockerContainerInfo(name, isRunning, addresses));
        }

        return result;
    }

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();
}
