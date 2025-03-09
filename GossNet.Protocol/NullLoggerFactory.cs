using Microsoft.Extensions.Logging;

namespace GossNet.Protocol;

internal static class NullLoggerFactory
{
    public static ILoggerFactory Instance { get; } = new Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory();
}