using BuildingBlocks.Authorization;
using BuildingBlocks.Context;
using CareConnect.Application.Authorization;
using Xunit;

namespace CareConnect.Tests.Authorization;

public class CareConnectAuthHelperTests
{
    [Theory]
    [InlineData(PermissionCodes.ReferralAccept)]
    [InlineData(PermissionCodes.ReferralDecline)]
    [InlineData(PermissionCodes.ReferralUpdateStatus)]
    public async Task RequireAsync_AllowsMigratedLawFirmReferrerSession_ForReferralProcessingPermissions(string permission)
    {
        var ctx = new TestRequestContext
        {
            OrgType = "LAW_FIRM",
            ProductRoles = [$"{ProductCodes.SynqCareConnect}:{ProductRoleCodes.CareConnectReferrer}"],
        };
        var auth = new AuthorizationService(new DenyAllPermissionService());

        await CareConnectAuthHelper.RequireAsync(ctx, auth, permission);
    }

    [Fact]
    public async Task RequireAsync_DoesNotApplyMigratedReferrerCompatibility_ToNonLawFirmSessions()
    {
        var ctx = new TestRequestContext
        {
            OrgType = "PROVIDER",
            ProductRoles = [$"{ProductCodes.SynqCareConnect}:{ProductRoleCodes.CareConnectReferrer}"],
        };
        var auth = new AuthorizationService(new DenyAllPermissionService());

        await Assert.ThrowsAsync<BuildingBlocks.Exceptions.ForbiddenException>(() =>
            CareConnectAuthHelper.RequireAsync(ctx, auth, PermissionCodes.ReferralAccept));
    }

    private sealed class DenyAllPermissionService : IPermissionService
    {
        public Task<bool> HasPermissionAsync(
            IReadOnlyCollection<string> productRoleCodes,
            string permissionCode,
            CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<IReadOnlySet<string>> GetPermissionsAsync(
            IReadOnlyCollection<string> productRoleCodes,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
    }

    private sealed class TestRequestContext : ICurrentRequestContext
    {
        public bool IsAuthenticated { get; init; } = true;
        public Guid? UserId { get; init; } = Guid.CreateVersion7();
        public Guid? TenantId { get; init; } = Guid.CreateVersion7();
        public string? TenantCode { get; init; }
        public string? Email { get; init; }
        public string? Name { get; init; }
        public Guid? OrgId { get; init; } = Guid.CreateVersion7();
        public string? OrgType { get; init; }
        public Guid? OrgTypeId { get; init; }
        public string? ProviderMode { get; init; }
        public bool IsSellMode { get; init; } = true;
        public bool IsManageMode { get; init; }
        public IReadOnlyCollection<string> Roles { get; init; } = [];
        public IReadOnlyCollection<string> ProductRoles { get; init; } = [];
        public IReadOnlyCollection<string> Permissions { get; init; } = [];
        public bool IsPlatformAdmin { get; init; }
        public IReadOnlyList<Guid> TenantIds { get; init; } = [];
    }
}
