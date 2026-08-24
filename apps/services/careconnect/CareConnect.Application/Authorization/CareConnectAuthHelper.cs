using BuildingBlocks.Authorization;
using BuildingBlocks.Context;

namespace CareConnect.Application.Authorization;

/// <summary>
/// CareConnect-specific authorization helper.
/// Applies PlatformAdmin and TenantAdmin bypasses before delegating
/// capability checks to the <see cref="AuthorizationService"/>.
/// </summary>
public static class CareConnectAuthHelper
{
    /// <summary>
    /// Throws <see cref="BuildingBlocks.Exceptions.ForbiddenException"/> when the user
    /// does not hold the required capability and is not a PlatformAdmin or TenantAdmin.
    /// </summary>
    // LSCC-001: CareConnect permission enforcement — two-level bypass then capability check
    public static async Task RequireAsync(
        ICurrentRequestContext ctx,
        AuthorizationService authSvc,
        string capabilityCode,
        CancellationToken ct = default)
    {
        // LSCC-001: PlatformAdmin and TenantAdmin bypass all capability checks
        if (ctx.IsPlatformAdmin) return;
        if (ctx.Roles.Contains(Roles.TenantAdmin, StringComparer.OrdinalIgnoreCase)) return;
        if (IsMigratedLawFirmReferrerSession(ctx, capabilityCode)) return;

        // LSCC-001: All other users must hold the specific capability for this operation
        if (!await authSvc.IsAuthorizedAsync(ctx, capabilityCode, ct))
            throw new BuildingBlocks.Exceptions.ForbiddenException(capabilityCode);
    }

    /// <summary>
    /// Returns true when the user is a PlatformAdmin, TenantAdmin, or holds at least
    /// one of the supplied capability codes.
    /// </summary>
    public static async Task<bool> HasAnyAsync(
        ICurrentRequestContext ctx,
        AuthorizationService authSvc,
        IEnumerable<string> capabilityCodes,
        CancellationToken ct = default)
    {
        if (ctx.IsPlatformAdmin) return true;
        if (ctx.Roles.Contains(Roles.TenantAdmin, StringComparer.OrdinalIgnoreCase)) return true;

        foreach (var code in capabilityCodes)
        {
            if (IsMigratedLawFirmReferrerSession(ctx, code))
                return true;

            if (await authSvc.IsAuthorizedAsync(ctx, code, ct))
                return true;
        }
        return false;
    }

    private static bool IsMigratedLawFirmReferrerSession(ICurrentRequestContext ctx, string capabilityCode)
    {
        if (capabilityCode is not (PermissionCodes.ReferralAccept
            or PermissionCodes.ReferralDecline
            or PermissionCodes.ReferralUpdateStatus))
        {
            return false;
        }

        if (!string.Equals(ctx.OrgType, "LAW_FIRM", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(ctx.OrgType, "LawFirm", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return ctx.ProductRoles.Any(role =>
            string.Equals(role, ProductRoleCodes.CareConnectReferrer, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, $"{ProductCodes.SynqCareConnect}:{ProductRoleCodes.CareConnectReferrer}", StringComparison.OrdinalIgnoreCase));
    }
}
