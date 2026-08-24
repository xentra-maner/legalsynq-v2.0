namespace CareConnect.Application.Interfaces;

/// <summary>
/// LSV3-1083 — Law Firm Company Super Admin/Manager.
///
/// Lets a CARECONNECT_REFERRER_ADMIN (or TenantAdmin/PlatformAdmin) view and manage the
/// users belonging to one law-firm organization. CareConnect has no local user domain —
/// this service enforces org-ownership (mirroring <see cref="INetworkService"/>'s
/// callerOrgId/isTenantAdmin pattern) and then proxies to Identity via
/// <see cref="IIdentityOrganizationService"/>.
/// </summary>
public interface ILawFirmUserService
{
    Task<IReadOnlyList<LawFirmUserSummary>> ListUsersAsync(
        Guid targetOrgId, Guid? callerOrgId, bool isTenantAdmin, CancellationToken ct = default);

    Task<LawFirmUserInviteResult> InviteUserAsync(
        Guid targetOrgId, Guid tenantId, string email, string firstName, string lastName, string? roleCode,
        Guid? callerOrgId, bool isTenantAdmin, CancellationToken ct = default);

    Task ResendInviteAsync(
        Guid targetOrgId, Guid userId, Guid? callerOrgId, bool isTenantAdmin, CancellationToken ct = default);

    Task ActivateUserAsync(
        Guid targetOrgId, Guid userId, Guid? callerOrgId, bool isTenantAdmin, CancellationToken ct = default);

    Task DeactivateUserAsync(
        Guid targetOrgId, Guid userId, Guid? callerOrgId, bool isTenantAdmin, CancellationToken ct = default);

    Task<Guid> AssignRoleAsync(
        Guid targetOrgId, Guid tenantId, Guid userId, string roleCode, Guid? callerOrgId, bool isTenantAdmin, CancellationToken ct = default);

    Task RevokeRoleAsync(
        Guid targetOrgId, Guid userId, Guid assignmentId, Guid? callerOrgId, bool isTenantAdmin, CancellationToken ct = default);
}
