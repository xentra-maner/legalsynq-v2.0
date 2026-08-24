// LSCC-010 / CC2-INT-B04: Cross-service calls to the Identity service.
// Identity service = membership / access only (BLK-CC-01).
//
// Retired methods (BLK-ID-01 → now use ITenantServiceClient + IIdentityMembershipClient):
//   - CheckTenantCodeAvailableAsync  (was GET /api/admin/tenants/check-code)
//   - SelfProvisionProviderTenantAsync (was POST /api/admin/tenants/self-provision)
namespace CareConnect.Application.Interfaces;

/// <summary>
/// Thin cross-service abstraction over Identity service endpoints used during
/// provider auto-provisioning and user invitation.
///
/// Scope (BLK-CC-01): Identity = org creation + user invitation ONLY.
/// Tenant lifecycle (check-code, provision) is handled by ITenantServiceClient.
/// Tenant membership (assign-tenant, assign-roles) is handled by IIdentityMembershipClient.
/// </summary>
public interface IIdentityOrganizationService
{
    // ── LSCC-010: Provider org creation ──────────────────────────────────────

    /// <summary>
    /// Creates or resolves a minimal PROVIDER Organization in the Identity service
    /// for the given CareConnect provider.
    ///
    /// Idempotency: the identity endpoint uses (TenantId + ProviderCcId) as the
    /// unique key. Repeated calls with the same inputs return the same org ID.
    ///
    /// Returns the Identity OrganizationId on success, null on any failure.
    /// Callers must treat null as "fall back to LSCC-009".
    /// </summary>
    Task<Guid?> EnsureProviderOrganizationAsync(
        Guid              tenantId,
        Guid              providerCcId,
        string            providerName,
        CancellationToken ct = default,
        bool              globalScope = false);

    // ── CC2-INT-B04: Token → Identity Bridge — user invitation ───────────────

    /// <summary>
    /// Creates an inactive Identity user under the given org's tenant and sends
    /// them an invitation email so they can set a password and log in.
    ///
    /// Idempotent: if a user with the given email already exists in the org's tenant
    /// the existing user record is returned and no duplicate is created.
    ///
    /// Returns a result on success (isNew=true for new users, false for existing).
    /// Returns null on any failure — non-fatal; provider org link is already established.
    /// </summary>
    Task<ProvisionProviderUserResult?> InviteProviderUserAsync(
        Guid              orgId,
        string            email,
        string            firstName,
        string?           lastName,
        CancellationToken ct = default);

    // ── CC2-ENROLL: Self-enrollment — direct password registration ────────────

    /// <summary>
    /// Creates an immediately ACTIVE Identity user with the caller-supplied password.
    /// No invitation email is sent — the user has set their password in the enrollment form.
    ///
    /// Returns (UserId, isNew) on success, null on any failure.
    /// </summary>
    Task<SelfRegisterResult?> RegisterUserDirectlyAsync(
        Guid              orgId,
        Guid              tenantId,
        string            email,
        string            password,
        string            firstName,
        string?           lastName,
        string?           phone,
        CancellationToken ct = default,
        string?           title = null);

    // ── CC2-ENROLL-FIRM: Law firm self-enrollment — org creation ─────────────

    /// <summary>
    /// Creates or resolves a minimal LAW_FIRM Organization in Identity for a law firm
    /// self-enrolling via the CareConnect portal from a referral status page.
    ///
    /// Idempotency: keyed on (tenantId, contactEmail). Repeated calls with the same inputs
    /// return the same org ID.
    ///
    /// Returns the Identity OrganizationId on success, null on any failure.
    /// </summary>
    Task<Guid?> EnsureLawFirmOrganizationAsync(
        Guid              tenantId,
        string            firmName,
        string            contactEmail,
        CancellationToken ct = default);

    // ── CC-PORTAL-CHECK: Referrer portal access lookup ────────────────────────

    /// <summary>
    /// Returns the tenant-scoped CareConnect portal-access status for the supplied email.
    ///
    /// Used by the public referral success screen to distinguish:
    ///   - already active in this tenant
    ///   - existing account on another tenant that must be linked with password verification
    ///   - brand-new user with no account yet
    ///
    /// Always returns <see cref="ReferrerPortalAccessStatuses.NoAccount"/> on any
    /// infrastructure failure — never throws.
    /// </summary>
    Task<string> GetReferrerPortalAccessStatusAsync(
        Guid              tenantId,
        string            email,
        CancellationToken ct = default);

    // ── CC-OWNER-CHECK: Tenant owner referral block ────────────────────────

    /// <summary>
    /// Returns true if the supplied email belongs to the tenant owner of the given tenant.
    ///
    /// Used by the public referral endpoint to block tenant owners (network operators)
    /// from submitting referrals — they are the network operator, not a referrer.
    ///
    /// Always returns false on any infrastructure failure — never throws.
    /// </summary>
    Task<bool> CheckTenantOwnerEmailAsync(
        Guid              tenantId,
        string            email,
        CancellationToken ct = default);

    /// <summary>
    /// Returns true if the supplied email belongs to the owner of ANY tenant.
    /// Used to block all tenant owners from submitting referrals or self-enrolling
    /// regardless of which tenant's network they are acting on.
    /// Always returns false on any infrastructure failure — never throws.
    /// </summary>
    Task<bool> CheckAnyTenantOwnerEmailAsync(
        string            email,
        CancellationToken ct = default);

    /// <summary>
    /// Resolves an Identity organization name by organization ID.
    ///
    /// Returns null when the organization is not found or Identity is unavailable.
    /// </summary>
    Task<string?> GetOrganizationNameAsync(
        Guid              orgId,
        CancellationToken ct = default);

    Task<List<LawFirmOrganizationOption>> ListLawFirmOrganizationsAsync(
        Guid              tenantId,
        CancellationToken ct = default);

    // ── LSV3-1083: Law-firm-scoped user management ──────────────────────────
    //
    // Unlike the best-effort methods above, these back a primary CRUD feature
    // (a law-firm admin managing their own firm's users), so callers should
    // treat a non-Success outcome as an error to surface, not a silent no-op.

    Task<(LawFirmUserOperationOutcome Outcome, IReadOnlyList<LawFirmUserSummary>? Items, string? Error)> ListOrganizationUsersAsync(
        Guid              organizationId,
        CancellationToken ct = default);

    Task<(LawFirmUserOperationOutcome Outcome, LawFirmUserInviteResult? Result, string? Error)> InviteOrganizationUserAsync(
        Guid              organizationId,
        Guid              tenantId,
        string            email,
        string            firstName,
        string            lastName,
        string            roleCode,
        CancellationToken ct = default);

    Task<(LawFirmUserOperationOutcome Outcome, string? Error)> ResendOrganizationUserInviteAsync(
        Guid              organizationId,
        Guid              userId,
        CancellationToken ct = default);

    Task<(LawFirmUserOperationOutcome Outcome, string? Error)> ActivateOrganizationUserAsync(
        Guid              organizationId,
        Guid              userId,
        CancellationToken ct = default);

    Task<(LawFirmUserOperationOutcome Outcome, string? Error)> DeactivateOrganizationUserAsync(
        Guid              organizationId,
        Guid              userId,
        CancellationToken ct = default);

    Task<(LawFirmUserOperationOutcome Outcome, Guid? AssignmentId, string? Error)> AssignOrganizationUserRoleAsync(
        Guid              organizationId,
        Guid              tenantId,
        Guid              userId,
        string            roleCode,
        CancellationToken ct = default);

    Task<(LawFirmUserOperationOutcome Outcome, string? Error)> RevokeOrganizationUserRoleAsync(
        Guid              organizationId,
        Guid              userId,
        Guid              assignmentId,
        CancellationToken ct = default);
}

/// <summary>LSV3-1083: outcome of a law-firm-scoped user-management call to Identity.</summary>
public enum LawFirmUserOperationOutcome
{
    Success,
    NotFound,
    Conflict,
    BadRequest,
    ServiceUnavailable,
}

public sealed class LawFirmUserRoleAssignmentInfo
{
    public Guid   AssignmentId { get; init; }
    public string RoleCode     { get; init; } = string.Empty;
}

public sealed class LawFirmUserSummary
{
    public Guid   UserId    { get; init; }
    public string Email     { get; init; } = string.Empty;
    public string FirstName { get; init; } = string.Empty;
    public string LastName  { get; init; } = string.Empty;
    public bool   IsActive  { get; init; }
    public string Status    { get; init; } = string.Empty;
    public List<LawFirmUserRoleAssignmentInfo> Roles { get; init; } = [];
}

public sealed class LawFirmUserInviteResult
{
    public Guid    UserId         { get; init; }
    public Guid?   InvitationId   { get; init; }
    public string  Email          { get; init; } = string.Empty;
    public bool    IsNew          { get; init; }
}

// ── Result types ───────────────────────────────────────────────────────────────

public sealed class SelfRegisterResult
{
    public Guid UserId         { get; init; }
    public bool IsNew          { get; init; }
    /// <summary>True when Identity rejected enrollment because the email belongs to the tenant owner.</summary>
    public bool IsOwnerBlocked { get; init; }
}

public sealed class ProvisionProviderUserResult
{
    public Guid  UserId         { get; init; }
    public Guid? InvitationId   { get; init; }
    public bool  IsNew          { get; init; }
    public bool  InvitationSent { get; init; }
    /// <summary>True when Identity rejected enrollment because the email belongs to the tenant owner.</summary>
    public bool  IsOwnerBlocked { get; init; }
}

public static class ReferrerPortalAccessStatuses
{
    public const string ActiveInTenant          = "active_in_tenant";
    public const string ExistingUserOtherTenant = "existing_user_other_tenant";
    public const string NoAccount               = "no_account";
}

public sealed class LawFirmOrganizationOption
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
}
