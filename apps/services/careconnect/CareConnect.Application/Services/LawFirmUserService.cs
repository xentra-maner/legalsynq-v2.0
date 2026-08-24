using BuildingBlocks.Authorization;
using BuildingBlocks.Exceptions;
using CareConnect.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace CareConnect.Application.Services;

// LSV3-1083 — Law Firm Company Super Admin/Manager.
public class LawFirmUserService : ILawFirmUserService
{
    private static readonly IReadOnlyList<string> AssignableRoleCodes =
    [
        ProductRoleCodes.CareConnectReferrer,
        ProductRoleCodes.CareConnectReferrerAdmin,
    ];

    private readonly IIdentityOrganizationService _identity;
    private readonly ILogger<LawFirmUserService> _logger;

    public LawFirmUserService(IIdentityOrganizationService identity, ILogger<LawFirmUserService> logger)
    {
        _identity = identity;
        _logger = logger;
    }

    public async Task<IReadOnlyList<LawFirmUserSummary>> ListUsersAsync(
        Guid targetOrgId, Guid? callerOrgId, bool isTenantAdmin, CancellationToken ct = default)
    {
        EnsureOwnership(targetOrgId, callerOrgId, isTenantAdmin);

        var (outcome, items, error) = await _identity.ListOrganizationUsersAsync(targetOrgId, ct);
        ThrowIfNotSuccess(outcome, error, "list users for");
        return items ?? [];
    }

    public async Task<LawFirmUserInviteResult> InviteUserAsync(
        Guid targetOrgId, Guid tenantId, string email, string firstName, string lastName, string? roleCode,
        Guid? callerOrgId, bool isTenantAdmin, CancellationToken ct = default)
    {
        EnsureOwnership(targetOrgId, callerOrgId, isTenantAdmin);

        var resolvedRoleCode = string.IsNullOrWhiteSpace(roleCode)
            ? ProductRoleCodes.CareConnectReferrer
            : roleCode.Trim().ToUpperInvariant();
        EnsureAssignableRole(resolvedRoleCode);

        var (outcome, result, error) = await _identity.InviteOrganizationUserAsync(
            targetOrgId, tenantId, email, firstName, lastName, resolvedRoleCode, ct);
        ThrowIfNotSuccess(outcome, error, "invite a user into");
        return result!;
    }

    public async Task ResendInviteAsync(
        Guid targetOrgId, Guid userId, Guid? callerOrgId, bool isTenantAdmin, CancellationToken ct = default)
    {
        EnsureOwnership(targetOrgId, callerOrgId, isTenantAdmin);

        var (outcome, error) = await _identity.ResendOrganizationUserInviteAsync(targetOrgId, userId, ct);
        ThrowIfNotSuccess(outcome, error, "resend an invitation in");
    }

    public async Task ActivateUserAsync(
        Guid targetOrgId, Guid userId, Guid? callerOrgId, bool isTenantAdmin, CancellationToken ct = default)
    {
        EnsureOwnership(targetOrgId, callerOrgId, isTenantAdmin);

        var (outcome, error) = await _identity.ActivateOrganizationUserAsync(targetOrgId, userId, ct);
        ThrowIfNotSuccess(outcome, error, "activate a user in");
    }

    public async Task DeactivateUserAsync(
        Guid targetOrgId, Guid userId, Guid? callerOrgId, bool isTenantAdmin, CancellationToken ct = default)
    {
        EnsureOwnership(targetOrgId, callerOrgId, isTenantAdmin);

        var (outcome, error) = await _identity.DeactivateOrganizationUserAsync(targetOrgId, userId, ct);
        ThrowIfNotSuccess(outcome, error, "deactivate a user in");
    }

    public async Task<Guid> AssignRoleAsync(
        Guid targetOrgId, Guid tenantId, Guid userId, string roleCode, Guid? callerOrgId, bool isTenantAdmin, CancellationToken ct = default)
    {
        EnsureOwnership(targetOrgId, callerOrgId, isTenantAdmin);

        var resolvedRoleCode = (roleCode ?? string.Empty).Trim().ToUpperInvariant();
        EnsureAssignableRole(resolvedRoleCode);

        var (outcome, assignmentId, error) = await _identity.AssignOrganizationUserRoleAsync(targetOrgId, tenantId, userId, resolvedRoleCode, ct);
        ThrowIfNotSuccess(outcome, error, "assign a role to a user in");
        return assignmentId!.Value;
    }

    public async Task RevokeRoleAsync(
        Guid targetOrgId, Guid userId, Guid assignmentId, Guid? callerOrgId, bool isTenantAdmin, CancellationToken ct = default)
    {
        EnsureOwnership(targetOrgId, callerOrgId, isTenantAdmin);

        var (outcome, error) = await _identity.RevokeOrganizationUserRoleAsync(targetOrgId, userId, assignmentId, ct);
        ThrowIfNotSuccess(outcome, error, "revoke a role from a user in");
    }

    // LSV3-1083: a CareConnectReferrerAdmin (law-firm-scoped) caller may only manage users
    // in their own organization — a TenantAdmin/PlatformAdmin may manage any org in the tenant.
    private static void EnsureOwnership(Guid targetOrgId, Guid? callerOrgId, bool isTenantAdmin)
    {
        if (!isTenantAdmin && callerOrgId != targetOrgId)
            throw new ForbiddenException("You can only manage users in your own organization.");
    }

    private static void EnsureAssignableRole(string roleCode)
    {
        if (!AssignableRoleCodes.Contains(roleCode))
            throw new ForbiddenException(
                $"Only {ProductRoleCodes.CareConnectReferrer} and {ProductRoleCodes.CareConnectReferrerAdmin} roles may be assigned here.");
    }

    private void ThrowIfNotSuccess(LawFirmUserOperationOutcome outcome, string? error, string action)
    {
        if (outcome == LawFirmUserOperationOutcome.Success)
            return;

        _logger.LogWarning("Law-firm user management: failed to {Action} organization ({Outcome}): {Error}", action, outcome, error);

        throw outcome switch
        {
            LawFirmUserOperationOutcome.NotFound => new NotFoundException(error ?? "Resource not found."),
            LawFirmUserOperationOutcome.Conflict => new ConflictException(error ?? "The request conflicts with existing state."),
            LawFirmUserOperationOutcome.BadRequest => new ValidationException("Validation failed.", new() { ["request"] = [error ?? "Invalid request."] }),
            _ => new ServiceUnavailableException(error ?? "Identity service is unavailable."),
        };
    }
}
