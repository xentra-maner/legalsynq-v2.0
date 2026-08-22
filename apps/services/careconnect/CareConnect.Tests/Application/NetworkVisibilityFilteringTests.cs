using CareConnect.Application.Interfaces;
using CareConnect.Application.Repositories;
using CareConnect.Application.Services;
using CareConnect.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CareConnect.Tests.Application;

// Single-tenant-network cutover: NetworkProvider.Visibility now actually gates what
// GetByIdAsync/GetAllAsync/GetMarkersAsync return — a Private provider is hidden from
// everyone except the owning organization and a caller who "sees all" (tenant admin /
// platform admin / NetworkManager).
public class NetworkVisibilityFilteringTests
{
    private static NetworkService BuildSut(INetworkRepository networks) =>
        new(
            networks,
            Mock.Of<ICategoryRepository>(),
            Mock.Of<ISpecialtyRepository>(),
            Mock.Of<IProviderImportParser>(),
            NullLogger<NetworkService>.Instance);

    private static (Provider Provider, Facility Facility, NetworkProvider Membership) BuildMembership(
        Guid tenantId, Guid networkId, string visibility, Guid? owningOrganizationId)
    {
        var provider = Provider.Create(
            tenantId, "Jane Provider", "Jane Practice", "jane@example.com", "555-0100",
            "123 Main St", "Austin", "TX", "78701", true, true, null);
        var facility = Facility.Create(
            tenantId, "Jane Practice", "123 Main St", "Austin", "TX", "78701", "555-0100",
            true, null, "jane@example.com");
        var membership = NetworkProvider.Create(
            tenantId, networkId, provider.Id, facility.Id, true, true, owningOrganizationId, visibility);
        SetNavigation(membership, nameof(NetworkProvider.Provider), provider);
        SetNavigation(membership, nameof(NetworkProvider.Facility), facility);
        return (provider, facility, membership);
    }

    private static void SetNavigation<T>(object target, string propertyName, T value)
    {
        var property = target.GetType().GetProperty(propertyName)
            ?? throw new InvalidOperationException($"Property {propertyName} was not found.");
        property.SetValue(target, value);
    }

    // ── GetByIdAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetByIdAsync_PrivateOwnedByOtherOrg_HiddenFromNonAdminCaller()
    {
        var tenantId = Guid.CreateVersion7();
        var networkId = Guid.CreateVersion7();
        var ownerOrgId = Guid.CreateVersion7();
        var callerOrgId = Guid.CreateVersion7();
        var (_, _, membership) = BuildMembership(tenantId, networkId, ProviderVisibility.Private, ownerOrgId);
        var network = ProviderNetwork.Create(tenantId, "Network", string.Empty);
        SetNavigation(network, nameof(ProviderNetwork.NetworkProviders), new List<NetworkProvider> { membership });

        var networks = new Mock<INetworkRepository>();
        networks.Setup(r => r.GetWithProvidersAsync(tenantId, networkId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(network);

        var sut = BuildSut(networks.Object);
        var result = await sut.GetByIdAsync(tenantId, networkId, default,
            callerOrgId: callerOrgId, isTenantAdmin: false, isNetworkManager: false);

        Assert.Empty(result.Providers);
    }

    [Fact]
    public async Task GetByIdAsync_PrivateOwnedByCallerOrg_VisibleToOwningOrg()
    {
        var tenantId = Guid.CreateVersion7();
        var networkId = Guid.CreateVersion7();
        var ownerOrgId = Guid.CreateVersion7();
        var (_, _, membership) = BuildMembership(tenantId, networkId, ProviderVisibility.Private, ownerOrgId);
        var network = ProviderNetwork.Create(tenantId, "Network", string.Empty);
        SetNavigation(network, nameof(ProviderNetwork.NetworkProviders), new List<NetworkProvider> { membership });

        var networks = new Mock<INetworkRepository>();
        networks.Setup(r => r.GetWithProvidersAsync(tenantId, networkId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(network);

        var sut = BuildSut(networks.Object);
        var result = await sut.GetByIdAsync(tenantId, networkId, default,
            callerOrgId: ownerOrgId, isTenantAdmin: false, isNetworkManager: false);

        Assert.Single(result.Providers);
    }

    [Fact]
    public async Task GetByIdAsync_Public_VisibleToAnyCaller()
    {
        var tenantId = Guid.CreateVersion7();
        var networkId = Guid.CreateVersion7();
        var ownerOrgId = Guid.CreateVersion7();
        var callerOrgId = Guid.CreateVersion7();
        var (_, _, membership) = BuildMembership(tenantId, networkId, ProviderVisibility.Public, ownerOrgId);
        var network = ProviderNetwork.Create(tenantId, "Network", string.Empty);
        SetNavigation(network, nameof(ProviderNetwork.NetworkProviders), new List<NetworkProvider> { membership });

        var networks = new Mock<INetworkRepository>();
        networks.Setup(r => r.GetWithProvidersAsync(tenantId, networkId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(network);

        var sut = BuildSut(networks.Object);
        var result = await sut.GetByIdAsync(tenantId, networkId, default,
            callerOrgId: callerOrgId, isTenantAdmin: false, isNetworkManager: false);

        Assert.Single(result.Providers);
    }

    [Fact]
    public async Task GetByIdAsync_TenantAdmin_SeesPrivateProvidersRegardlessOfOwner()
    {
        var tenantId = Guid.CreateVersion7();
        var networkId = Guid.CreateVersion7();
        var ownerOrgId = Guid.CreateVersion7();
        var (_, _, membership) = BuildMembership(tenantId, networkId, ProviderVisibility.Private, ownerOrgId);
        var network = ProviderNetwork.Create(tenantId, "Network", string.Empty);
        SetNavigation(network, nameof(ProviderNetwork.NetworkProviders), new List<NetworkProvider> { membership });

        var networks = new Mock<INetworkRepository>();
        networks.Setup(r => r.GetWithProvidersAsync(tenantId, networkId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(network);

        var sut = BuildSut(networks.Object);
        var result = await sut.GetByIdAsync(tenantId, networkId, default,
            callerOrgId: null, isTenantAdmin: true, isNetworkManager: false);

        Assert.Single(result.Providers);
    }

    [Fact]
    public async Task GetByIdAsync_NetworkManager_SeesPrivateProvidersRegardlessOfOwner()
    {
        var tenantId = Guid.CreateVersion7();
        var networkId = Guid.CreateVersion7();
        var ownerOrgId = Guid.CreateVersion7();
        var (_, _, membership) = BuildMembership(tenantId, networkId, ProviderVisibility.Private, ownerOrgId);
        var network = ProviderNetwork.Create(tenantId, "Network", string.Empty);
        SetNavigation(network, nameof(ProviderNetwork.NetworkProviders), new List<NetworkProvider> { membership });

        var networks = new Mock<INetworkRepository>();
        networks.Setup(r => r.GetWithProvidersAsync(tenantId, networkId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(network);

        var sut = BuildSut(networks.Object);
        var result = await sut.GetByIdAsync(tenantId, networkId, default,
            callerOrgId: Guid.CreateVersion7(), isTenantAdmin: false, isNetworkManager: true);

        Assert.Single(result.Providers);
    }

    [Fact]
    public async Task GetByIdAsync_LegacyUnownedPrivate_VisibleToEveryone()
    {
        // A NetworkProvider with OwningOrganizationId == null predates per-provider
        // ownership tracking — treated as tenant-owned/visible-to-all even if marked Private.
        var tenantId = Guid.CreateVersion7();
        var networkId = Guid.CreateVersion7();
        var (_, _, membership) = BuildMembership(tenantId, networkId, ProviderVisibility.Private, owningOrganizationId: null);
        var network = ProviderNetwork.Create(tenantId, "Network", string.Empty);
        SetNavigation(network, nameof(ProviderNetwork.NetworkProviders), new List<NetworkProvider> { membership });

        var networks = new Mock<INetworkRepository>();
        networks.Setup(r => r.GetWithProvidersAsync(tenantId, networkId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(network);

        var sut = BuildSut(networks.Object);
        var result = await sut.GetByIdAsync(tenantId, networkId, default,
            callerOrgId: Guid.CreateVersion7(), isTenantAdmin: false, isNetworkManager: false);

        Assert.Single(result.Providers);
    }

    // ── GetAllAsync (provider count) ────────────────────────────────────────

    [Fact]
    public async Task GetAllAsync_ProviderCount_ExcludesPrivateProvidersOwnedByOtherOrg()
    {
        var tenantId = Guid.CreateVersion7();
        var networkId = Guid.CreateVersion7();
        var ownerOrgId = Guid.CreateVersion7();
        var callerOrgId = Guid.CreateVersion7();
        var (_, _, membership) = BuildMembership(tenantId, networkId, ProviderVisibility.Private, ownerOrgId);
        var network = ProviderNetwork.Create(tenantId, "Network", string.Empty);
        SetNavigation(network, nameof(ProviderNetwork.NetworkProviders), new List<NetworkProvider> { membership });

        var networks = new Mock<INetworkRepository>();
        networks.Setup(r => r.GetAllByTenantAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderNetwork> { network });
        networks.Setup(r => r.GetWithProvidersAsync(tenantId, network.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(network);

        var sut = BuildSut(networks.Object);
        var result = await sut.GetAllAsync(tenantId, default, callerOrgId: callerOrgId, isTenantAdmin: false, isNetworkManager: false);

        Assert.Equal(0, result.Single().ProviderCount);
    }

    // ── GetMarkersAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetMarkersAsync_PrivateOwnedByOtherOrg_ExcludedFromMarkers()
    {
        var tenantId = Guid.CreateVersion7();
        var networkId = Guid.CreateVersion7();
        var ownerOrgId = Guid.CreateVersion7();
        var callerOrgId = Guid.CreateVersion7();
        var (_, _, membership) = BuildMembership(tenantId, networkId, ProviderVisibility.Private, ownerOrgId);
        var network = ProviderNetwork.Create(tenantId, "Network", string.Empty);

        var networks = new Mock<INetworkRepository>();
        networks.Setup(r => r.GetByIdAsync(tenantId, networkId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(network);
        networks.Setup(r => r.GetNetworkProviderMembershipsAsync(tenantId, networkId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NetworkProvider> { membership });

        var sut = BuildSut(networks.Object);
        var result = await sut.GetMarkersAsync(tenantId, networkId, default,
            callerOrgId: callerOrgId, isTenantAdmin: false, isNetworkManager: false);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetMarkersAsync_PrivateOwnedByCallerOrg_IncludedInMarkers()
    {
        var tenantId = Guid.CreateVersion7();
        var networkId = Guid.CreateVersion7();
        var ownerOrgId = Guid.CreateVersion7();
        var (_, _, membership) = BuildMembership(tenantId, networkId, ProviderVisibility.Private, ownerOrgId);
        var network = ProviderNetwork.Create(tenantId, "Network", string.Empty);

        var networks = new Mock<INetworkRepository>();
        networks.Setup(r => r.GetByIdAsync(tenantId, networkId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(network);
        networks.Setup(r => r.GetNetworkProviderMembershipsAsync(tenantId, networkId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NetworkProvider> { membership });

        var sut = BuildSut(networks.Object);
        var result = await sut.GetMarkersAsync(tenantId, networkId, default,
            callerOrgId: ownerOrgId, isTenantAdmin: false, isNetworkManager: false);

        Assert.Single(result);
    }
}
