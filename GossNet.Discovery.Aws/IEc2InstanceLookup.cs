using Amazon;
using Amazon.EC2;
using Amazon.EC2.Model;

namespace GossNet.Discovery.Aws;

/// <summary>An EC2 instance matching the configured tag.</summary>
/// <param name="Address">The address to reach the instance on.</param>
/// <param name="InstanceId">The instance id, used for diagnostics.</param>
public sealed record Ec2Instance(string Address, string InstanceId);

/// <summary>
/// The single EC2 query <see cref="Ec2TagNodeDiscovery"/> needs.
/// </summary>
/// <remarks>Kept narrow so discovery can be tested without AWS credentials or a network.</remarks>
public interface IEc2InstanceLookup : IDisposable
{
    /// <summary>
    /// Lists running instances carrying a tag.
    /// </summary>
    /// <param name="tagKey">The tag key.</param>
    /// <param name="tagValue">The tag value.</param>
    /// <param name="usePrivateIp">Whether to return private rather than public addresses.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    ValueTask<IReadOnlyList<Ec2Instance>> GetInstancesAsync(
        string tagKey,
        string tagValue,
        bool usePrivateIp,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IEc2InstanceLookup"/> backed by the EC2 API.
/// </summary>
/// <remarks>
/// Requires the <c>ec2:DescribeInstances</c> IAM action. Credentials come from the ambient
/// AWS SDK chain — instance profile, environment, or shared config.
/// </remarks>
public sealed class Ec2InstanceLookup : IEc2InstanceLookup
{
    private readonly AmazonEC2Client _client;

    /// <summary>Creates a client from discovery options.</summary>
    /// <param name="options">The AWS settings.</param>
    public Ec2InstanceLookup(AwsDiscoveryOptions options) =>
        _client = string.IsNullOrEmpty(options.Region)
            ? new AmazonEC2Client()
            : new AmazonEC2Client(RegionEndpoint.GetBySystemName(options.Region));

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<Ec2Instance>> GetInstancesAsync(
        string tagKey,
        string tagValue,
        bool usePrivateIp,
        CancellationToken cancellationToken = default)
    {
        var instances = new List<Ec2Instance>();
        string? nextToken = null;

        do
        {
            var request = new DescribeInstancesRequest
            {
                Filters =
                [
                    new Filter($"tag:{tagKey}", [tagValue]),

                    // Terminated and stopped instances keep their tags, and a stopped
                    // instance's address is either gone or belongs to something else.
                    new Filter("instance-state-name", ["running"])
                ],
                NextToken = nextToken
            };

            var response = await _client.DescribeInstancesAsync(request, cancellationToken).ConfigureAwait(false);

            foreach (var reservation in response.Reservations ?? [])
            {
                foreach (var instance in reservation.Instances ?? [])
                {
                    var address = usePrivateIp ? instance.PrivateIpAddress : instance.PublicIpAddress;

                    // An instance can legitimately lack a public address; skipping is
                    // better than emitting a neighbour that can never be reached.
                    if (!string.IsNullOrEmpty(address))
                    {
                        instances.Add(new Ec2Instance(address, instance.InstanceId));
                    }
                }
            }

            // A cluster larger than one page would otherwise be silently truncated.
            nextToken = response.NextToken;
        }
        while (!string.IsNullOrEmpty(nextToken));

        return instances;
    }

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();
}
