using BuildingBlocks.Exceptions;
using CareConnect.Application.DTOs;
using CareConnect.Application.Interfaces;
using CareConnect.Application.Repositories;
using CareConnect.Application.Services;
using CareConnect.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CareConnect.Tests.Application;

// LSV3-1084: a CareConnectReferrerAdmin (law-firm-scoped, not a NetworkManager or system
// admin) may only rename/delete a network their own organization created. Networks created
// before this field existed (OwningOrganizationId == null) are treated as tenant-admin-owned.
public class NetworkOwnershipTests
{
    private static NetworkService BuildSut(INetworkRepository networks) =>
        new(
            networks,
            Mock.Of<ICategoryRepository>(),
            Mock.Of<ISpecialtyRepository>(),
            Mock.Of<IProviderImportParser>(),
            NullLogger<NetworkService>.Instance);

    [Fact]
    public async Task UpdateAsync_WhenCallerDidNotOwnNetwork_ThrowsForbidden()
    {
        var tenantId = Guid.CreateVersion7();
        var ownerOrgId = Guid.CreateVersion7();
        var callerOrgId = Guid.CreateVersion7();
        var network = ProviderNetwork.Create(tenantId, "Tenant Network", string.Empty, ownerOrgId);

        var networks = new Mock<INetworkRepository>();
        networks.Setup(r => r.GetWithProvidersAsync(tenantId, network.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(network);

        var sut = BuildSut(networks.Object);

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            sut.UpdateAsync(tenantId, network.Id, null, new UpdateNetworkRequest("New Name", ""), default,
                isTenantAdmin: false, callerOrgId: callerOrgId, isNetworkManager: false));

        networks.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateAsync_WhenNetworkHasNoOwner_TreatedAsTenantAdminOwned_ThrowsForbiddenForReferrerAdmin()
    {
        // Every network that existed before LSV3-1084 has OwningOrganizationId == null —
        // these must stay view-only for a ReferrerAdmin-only caller.
        var tenantId = Guid.CreateVersion7();
        var callerOrgId = Guid.CreateVersion7();
        var network = ProviderNetwork.Create(tenantId, "Legacy Network", string.Empty);

        var networks = new Mock<INetworkRepository>();
        networks.Setup(r => r.GetWithProvidersAsync(tenantId, network.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(network);

        var sut = BuildSut(networks.Object);

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            sut.UpdateAsync(tenantId, network.Id, null, new UpdateNetworkRequest("New Name", ""), default,
                isTenantAdmin: false, callerOrgId: callerOrgId, isNetworkManager: false));
    }

    [Fact]
    public async Task UpdateAsync_WhenCallerOwnsNetwork_Succeeds()
    {
        var tenantId = Guid.CreateVersion7();
        var callerOrgId = Guid.CreateVersion7();
        var network = ProviderNetwork.Create(tenantId, "My Network", string.Empty, callerOrgId);

        var networks = new Mock<INetworkRepository>();
        networks.Setup(r => r.GetWithProvidersAsync(tenantId, network.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(network);
        networks.Setup(r => r.NameExistsAsync(tenantId, "New Name", network.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        networks.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = BuildSut(networks.Object);

        var result = await sut.UpdateAsync(tenantId, network.Id, null, new UpdateNetworkRequest("New Name", ""), default,
            isTenantAdmin: false, callerOrgId: callerOrgId, isNetworkManager: false);

        Assert.Equal("New Name", result.Name);
    }

    [Fact]
    public async Task UpdateAsync_WhenCallerIsNetworkManager_BypassesOwnershipCheck()
    {
        var tenantId = Guid.CreateVersion7();
        var ownerOrgId = Guid.CreateVersion7();
        var callerOrgId = Guid.CreateVersion7();
        var network = ProviderNetwork.Create(tenantId, "Tenant Network", string.Empty, ownerOrgId);

        var networks = new Mock<INetworkRepository>();
        networks.Setup(r => r.GetWithProvidersAsync(tenantId, network.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(network);
        networks.Setup(r => r.NameExistsAsync(tenantId, "New Name", network.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        networks.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = BuildSut(networks.Object);

        var result = await sut.UpdateAsync(tenantId, network.Id, null, new UpdateNetworkRequest("New Name", ""), default,
            isTenantAdmin: false, callerOrgId: callerOrgId, isNetworkManager: true);

        Assert.Equal("New Name", result.Name);
    }

    [Fact]
    public async Task DeleteAsync_WhenCallerDidNotOwnNetwork_ThrowsForbidden()
    {
        var tenantId = Guid.CreateVersion7();
        var ownerOrgId = Guid.CreateVersion7();
        var callerOrgId = Guid.CreateVersion7();
        var network = ProviderNetwork.Create(tenantId, "Tenant Network", string.Empty, ownerOrgId);

        var networks = new Mock<INetworkRepository>();
        networks.Setup(r => r.GetByIdAsync(tenantId, network.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(network);

        var sut = BuildSut(networks.Object);

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            sut.DeleteAsync(tenantId, network.Id, default,
                isTenantAdmin: false, callerOrgId: callerOrgId, isNetworkManager: false));

        networks.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteAsync_WhenCallerOwnsNetwork_Succeeds()
    {
        var tenantId = Guid.CreateVersion7();
        var callerOrgId = Guid.CreateVersion7();
        var network = ProviderNetwork.Create(tenantId, "My Network", string.Empty, callerOrgId);

        var networks = new Mock<INetworkRepository>();
        networks.Setup(r => r.GetByIdAsync(tenantId, network.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(network);
        networks.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = BuildSut(networks.Object);

        await sut.DeleteAsync(tenantId, network.Id, default,
            isTenantAdmin: false, callerOrgId: callerOrgId, isNetworkManager: false);

        networks.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateAsync_StampsOwningOrganizationIdFromCaller()
    {
        var tenantId = Guid.CreateVersion7();
        var callerOrgId = Guid.CreateVersion7();

        var networks = new Mock<INetworkRepository>();
        networks.Setup(r => r.NameExistsAsync(tenantId, "New Network", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        ProviderNetwork? added = null;
        networks.Setup(r => r.AddAsync(It.IsAny<ProviderNetwork>(), It.IsAny<CancellationToken>()))
            .Callback<ProviderNetwork, CancellationToken>((n, _) => added = n)
            .Returns(Task.CompletedTask);
        networks.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = BuildSut(networks.Object);

        await sut.CreateAsync(tenantId, null, new CreateNetworkRequest("New Network", ""), default,
            owningOrganizationId: callerOrgId);

        Assert.Equal(callerOrgId, added?.OwningOrganizationId);
    }
}
