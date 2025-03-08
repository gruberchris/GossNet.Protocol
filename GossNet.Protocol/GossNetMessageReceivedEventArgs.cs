namespace GossNet.Protocol;

public class GossNetMessageReceivedEventArgs<T> : EventArgs where T : GossNetMessageBase
{
    public required T Message { get; init; }
}