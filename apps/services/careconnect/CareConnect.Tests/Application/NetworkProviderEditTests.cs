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

public class NetworkProviderEditTests
{
    [Fact]
    public async Task UpdateProviderAsync_WhenProviderIsNotInNetwork_ThrowsNotFoundException()
    {
        var tenantId = Guid.CreateVersion7();
        var networkId = Guid.CreateVersion7();
        var providerId = Guid.CreateVersion7();
        var networks = new Mock<INetworkRepository>();
        networks.Setup(r => r.GetByIdAsync(tenantId, networkId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProviderNetwork.Create(tenantId, "Network", string.Empty));
        networks.Setup(r => r.GetMembershipAsync(networkId, providerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((NetworkProvider?)null);
        networks.Setup(r => r.GetMembershipByIdOrProviderAsync(networkId, providerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((NetworkProvider?)null);

        var sut = BuildSut(networks.Object, Mock.Of<ISpecialtyRepository>());

        await Assert.ThrowsAsync<NotFoundException>(() =>
            sut.UpdateProviderAsync(tenantId, networkId, providerId, ValidUpdateRequest([Guid.CreateVersion7()]), null));

        networks.Verify(r => r.UpdateProviderInRegistryAsync(It.IsAny<Provider>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateProviderAsync_WithActiveSpecialty_SyncsProviderSpecialties()
    {
        var tenantId = Guid.CreateVersion7();
        var networkId = Guid.CreateVersion7();
        var specialtyId = Guid.CreateVersion7();
        var specialty = Specialty.Create("Pain Doctors", "PAIN_DOCTORS", null);
        var provider = Provider.Create(
            tenantId,
            "Jane Provider",
            "Jane Practice",
            "jane@example.com",
            "555-0100",
            "123 Main St",
            "Austin",
            "TX",
            "78701",
            true,
            true,
            null);
        var providerId = provider.Id;
        var facility = Facility.Create(
            tenantId,
            "Jane Practice",
            "123 Main St",
            "Austin",
            "TX",
            "78701",
            "555-0100",
            true,
            null,
            "jane@example.com");
        var membership = NetworkProvider.Create(tenantId, networkId, providerId, facility.Id, true, true);
        SetNavigation(membership, nameof(NetworkProvider.Provider), provider);
        SetNavigation(membership, nameof(NetworkProvider.Facility), facility);

        var networks = new Mock<INetworkRepository>();
        networks.Setup(r => r.GetByIdAsync(tenantId, networkId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProviderNetwork.Create(tenantId, "Network", string.Empty));
        networks.Setup(r => r.GetMembershipByIdOrProviderAsync(networkId, providerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(membership);
        networks.Setup(r => r.GetMembershipByIdOrProviderAsync(networkId, membership.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(membership);
        networks.Setup(r => r.UpdateProviderInRegistryAsync(provider, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        networks.Setup(r => r.GetFacilityByIdAsync(tenantId, facility.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(facility);
        networks.Setup(r => r.UpdateFacilityAsync(facility, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        networks.Setup(r => r.SyncProviderSpecialtiesAsync(provider.Id, It.Is<List<Guid>>(ids => ids.SequenceEqual(new[] { specialtyId })), It.IsAny<CancellationToken>()))
            .Callback<Guid, List<Guid>, CancellationToken>((_, _, _) =>
            {
                provider.ProviderSpecialties.Clear();
                provider.ProviderSpecialties.Add(new ProviderSpecialty
                {
                    ProviderId = provider.Id,
                    SpecialtyId = specialty.Id,
                    Specialty = specialty,
                    IsPrimary = true
                });
            })
            .Returns(Task.CompletedTask);
        networks.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var specialties = new Mock<ISpecialtyRepository>();
        specialties.Setup(r => r.GetActiveByIdsAsync(It.Is<List<Guid>>(ids => ids.SequenceEqual(new[] { specialtyId })), It.IsAny<CancellationToken>()))
            .ReturnsAsync([specialty]);

        var sut = BuildSut(networks.Object, specialties.Object);

        var result = await sut.UpdateProviderAsync(tenantId, networkId, providerId, ValidUpdateRequest([specialtyId]), null);

        Assert.Equal("Pain Doctors", result.PrimarySpecialty);
        Assert.Equal("Dr.", result.Title);
        Assert.Equal("Dr. Jane Provider", result.Name);
        Assert.Equal("Jane Practice - North", facility.Name);
        networks.Verify(r => r.SyncProviderSpecialtiesAsync(
            provider.Id,
            It.Is<List<Guid>>(ids => ids.SequenceEqual(new[] { specialtyId })),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateProviderAsync_WhenTogglingLocationInactive_DoesNotDeactivateFacility()
    {
        // The per-location "Active" checkbox (Save Location / Save Provider Setup) is a
        // separate, pre-existing feature from deletion — it must only affect the
        // NetworkProvider membership's own status, never cc_Facilities.IsActive. Only
        // RemoveProviderAsync (Delete Location) is allowed to deactivate the Facility.
        var tenantId = Guid.CreateVersion7();
        var networkId = Guid.CreateVersion7();
        var specialtyId = Guid.CreateVersion7();
        var specialty = Specialty.Create("Pain Doctors", "PAIN_DOCTORS", null);
        var provider = Provider.Create(
            tenantId, "Jane Provider", "Jane Practice", "jane@example.com", "555-0100",
            "123 Main St", "Austin", "TX", "78701", true, true, null);
        var providerId = provider.Id;
        var facility = Facility.Create(
            tenantId, "Jane Practice", "123 Main St", "Austin", "TX", "78701", "555-0100",
            true, null, "jane@example.com");
        var membership = NetworkProvider.Create(tenantId, networkId, providerId, facility.Id, true, true);
        SetNavigation(membership, nameof(NetworkProvider.Provider), provider);
        SetNavigation(membership, nameof(NetworkProvider.Facility), facility);

        var networks = new Mock<INetworkRepository>();
        networks.Setup(r => r.GetByIdAsync(tenantId, networkId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProviderNetwork.Create(tenantId, "Network", string.Empty));
        networks.Setup(r => r.GetMembershipByIdOrProviderAsync(networkId, providerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(membership);
        networks.Setup(r => r.GetMembershipByIdOrProviderAsync(networkId, membership.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(membership);
        networks.Setup(r => r.UpdateProviderInRegistryAsync(provider, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        networks.Setup(r => r.GetFacilityByIdAsync(tenantId, facility.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(facility);
        networks.Setup(r => r.UpdateFacilityAsync(facility, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        networks.Setup(r => r.SyncProviderSpecialtiesAsync(provider.Id, It.IsAny<List<Guid>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        networks.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var specialties = new Mock<ISpecialtyRepository>();
        specialties.Setup(r => r.GetActiveByIdsAsync(It.Is<List<Guid>>(ids => ids.SequenceEqual(new[] { specialtyId })), It.IsAny<CancellationToken>()))
            .ReturnsAsync([specialty]);

        var sut = BuildSut(networks.Object, specialties.Object);

        var inactiveRequest = ValidUpdateRequest([specialtyId]) with { IsActive = false, AcceptingReferrals = false };
        var result = await sut.UpdateProviderAsync(tenantId, networkId, providerId, inactiveRequest, null);

        Assert.False(membership.IsActive);
        Assert.False(result.IsActive);
        Assert.True(facility.IsActive);
        Assert.True(result.FacilityIsActive);
    }

    [Fact]
    public async Task UpdateProviderAsync_WhenCallerDidNotOwnProvider_ThrowsForbidden()
    {
        // LSV3-1084: a CareConnectReferrerAdmin (not a NetworkManager or tenant admin)
        // may only edit providers their own organization added to the network.
        var tenantId = Guid.CreateVersion7();
        var networkId = Guid.CreateVersion7();
        var providerId = Guid.CreateVersion7();
        var ownerOrgId = Guid.CreateVersion7();
        var callerOrgId = Guid.CreateVersion7();
        var provider = Provider.Create(
            tenantId, "Jane Provider", "Jane Practice", "jane@example.com", "555-0100",
            "123 Main St", "Austin", "TX", "78701", true, true, null);
        var facility = Facility.Create(
            tenantId, "Jane Practice", "123 Main St", "Austin", "TX", "78701", "555-0100",
            true, null, "jane@example.com");
        var membership = NetworkProvider.Create(tenantId, networkId, provider.Id, facility.Id, true, true, ownerOrgId);
        SetNavigation(membership, nameof(NetworkProvider.Provider), provider);
        SetNavigation(membership, nameof(NetworkProvider.Facility), facility);

        var networks = new Mock<INetworkRepository>();
        networks.Setup(r => r.GetByIdAsync(tenantId, networkId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProviderNetwork.Create(tenantId, "Network", string.Empty));
        networks.Setup(r => r.GetMembershipByIdOrProviderAsync(networkId, providerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(membership);

        var sut = BuildSut(networks.Object, Mock.Of<ISpecialtyRepository>());

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            sut.UpdateProviderAsync(tenantId, networkId, providerId, ValidUpdateRequest([]), null,
                callerOrgId: callerOrgId, isNetworkManager: false, isTenantAdmin: false));

        networks.Verify(r => r.UpdateProviderInRegistryAsync(It.IsAny<Provider>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateProviderAsync_WhenCallerIsNetworkManager_BypassesOwnershipCheck()
    {
        var tenantId = Guid.CreateVersion7();
        var networkId = Guid.CreateVersion7();
        var providerId = Guid.CreateVersion7();
        var ownerOrgId = Guid.CreateVersion7();
        var callerOrgId = Guid.CreateVersion7();
        var specialtyId = Guid.CreateVersion7();
        var specialty = Specialty.Create("Pain Doctors", "PAIN_DOCTORS", null);
        var provider = Provider.Create(
            tenantId, "Jane Provider", "Jane Practice", "jane@example.com", "555-0100",
            "123 Main St", "Austin", "TX", "78701", true, true, null);
        var facility = Facility.Create(
            tenantId, "Jane Practice", "123 Main St", "Austin", "TX", "78701", "555-0100",
            true, null, "jane@example.com");
        var membership = NetworkProvider.Create(tenantId, networkId, provider.Id, facility.Id, true, true, ownerOrgId);
        SetNavigation(membership, nameof(NetworkProvider.Provider), provider);
        SetNavigation(membership, nameof(NetworkProvider.Facility), facility);

        var networks = new Mock<INetworkRepository>();
        networks.Setup(r => r.GetByIdAsync(tenantId, networkId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProviderNetwork.Create(tenantId, "Network", string.Empty));
        networks.Setup(r => r.GetMembershipByIdOrProviderAsync(networkId, providerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(membership);
        networks.Setup(r => r.GetMembershipByIdOrProviderAsync(networkId, membership.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(membership);
        networks.Setup(r => r.UpdateProviderInRegistryAsync(provider, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        networks.Setup(r => r.GetFacilityByIdAsync(tenantId, facility.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(facility);
        networks.Setup(r => r.UpdateFacilityAsync(facility, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        networks.Setup(r => r.SyncProviderSpecialtiesAsync(provider.Id, It.IsAny<List<Guid>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        networks.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var specialties = new Mock<ISpecialtyRepository>();
        specialties.Setup(r => r.GetActiveByIdsAsync(It.Is<List<Guid>>(ids => ids.SequenceEqual(new[] { specialtyId })), It.IsAny<CancellationToken>()))
            .ReturnsAsync([specialty]);

        var sut = BuildSut(networks.Object, specialties.Object);

        var result = await sut.UpdateProviderAsync(tenantId, networkId, providerId, ValidUpdateRequest([specialtyId]), null,
            callerOrgId: callerOrgId, isNetworkManager: true, isTenantAdmin: false);

        Assert.Equal("Dr. Jane Provider", result.Name);
    }

    [Fact]
    public async Task RemoveProviderAsync_WhenCallerDidNotOwnProvider_ThrowsForbidden()
    {
        var tenantId = Guid.CreateVersion7();
        var networkId = Guid.CreateVersion7();
        var ownerOrgId = Guid.CreateVersion7();
        var callerOrgId = Guid.CreateVersion7();
        var facility = Facility.Create(
            tenantId, "Jane Practice", "123 Main St", "Austin", "TX", "78701", "555-0100", true, null);
        var membership = NetworkProvider.Create(tenantId, networkId, Guid.CreateVersion7(), facility.Id, true, true, ownerOrgId);
        SetNavigation(membership, nameof(NetworkProvider.Facility), facility);

        var networks = new Mock<INetworkRepository>();
        networks.Setup(r => r.GetByIdAsync(tenantId, networkId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProviderNetwork.Create(tenantId, "Network", string.Empty));
        networks.Setup(r => r.GetMembershipByIdOrProviderAsync(networkId, membership.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(membership);

        var sut = BuildSut(networks.Object, Mock.Of<ISpecialtyRepository>());

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            sut.RemoveProviderAsync(tenantId, networkId, membership.Id, cascadeFacility: false, userId: null,
                callerOrgId: callerOrgId, isNetworkManager: false, isTenantAdmin: false));

        Assert.True(membership.IsActive);
        networks.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RemoveProviderAsync_WhenCallerOwnsProvider_Succeeds()
    {
        var tenantId = Guid.CreateVersion7();
        var networkId = Guid.CreateVersion7();
        var callerOrgId = Guid.CreateVersion7();
        var facility = Facility.Create(
            tenantId, "Jane Practice", "123 Main St", "Austin", "TX", "78701", "555-0100", true, null);
        var membership = NetworkProvider.Create(tenantId, networkId, Guid.CreateVersion7(), facility.Id, true, true, callerOrgId);
        SetNavigation(membership, nameof(NetworkProvider.Facility), facility);

        var networks = new Mock<INetworkRepository>();
        networks.Setup(r => r.GetByIdAsync(tenantId, networkId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProviderNetwork.Create(tenantId, "Network", string.Empty));
        networks.Setup(r => r.GetMembershipByIdOrProviderAsync(networkId, membership.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(membership);
        networks.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = BuildSut(networks.Object, Mock.Of<ISpecialtyRepository>());

        await sut.RemoveProviderAsync(tenantId, networkId, membership.Id, cascadeFacility: false, userId: null,
            callerOrgId: callerOrgId, isNetworkManager: false, isTenantAdmin: false);

        Assert.False(membership.IsActive);
    }

    [Fact]
    public async Task RemoveProviderAsync_SoftDeletesMembershipAndTagsFacilityInactive()
    {
        var tenantId = Guid.CreateVersion7();
        var networkId = Guid.CreateVersion7();
        var providerId = Guid.CreateVersion7();
        var facility = Facility.Create(
            tenantId, "Jane Practice", "123 Main St", "Austin", "TX", "78701", "555-0100", true, null);
        var membership = NetworkProvider.Create(tenantId, networkId, providerId, facility.Id, true, true);
        SetNavigation(membership, nameof(NetworkProvider.Facility), facility);

        var networks = new Mock<INetworkRepository>();
        networks.Setup(r => r.GetByIdAsync(tenantId, networkId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProviderNetwork.Create(tenantId, "Network", string.Empty));
        networks.Setup(r => r.GetMembershipByIdOrProviderAsync(networkId, membership.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(membership);
        networks.Setup(r => r.HasOtherActiveNetworkProviderForFacilityAsync(tenantId, facility.Id, membership.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        networks.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = BuildSut(networks.Object, Mock.Of<ISpecialtyRepository>());

        await sut.RemoveProviderAsync(tenantId, networkId, membership.Id, cascadeFacility: true, userId: null);

        Assert.False(membership.IsActive);
        Assert.False(membership.AcceptingReferrals);
        Assert.False(facility.IsActive);
        networks.Verify(r => r.RemoveProviderAsync(It.IsAny<NetworkProvider>(), It.IsAny<CancellationToken>()), Times.Never);
        networks.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RemoveProviderAsync_WhenCascadeFacilityIsFalse_LeavesFacilityUntouched()
    {
        // The tenant-portal "Remove from network" (X) icon is a distinct action from
        // "Delete location" — it must keep its original, membership-only soft delete and
        // never touch cc_Facilities.IsActive, even though both actions call this same method.
        var tenantId = Guid.CreateVersion7();
        var networkId = Guid.CreateVersion7();
        var providerId = Guid.CreateVersion7();
        var facility = Facility.Create(
            tenantId, "Jane Practice", "123 Main St", "Austin", "TX", "78701", "555-0100", true, null);
        var membership = NetworkProvider.Create(tenantId, networkId, providerId, facility.Id, true, true);
        SetNavigation(membership, nameof(NetworkProvider.Facility), facility);

        var networks = new Mock<INetworkRepository>();
        networks.Setup(r => r.GetByIdAsync(tenantId, networkId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProviderNetwork.Create(tenantId, "Network", string.Empty));
        networks.Setup(r => r.GetMembershipByIdOrProviderAsync(networkId, membership.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(membership);
        networks.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = BuildSut(networks.Object, Mock.Of<ISpecialtyRepository>());

        await sut.RemoveProviderAsync(tenantId, networkId, membership.Id, cascadeFacility: false, userId: null);

        Assert.False(membership.IsActive);
        Assert.False(membership.AcceptingReferrals);
        Assert.True(facility.IsActive);
        networks.Verify(r => r.HasOtherActiveNetworkProviderForFacilityAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        networks.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RemoveProviderAsync_WhenFacilityStillUsedByAnotherActiveMembership_LeavesFacilityActive()
    {
        var tenantId = Guid.CreateVersion7();
        var networkId = Guid.CreateVersion7();
        var providerId = Guid.CreateVersion7();
        var facility = Facility.Create(
            tenantId, "Shared Clinic", "1 Plaza Dr", "Austin", "TX", "78701", "555-0100", true, null);
        var membership = NetworkProvider.Create(tenantId, networkId, providerId, facility.Id, true, true);
        SetNavigation(membership, nameof(NetworkProvider.Facility), facility);

        var networks = new Mock<INetworkRepository>();
        networks.Setup(r => r.GetByIdAsync(tenantId, networkId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProviderNetwork.Create(tenantId, "Network", string.Empty));
        networks.Setup(r => r.GetMembershipByIdOrProviderAsync(networkId, membership.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(membership);
        // Another active NetworkProvider (different provider/network) still points at this same facility.
        networks.Setup(r => r.HasOtherActiveNetworkProviderForFacilityAsync(tenantId, facility.Id, membership.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        networks.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = BuildSut(networks.Object, Mock.Of<ISpecialtyRepository>());

        await sut.RemoveProviderAsync(tenantId, networkId, membership.Id, cascadeFacility: true, userId: null);

        Assert.False(membership.IsActive);
        Assert.True(facility.IsActive);
    }

    [Fact]
    public async Task RemoveProviderAsync_WhenMembershipHasNoFacility_SoftDeletesWithoutThrowing()
    {
        // Legacy memberships can have FacilityId == Guid.Empty with no matching Facility row
        // (see GetMembershipByIdOrProviderAsync's ThenByDescending ordering) — the Facility
        // navigation stays null in that case and must not be dereferenced.
        var tenantId = Guid.CreateVersion7();
        var networkId = Guid.CreateVersion7();
        var providerId = Guid.CreateVersion7();
        var membership = NetworkProvider.Create(tenantId, networkId, providerId, Guid.Empty, true, true);

        var networks = new Mock<INetworkRepository>();
        networks.Setup(r => r.GetByIdAsync(tenantId, networkId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProviderNetwork.Create(tenantId, "Network", string.Empty));
        networks.Setup(r => r.GetMembershipByIdOrProviderAsync(networkId, membership.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(membership);
        networks.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = BuildSut(networks.Object, Mock.Of<ISpecialtyRepository>());

        await sut.RemoveProviderAsync(tenantId, networkId, membership.Id, cascadeFacility: true, userId: null);

        Assert.False(membership.IsActive);
        networks.Verify(r => r.HasOtherActiveNetworkProviderForFacilityAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        networks.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // 4-AC3: Tenant Admin controls whether a Law Firm's provider is Public (shared
    // network) or Private (Law Firm network only) — a CareConnectReferrerAdmin, even
    // one who owns the provider, must not be able to change Visibility.
    [Fact]
    public async Task UpdateProviderAsync_WhenTenantAdmin_CanChangeVisibility()
    {
        var (sut, networks, tenantId, networkId, providerId, membership, specialtyId) = BuildVisibilityFixture();

        var request = ValidUpdateRequest([specialtyId]) with { Visibility = ProviderVisibility.Public };
        var result = await sut.UpdateProviderAsync(tenantId, networkId, providerId, request, null,
            isTenantAdmin: true, callerOrgId: null, isNetworkManager: false);

        Assert.Equal(ProviderVisibility.Public, membership.Visibility);
        Assert.Equal(ProviderVisibility.Public, result.Visibility);
    }

    [Fact]
    public async Task UpdateProviderAsync_WhenReferrerAdminOwnsProvider_CannotChangeVisibility()
    {
        var ownerOrgId = Guid.CreateVersion7();
        var (sut, networks, tenantId, networkId, providerId, membership, specialtyId) =
            BuildVisibilityFixture(owningOrganizationId: ownerOrgId);

        var request = ValidUpdateRequest([specialtyId]) with { Visibility = ProviderVisibility.Public };

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            sut.UpdateProviderAsync(tenantId, networkId, providerId, request, null,
                isTenantAdmin: false, callerOrgId: ownerOrgId, isNetworkManager: false));

        Assert.Equal(ProviderVisibility.Private, membership.Visibility);
        networks.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateProviderAsync_WhenNetworkManagerButNotTenantAdmin_CannotChangeVisibility()
    {
        // NetworkManager bypasses the ownership check (LSV3-1084) but must still be
        // rejected here — only a system/tenant admin may change Visibility.
        var ownerOrgId = Guid.CreateVersion7();
        var callerOrgId = Guid.CreateVersion7();
        var (sut, networks, tenantId, networkId, providerId, membership, specialtyId) =
            BuildVisibilityFixture(owningOrganizationId: ownerOrgId);

        var request = ValidUpdateRequest([specialtyId]) with { Visibility = ProviderVisibility.Public };

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            sut.UpdateProviderAsync(tenantId, networkId, providerId, request, null,
                isTenantAdmin: false, callerOrgId: callerOrgId, isNetworkManager: true));

        Assert.Equal(ProviderVisibility.Private, membership.Visibility);
        networks.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateProviderAsync_WhenVisibilityOmitted_LeavesExistingVisibilityUnchanged()
    {
        var ownerOrgId = Guid.CreateVersion7();
        var (sut, networks, tenantId, networkId, providerId, membership, specialtyId) =
            BuildVisibilityFixture(owningOrganizationId: ownerOrgId);

        // Referrer admin editing their own provider's other fields (name, phone, etc.)
        // without touching Visibility must not be blocked or alter the stored value.
        var request = ValidUpdateRequest([specialtyId]);
        var result = await sut.UpdateProviderAsync(tenantId, networkId, providerId, request, null,
            isTenantAdmin: false, callerOrgId: ownerOrgId, isNetworkManager: false);

        Assert.Equal(ProviderVisibility.Private, membership.Visibility);
        Assert.Equal(ProviderVisibility.Private, result.Visibility);
    }

    [Fact]
    public async Task UpdateProviderAsync_WhenVisibilityIsInvalid_ThrowsArgumentOutOfRange()
    {
        var (sut, networks, tenantId, networkId, providerId, _, specialtyId) = BuildVisibilityFixture();

        var request = ValidUpdateRequest([specialtyId]) with { Visibility = "Shared" };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            sut.UpdateProviderAsync(tenantId, networkId, providerId, request, null,
                isTenantAdmin: true, callerOrgId: null, isNetworkManager: false));
    }

    private static (
        NetworkService Sut,
        Mock<INetworkRepository> Networks,
        Guid TenantId,
        Guid NetworkId,
        Guid ProviderId,
        NetworkProvider Membership,
        Guid SpecialtyId) BuildVisibilityFixture(Guid? owningOrganizationId = null)
    {
        var tenantId = Guid.CreateVersion7();
        var networkId = Guid.CreateVersion7();
        var specialtyId = Guid.CreateVersion7();
        var specialty = Specialty.Create("Pain Doctors", "PAIN_DOCTORS", null);
        var provider = Provider.Create(
            tenantId, "Jane Provider", "Jane Practice", "jane@example.com", "555-0100",
            "123 Main St", "Austin", "TX", "78701", true, true, null);
        var providerId = provider.Id;
        var facility = Facility.Create(
            tenantId, "Jane Practice", "123 Main St", "Austin", "TX", "78701", "555-0100",
            true, null, "jane@example.com");
        var membership = NetworkProvider.Create(
            tenantId, networkId, providerId, facility.Id, true, true,
            owningOrganizationId, ProviderVisibility.Private);
        SetNavigation(membership, nameof(NetworkProvider.Provider), provider);
        SetNavigation(membership, nameof(NetworkProvider.Facility), facility);

        var networks = new Mock<INetworkRepository>();
        networks.Setup(r => r.GetByIdAsync(tenantId, networkId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProviderNetwork.Create(tenantId, "Network", string.Empty));
        networks.Setup(r => r.GetMembershipByIdOrProviderAsync(networkId, providerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(membership);
        networks.Setup(r => r.GetMembershipByIdOrProviderAsync(networkId, membership.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(membership);
        networks.Setup(r => r.UpdateProviderInRegistryAsync(provider, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        networks.Setup(r => r.GetFacilityByIdAsync(tenantId, facility.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(facility);
        networks.Setup(r => r.UpdateFacilityAsync(facility, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        networks.Setup(r => r.SyncProviderSpecialtiesAsync(provider.Id, It.IsAny<List<Guid>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        networks.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var specialties = new Mock<ISpecialtyRepository>();
        specialties.Setup(r => r.GetActiveByIdsAsync(It.Is<List<Guid>>(ids => ids.SequenceEqual(new[] { specialtyId })), It.IsAny<CancellationToken>()))
            .ReturnsAsync([specialty]);

        var sut = BuildSut(networks.Object, specialties.Object);
        return (sut, networks, tenantId, networkId, providerId, membership, specialtyId);
    }

    private static NetworkService BuildSut(INetworkRepository networks, ISpecialtyRepository specialties) =>
        new(
            networks,
            Mock.Of<ICategoryRepository>(),
            specialties,
            Mock.Of<IProviderImportParser>(),
            NullLogger<NetworkService>.Instance);

    private static UpdateNetworkProviderRequest ValidUpdateRequest(List<Guid> specialtyIds) => new(
        FirstName: "Jane",
        LastName: "Provider",
        OrganizationName: "Jane Practice",
        FacilityName: "Jane Practice - North",
        Email: "jane@example.com",
        Phone: "555-0100",
        AddressLine1: "123 Main St",
        City: "Austin",
        State: "TX",
        PostalCode: "78701",
        IsActive: true,
        AcceptingReferrals: true,
        SpecialtyIds: specialtyIds,
        Title: "Dr.");

    private static void SetNavigation<T>(object target, string propertyName, T value)
    {
        var property = target.GetType().GetProperty(propertyName)
            ?? throw new InvalidOperationException($"Property {propertyName} was not found.");
        property.SetValue(target, value);
    }
}
