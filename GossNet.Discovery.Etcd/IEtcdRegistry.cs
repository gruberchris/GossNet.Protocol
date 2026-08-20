using dotnet_etcd;
using dotnet_etcd.interfaces;
using Etcdserverpb;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GossNet.Discovery.Etcd;

/// <summary>
/// The etcd operations <see cref="EtcdNodeDiscovery"/> needs.
/// </summary>
/// <remarks>Kept narrow so discovery can be tested without a running etcd cluster.</remarks>
public interface IEtcdRegistry : IDisposable
{
    /// <summary>
    /// Registers a member under a lease and keeps that registration alive until cancelled.
    /// </summary>
    /// <param name="key">The full key to write.</param>
    /// <param name="value">The value to store.</param>
    /// <param name="ttl">The lease time-to-live.</param>
    /// <param name="cancellationToken">Ends the registration and stops renewing the lease.</param>
    /// <remarks>
    /// Returns once the key is written; renewal continues in the background. If the lease
    /// is lost anyway — etcd restarted, or a partition outlasted the TTL — the
    /// registration is re-established rather than silently abandoned, so the node does
    /// not become permanently invisible to its peers. etcd deletes the key when renewal
    /// stops for good, so a crashed node removes itself.
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
    /// <summary>Pause before trying to re-establish a lost registration.</summary>
    private static readonly TimeSpan ReRegisterDelay = TimeSpan.FromSeconds(2);

    private readonly EtcdClient _client;
    private readonly Grpc.Core.Metadata? _authentication;
    private readonly ILogger _logger;

    private long _leaseId;

    /// <summary>Connects using the configured endpoint.</summary>
    /// <param name="options">The etcd settings.</param>
    /// <param name="logger">Optional logger; lease loss and re-registration are reported through it.</param>
    /// <exception cref="ArgumentException">No connection string was supplied.</exception>
    public EtcdRegistry(EtcdDiscoveryOptions options, ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;

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
        await RegisterOnceAsync(key, value, ttl, cancellationToken).ConfigureAwait(false);

        // Renewal runs for the lifetime of the token. When it stops for good, etcd expires
        // the lease and deletes the key, which is what makes a crashed node disappear by
        // itself. But a lease lost while this process is still alive — etcd restarted, or
        // a partition outlasted the TTL — must be re-established, or the node keeps seeing
        // its peers while being permanently invisible to them.
        _ = Task.Run(() => SuperviseLeaseAsync(key, value, ttl, cancellationToken), CancellationToken.None);
    }

    /// <summary>Grants a fresh lease and writes the member key under it.</summary>
    private async Task RegisterOnceAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken)
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
    }

    /// <summary>Keeps the lease alive, re-registering from scratch whenever it is lost.</summary>
    private async Task SuperviseLeaseAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _client.LeaseKeepAlive(_leaseId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "etcd lease keep-alive for '{Key}' failed; re-registering", key);
            }

            // Keep-alive returning at all means the lease is gone or going. Pause, then
            // re-register with a fresh lease rather than renewing an id etcd may have
            // already forgotten.
            try
            {
                await Task.Delay(ReRegisterDelay, cancellationToken).ConfigureAwait(false);
                await RegisterOnceAsync(key, value, ttl, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation("Re-registered '{Key}' in etcd after losing the lease", key);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // etcd still unreachable; the loop pauses and tries again.
                _logger.LogWarning(ex, "Failed to re-register '{Key}' in etcd; retrying", key);
            }
        }
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
        // Capacity one with DropWrite coalesces bursts: each signal costs the consumer a
        // re-read of the whole prefix, so events arriving while one re-read is in flight
        // collapse into a single pending signal.
        var changes = System.Threading.Channels.Channel.CreateBounded<bool>(
            new System.Threading.Channels.BoundedChannelOptions(1)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropWrite
            });

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
