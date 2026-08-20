using dotnet_etcd;
using dotnet_etcd.interfaces;
using Etcdserverpb;
using Google.Protobuf;

namespace GossNet.Discovery.Etcd;

/// <summary>
/// The etcd operations <see cref="EtcdNodeDiscovery"/> needs.
/// </summary>
/// <remarks>Kept narrow so discovery can be tested without a running etcd cluster.</remarks>
public interface IEtcdRegistry : IDisposable
{
    /// <summary>
    /// Registers a member under a lease and keeps that lease alive until cancelled.
    /// </summary>
    /// <param name="key">The full key to write.</param>
    /// <param name="value">The value to store.</param>
    /// <param name="ttl">The lease time-to-live.</param>
    /// <param name="cancellationToken">Ends the registration and stops renewing the lease.</param>
    /// <remarks>
    /// Returns once the key is written; renewal continues in the background. etcd deletes
    /// the key when renewal stops, so a crashed node removes itself.
    /// </remarks>
    ValueTask RegisterAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken = default);

    /// <summary>Lists the values of every key under a prefix.</summary>
    /// <param name="prefix">The key prefix.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    ValueTask<IReadOnlyList<string>> GetMembersAsync(string prefix, CancellationToken cancellationToken = default);

    /// <summary>Yields once each time any key under the prefix is added, changed or removed.</summary>
    /// <param name="prefix">The key prefix.</param>
    /// <param name="cancellationToken">Ends the watch.</param>
    IAsyncEnumerable<bool> WatchAsync(string prefix, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IEtcdRegistry"/> backed by an etcd cluster.
/// </summary>
public sealed class EtcdRegistry : IEtcdRegistry
{
    private readonly EtcdClient _client;
    private readonly Grpc.Core.Metadata? _authentication;

    private long _leaseId;

    /// <summary>Connects using the configured endpoint.</summary>
    /// <param name="options">The etcd settings.</param>
    /// <exception cref="ArgumentException">No connection string was supplied.</exception>
    public EtcdRegistry(EtcdDiscoveryOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new ArgumentException(
                $"{nameof(EtcdDiscoveryOptions.ConnectionString)} is required when no registry is supplied.",
                nameof(options));
        }

        _client = new EtcdClient(options.ConnectionString!);

        if (!string.IsNullOrEmpty(options.Username))
        {
            var token = _client.Authenticate(new AuthenticateRequest
            {
                Name = options.Username,
                Password = options.Password ?? string.Empty
            });

            _authentication = new Grpc.Core.Metadata { { "token", token.Token } };
        }
    }

    /// <inheritdoc />
    public async ValueTask RegisterAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        var lease = await _client
            .LeaseGrantAsync(new LeaseGrantRequest { TTL = (long)ttl.TotalSeconds }, _authentication, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        _leaseId = lease.ID;

        await _client.PutAsync(
            new PutRequest
            {
                Key = ByteString.CopyFromUtf8(key),
                Value = ByteString.CopyFromUtf8(value),
                Lease = _leaseId
            },
            _authentication,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Renewal runs for the lifetime of the token. When it stops, etcd expires the lease
        // and deletes the key, which is what makes a crashed node disappear by itself.
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await _client.LeaseKeepAlive(_leaseId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Losing the lease is a legitimate outcome: the key expires and peers
                    // stop seeing this node, which is the correct signal.
                }
            },
            CancellationToken.None);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<string>> GetMembersAsync(string prefix, CancellationToken cancellationToken = default)
    {
        var response = await _client
            .GetRangeAsync(prefix, _authentication, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var members = new List<string>(response.Kvs.Count);

        foreach (var kv in response.Kvs)
        {
            members.Add(kv.Value.ToStringUtf8());
        }

        return members;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<bool> WatchAsync(
        string prefix,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var changes = System.Threading.Channels.Channel.CreateUnbounded<bool>();

        // The client's watch is callback-based, so it is adapted onto a channel to give the
        // pull-based sequence IWatchableNodeDiscovery expects.
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await _client.WatchRangeAsync(
                        prefix,
                        (WatchResponse _) => changes.Writer.TryWrite(true),
                        _authentication,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    changes.Writer.TryComplete(ex);
                }
                finally
                {
                    changes.Writer.TryComplete();
                }
            },
            CancellationToken.None);

        await foreach (var change in changes.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return change;
        }
    }

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();
}
