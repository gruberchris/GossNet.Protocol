namespace GossNet.Protocol;

/// <summary>
/// Identifies a node by host and port.
/// </summary>
public class GossNetNodeHostEntry : IEquatable<GossNetNodeHostEntry>, IComparable<GossNetNodeHostEntry>
{
    /// <summary>Gets the hostname or IP address.</summary>
    public required string Hostname { get; init; }

    /// <summary>Gets the UDP port.</summary>
    public int Port { get; init; }

    /// <inheritdoc />
    public override string ToString() => $"{Hostname}:{Port}";

    /// <summary>
    /// Orders by hostname, then by port.
    /// </summary>
    /// <param name="other">The entry to compare against.</param>
    /// <remarks>
    /// Compares the fields directly. The previous implementation formatted both sides
    /// with <see cref="ToString"/> and compared the strings, allocating twice per
    /// comparison on a path that runs once per neighbour per message — and ordering
    /// ports lexically, so "host:10" sorted before "host:9".
    /// </remarks>
    public int CompareTo(GossNetNodeHostEntry? other)
    {
        if (other is null)
        {
            return 1;
        }

        var byHostname = string.Compare(Hostname, other.Hostname, StringComparison.Ordinal);

        return byHostname != 0 ? byHostname : Port.CompareTo(other.Port);
    }

    /// <inheritdoc />
    public bool Equals(GossNetNodeHostEntry? other) =>
        other is not null && (ReferenceEquals(this, other) || (Port == other.Port && Hostname == other.Hostname));

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as GossNetNodeHostEntry);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Hostname, Port);

    /// <summary>Determines whether two entries denote the same host and port.</summary>
    public static bool operator ==(GossNetNodeHostEntry? left, GossNetNodeHostEntry? right) =>
        left is null ? right is null : left.Equals(right);

    /// <summary>Determines whether two entries denote different hosts or ports.</summary>
    public static bool operator !=(GossNetNodeHostEntry? left, GossNetNodeHostEntry? right) => !(left == right);

    /// <summary>Determines whether the left entry sorts before the right.</summary>
    public static bool operator <(GossNetNodeHostEntry? left, GossNetNodeHostEntry? right) =>
        left is null ? right is not null : left.CompareTo(right) < 0;

    /// <summary>Determines whether the left entry sorts before or equal to the right.</summary>
    public static bool operator <=(GossNetNodeHostEntry? left, GossNetNodeHostEntry? right) =>
        left is null || left.CompareTo(right) <= 0;

    /// <summary>Determines whether the left entry sorts after the right.</summary>
    public static bool operator >(GossNetNodeHostEntry? left, GossNetNodeHostEntry? right) => right < left;

    /// <summary>Determines whether the left entry sorts after or equal to the right.</summary>
    public static bool operator >=(GossNetNodeHostEntry? left, GossNetNodeHostEntry? right) => right <= left;
}
