namespace GossNet.Protocol;

public class GossNetChannelMessage<T> where T : GossNetMessageBase
{
    public required T Message { get; init; }
}