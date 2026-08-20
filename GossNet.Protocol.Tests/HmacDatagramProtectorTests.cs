using System.Text;

namespace GossNet.Protocol.Tests;

[TestClass]
public sealed class HmacDatagramProtectorTests
{
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("cluster-shared-key-0123456789");
    private static readonly byte[] OtherKey = Encoding.UTF8.GetBytes("a-completely-different-key-456");

    private static byte[] Payload(string text = "hello gossip") => Encoding.UTF8.GetBytes(text);

    [TestMethod]
    public void Protect_RoundTrips()
    {
        var protector = new HmacDatagramProtector(Key);

        var datagram = protector.Protect(Payload());

        Assert.IsTrue(protector.TryUnprotect(datagram, out var payload));
        CollectionAssert.AreEqual(Payload(), payload);
    }

    [TestMethod]
    public void Protect_AddsExactlyTheDeclaredOverhead()
    {
        var protector = new HmacDatagramProtector(Key);
        var payload = Payload();

        var datagram = protector.Protect(payload);

        Assert.AreEqual(payload.Length + protector.Overhead, datagram.Length);
    }

    [TestMethod]
    public void TamperedPayload_IsRejected()
    {
        var protector = new HmacDatagramProtector(Key);
        var datagram = protector.Protect(Payload());

        datagram[^1] ^= 0x01;

        Assert.IsFalse(protector.TryUnprotect(datagram, out _));
    }

    [TestMethod]
    public void TamperedTag_IsRejected()
    {
        var protector = new HmacDatagramProtector(Key);
        var datagram = protector.Protect(Payload());

        // Byte 3 is the first tag byte, after the magic and version.
        datagram[3] ^= 0x01;

        Assert.IsFalse(protector.TryUnprotect(datagram, out _));
    }

    [TestMethod]
    public void WrongKey_IsRejected()
    {
        var signer = new HmacDatagramProtector(Key);
        var verifier = new HmacDatagramProtector(OtherKey);

        var datagram = signer.Protect(Payload());

        Assert.IsFalse(verifier.TryUnprotect(datagram, out _));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(10)]
    [DataRow(34)] // one byte short of a full header
    public void TruncatedDatagram_IsRejected(int length)
    {
        var protector = new HmacDatagramProtector(Key);

        Assert.IsFalse(protector.TryUnprotect(new byte[length], out _));
    }

    [TestMethod]
    public void PlaintextDatagram_IsRejected()
    {
        var protector = new HmacDatagramProtector(Key);

        // Longer than a header but carrying no frame at all: the pre-authentication
        // wire format, or another application entirely.
        Assert.IsFalse(protector.TryUnprotect(Encoding.UTF8.GetBytes(new string('x', 100)), out _));
    }

    [TestMethod]
    public void UnknownVersion_IsRejected()
    {
        var protector = new HmacDatagramProtector(Key);
        var datagram = protector.Protect(Payload());

        datagram[2] = 0x02;

        Assert.IsFalse(protector.TryUnprotect(datagram, out _));
    }

    // ---------------------------------------------------------------------------
    // Key rotation.
    // ---------------------------------------------------------------------------

    [TestMethod]
    public void AcceptedKey_StillVerifies()
    {
        // A node mid-rotation: signs with the new key, still accepts the old one.
        var oldNode = new HmacDatagramProtector(Key);
        var newNode = new HmacDatagramProtector(OtherKey, acceptedKeys: [Key]);

        var fromOldNode = oldNode.Protect(Payload());

        Assert.IsTrue(newNode.TryUnprotect(fromOldNode, out var payload));
        CollectionAssert.AreEqual(Payload(), payload);
    }

    [TestMethod]
    public void PrimaryKey_SignsEvenWithAcceptedKeysPresent()
    {
        var rotating = new HmacDatagramProtector(OtherKey, acceptedKeys: [Key]);
        var upgraded = new HmacDatagramProtector(OtherKey);

        var datagram = rotating.Protect(Payload());

        Assert.IsTrue(upgraded.TryUnprotect(datagram, out _), "the primary key must be the signing key");
    }

    // ---------------------------------------------------------------------------
    // Construction.
    // ---------------------------------------------------------------------------

    [TestMethod]
    public void ShortKey_IsRefused()
    {
        Assert.ThrowsExactly<ArgumentException>(() => _ = new HmacDatagramProtector("too-short"u8.ToArray()));
    }

    [TestMethod]
    public void ShortAcceptedKey_IsRefused()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => _ = new HmacDatagramProtector(Key, acceptedKeys: ["too-short"u8.ToArray()]));
    }

    [TestMethod]
    public void MutatingTheCallersKeyArray_DoesNotAffectTheProtector()
    {
        var key = (byte[])Key.Clone();
        var protector = new HmacDatagramProtector(key);
        var datagram = protector.Protect(Payload());

        key[0] ^= 0xFF;

        Assert.IsTrue(protector.TryUnprotect(datagram, out _), "the protector must have copied the key");
    }
}
