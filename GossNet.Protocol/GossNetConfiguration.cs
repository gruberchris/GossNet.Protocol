namespace GossNet.Protocol;

public enum NodeDiscovery
{
    Dns,
    Consul,
    Kubernetes,
    Docker,
    StaticList
}

public class GossNetConfiguration
{
    public required string Hostname { get; init; }
    
    public int Port { get; init; } = 9055;
    
    public NodeDiscovery NodeDiscovery { get; init; }
    
    public  IEnumerable<GossNetNodeHostEntry> StaticNodes { get; init; } = new List<GossNetNodeHostEntry>();
}