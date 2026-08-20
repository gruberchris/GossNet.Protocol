using global::Azure.Identity;
using global::Azure.ResourceManager;
using global::Azure.ResourceManager.Compute;
using global::Azure.ResourceManager.Resources;

namespace GossNet.Discovery.Azure;

/// <summary>An Azure virtual machine matching the configured tag.</summary>
/// <param name="Address">The address to reach the machine on.</param>
/// <param name="Name">The machine name, used for diagnostics.</param>
public sealed record AzureInstance(string Address, string Name);

/// <summary>
/// The single Azure query <see cref="AzureTagNodeDiscovery"/> needs.
/// </summary>
/// <remarks>Kept narrow so discovery can be tested without Azure credentials or a network.</remarks>
public interface IAzureInstanceLookup : IDisposable
{
    /// <summary>
    /// Lists machines in a resource group carrying a tag.
    /// </summary>
    /// <param name="resourceGroup">The resource group to search.</param>
    /// <param name="tagKey">The tag key.</param>
    /// <param name="tagValue">The tag value.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    ValueTask<IReadOnlyList<AzureInstance>> GetInstancesAsync(
        string resourceGroup,
        string tagKey,
        string tagValue,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IAzureInstanceLookup"/> backed by Azure Resource Manager.
/// </summary>
/// <remarks>
/// Credentials come from <see cref="DefaultAzureCredential"/> — managed identity on a VM,
/// environment variables, or a developer sign-in. The identity needs <c>Reader</c> on the
/// resource group.
/// </remarks>
public sealed class AzureInstanceLookup : IAzureInstanceLookup
{
    private readonly ArmClient _client;
    private readonly string? _subscriptionId;

    /// <summary>Creates a client from discovery options.</summary>
    /// <param name="options">The Azure settings.</param>
    public AzureInstanceLookup(AzureDiscoveryOptions options)
    {
        _client = new ArmClient(new DefaultAzureCredential());
        _subscriptionId = options.SubscriptionId;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<AzureInstance>> GetInstancesAsync(
        string resourceGroup,
        string tagKey,
        string tagValue,
        CancellationToken cancellationToken = default)
    {
        SubscriptionResource subscription = string.IsNullOrEmpty(_subscriptionId)
            ? await _client.GetDefaultSubscriptionAsync(cancellationToken).ConfigureAwait(false)
            : _client.GetSubscriptionResource(SubscriptionResource.CreateResourceIdentifier(_subscriptionId));

        var group = await subscription.GetResourceGroups()
            .GetAsync(resourceGroup, cancellationToken)
            .ConfigureAwait(false);

        var instances = new List<AzureInstance>();

        // Called as static methods rather than through `using` directives: the Compute and
        // Network packages both extend these types with same-named members, and importing
        // both makes `GetVirtualMachines` bind to the wrong one.
        var machines = ComputeExtensions.GetVirtualMachines(group.Value);

        await foreach (var machine in machines.GetAllAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (machine.Data.Tags is null ||
                !machine.Data.Tags.TryGetValue(tagKey, out var value) ||
                !string.Equals(value, tagValue, StringComparison.Ordinal))
            {
                continue;
            }

            var address = await ResolvePrivateAddressAsync(machine, cancellationToken).ConfigureAwait(false);

            // A machine can legitimately have no usable NIC yet while it is provisioning;
            // skipping beats emitting a neighbour that can never be reached.
            if (!string.IsNullOrEmpty(address))
            {
                instances.Add(new AzureInstance(address!, machine.Data.Name));
            }
        }

        return instances;
    }

    /// <summary>Reads the primary NIC's private address.</summary>
    /// <remarks>
    /// Private addressing is correct for nodes inside one virtual network, which is the
    /// normal shape. A machine's address lives on its network interface rather than the
    /// machine resource, so it takes a second lookup.
    /// </remarks>
    private async ValueTask<string?> ResolvePrivateAddressAsync(
        VirtualMachineResource machine,
        CancellationToken cancellationToken)
    {
        foreach (var reference in machine.Data.NetworkProfile?.NetworkInterfaces ?? [])
        {
            if (reference.Id is null)
            {
                continue;
            }

            var nic = await global::Azure.ResourceManager.Network.NetworkExtensions
                .GetNetworkInterfaceResource(_client, reference.Id)
                .GetAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            foreach (var configuration in nic.Value.Data.IPConfigurations)
            {
                if (!string.IsNullOrEmpty(configuration.PrivateIPAddress))
                {
                    return configuration.PrivateIPAddress;
                }
            }
        }

        return null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // ArmClient holds no unmanaged resources and exposes no Dispose; the method exists
        // so the seam matches every other provider's lookup contract.
    }
}
