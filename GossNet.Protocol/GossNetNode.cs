using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace GossNet.Protocol;

public class GossNetNode<T> : IGossNetNode<T> where T : GossNetMessageBase, new()
{
    private readonly GossNetConfiguration _configuration;
    private readonly IUdpClient _udpClient;
    private readonly ILogger<GossNetNode<T>> _logger;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _processingTask;
    private readonly ExpiringMessageCache<T> _processedMessages;
    private readonly string _nodePrefix;
    private readonly Channel<GossNetChannelMessage<T>> _messageChannel;
    private readonly List<ChannelReader<GossNetChannelMessage<T>>> _subscribers = new();

    private readonly SemaphoreSlim _channelSubscribersSemaphoreSlim = new(1, 1);
    private readonly SemaphoreSlim _udpClientReceiveSemaphoreSlim = new(1, 1);
    private readonly SemaphoreSlim _udpClientSendSemaphoreSlim = new(1, 1);

    public GossNetNode(GossNetConfiguration configuration, ILogger<GossNetNode<T>>? logger = null, IUdpClient? udpClient = null)
    {
        _configuration = configuration;
        _udpClient = udpClient ?? new UdpClientAdapter(configuration.Port);
        _udpClient.EnableBroadcast = true;
        _logger = logger ?? NullLoggerFactory.Instance.CreateLogger<GossNetNode<T>>();
        _nodePrefix = $"[{_configuration.Hostname}:{_configuration.Port}] ";

        _processedMessages = new ExpiringMessageCache<T>(
            TimeSpan.FromSeconds(configuration.MessageTtlSeconds));

        _messageChannel = Channel.CreateUnbounded<GossNetChannelMessage<T>>(
            new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });

        _logger.LogDebug("{Prefix}GossNetNode initialized", _nodePrefix);
    }

    public async Task<ChannelReader<GossNetChannelMessage<T>>> SubscribeAsync()
    {
        await _channelSubscribersSemaphoreSlim.WaitAsync();

        try
        {
            var reader = _messageChannel.Reader;
            _subscribers.Add(reader);
            _logger.LogDebug("{Prefix}Channel subscriber added", _nodePrefix);
            return reader;
        }
        finally
        {
            _channelSubscribersSemaphoreSlim.Release();
        }
    }

    public async Task UnsubscribeAsync(ChannelReader<GossNetChannelMessage<T>> reader)
    {
        await _channelSubscribersSemaphoreSlim.WaitAsync();

        try
        {
            if (_subscribers.Remove(reader))
            {
                _logger.LogDebug("{Prefix}Channel subscriber removed", _nodePrefix);
            }
        }
        finally
        {
            _channelSubscribersSemaphoreSlim.Release();
        }
    }

    public async Task<int> SendAsync(T message)
    {
        _logger.LogDebug("{Prefix}Sending message: {Data}", _nodePrefix, message.Serialize());

        MarkSelfAsNotified(message);
        _processedMessages.TryAdd(message);

        var result = await SocializeMessageAsync(message);
        _logger.LogDebug("{Prefix}Message sent to {Count} neighbors: {Neighbors}", _nodePrefix, result, string.Join(", ", message.NotifiedNodes));

        return result;
    }

    private async Task<T> ReceiveAsync()
    {
        T resultMessage;

        await _udpClientReceiveSemaphoreSlim.WaitAsync();

        try
        {
            var result = await _udpClient.ReceiveAsync();
            var data = Encoding.UTF8.GetString(result.Buffer);
            _logger.LogTrace("{Prefix}Received message from {EndPoint}: {Data}", _nodePrefix, result.RemoteEndPoint, data);

            var message = new T();
            message.Deserialize(data);
            resultMessage = message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Prefix}Error receiving message: {Message}", _nodePrefix, ex.Message);
            throw;
        }
        finally
        {
            _udpClientReceiveSemaphoreSlim.Release();
        }

        return resultMessage;
    }

    public void Start()
    {
        _logger.LogInformation("{Prefix}Starting GossNetNode", _nodePrefix);

        _cancellationTokenSource = new CancellationTokenSource();
        var token = _cancellationTokenSource.Token;

        _processingTask = Task.Run(async () =>
        {
            _logger.LogDebug("{Prefix}Message processing loop started", _nodePrefix);

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var message = await ReceiveAsync();
                    await ProcessMessageAsync(message);
                }
                catch (OperationCanceledException oce)
                {
                    _logger.LogDebug(oce, "{Prefix}Message processing loop was canceled", _nodePrefix);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "{Prefix}Error in message processing loop: {Message}", _nodePrefix, ex.Message);
                }
            }

            _logger.LogDebug("{Prefix}Message processing loop ended", _nodePrefix);
        }, token);
    }

    public async Task StopAsync()
    {
        _logger.LogInformation("{Prefix}Stopping GossNetNode", _nodePrefix);

        if (_cancellationTokenSource != null)
        {
            await _cancellationTokenSource.CancelAsync();

            if (_processingTask != null)
            {
                try
                {
                    await _processingTask;
                }
                catch (OperationCanceledException oce)
                {
                    _logger.LogDebug(oce, "{Prefix}Processing task was canceled", _nodePrefix);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "{Prefix}Error waiting for processing task to complete", _nodePrefix);
                }
            }

            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = null;
            _processingTask = null;

            // Complete the channel when stopping
            _messageChannel.Writer.Complete();

            _logger.LogDebug("{Prefix}GossNetNode stopped", _nodePrefix);
        }
    }

    private async Task<int> ProcessMessageAsync(T message)
    {
        _logger.LogDebug("{Prefix}Processing received message: {Data}", _nodePrefix, message.Serialize());

        if (!_processedMessages.TryAdd(message))
        {
            _logger.LogDebug("{Prefix}Ignoring previously processed message id: {Id}", _nodePrefix, message.Id);
            return 0;
        }

        MarkSelfAsNotified(message);
        
        var args = new GossNetChannelMessage<T> { Message = message };
        await WriteToChannelAsync(args);

        var result = await SocializeMessageAsync(message);

        _logger.LogDebug("{Prefix}Message processed and forwarded to {Count} nodes", _nodePrefix, result);

        return result;
    }

    private async Task WriteToChannelAsync(GossNetChannelMessage<T> args)
    {
        try
        {
            _logger.LogTrace("{Prefix}Writing message to channel", _nodePrefix);
            await _messageChannel.Writer.WriteAsync(args);
            _logger.LogTrace("{Prefix}Message written to channel successfully", _nodePrefix);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Prefix}Error writing to message channel", _nodePrefix);
            throw;
        }
    }

    private void MarkSelfAsNotified(T message)
    {
        if (!message.NotifiedNodes.Any(n => n.Hostname == _configuration.Hostname && n.Port == _configuration.Port))
        {
            message.NotifiedNodes = message.NotifiedNodes.Append(new GossNetNodeHostEntry {
                Hostname = _configuration.Hostname,
                Port = _configuration.Port
            }).ToArray();

            _logger.LogTrace("{Prefix}Marked self as notified for message id: {Id}", _nodePrefix, message.Id);
        }
    }

    private async Task<int> SocializeMessageAsync(T message)
    {
        var data = Encoding.UTF8.GetBytes(message.Serialize());

        var neighbors = GossNetDiscovery.GetNeighbours(_configuration).ToArray();
        var neighborsString = string.Join(", ", neighbors.Select(n => n.ToString()));
        _logger.LogDebug("{Prefix}Found {Count} neighbors: {Neighbors}", _nodePrefix, neighbors.Length, neighborsString);

        var sentCount = 0;

        foreach (var neighbour in neighbors)
        {
            if (message.NotifiedNodes.Any(n => n.Hostname == neighbour.Hostname && n.Port == neighbour.Port))
            {
                _logger.LogTrace("{Prefix}Skipping already notified neighbor {Host}:{Port} for message id: {Id}", 
                    _nodePrefix, neighbour.Hostname, neighbour.Port, message.Id);
                continue;
            }

            var hostname = neighbour.Hostname;
            var port = neighbour.Port;

            await _udpClientSendSemaphoreSlim.WaitAsync();

            try
            {
                _logger.LogTrace("{Prefix}Sending message id: {Id} to {Host}:{Port}", _nodePrefix, message.Id, hostname, port);
                var result = await _udpClient.SendAsync(data, data.Length, hostname, port);

                if (result > 0)
                {
                    sentCount++;
                    _logger.LogTrace("{Prefix}Successfully sent message id: {Id} as {Bytes} bytes to {Host}:{Port}", 
                        _nodePrefix, message.Id, result, hostname, port);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Prefix}Error sending message id: {Id} to {Host}:{Port}", _nodePrefix, message.Id, hostname, port);
            }
            finally
            {
                _udpClientSendSemaphoreSlim.Release();
            }
        }

        return sentCount;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing) return;

        _logger.LogInformation("{Prefix}Disposing GossNetNode", _nodePrefix);

        try
        {
            _cancellationTokenSource?.Cancel();
            _processingTask?.Wait(TimeSpan.FromSeconds(5));
            _udpClient.Dispose();
            _cancellationTokenSource?.Dispose();
            _messageChannel.Writer.Complete();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Prefix}Error during GossNetNode disposal", _nodePrefix);
        }
    }
}