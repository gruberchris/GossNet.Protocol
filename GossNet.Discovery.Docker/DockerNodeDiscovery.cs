using GossNet.Protocol;

namespace GossNet.Discovery.Docker;

/// <summary>
/// Discovers GossNet neighbours by listing labelled Docker containers.
/// </summary>
/// <example>
/// <code>
/// var dockerOptions = new DockerDiscoveryOptions
/// {
///     Label = "app=gossnet",
///     NetworkName = "gossnet-net"
/// };
///
/// var configuration = new GossNetConfiguration
/// {
///     Hostname = "172.18.0.2",
///     Port = 9055,
///     DiscoveryProviderFactory = cfg => new DockerNodeDiscovery(cfg, dockerOptions)
/// };
/// </code>
/// </example>
public sealed class DockerNodeDiscovery : CachingNodeDiscovery, IDisposable
{
    private readonly GossNetConfiguration _configuration;
    private readonly DockerDiscoveryOptions _options;
    private readonly IDockerContainerLookup _lookup;
    private readonly bool _ownsLookup;

    /// <summary>
    /// Initializes Docker-based discovery.
    /// </summary>
    /// <param name="configuration">The node configuration, used to exclude this node from its own results.</param>
    /// <param name="options">The Docker settings.</param>
    /// <param name="lookup">Lookup to use; one is created from <paramref name="options"/> when omitted.</param>
    public DockerNodeDiscovery(
        GossNetConfiguration configuration,
        DockerDiscoveryOptions options,
        IDockerContainerLookup? lookup = null)
        : base(options.CacheDuration)
    {
        if (string.IsNullOrWhiteSpace(options.Label))
        {
            throw new ArgumentException(
                $"{nameof(DockerDiscoveryOptions.Label)} must be provided so discovery can identify the gossip containers.",
                nameof(options));
        }

        _configuration = configuration;
        _options = options;

        // Only dispose a lookup we created; a caller-supplied one may be shared.
        _ownsLookup = lookup is null;
        _lookup = lookup ?? new DockerContainerLookup(options);
    }

    /// <inheritdoc />
    protected override async ValueTask<IReadOnlyList<GossNetNodeHostEntry>> ResolveAsync(CancellationToken cancellationToken)
    {
        var port = _options.Port ?? _configuration.Port;

        IReadOnlyList<DockerContainerInfo> containers;

        try
        {
            containers = await _lookup
                .ListContainersAsync(_options.Label, _options.RunningOnly, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Surfaced rather than swallowed: an unreachable daemon must not look like a
            // network with no other nodes in it.
            throw new NodeDiscoveryException(
                $"Failed to list Docker containers labelled '{_options.Label}'.", ex);
        }

        var candidates = new List<GossNetNodeHostEntry>(containers.Count);

        foreach (var container in containers)
        {
            if (_options.RunningOnly && !container.IsRunning)
            {
                continue;
            }

            var address = SelectAddress(container);

            // A container can be listed before it is attached to a network, in which
            // case it has no address to gossip at.
            if (address is null)
            {
                continue;
            }

            candidates.Add(new GossNetNodeHostEntry { Hostname = address, Port = port });
        }

        // The container running this node carries the same label.
        return ExcludeSelf(candidates, _configuration);
    }

    private string? SelectAddress(DockerContainerInfo container)
    {
        if (_options.NetworkName is not null)
        {
            // Explicit network wins, and a container that is not on it is not reachable
            // at a predictable address, so it is skipped rather than guessed at.
            return container.NetworkAddresses.TryGetValue(_options.NetworkName, out var address) ? address : null;
        }

        foreach (var address in container.NetworkAddresses.Values)
        {
            return address;
        }

        return null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsLookup)
        {
            _lookup.Dispose();
        }
    }
}
