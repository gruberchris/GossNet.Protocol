using System.Security.Cryptography;

namespace GossNet.Protocol;

/// <summary>
/// Authenticates datagrams with HMAC-SHA256 under a shared key.
/// </summary>
/// <remarks>
/// <para>
/// Frame layout: 2-byte magic (<c>GN</c>), 1-byte version, 32-byte tag, then the payload.
/// The tag covers the payload, so any alteration — injection, tampering, truncation — fails
/// verification and the datagram is dropped before it is parsed.
/// </para>
/// <para>
/// This provides authenticity and integrity, <strong>not confidentiality</strong>:
/// payloads remain readable on the wire. The trust model is one shared key per cluster —
/// every holder of the key can produce messages indistinguishable from any other member's.
/// </para>
/// <para>
/// Key rotation: sign with the primary key while also accepting older keys, roll the new
/// key out to every node as an accepted key first, then promote it to primary everywhere,
/// then drop the old key.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var configuration = new GossNetConfiguration
/// {
///     Hostname = "10.0.0.4",
///     Port = 9055,
///     DatagramProtector = new HmacDatagramProtector(clusterKey)
/// };
/// </code>
/// </example>
public sealed class HmacDatagramProtector : IDatagramProtector
{
    /// <summary>Shortest key accepted, in bytes. Anything shorter is trivially brute-forced.</summary>
    public const int MinKeyBytes = 16;

    private const byte MagicHigh = (byte)'G';
    private const byte MagicLow = (byte)'N';
    private const byte Version = 0x01;
    private const int TagBytes = 32;
    private const int HeaderBytes = 2 + 1 + TagBytes;

    private readonly byte[] _primaryKey;
    private readonly byte[][] _acceptedKeys;

    /// <summary>
    /// Initializes the protector.
    /// </summary>
    /// <param name="key">The key used to sign and verify.</param>
    /// <param name="acceptedKeys">
    /// Additional keys accepted for verification only, allowing rotation without a
    /// simultaneous cluster-wide restart.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    /// <exception cref="ArgumentException">A key is shorter than <see cref="MinKeyBytes"/> bytes.</exception>
    public HmacDatagramProtector(byte[] key, IEnumerable<byte[]>? acceptedKeys = null)
    {
        if (key is null)
        {
            throw new ArgumentNullException(nameof(key));
        }

        ValidateKey(key, nameof(key));

        // Copied so a caller mutating its array afterwards cannot desynchronize a cluster.
        _primaryKey = (byte[])key.Clone();

        var accepted = new List<byte[]> { _primaryKey };

        foreach (var additional in acceptedKeys ?? [])
        {
            if (additional is null)
            {
                throw new ArgumentException("Accepted keys cannot be null.", nameof(acceptedKeys));
            }

            ValidateKey(additional, nameof(acceptedKeys));
            accepted.Add((byte[])additional.Clone());
        }

        _acceptedKeys = [.. accepted];
    }

    /// <inheritdoc />
    public int Overhead => HeaderBytes;

    /// <inheritdoc />
    public byte[] Protect(byte[] payload)
    {
        if (payload is null)
        {
            throw new ArgumentNullException(nameof(payload));
        }

        var datagram = new byte[HeaderBytes + payload.Length];

        datagram[0] = MagicHigh;
        datagram[1] = MagicLow;
        datagram[2] = Version;

        var tag = ComputeTag(_primaryKey, payload, 0, payload.Length);
        Buffer.BlockCopy(tag, 0, datagram, 3, TagBytes);
        Buffer.BlockCopy(payload, 0, datagram, HeaderBytes, payload.Length);

        return datagram;
    }

    /// <inheritdoc />
    public bool TryUnprotect(byte[] datagram, out byte[] payload)
    {
        payload = null!;

        if (datagram is null ||
            datagram.Length < HeaderBytes ||
            datagram[0] != MagicHigh ||
            datagram[1] != MagicLow ||
            datagram[2] != Version)
        {
            return false;
        }

        foreach (var key in _acceptedKeys)
        {
            var expected = ComputeTag(key, datagram, HeaderBytes, datagram.Length - HeaderBytes);

            if (TagsEqual(expected, datagram))
            {
                payload = new byte[datagram.Length - HeaderBytes];
                Buffer.BlockCopy(datagram, HeaderBytes, payload, 0, payload.Length);

                return true;
            }
        }

        return false;
    }

    private static void ValidateKey(byte[] key, string parameterName)
    {
        if (key.Length < MinKeyBytes)
        {
            throw new ArgumentException($"Keys must be at least {MinKeyBytes} bytes.", parameterName);
        }
    }

    private static byte[] ComputeTag(byte[] key, byte[] data, int offset, int count)
    {
#if NET8_0_OR_GREATER
        return HMACSHA256.HashData(key, data.AsSpan(offset, count));
#else
        // HMACSHA256 instances are not thread-safe, and this runs concurrently on the
        // send path and the receive loop, so one is created per call.
        using var hmac = new HMACSHA256(key);

        return hmac.ComputeHash(data, offset, count);
#endif
    }

    /// <summary>
    /// Compares the expected tag against the one in the datagram in constant time, so
    /// verification latency leaks nothing about how many leading bytes matched.
    /// </summary>
    private static bool TagsEqual(byte[] expected, byte[] datagram)
    {
#if NET8_0_OR_GREATER
        return CryptographicOperations.FixedTimeEquals(expected, datagram.AsSpan(3, TagBytes));
#else
        var difference = 0;

        for (var i = 0; i < TagBytes; i++)
        {
            difference |= expected[i] ^ datagram[3 + i];
        }

        return difference == 0;
#endif
    }
}
