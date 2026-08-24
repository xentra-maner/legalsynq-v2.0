using BuildingBlocks.Authorization;

namespace CareConnect.Infrastructure.Services;

public sealed class CareConnectPermissionService : IPermissionService
{
    // JWT product_roles claims arrive as "SYNQ_CARECONNECT:CARECONNECT_RECEIVER".
    // Strip the product-code prefix before the dictionary lookup so that both the
    // bare role codes used in unit tests and the prefixed form used at runtime match.
    private const string ProductPrefix = ProductCodes.SynqCareConnect + ":";

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> RolePermissions =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [ProductRoleCodes.CareConnectReferrer] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                PermissionCodes.ReferralCreate,
                PermissionCodes.ReferralReadOwn,
                PermissionCodes.ReferralCancel,
                PermissionCodes.ProviderSearch,
                PermissionCodes.ProviderMap,
                PermissionCodes.AppointmentCreate,
                PermissionCodes.AppointmentReadOwn,
                PermissionCodes.DashboardRead,
            },
            [ProductRoleCodes.CareConnectReferrerAdmin] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                PermissionCodes.ReferralCreate,
                PermissionCodes.ReferralReadOwn,
                PermissionCodes.ReferralAccept,
                PermissionCodes.ReferralDecline,
                PermissionCodes.ReferralUpdateStatus,
                PermissionCodes.ReferralCancel,
                PermissionCodes.ProviderSearch,
                PermissionCodes.ProviderMap,
                PermissionCodes.ProviderManage,
                PermissionCodes.AppointmentCreate,
                PermissionCodes.AppointmentReadOwn,
                PermissionCodes.DashboardRead,
            },
            [ProductRoleCodes.CareConnectReceiver] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                PermissionCodes.ReferralReadAddressed,
                PermissionCodes.ReferralAccept,
                PermissionCodes.ReferralDecline,
                PermissionCodes.ReferralUpdateStatus,
                PermissionCodes.ReferralCancel,
                PermissionCodes.AppointmentCreate,
                PermissionCodes.AppointmentUpdate,
                PermissionCodes.AppointmentManage,
                PermissionCodes.AppointmentReadOwn,
                PermissionCodes.ScheduleManage,
                PermissionCodes.ProviderSearch,
                PermissionCodes.ProviderMap,
                PermissionCodes.DashboardRead,
            },
        };

    public Task<bool> HasPermissionAsync(
        IReadOnlyCollection<string> productRoleCodes,
        string permissionCode,
        CancellationToken ct = default)
    {
        foreach (var roleCode in productRoleCodes)
        {
            var key = StripPrefix(roleCode);
            if (RolePermissions.TryGetValue(key, out var perms) && perms.Contains(permissionCode))
                return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task<IReadOnlySet<string>> GetPermissionsAsync(
        IReadOnlyCollection<string> productRoleCodes,
        CancellationToken ct = default)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var roleCode in productRoleCodes)
        {
            var key = StripPrefix(roleCode);
            if (RolePermissions.TryGetValue(key, out var perms))
                result.UnionWith(perms);
        }
        return Task.FromResult<IReadOnlySet<string>>(result);
    }

    private static string StripPrefix(string roleCode) =>
        roleCode.StartsWith(ProductPrefix, StringComparison.OrdinalIgnoreCase)
            ? roleCode[ProductPrefix.Length..]
            : roleCode;
}
