namespace GossNet.Protocol.Tests;

[TestClass]
public sealed class GossNetNodeHostEntryTests
{
    private static GossNetNodeHostEntry Entry(string hostname, int port) => new() { Hostname = hostname, Port = port };

    [TestMethod]
    public void Equals_ComparesHostnameAndPort()
    {
        Assert.AreEqual(Entry("host", 1), Entry("host", 1));
        Assert.AreNotEqual(Entry("host", 1), Entry("host", 2));
        Assert.AreNotEqual(Entry("other", 1), Entry("host", 1));
    }

    [TestMethod]
    public void Equals_ImplementsIEquatable()
    {
        // Generic collections use IEquatable<T> to avoid boxing; the type previously
        // overrode Equals(object) without declaring it.
        IEquatable<GossNetNodeHostEntry> entry = Entry("host", 1);

        Assert.IsTrue(entry.Equals(Entry("host", 1)));
        Assert.IsFalse(entry.Equals(Entry("host", 2)));
        Assert.IsFalse(entry.Equals(null));
    }

    [TestMethod]
    public void GetHashCode_IsStableForEqualEntries() =>
        Assert.AreEqual(Entry("host", 1).GetHashCode(), Entry("host", 1).GetHashCode());

    [TestMethod]
    public void WorksAsADictionaryKeyAndInSets()
    {
        var set = new HashSet<GossNetNodeHostEntry> { Entry("host", 1), Entry("host", 1), Entry("host", 2) };

        Assert.AreEqual(2, set.Count);
        Assert.IsTrue(set.Contains(Entry("host", 1)));
    }

    [TestMethod]
    public void Operators_CompareByValue()
    {
        Assert.IsTrue(Entry("host", 1) == Entry("host", 1));
        Assert.IsTrue(Entry("host", 1) != Entry("host", 2));
        Assert.IsTrue((GossNetNodeHostEntry?)null == (GossNetNodeHostEntry?)null);
        Assert.IsTrue(Entry("host", 1) != null);
    }

    /// <summary>
    /// CompareTo used to format both sides with ToString and compare the strings, so
    /// ports were ordered lexically and "host:10" sorted before "host:9".
    /// </summary>
    [TestMethod]
    public void CompareTo_OrdersPortsNumerically()
    {
        Assert.IsTrue(Entry("host", 9).CompareTo(Entry("host", 10)) < 0);
        Assert.IsTrue(Entry("host", 10).CompareTo(Entry("host", 9)) > 0);
        Assert.AreEqual(0, Entry("host", 9).CompareTo(Entry("host", 9)));
    }

    [TestMethod]
    public void CompareTo_OrdersByHostnameFirst()
    {
        Assert.IsTrue(Entry("a", 999).CompareTo(Entry("b", 1)) < 0);
        Assert.IsTrue(Entry("host", 1).CompareTo(null) > 0);
    }

    [TestMethod]
    public void Sorting_ProducesHostThenPortOrder()
    {
        var entries = new List<GossNetNodeHostEntry>
        {
            Entry("b", 1), Entry("a", 10), Entry("a", 9), Entry("a", 2)
        };

        entries.Sort();

        CollectionAssert.AreEqual(
            new[] { Entry("a", 2), Entry("a", 9), Entry("a", 10), Entry("b", 1) },
            entries);
    }

    [TestMethod]
    public void ToString_FormatsAsHostColonPort() => Assert.AreEqual("host:1", Entry("host", 1).ToString());
}
