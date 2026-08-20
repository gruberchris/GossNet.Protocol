using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GossNet.Protocol;

/// <summary>
/// A gossip protocol node that exchanges messages with its neighbours over UDP.
/// </summary>
/// <typeparam name="T">The gossip message type.</typeparam>
public class GossNetNode<T> : IGossNetNode<T> where T : GossNetMessageBase, new()
{
    private static readonly TimeSpan MinReceiveRetryDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan MaxReceiveRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DisposeStopTimeout = TimeSpan.FromSeconds(5);

    private readonly GossNetConfiguration _configuration;
    private readonly IUdpClient _udpClient;
    private readonly ILogger<GossNetNode<T>> _logger;
    private readonly ExpiringMessageCache<T> _processedMessages;
    private readonly INodeDiscovery _discovery;

    /// <summary>
    /// Set when the discovery provider learns from traffic. Resolved once here rather than
    /// type-checked per message, since this is on the path of every send and receive.
    /// </summary>
    private readonly IObservingNodeDiscovery? _observer;

    /// <summary>
    /// Whether this node built its own discovery provider and must therefore dispose it.
    /// A caller-supplied provider may be shared between nodes and is left alone.
    /// </summary>
    private readonly bool _ownsDiscovery;

    private readonly string _nodePrefix;

    /// <summary>Serializes sends, which originate from both callers and the receive loop.</summary>
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    /// <summary>Guards <see cref="_subscribers"/> and the start/stop/dispose lifecycle.</summary>
    private readonly object _syncRoot = new();

    /// <summary>
    /// Copy-on-write snapshot so the receive loop can fan out without taking a lock.
    /// Only ever replaced under <see cref="_syncRoot"/>, never mutated in place.
    /// </summary>
    private Subscription[] _subscribers = [];

    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _processingTask;
    private Task? _watchTask;
    private bool _disposed;

    /// <summary>
    /// The latest list pushed by a watching provider, or null when none is active. Read on
    /// the send path in preference to querying the provider.
    /// </summary>
    private volatile IReadOnlyList<GossNetNodeHostEntry>? _watchedNeighbours;

    /// <summary>
    /// Initializes a new node.
    /// </summary>
    /// <param name="configuration">Node settings.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="udpClient">Optional transport; a real UDP socket is created when omitted.</param>
    public GossNetNode(GossNetConfiguration configuration, ILogger<GossNetNode<T>>? logger = null, IUdpClient? udpClient = null)
    {
        _configuration = configuration;

        // Resolved up front so a node configured for a discovery mechanism that is not
        // available fails here rather than silently gossiping to nobody.
        (_discovery, _ownsDiscovery) = GossNetDiscovery.CreateProvider(configuration);
        _observer = _discovery as IObservingNodeDiscovery;

        _udpClient = udpClient ?? new UdpClientAdapter(configuration.Port);
        _udpClient.EnableBroadcast = true;
        _logger = logger ?? NullLogger<GossNetNode<T>>.Instance;
        _nodePrefix = $"[{configuration.Hostname}:{configuration.Port}] ";

        _processedMessages = new ExpiringMessageCache<T>(TimeSpan.FromSeconds(configuration.MessageTtlSeconds));

        _logger.LogDebug("{Prefix}GossNetNode initialized", _nodePrefix);
    }

    /// <inheritdoc />
    public IGossNetSubscription<T> Subscribe()
    {
        lock (_syncRoot)
        {
            ThrowIfDisposed();

            var subscription = new Subscription(this, _configuration.SubscriberQueueCapacity);
            _subscribers = [.. _subscribers, subscription];

            _logger.LogDebug("{Prefix}Subscriber added ({Count} total)", _nodePrefix, _subscribers.Length);

            return subscription;
        }
    }

    private void Unsubscribe(Subscription subscription)
    {
        lock (_syncRoot)
        {
            var remaining = new List<Subscription>(_subscribers.Length);

            foreach (var existing in _subscribers)
            {
                if (!ReferenceEquals(existing, subscription))
                {
                    remaining.Add(existing);
                }
            }

            if (remaining.Count == _subscribers.Length)
            {
                return;
            }

            _subscribers = [.. remaining];
            _logger.LogDebug("{Prefix}Subscriber removed ({Count} remaining)", _nodePrefix, _subscribers.Length);
        }
    }

    /// <inheritdoc />
    public async Task<int> SendAsync(T message, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        _logger.LogDebug("{Prefix}Sending message id: {Id}", _nodePrefix, message.Id);

        MarkSelfAsNotified(message);
        _processedMessages.TryAdd(message);
        ObserveNotifiedNodes(message);

        var sent = await SocializeMessageAsync(message, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("{Prefix}Message id: {Id} sent to {Count} neighbours", _nodePrefix, message.Id, sent);

        return sent;
    }

    /// <inheritdoc />
    public void Start()
    {
        lock (_syncRoot)
        {
            ThrowIfDisposed();

            if (_processingTask is not null)
            {
                _logger.LogDebug("{Prefix}Start ignored; node is already running", _nodePrefix);
                return;
            }

            _logger.LogInformation("{Prefix}Starting GossNetNode", _nodePrefix);

            _cancellationTokenSource = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;

            // CancellationToken.None: the token cancels the loop from the inside, it must
            // not prevent the task being scheduled or the task would never observe it.
            _processingTask = Task.Run(() => ProcessLoopAsync(token), CancellationToken.None);

            if (_discovery is IWatchableNodeDiscovery watchable)
            {
                _watchTask = Task.Run(() => WatchLoopAsync(watchable, token), CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// Keeps <see cref="_watchedNeighbours"/> current from a provider that pushes changes.
    /// </summary>
    /// <remarks>
    /// A watch is an optimization, never a requirement. If the backend's change feed fails,
    /// the snapshot is dropped and the node returns to asking the provider per message —
    /// slower to notice membership changes, but still correct. Taking the node down because
    /// a watch broke would be strictly worse than polling.
    /// </remarks>
    private async Task WatchLoopAsync(IWatchableNodeDiscovery watchable, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var neighbours in watchable.WatchAsync(cancellationToken).ConfigureAwait(false))
            {
                _watchedNeighbours = neighbours;

                _logger.LogDebug("{Prefix}Discovery watch reported {Count} neighbours", _nodePrefix, neighbours.Count);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Prefix}Discovery watch failed; falling back to polling", _nodePrefix);
        }
        finally
        {
            _watchedNeighbours = null;
        }
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        CancellationTokenSource? cancellationTokenSource;
        Task? processingTask;
        Task? watchTask;

        lock (_syncRoot)
        {
            cancellationTokenSource = _cancellationTokenSource;
            processingTask = _processingTask;
            watchTask = _watchTask;
            _cancellationTokenSource = null;
            _processingTask = null;
            _watchTask = null;
        }

        if (cancellationTokenSource is null)
        {
            return;
        }

        _logger.LogInformation("{Prefix}Stopping GossNetNode", _nodePrefix);

#if NET8_0_OR_GREATER
        await cancellationTokenSource.CancelAsync().ConfigureAwait(false);
#else
        cancellationTokenSource.Cancel();
#endif

        if (processingTask is not null)
        {
            try
            {
                await processingTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Prefix}Error waiting for the processing loop to stop", _nodePrefix);
            }
        }

        if (watchTask is not null)
        {
            try
            {
                await watchTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Prefix}Error waiting for the discovery watch to stop", _nodePrefix);
            }
        }

        // A stopped node must not keep using membership a cancelled watch left behind.
        _watchedNeighbours = null;

        cancellationTokenSource.Dispose();

        _logger.LogDebug("{Prefix}GossNetNode stopped", _nodePrefix);
    }

    private async Task ProcessLoopAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("{Prefix}Message processing loop started", _nodePrefix);

        var consecutiveFailures = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var received = await _udpClient.ReceiveAsync(cancellationToken).ConfigureAwait(false);

                var data = Encoding.UTF8.GetString(received.Buffer);
                _logger.LogTrace("{Prefix}Received {Bytes} bytes from {EndPoint}", _nodePrefix, received.Buffer.Length, received.RemoteEndPoint);

                var message = new T();
                message.Deserialize(data);

                consecutiveFailures = 0;

                await ProcessMessageAsync(message, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                // The socket is gone; retrying would spin forever.
                _logger.LogDebug("{Prefix}Transport disposed; ending message processing loop", _nodePrefix);
                break;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                var delay = GetRetryDelay(consecutiveFailures);

                // Without this delay a persistently failing socket produces a hot loop
                // that burns a core and floods the log.
                _logger.LogError(ex, "{Prefix}Error in message processing loop (failure {Count}); retrying in {Delay}ms",
                    _nodePrefix, consecutiveFailures, delay.TotalMilliseconds);

                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        _logger.LogDebug("{Prefix}Message processing loop ended", _nodePrefix);
    }

    private static TimeSpan GetRetryDelay(int consecutiveFailures)
    {
        var exponent = Math.Min(consecutiveFailures - 1, 10);
        var milliseconds = MinReceiveRetryDelay.TotalMilliseconds * Math.Pow(2, exponent);

        return TimeSpan.FromMilliseconds(Math.Min(milliseconds, MaxReceiveRetryDelay.TotalMilliseconds));
    }

    private async Task<int> ProcessMessageAsync(T message, CancellationToken cancellationToken)
    {
        if (!_processedMessages.TryAdd(message))
        {
            _logger.LogDebug("{Prefix}Ignoring previously processed message id: {Id}", _nodePrefix, message.Id);
            return 0;
        }

        MarkSelfAsNotified(message);
        ObserveNotifiedNodes(message);
        PublishToSubscribers(message);

        var forwarded = await SocializeMessageAsync(message, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("{Prefix}Message id: {Id} processed and forwarded to {Count} neighbours", _nodePrefix, message.Id, forwarded);

        return forwarded;
    }

    private void PublishToSubscribers(T message)
    {
        // Lock-free read of the copy-on-write snapshot.
        var subscribers = _subscribers;

        // With no subscribers nothing is buffered at all. The previous implementation
        // wrote every message into a shared unbounded channel whether or not anyone
        // was reading, which grew without limit for the lifetime of the node.
        if (subscribers.Length == 0)
        {
            return;
        }

        var envelope = new GossNetChannelMessage<T> { Message = message };

        foreach (var subscriber in subscribers)
        {
            subscriber.Publish(envelope, _logger, _nodePrefix);
        }
    }

    /// <summary>
    /// Feeds a message's notified list to a discovery provider that learns from traffic.
    /// </summary>
    /// <remarks>
    /// Wrapped because this runs on the receive loop: a provider that throws here would
    /// otherwise be indistinguishable from a transport failure and would trip the loop's
    /// backoff, stalling every message the node handles.
    /// </remarks>
    private void ObserveNotifiedNodes(T message)
    {
        if (_observer is null)
        {
            return;
        }

        try
        {
            _observer.Observe(message.NotifiedNodes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Prefix}Discovery provider threw while observing message id: {Id}", _nodePrefix, message.Id);
        }
    }

    private void MarkSelfAsNotified(T message)
    {
        foreach (var notified in message.NotifiedNodes)
        {
            if (notified.Hostname == _configuration.Hostname && notified.Port == _configuration.Port)
            {
                return;
            }
        }

        message.NotifiedNodes =
        [
            .. message.NotifiedNodes,
            new GossNetNodeHostEntry { Hostname = _configuration.Hostname, Port = _configuration.Port }
        ];

        _logger.LogTrace("{Prefix}Marked self as notified for message id: {Id}", _nodePrefix, message.Id);
    }

    private async Task<int> SocializeMessageAsync(T message, CancellationToken cancellationToken)
    {
        var data = Encoding.UTF8.GetBytes(message.Serialize());

        // Checked here rather than in Serialize so that custom Serialize overrides are
        // covered too. Without it an oversized payload fails at the socket with an
        // opaque error that gives no hint the message itself was the problem.
        if (data.Length > GossNetMessageBase.MaxDatagramBytes)
        {
            throw new InvalidOperationException(
                $"Serialized message id {message.Id} is {data.Length} bytes, which exceeds the maximum UDP " +
                $"datagram payload of {GossNetMessageBase.MaxDatagramBytes} bytes. Reduce the message size " +
                "or split it across multiple messages.");
        }

        // A watching provider has already told us the membership, so asking again would
        // just re-read its cache.
        var neighbours = _watchedNeighbours
            ?? await _discovery.GetNeighboursAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("{Prefix}Found {Count} neighbours", _nodePrefix, neighbours.Count);

        var sentCount = 0;

        foreach (var neighbour in neighbours)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsAlreadyNotified(message, neighbour))
            {
                _logger.LogTrace("{Prefix}Skipping already notified neighbour {Neighbour} for message id: {Id}",
                    _nodePrefix, neighbour, message.Id);
                continue;
            }

            await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                var sent = await _udpClient.SendAsync(data, neighbour.Hostname, neighbour.Port, cancellationToken).ConfigureAwait(false);

                if (sent > 0)
                {
                    sentCount++;
                    _logger.LogTrace("{Prefix}Sent message id: {Id} as {Bytes} bytes to {Neighbour}",
                        _nodePrefix, message.Id, sent, neighbour);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One unreachable neighbour must not stop the message reaching the others.
                _logger.LogError(ex, "{Prefix}Error sending message id: {Id} to {Neighbour}", _nodePrefix, message.Id, neighbour);
            }
            finally
            {
                _sendGate.Release();
            }
        }

        return sentCount;
    }

    private static bool IsAlreadyNotified(T message, GossNetNodeHostEntry neighbour)
    {
        foreach (var notified in message.NotifiedNodes)
        {
            if (notified.Hostname == neighbour.Hostname && notified.Port == neighbour.Port)
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        await StopAsync().ConfigureAwait(false);
        ReleaseResources();

        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases resources held by the node.
    /// </summary>
    /// <param name="disposing">True when called from <see cref="Dispose()"/>.</param>
    /// <remarks>
    /// Prefer <see cref="DisposeAsync"/>: stopping is inherently asynchronous, so the
    /// synchronous path can only wait a bounded time for the receive loop to unwind.
    /// </remarks>
    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }

        CancellationTokenSource? cancellationTokenSource;
        Task? processingTask;
        Task? watchTask;

        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            cancellationTokenSource = _cancellationTokenSource;
            processingTask = _processingTask;
            watchTask = _watchTask;
            _cancellationTokenSource = null;
            _processingTask = null;
            _watchTask = null;
        }

        _logger.LogInformation("{Prefix}Disposing GossNetNode", _nodePrefix);

        try
        {
            cancellationTokenSource?.Cancel();
            processingTask?.Wait(DisposeStopTimeout);
            watchTask?.Wait(DisposeStopTimeout);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Prefix}Error stopping the processing loop during disposal", _nodePrefix);
        }
        finally
        {
            cancellationTokenSource?.Dispose();
            ReleaseResources();
        }
    }

    private void ReleaseResources()
    {
        Subscription[] subscribers;

        lock (_syncRoot)
        {
            subscribers = _subscribers;
            _subscribers = [];
        }

        // Completing each reader ends any `await foreach` a consumer is running.
        foreach (var subscriber in subscribers)
        {
            subscriber.Complete();
        }

        _udpClient.Dispose();
        _sendGate.Dispose();

        // MemoryCache owns a timer, so failing to dispose the cache leaked one per node.
        _processedMessages.Dispose();

        // Only a provider this node built. Multicast discovery holds a socket and two
        // background loops, so leaving it undisposed leaks both for the process lifetime.
        if (_ownsDiscovery)
        {
            (_discovery as IDisposable)?.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(GossNetNode<T>));
        }
    }

    /// <summary>
    /// A subscriber's private, bounded delivery queue.
    /// </summary>
    private sealed class Subscription(GossNetNode<T> node, int capacity) : IGossNetSubscription<T>
    {
        private readonly Channel<GossNetChannelMessage<T>> _channel =
            Channel.CreateBounded<GossNetChannelMessage<T>>(new BoundedChannelOptions(capacity)
            {
                // Drop this subscriber's oldest message rather than blocking the shared
                // receive loop: a slow consumer degrades only itself.
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = true
            });

        private int _disposed;

        public ChannelReader<GossNetChannelMessage<T>> Reader => _channel.Reader;

        internal void Publish(GossNetChannelMessage<T> envelope, ILogger logger, string nodePrefix)
        {
            if (_channel.Reader.CanCount && _channel.Reader.Count >= capacity)
            {
                logger.LogWarning("{Prefix}Subscriber queue is full ({Capacity}); dropping the oldest message",
                    nodePrefix, capacity);
            }

            // Never blocks: DropOldest evicts instead of waiting.
            _channel.Writer.TryWrite(envelope);
        }

        internal void Complete() => _channel.Writer.TryComplete();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            node.Unsubscribe(this);
            Complete();
        }
    }
}
