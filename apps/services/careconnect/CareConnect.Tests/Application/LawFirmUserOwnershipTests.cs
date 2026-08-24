using BuildingBlocks.Authorization;
using BuildingBlocks.Exceptions;
using CareConnect.Application.Interfaces;
using CareConnect.Application.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CareConnect.Tests.Application;

// LSV3-1083: a CareConnectReferrerAdmin (law-firm-scoped) caller may only manage users
// in their own organization — a TenantAdmin/PlatformAdmin may manage any org in the tenant.
public class LawFirmUserOwnershipTests
{
    private static LawFirmUserService BuildSut(IIdentityOrganizationService identity) =>
        new(identity, NullLogger<LawFirmUserService>.Instance);

    [Fact]
    public async Task ListUsersAsync_WhenCallerOrgDiffersFromTarget_ThrowsForbidden()
    {
        var targetOrgId = Guid.CreateVersion7();
        var callerOrgId = Guid.CreateVersion7();
        var identity = new Mock<IIdentityOrganizationService>(MockBehavior.Strict);
        var sut = BuildSut(identity.Object);

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            sut.ListUsersAsync(targetOrgId, callerOrgId, isTenantAdmin: false, default));

        identity.Verify(i => i.ListOrganizationUsersAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ListUsersAsync_WhenCallerOrgMatchesTarget_Succeeds()
    {
        var orgId = Guid.CreateVersion7();
        var identity = new Mock<IIdentityOrganizationService>();
        identity.Setup(i => i.ListOrganizationUsersAsync(orgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((LawFirmUserOperationOutcome.Success, (IReadOnlyList<LawFirmUserSummary>?)new List<LawFirmUserSummary>(), (string?)null));

        var sut = BuildSut(identity.Object);
        var result = await sut.ListUsersAsync(orgId, orgId, isTenantAdmin: false, default);

        Assert.Empty(result);
    }

    [Fact]
    public async Task ListUsersAsync_WhenCallerIsTenantAdmin_BypassesOwnershipCheck()
    {
        var targetOrgId = Guid.CreateVersion7();
        var identity = new Mock<IIdentityOrganizationService>();
        identity.Setup(i => i.ListOrganizationUsersAsync(targetOrgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((LawFirmUserOperationOutcome.Success, (IReadOnlyList<LawFirmUserSummary>?)new List<LawFirmUserSummary>(), (string?)null));

        var sut = BuildSut(identity.Object);
        var result = await sut.ListUsersAsync(targetOrgId, callerOrgId: null, isTenantAdmin: true, default);

        Assert.Empty(result);
    }

    [Fact]
    public async Task InviteUserAsync_WhenCallerOrgDiffersFromTarget_ThrowsForbidden()
    {
        var targetOrgId = Guid.CreateVersion7();
        var callerOrgId = Guid.CreateVersion7();
        var identity = new Mock<IIdentityOrganizationService>(MockBehavior.Strict);
        var sut = BuildSut(identity.Object);

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            sut.InviteUserAsync(targetOrgId, Guid.CreateVersion7(), "a@b.com", "First", "Last", null, callerOrgId, isTenantAdmin: false, default));
    }

    [Fact]
    public async Task ActivateUserAsync_WhenCallerOrgDiffersFromTarget_ThrowsForbidden()
    {
        var targetOrgId = Guid.CreateVersion7();
        var callerOrgId = Guid.CreateVersion7();
        var identity = new Mock<IIdentityOrganizationService>(MockBehavior.Strict);
        var sut = BuildSut(identity.Object);

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            sut.ActivateUserAsync(targetOrgId, Guid.CreateVersion7(), callerOrgId, isTenantAdmin: false, default));
    }

    [Fact]
    public async Task DeactivateUserAsync_WhenCallerOrgDiffersFromTarget_ThrowsForbidden()
    {
        var targetOrgId = Guid.CreateVersion7();
        var callerOrgId = Guid.CreateVersion7();
        var identity = new Mock<IIdentityOrganizationService>(MockBehavior.Strict);
        var sut = BuildSut(identity.Object);

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            sut.DeactivateUserAsync(targetOrgId, Guid.CreateVersion7(), callerOrgId, isTenantAdmin: false, default));
    }

    [Fact]
    public async Task RevokeRoleAsync_WhenCallerOrgDiffersFromTarget_ThrowsForbidden()
    {
        var targetOrgId = Guid.CreateVersion7();
        var callerOrgId = Guid.CreateVersion7();
        var identity = new Mock<IIdentityOrganizationService>(MockBehavior.Strict);
        var sut = BuildSut(identity.Object);

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            sut.RevokeRoleAsync(targetOrgId, Guid.CreateVersion7(), Guid.CreateVersion7(), callerOrgId, isTenantAdmin: false, default));
    }

    [Theory]
    [InlineData(ProductRoleCodes.CareConnectNetworkManager)]
    [InlineData(ProductRoleCodes.CareConnectReceiver)]
    [InlineData("NOT_A_REAL_ROLE")]
    public async Task AssignRoleAsync_WhenRoleCodeIsNotAllowListed_ThrowsForbidden_AndNeverCallsIdentity(string roleCode)
    {
        var orgId = Guid.CreateVersion7();
        var identity = new Mock<IIdentityOrganizationService>(MockBehavior.Strict);
        var sut = BuildSut(identity.Object);

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            sut.AssignRoleAsync(orgId, Guid.CreateVersion7(), Guid.CreateVersion7(), roleCode, orgId, isTenantAdmin: false, default));

        identity.Verify(i => i.AssignOrganizationUserRoleAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(ProductRoleCodes.CareConnectReferrer)]
    [InlineData(ProductRoleCodes.CareConnectReferrerAdmin)]
    public async Task AssignRoleAsync_WhenRoleCodeIsAllowListed_CallsIdentity(string roleCode)
    {
        var orgId = Guid.CreateVersion7();
        var tenantId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        var assignmentId = Guid.CreateVersion7();
        var identity = new Mock<IIdentityOrganizationService>();
        identity.Setup(i => i.AssignOrganizationUserRoleAsync(orgId, tenantId, userId, roleCode, It.IsAny<CancellationToken>()))
            .ReturnsAsync((LawFirmUserOperationOutcome.Success, (Guid?)assignmentId, (string?)null));

        var sut = BuildSut(identity.Object);
        var result = await sut.AssignRoleAsync(orgId, tenantId, userId, roleCode, orgId, isTenantAdmin: false, default);

        Assert.Equal(assignmentId, result);
    }
}
