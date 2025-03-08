using System.Net.Sockets;
using System.Text;

namespace GossNet.Protocol;

public class GossNetNode<T> : IDisposable where T : GossNetMessageBase, new()
{
    private  readonly GossNetConfiguration _configuration;
    private readonly UdpClient _udpClient;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _processingTask;
    private event EventHandler<GossNetMessageReceivedEventArgs<T>>? GossNetMessageReceived;
    private readonly SemaphoreSlim _gossNetMessageReceivedSemaphoreSlim = new(1, 1);
    private readonly SemaphoreSlim _udpClientReceiveSemaphoreSlim = new(1, 1);
    private readonly SemaphoreSlim _udpClientSendSemaphoreSlim = new(1, 1);

    public GossNetNode(GossNetConfiguration configuration)
    {
        _configuration = configuration;
        _udpClient = new UdpClient( _configuration.Port);
        _udpClient.EnableBroadcast = true;
    }
    
    public async Task SubscribeAsync(EventHandler<GossNetMessageReceivedEventArgs<T>> handler)
    {
        await _gossNetMessageReceivedSemaphoreSlim.WaitAsync();

        try
        {
            GossNetMessageReceived += handler;
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
        }
        finally
        {
            _gossNetMessageReceivedSemaphoreSlim.Release();
        }
    }
    
    public async Task<int> SendAsync(T message)
    {
        MarkSelfAsNotified(message);
        
        await InvokeGossNetMessageReceivedAsync(new GossNetMessageReceivedEventArgs<T> { Message = message });
        
        return await SocializeMessageAsync(message);
    }
    
    protected virtual async Task<T> ReceiveAsync()
    {
        T resultMessage;
        
        await _udpClientReceiveSemaphoreSlim.WaitAsync();
        
        try
        {
            var result = await _udpClient.ReceiveAsync();
            var data = Encoding.UTF8.GetString(result.Buffer);
            var message = new T();
            message.Deserialize(data);
            resultMessage = message;
        }
        finally
        {
            _udpClientReceiveSemaphoreSlim.Release();
        }
        
        return resultMessage;
    }

    public void Start()
    {
        _cancellationTokenSource = new CancellationTokenSource();
        var token = _cancellationTokenSource.Token;

        _processingTask = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                var message = await ReceiveAsync();
                var result = await ProcessMessageAsync(message);
            }
        }, token);
    }
    
    public async Task StopAsync()
    {
        if (_cancellationTokenSource != null)
        {
            await _cancellationTokenSource.CancelAsync();
            
            if (_processingTask != null)
            {
                await _processingTask;
            }
            
            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = null;
            _processingTask = null;
        }
    }
    
    protected virtual async Task<int> ProcessMessageAsync(T message)
    {
        MarkSelfAsNotified(message);
        
        await InvokeGossNetMessageReceivedAsync(new GossNetMessageReceivedEventArgs<T> { Message = message });
        
        var result = await SocializeMessageAsync(message);
        
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
        }
    }
    
    private async Task InvokeGossNetMessageReceivedAsync(GossNetMessageReceivedEventArgs<T> args)
    {
        await _gossNetMessageReceivedSemaphoreSlim.WaitAsync();

        try
        {
            GossNetMessageReceived?.Invoke(this, args);
        }
        finally
        {
            _gossNetMessageReceivedSemaphoreSlim.Release();
        }
    }
    
    protected virtual async Task<int> SocializeMessageAsync(T message)
    {
        var data = Encoding.UTF8.GetBytes(message.Serialize());
        
        var neighbours = GossNetDiscovery.GetNeighbours(_configuration);
        
        var result = 0;
        
        foreach (var neighbour in neighbours)
        {
            if (message.NotifiedNodes.Any(n => n.Hostname == neighbour.Hostname && n.Port == neighbour.Port)) continue;
            
            var hostname = neighbour.Hostname;
            var port = neighbour.Port;
                
            await _udpClientSendSemaphoreSlim.WaitAsync();

            try
            {
                result = await _udpClient.SendAsync(data, data.Length, hostname, port);
            }
            finally
            {
                _udpClientSendSemaphoreSlim.Release();
            }
        }
        
        return result;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing) return;
        
        _cancellationTokenSource?.Cancel();
        _processingTask?.Wait(TimeSpan.FromSeconds(5));
        _udpClient.Dispose();
        _cancellationTokenSource?.Dispose();
    }
}