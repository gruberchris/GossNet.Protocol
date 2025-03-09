using System.Text;
using Microsoft.Extensions.Logging;

namespace GossNet.Protocol;

public class GossNetNode<T> : IGossNetNode<T> where T : GossNetMessageBase, new()
{
    private readonly GossNetConfiguration _configuration;
    private readonly IUdpClient _udpClient;
    private readonly ILogger<GossNetNode<T>> _logger;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _processingTask;
    private event EventHandler<GossNetMessageReceivedEventArgs<T>>? GossNetMessageReceived;
    private readonly SemaphoreSlim _gossNetMessageReceivedSemaphoreSlim = new(1, 1);
    private readonly SemaphoreSlim _udpClientReceiveSemaphoreSlim = new(1, 1);
    private readonly SemaphoreSlim _udpClientSendSemaphoreSlim = new(1, 1);

    public GossNetNode(GossNetConfiguration configuration, ILogger<GossNetNode<T>>? logger = null, IUdpClient? udpClient = null)
    {
        _configuration = configuration;
        _udpClient = udpClient ?? new UdpClientAdapter(configuration.Port);
        _udpClient.EnableBroadcast = true;
        _logger = logger ?? NullLoggerFactory.Instance.CreateLogger<GossNetNode<T>>();
        
        _logger.LogDebug("GossNetNode initialized on {Hostname}:{Port}", configuration.Hostname, configuration.Port);
    }

    public async Task SubscribeAsync(EventHandler<GossNetMessageReceivedEventArgs<T>> handler)
    {
        await _gossNetMessageReceivedSemaphoreSlim.WaitAsync();

        try
        {
            GossNetMessageReceived += handler;
            _logger.LogDebug("Event handler subscribed");
        }
        finally
        {
            _gossNetMessageReceivedSemaphoreSlim.Release();
        }
    }

    public async Task UnsubscribeAsync(EventHandler<GossNetMessageReceivedEventArgs<T>> handler)
    {
        await _gossNetMessageReceivedSemaphoreSlim.WaitAsync();

        try
        {
            GossNetMessageReceived -= handler;
            _logger.LogDebug("Event handler unsubscribed");
        }
        finally
        {
            _gossNetMessageReceivedSemaphoreSlim.Release();
        }
    }

    public async Task<int> SendAsync(T message)
    {
        _logger.LogDebug("Sending message: {Data}", message.Serialize());
        
        MarkSelfAsNotified(message);

        var result = await SocializeMessageAsync(message);
        _logger.LogDebug("Message sent to {Count} neighbors: {Neighbors}", result, string.Join(", ", message.NotifiedNodes));
        
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
            _logger.LogTrace("Received message from {EndPoint}: {Data}", result.RemoteEndPoint, data);
            
            var message = new T();
            message.Deserialize(data);
            resultMessage = message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error receiving message: {Message}", ex.Message);
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
        _logger.LogInformation("Starting GossNetNode: {Hostname}:{Port}", _configuration.Hostname, _configuration.Port);
        
        _cancellationTokenSource = new CancellationTokenSource();
        var token = _cancellationTokenSource.Token;

        _processingTask = Task.Run(async () =>
        {
            _logger.LogDebug("Message processing loop started");
            
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var message = await ReceiveAsync();
                    await ProcessMessageAsync(message);
                }
                catch (OperationCanceledException oce)
                {
                    _logger.LogDebug(oce, "Message processing loop was canceled");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in message processing loop: {Message}", ex.Message);
                }
            }
            
            _logger.LogDebug("Message processing loop ended");
        }, token);
    }

    public async Task StopAsync()
    {
        _logger.LogInformation("Stopping GossNetNode on {Hostname}:{Port}", _configuration.Hostname, _configuration.Port);
        
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
                    _logger.LogDebug(oce, "Processing task was canceled");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error waiting for processing task to complete");
                }
            }

            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = null;
            _processingTask = null;
            
            _logger.LogDebug("GossNetNode stopped");
        }
    }

    private async Task<int> ProcessMessageAsync(T message)
    {
        _logger.LogDebug("Processing received message: {Data}", message.Serialize());
        
        MarkSelfAsNotified(message);

        await InvokeGossNetMessageReceivedAsync(new GossNetMessageReceivedEventArgs<T> { Message = message });

        var result = await SocializeMessageAsync(message);
        
        _logger.LogDebug("Message processed and forwarded to {Count} nodes", result);
        
        return result;
    }

    private void MarkSelfAsNotified(T message)
    {
        if (!message.NotifiedNodes.Any(n => n.Hostname == _configuration.Hostname && n.Port == _configuration.Port))
        {
            message.NotifiedNodes = message.NotifiedNodes.Append(new GossNetNodeHostEntry {
                Hostname = _configuration.Hostname,
                Port = _configuration.Port
            }).ToArray();
            
            _logger.LogTrace("Marked self ({Host}:{Port}) as notified for message id: {Id}", _configuration.Hostname, _configuration.Port, message.Id);
        }
    }

    private async Task InvokeGossNetMessageReceivedAsync(GossNetMessageReceivedEventArgs<T> args)
    {
        await _gossNetMessageReceivedSemaphoreSlim.WaitAsync();

        try
        {
            _logger.LogTrace("Invoking message received handlers");
            GossNetMessageReceived?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error invoking message received handlers");
            throw;
        }
        finally
        {
            _gossNetMessageReceivedSemaphoreSlim.Release();
        }
    }

    private async Task<int> SocializeMessageAsync(T message)
    {
        var data = Encoding.UTF8.GetBytes(message.Serialize());

        var neighbors = GossNetDiscovery.GetNeighbours(_configuration).ToArray();
        var neighborsString = string.Join(", ", neighbors.Select(n => n.ToString()));
        _logger.LogDebug("Found {Count} neighbors: {Neighbors}", neighbors.Length, neighborsString);

        var sentCount = 0;

        foreach (var neighbour in neighbors)
        {
            if (message.NotifiedNodes.Any(n => n.Hostname == neighbour.Hostname && n.Port == neighbour.Port))
            {
                _logger.LogTrace("Skipping already notified neighbor {Host}:{Port} for message id: {Id}", neighbour.Hostname, neighbour.Port, message.Id);
                continue;
            }

            var hostname = neighbour.Hostname;
            var port = neighbour.Port;

            await _udpClientSendSemaphoreSlim.WaitAsync();

            try
            {
                _logger.LogTrace("Sending message id: {Id} to {Host}:{Port}", message.Id, hostname, port);
                var result = await _udpClient.SendAsync(data, data.Length, hostname, port);
                
                if (result > 0)
                {
                    sentCount++;
                    _logger.LogTrace("Successfully sent message id: {Id} as  {Bytes} bytes to {Host}:{Port}",  message.Id, result, hostname, port);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending message id: {Id} to {Host}:{Port}", message.Id, hostname, port);
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

        _logger.LogInformation("Disposing GossNetNode");
        
        try
        {
            _cancellationTokenSource?.Cancel();
            _processingTask?.Wait(TimeSpan.FromSeconds(5));
            _udpClient.Dispose();
            _cancellationTokenSource?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during GossNetNode disposal");
        }
    }
}