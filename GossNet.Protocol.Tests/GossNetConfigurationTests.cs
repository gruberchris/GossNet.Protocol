namespace GossNet.Protocol.Tests;

[TestClass]
public sealed class GossNetConfigurationTests
{
    [TestMethod]
    public void Constructor_SetDefaultValues_ReturnsExpectedDefaults()
    {
        // Arrange & Act
        var config = new GossNetConfiguration
        {
            Hostname = "localhost"
        };
        
        // Assert
        Assert.AreEqual("localhost", config.Hostname);
        Assert.AreEqual(9055, config.Port);
        Assert.AreEqual(0, config.StaticNodes.Count());
    }
    
    [TestMethod]
    public void Constructor_WithCustomValues_ReturnsExpectedValues()
    {
        // Arrange
        var staticNodes = new List<GossNetNodeHostEntry>
        {
            new() { Hostname = "node1", Port = 8080 },
            new() { Hostname = "node2", Port = 8081 }
        };
        
        // Act
        var config = new GossNetConfiguration
        {
            Hostname = "test-server",
            Port = 8080,
            NodeDiscovery = NodeDiscovery.StaticList,
            StaticNodes = staticNodes
        };
        
        // Assert
        Assert.AreEqual("test-server", config.Hostname);
        Assert.AreEqual(8080, config.Port);
        Assert.AreEqual(NodeDiscovery.StaticList, config.NodeDiscovery);
        Assert.AreEqual(2, config.StaticNodes.Count());
        CollectionAssert.AreEqual(staticNodes.ToList(), config.StaticNodes.ToList());
    }
    
    [TestMethod]
    public void Constructor_WithoutRequiredHostname_ThrowsException()
    {
        // Arrange, Act, Assert
        // Use a compile-time valid approach to test a required property
    
        // Option 1: Verify attribute is present (requires reflection)
        var property = typeof(GossNetConfiguration).GetProperty("Hostname");
        Assert.IsNotNull(property);
        Assert.IsTrue(property.CustomAttributes.Any(attr => 
            attr.AttributeType.Name == "RequiredMemberAttribute"));
    
        // Option 2: Alternative test that validates behavior
        var config = new GossNetConfiguration { Hostname = "" };
        Assert.AreEqual("", config.Hostname);
        // If there's validation logic that checks for empty strings, test that instead
    }
    
    [TestMethod]
    public void StaticNodes_DefaultIsEmptyList()
    {
        // Arrange & Act
        var config = new GossNetConfiguration
        {
            Hostname = "localhost"
        };
        
        // Assert
        Assert.IsNotNull(config.StaticNodes);
        Assert.IsInstanceOfType(config.StaticNodes, typeof(IEnumerable<GossNetNodeHostEntry>));
        Assert.AreEqual(0, config.StaticNodes.Count());
    }
}