namespace GossNet.Protocol;

/// <summary>
/// Thrown when a discovery provider cannot determine a node's neighbours.
/// </summary>
/// <remarks>
/// Providers surface failures as this exception rather than returning an empty list,
/// so an unreachable discovery backend is never mistaken for a network of one.
/// </remarks>
public class NodeDiscoveryException : Exception
{
    /// <summary>Initializes the exception.</summary>
    public NodeDiscoveryException()
    {
    }

    /// <summary>Initializes the exception with a message.</summary>
    /// <param name="message">The error message.</param>
    public NodeDiscoveryException(string message) : base(message)
    {
    }

    /// <summary>Initializes the exception with a message and underlying cause.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying failure.</param>
    public NodeDiscoveryException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
