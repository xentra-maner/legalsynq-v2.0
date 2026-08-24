using BuildingBlocks.Authorization;
using CareConnect.Infrastructure.Services;
using Xunit;

namespace CareConnect.Tests.Authorization;

public class CareConnectPermissionServiceTests
{
    private readonly CareConnectPermissionService _sut = new();

    [Theory]
    [InlineData(ProductRoleCodes.CareConnectReferrer, PermissionCodes.ReferralCreate,    true)]
    [InlineData(ProductRoleCodes.CareConnectReferrer, PermissionCodes.ReferralReadOwn,   true)]
    [InlineData(ProductRoleCodes.CareConnectReferrer, PermissionCodes.ReferralCancel,    true)]
    [InlineData(ProductRoleCodes.CareConnectReferrer, PermissionCodes.ProviderSearch,    true)]
    [InlineData(ProductRoleCodes.CareConnectReferrer, PermissionCodes.ProviderMap,       true)]
    [InlineData(ProductRoleCodes.CareConnectReferrer, PermissionCodes.DashboardRead,     true)]
    [InlineData(ProductRoleCodes.CareConnectReferrer, PermissionCodes.ReferralAccept,        false)]
    [InlineData(ProductRoleCodes.CareConnectReferrer, PermissionCodes.ReferralDecline,       false)]
    [InlineData(ProductRoleCodes.CareConnectReferrer, PermissionCodes.ReferralUpdateStatus,  false)]
    [InlineData(ProductRoleCodes.CareConnectReferrer, PermissionCodes.ScheduleManage,        false)]
    [InlineData(ProductRoleCodes.CareConnectReferrer, PermissionCodes.ProviderManage,        false)]
    [InlineData(ProductRoleCodes.CareConnectReferrerAdmin, PermissionCodes.ReferralCreate,       true)]
    [InlineData(ProductRoleCodes.CareConnectReferrerAdmin, PermissionCodes.ReferralReadOwn,      true)]
    [InlineData(ProductRoleCodes.CareConnectReferrerAdmin, PermissionCodes.ProviderSearch,       true)]
    [InlineData(ProductRoleCodes.CareConnectReferrerAdmin, PermissionCodes.ProviderManage,       true)]
    [InlineData(ProductRoleCodes.CareConnectReferrerAdmin, PermissionCodes.ReferralAccept,       true)]
    [InlineData(ProductRoleCodes.CareConnectReferrerAdmin, PermissionCodes.ReferralDecline,      true)]
    [InlineData(ProductRoleCodes.CareConnectReferrerAdmin, PermissionCodes.ReferralUpdateStatus, true)]
    [InlineData(ProductRoleCodes.CareConnectReferrerAdmin, PermissionCodes.ScheduleManage,       false)]
    [InlineData(ProductRoleCodes.CareConnectReceiver, PermissionCodes.ReferralAccept,        true)]
    [InlineData(ProductRoleCodes.CareConnectReceiver, PermissionCodes.ReferralDecline,       true)]
    [InlineData(ProductRoleCodes.CareConnectReceiver, PermissionCodes.ReferralUpdateStatus,  true)]
    [InlineData(ProductRoleCodes.CareConnectReceiver, PermissionCodes.ReferralCancel,        true)]
    [InlineData(ProductRoleCodes.CareConnectReceiver, PermissionCodes.ReferralReadAddressed, true)]
    [InlineData(ProductRoleCodes.CareConnectReceiver, PermissionCodes.ScheduleManage,        true)]
    [InlineData(ProductRoleCodes.CareConnectReceiver, PermissionCodes.AppointmentManage,     true)]
    [InlineData(ProductRoleCodes.CareConnectReceiver, PermissionCodes.ReferralCreate,        false)]
    [InlineData(ProductRoleCodes.CareConnectReceiver, PermissionCodes.ProviderManage,        false)]
    public async Task HasPermissionAsync_MatchesExpectedMapping(string roleCode, string permission, bool expected)
    {
        var result = await _sut.HasPermissionAsync(new[] { roleCode }, permission);
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task HasPermissionAsync_EmptyRoles_ReturnsFalse()
    {
        var result = await _sut.HasPermissionAsync(Array.Empty<string>(), PermissionCodes.ReferralCreate);
        Assert.False(result);
    }

    [Fact]
    public async Task HasPermissionAsync_MultipleRoles_UnionOfPermissions()
    {
        var roles = new[] { ProductRoleCodes.CareConnectReferrer, ProductRoleCodes.CareConnectReceiver };
        Assert.True(await _sut.HasPermissionAsync(roles, PermissionCodes.ReferralCreate));
        Assert.True(await _sut.HasPermissionAsync(roles, PermissionCodes.ReferralAccept));
    }

    [Fact]
    public async Task HasPermissionAsync_UnknownRole_ReturnsFalse()
    {
        var result = await _sut.HasPermissionAsync(new[] { "UNKNOWN_ROLE" }, PermissionCodes.ReferralCreate);
        Assert.False(result);
    }

    // --- JWT prefixed-form tests (real production path: "SYNQ_CARECONNECT:CARECONNECT_RECEIVER") ---

    [Theory]
    [InlineData("SYNQ_CARECONNECT:CARECONNECT_RECEIVER", PermissionCodes.ReferralAccept,       true)]
    [InlineData("SYNQ_CARECONNECT:CARECONNECT_RECEIVER", PermissionCodes.ReferralDecline,      true)]
    [InlineData("SYNQ_CARECONNECT:CARECONNECT_RECEIVER", PermissionCodes.ReferralUpdateStatus, true)]
    [InlineData("SYNQ_CARECONNECT:CARECONNECT_RECEIVER", PermissionCodes.ReferralCancel,       true)]
    [InlineData("SYNQ_CARECONNECT:CARECONNECT_RECEIVER", PermissionCodes.ReferralCreate,       false)]
    [InlineData("SYNQ_CARECONNECT:CARECONNECT_REFERRER", PermissionCodes.ReferralCreate,       true)]
    [InlineData("SYNQ_CARECONNECT:CARECONNECT_REFERRER", PermissionCodes.ReferralCancel,       true)]
    [InlineData("SYNQ_CARECONNECT:CARECONNECT_REFERRER", PermissionCodes.ReferralUpdateStatus, false)]
    [InlineData("SYNQ_CARECONNECT:CARECONNECT_REFERRER", PermissionCodes.ReferralAccept,       false)]
    [InlineData("SYNQ_CARECONNECT:CARECONNECT_REFERRER_ADMIN", PermissionCodes.ReferralCreate, true)]
    [InlineData("SYNQ_CARECONNECT:CARECONNECT_REFERRER_ADMIN", PermissionCodes.ReferralAccept, true)]
    [InlineData("SYNQ_CARECONNECT:CARECONNECT_REFERRER_ADMIN", PermissionCodes.ReferralUpdateStatus, true)]
    [InlineData("SYNQ_CARECONNECT:CARECONNECT_REFERRER_ADMIN", PermissionCodes.ProviderManage, true)]
    [InlineData("synq_careconnect:careconnect_receiver", PermissionCodes.ReferralAccept,       true)]  // case-insensitive
    public async Task HasPermissionAsync_PrefixedJwtRoleCode_StripsAndMatches(string jwtRoleCode, string permission, bool expected)
    {
        var result = await _sut.HasPermissionAsync(new[] { jwtRoleCode }, permission);
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task GetPermissionsAsync_PrefixedJwtRoleCode_ReturnsExpectedSet()
    {
        var perms = await _sut.GetPermissionsAsync(new[] { "SYNQ_CARECONNECT:CARECONNECT_RECEIVER" });
        Assert.Contains(PermissionCodes.ReferralAccept, perms);
        Assert.Contains(PermissionCodes.ReferralUpdateStatus, perms);
        Assert.Contains(PermissionCodes.ReferralCancel, perms);
        Assert.DoesNotContain(PermissionCodes.ReferralCreate, perms);
    }

    [Fact]
    public async Task GetPermissionsAsync_Referrer_ReturnsExpectedSet()
    {
        var perms = await _sut.GetPermissionsAsync(new[] { ProductRoleCodes.CareConnectReferrer });
        Assert.Contains(PermissionCodes.ReferralCreate, perms);
        Assert.Contains(PermissionCodes.ProviderSearch, perms);
        Assert.DoesNotContain(PermissionCodes.ReferralAccept, perms);
        Assert.DoesNotContain(PermissionCodes.ScheduleManage, perms);
    }
}
