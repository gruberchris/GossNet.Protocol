namespace GossNet.Protocol;

public class GossNetNodeHostEntry : IComparable<GossNetNodeHostEntry>
{
    public required string Hostname { get; init; }
    public int Port { get; init; }
    
    public override string ToString() => $"{Hostname}:{Port}";
    
    public int CompareTo(GossNetNodeHostEntry? other)
    {
        return other == null ? 1 : string.Compare(ToString(), other.ToString(), StringComparison.Ordinal);
    }
    
    public override bool Equals(object? obj)
    {
        if (obj is not GossNetNodeHostEntry other)
            return false;
            
        return Hostname == other.Hostname && Port == other.Port;
    }
    
    public override int GetHashCode()
    {
        return HashCode.Combine(Hostname, Port);
    }
    
    public static bool operator ==(GossNetNodeHostEntry? left, GossNetNodeHostEntry? right)
    {
        if (left is null)
            return right is null;
        
        return left.Equals(right);
    }
    
    public static bool operator !=(GossNetNodeHostEntry? left, GossNetNodeHostEntry? right)
    {
        return !(left == right);
    }
    
    public static bool operator <(GossNetNodeHostEntry? left, GossNetNodeHostEntry? right)
    {
        if (left is null)
            return right is not null;
            
        return left.CompareTo(right) < 0;
    }
    
    public static bool operator <=(GossNetNodeHostEntry? left, GossNetNodeHostEntry? right)
    {
        if (left is null)
            return true;
            
        return left.CompareTo(right) <= 0;
    }
    
    public static bool operator >(GossNetNodeHostEntry? left, GossNetNodeHostEntry? right)
    {
        return right < left;
    }
    
    public static bool operator >=(GossNetNodeHostEntry? left, GossNetNodeHostEntry? right)
    {
        return right <= left;
    }
}