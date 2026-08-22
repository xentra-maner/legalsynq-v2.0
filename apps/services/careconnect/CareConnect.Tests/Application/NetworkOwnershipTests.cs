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

// Single-tenant-network cutover: nobody "owns" the shared ProviderNetwork anymore — only
// a tenant/platform admin or NetworkManager may rename/describe it. A CareConnectReferrerAdmin
// (law-firm-scoped, not a NetworkManager or system admin) can no longer edit the network's own
// metadata at all, regardless of who created it historically.
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
    public async Task UpdateAsync_WhenCallerIsReferrerAdmin_ThrowsForbidden()
    {
        var tenantId = Guid.CreateVersion7();
        var callerOrgId = Guid.CreateVersion7();
        var network = ProviderNetwork.Create(tenantId, "Provider Network", string.Empty);

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
    public async Task UpdateAsync_WhenCallerIsTenantAdmin_Succeeds()
    {
        var tenantId = Guid.CreateVersion7();
        var network = ProviderNetwork.Create(tenantId, "Provider Network", string.Empty);

        var networks = new Mock<INetworkRepository>();
        networks.Setup(r => r.GetWithProvidersAsync(tenantId, network.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(network);
        networks.Setup(r => r.NameExistsAsync(tenantId, "New Name", network.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        networks.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = BuildSut(networks.Object);

        var result = await sut.UpdateAsync(tenantId, network.Id, null, new UpdateNetworkRequest("New Name", ""), default,
            isTenantAdmin: true, callerOrgId: null, isNetworkManager: false);

        Assert.Equal("New Name", result.Name);
    }

    [Fact]
    public async Task UpdateAsync_WhenCallerIsNetworkManager_Succeeds()
    {
        var tenantId = Guid.CreateVersion7();
        var callerOrgId = Guid.CreateVersion7();
        var network = ProviderNetwork.Create(tenantId, "Provider Network", string.Empty);

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

    // ── GetOrCreateTenantNetworkAsync ────────────────────────────────────────

    [Fact]
    public async Task GetOrCreateTenantNetworkAsync_WhenNoneExists_CreatesOne()
    {
        var tenantId = Guid.CreateVersion7();

        var networks = new Mock<INetworkRepository>();
        networks.Setup(r => r.GetSingleForTenantAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderNetwork?)null);
        ProviderNetwork? added = null;
        networks.Setup(r => r.AddAsync(It.IsAny<ProviderNetwork>(), It.IsAny<CancellationToken>()))
            .Callback<ProviderNetwork, CancellationToken>((n, _) => added = n)
            .Returns(Task.CompletedTask);
        networks.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = BuildSut(networks.Object);
        var result = await sut.GetOrCreateTenantNetworkAsync(tenantId);

        Assert.NotNull(added);
        Assert.Equal(added!.Id, result.Id);
        Assert.Null(added.OwningOrganizationId);
        networks.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetOrCreateTenantNetworkAsync_WhenOneExists_ReturnsItWithoutCreating()
    {
        var tenantId = Guid.CreateVersion7();
        var existing = ProviderNetwork.Create(tenantId, "Provider Network", string.Empty);

        var networks = new Mock<INetworkRepository>();
        networks.Setup(r => r.GetSingleForTenantAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var sut = BuildSut(networks.Object);
        var result = await sut.GetOrCreateTenantNetworkAsync(tenantId);

        Assert.Equal(existing.Id, result.Id);
        networks.Verify(r => r.AddAsync(It.IsAny<ProviderNetwork>(), It.IsAny<CancellationToken>()), Times.Never);
        networks.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
