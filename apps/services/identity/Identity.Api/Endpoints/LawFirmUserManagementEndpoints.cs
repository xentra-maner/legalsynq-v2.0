using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildingBlocks.Authorization;
using ProductCodes = BuildingBlocks.Authorization.ProductCodes;
using Identity.Api.Helpers;
using Identity.Application.Interfaces;
using Identity.Domain;
using Identity.Infrastructure.Data;
using Identity.Infrastructure.Services;
using LegalSynq.AuditClient;
using LegalSynq.AuditClient.DTOs;
using LegalSynq.AuditClient.Enums;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Identity.Api.Endpoints;

/// <summary>
/// LSV3-1083 — Internal law-firm-scoped user management API.
///
/// These endpoints let CareConnect (acting on behalf of a caller already verified
/// to hold CARECONNECT_REFERRER_ADMIN for the given organization) list, invite,
/// activate/deactivate, and assign/revoke CareConnect product roles for the
/// users belonging to one law-firm Organization.
///
/// Internal-only (no public JWT auth) and secured with the provisioning token,
/// same as <see cref="UserMembershipEndpoints"/>.
///
/// Every route re-derives org membership from <c>UserOrganizationMemberships</c>
/// itself — the caller's own org-ownership check (performed by CareConnect before
/// calling in) is treated as advisory only, not authoritative, so a compromised or
/// buggy caller can never manage a user outside the target organization.
/// </summary>
public static class LawFirmUserManagementEndpoints
{
    private static readonly IReadOnlyList<string> AssignableRoleCodes =
    [
        ProductRoleCodes.CareConnectReferrer,
        ProductRoleCodes.CareConnectReferrerAdmin,
    ];

    public static void MapLawFirmUserManagementEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/internal/organizations/{organizationId:guid}/users");

        // ── GET /api/internal/organizations/{organizationId}/users ───────────
        group.MapGet("/", async (
            HttpContext       httpContext,
            Guid              organizationId,
            IdentityDbContext db,
            IConfiguration    configuration,
            ILoggerFactory    loggerFactory,
            CancellationToken ct) =>
        {
            var log = loggerFactory.CreateLogger("Identity.Api.LawFirmUsers.List");
            if (!ValidateProvisioningToken(httpContext, configuration, log, "law-firm-users-list"))
                return Results.Unauthorized();

            var members = await db.UserOrganizationMemberships
                .AsNoTracking()
                .Where(m => m.OrganizationId == organizationId && m.IsActive)
                .Join(db.Users.AsNoTracking(), m => m.UserId, u => u.Id, (m, u) => u)
                .Select(u => new
                {
                    u.Id,
                    u.Email,
                    u.FirstName,
                    u.LastName,
                    u.IsActive,
                })
                .ToListAsync(ct);

            var userIds = members.Select(m => m.Id).ToList();

            var roleCodesByUser = await db.UserRoleAssignments
                .AsNoTracking()
                .Where(a => userIds.Contains(a.UserId)
                         && a.ProductCode == ProductCodes.SynqCareConnect
                         && a.AssignmentStatus == AssignmentStatus.Active)
                .Select(a => new { a.UserId, a.Id, a.RoleCode })
                .ToListAsync(ct);

            var pendingInviteUserIds = await db.UserInvitations
                .AsNoTracking()
                .Where(i => userIds.Contains(i.UserId) && i.Status == UserInvitation.Statuses.Pending)
                .Select(i => i.UserId)
                .Distinct()
                .ToListAsync(ct);

            var items = members.Select(u => new OrgUserListItem(
                u.Id,
                u.Email,
                u.FirstName,
                u.LastName,
                u.IsActive,
                pendingInviteUserIds.Contains(u.Id) ? "Invited" : u.IsActive ? "Active" : "Inactive",
                roleCodesByUser
                    .Where(r => r.UserId == u.Id)
                    .Select(r => new OrgUserRoleAssignment(r.Id, r.RoleCode))
                    .ToList()));

            return Results.Ok(new { items });
        });

        // ── POST /api/internal/organizations/{organizationId}/users/invite ───
        group.MapPost("/invite", async (
            HttpContext                           httpContext,
            Guid                                   organizationId,
            InviteOrgUserRequest                   body,
            IdentityDbContext                      db,
            IPasswordHasher                        passwordHasher,
            IAuditEventClient                       auditClient,
            IOptions<NotificationsServiceOptions>  notifOptions,
            INotificationsEmailClient              emailClient,
            IWebHostEnvironment                    env,
            IConfiguration                          configuration,
            ILoggerFactory                          loggerFactory,
            CancellationToken                       ct) =>
        {
            var log = loggerFactory.CreateLogger("Identity.Api.LawFirmUsers.Invite");
            if (!ValidateProvisioningToken(httpContext, configuration, log, "law-firm-users-invite"))
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(body.Email))
                return Results.BadRequest(new { error = "email is required." });
            if (string.IsNullOrWhiteSpace(body.FirstName))
                return Results.BadRequest(new { error = "firstName is required." });
            if (string.IsNullOrWhiteSpace(body.LastName))
                return Results.BadRequest(new { error = "lastName is required." });

            var roleCode = string.IsNullOrWhiteSpace(body.RoleCode)
                ? ProductRoleCodes.CareConnectReferrer
                : body.RoleCode.Trim().ToUpperInvariant();
            if (!AssignableRoleCodes.Contains(roleCode))
                return Results.BadRequest(new
                {
                    error = $"roleCode must be one of: {string.Join(", ", AssignableRoleCodes)}.",
                });

            if (body.TenantId == Guid.Empty)
                return Results.BadRequest(new { error = "tenantId is required." });

            var org = await db.Organizations.FirstOrDefaultAsync(o => o.Id == organizationId && o.IsActive, ct);
            if (org is null)
                return Results.NotFound(new { error = $"Organization '{organizationId}' not found." });
            // The Organization's own TenantId column is unreliable for legacy/test data (nullable,
            // not always backfilled) — trust the tenantId the caller (CareConnect) already resolved
            // from its own JWT tenant_id claim, only guarding against an outright mismatch when the
            // column IS populated.
            if (org.TenantId is { } orgTenantId && orgTenantId != body.TenantId)
                return Results.BadRequest(new { error = "tenantId does not match the organization's tenant." });

            var tenantId = body.TenantId;
            var tenant = await db.Tenants.FindAsync([tenantId], ct);
            if (tenant is null)
                return Results.NotFound(new { error = $"Tenant '{tenantId}' not found." });

            var emailLower = body.Email.Trim().ToLowerInvariant();
            var existingUser = await db.Users.FirstOrDefaultAsync(u => u.Email == emailLower, ct);

            // A law-firm invite always creates a brand-new account — unlike the general
            // tenant InviteUser flow, it never reuses an existing Identity user, even one
            // that belongs to a different firm/tenant entirely. Reject up front so the
            // caller gets a clean validation error instead of the invite silently attaching
            // an unrelated account to this org, or (for an email that already has a
            // membership row here) crashing on the UserOrganizationMemberships unique
            // (UserId, OrganizationId) index.
            if (existingUser is not null)
                return Results.Conflict(new { error = $"A user with email '{emailLower}' already exists." });

            var tempPasswordHash = passwordHasher.Hash(Guid.CreateVersion7().ToString());
            var user = User.Create(tenantId, emailLower, tempPasswordHash, body.FirstName.Trim(), body.LastName.Trim());
            user.Deactivate();
            db.Users.Add(user);

            db.UserTenants.Add(UserTenant.Create(user.Id, tenantId));
            db.UserOrganizationMemberships.Add(UserOrganizationMembership.Create(user.Id, organizationId, MemberRole.Member));
            db.UserProductAccessRecords.Add(UserProductAccess.Create(tenantId, user.Id, ProductCodes.SynqCareConnect, organizationId));
            db.UserRoleAssignments.Add(UserRoleAssignment.Create(
                tenantId, user.Id, roleCode, ProductCodes.SynqCareConnect, organizationId));

            var rawToken = Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N");
            var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
            var invitation = UserInvitation.Create(user.Id, tenantId, tokenHash, UserInvitation.PortalOrigins.TenantPortal);
            db.UserInvitations.Add(invitation);

            await db.SaveChangesAsync(ct);

            _ = auditClient.IngestAsync(new IngestAuditEventRequest
            {
                EventType     = "careconnect.lawfirmuser.invited",
                EventCategory = EventCategory.Administrative,
                SourceSystem  = "identity-service",
                SourceService = "law-firm-user-management",
                Visibility    = VisibilityScope.Tenant,
                Severity      = SeverityLevel.Info,
                OccurredAtUtc = DateTimeOffset.UtcNow,
                Scope = new AuditEventScopeDto { ScopeType = ScopeType.Tenant, TenantId = tenantId.ToString() },
                Actor = new AuditEventActorDto { Type = ActorType.System, Name = "law-firm-user-management" },
                Entity = new AuditEventEntityDto { Type = "User", Id = user.Id.ToString() },
                Action      = "LawFirmUserInvited",
                Description = $"User '{emailLower}' invited to law firm organization {organizationId}.",
                IdempotencyKey = IdempotencyKey.For("identity-service", "careconnect.lawfirmuser.invited", invitation.Id.ToString()),
                Tags = ["careconnect", "law-firm-user-management", "invite"],
            });

            var displayName = $"{user.FirstName} {user.LastName}".Trim();
            var activationLink = TenantPortalUrlHelper.Build(tenant, "accept-invite", rawToken, notifOptions.Value);
            if (activationLink is null)
            {
                log.LogError(
                    "Law-firm user invite for {UserId} ({Email}, org={OrganizationId}): portal URL is not configured.",
                    user.Id, emailLower, organizationId);
                return Results.Ok(new { userId = user.Id, invitationId = invitation.Id, email = emailLower, isNew = true, activationLink = (string?)null });
            }

            var (emailConfigured, emailSuccess, emailError) = await emailClient.SendCareConnectLawFirmInviteEmailAsync(
                emailLower, displayName, activationLink, tenantId, ct);
            if (!emailConfigured || !emailSuccess)
                log.LogWarning(
                    "Law-firm user invite email not sent for {UserId} ({Email}): configured={Configured} error={Error}",
                    user.Id, emailLower, emailConfigured, emailError);

            if (!env.IsProduction())
            {
                return Results.Ok(new
                {
                    userId = user.Id,
                    invitationId = invitation.Id,
                    email = emailLower,
                    isNew = true,
                    inviteToken = rawToken,
                    activationLink,
                });
            }

            return Results.Ok(new { userId = user.Id, invitationId = invitation.Id, email = emailLower, isNew = true });
        });

        // ── POST /.../{userId}/activate  &  POST /.../{userId}/deactivate ────
        group.MapPost("/{userId:guid}/activate", (
            HttpContext httpContext, Guid organizationId, Guid userId,
            IdentityDbContext db, IAuditEventClient auditClient, IDeviceSessionService deviceSessionService,
            IConfiguration configuration, ILoggerFactory loggerFactory, CancellationToken ct) =>
            SetUserActiveStateAsync(httpContext, organizationId, userId, activate: true, db, auditClient, deviceSessionService, configuration, loggerFactory, ct));

        group.MapPost("/{userId:guid}/deactivate", (
            HttpContext httpContext, Guid organizationId, Guid userId,
            IdentityDbContext db, IAuditEventClient auditClient, IDeviceSessionService deviceSessionService,
            IConfiguration configuration, ILoggerFactory loggerFactory, CancellationToken ct) =>
            SetUserActiveStateAsync(httpContext, organizationId, userId, activate: false, db, auditClient, deviceSessionService, configuration, loggerFactory, ct));

        // ── POST /.../{userId}/resend-invite ─────────────────────────────────
        group.MapPost("/{userId:guid}/resend-invite", async (
            HttpContext                           httpContext,
            Guid                                  organizationId,
            Guid                                  userId,
            IdentityDbContext                     db,
            IAuditEventClient                     auditClient,
            IOptions<NotificationsServiceOptions> notifOptions,
            INotificationsEmailClient             emailClient,
            IWebHostEnvironment                   env,
            IConfiguration                        configuration,
            ILoggerFactory                        loggerFactory,
            CancellationToken                     ct) =>
        {
            var log = loggerFactory.CreateLogger("Identity.Api.LawFirmUsers.ResendInvite");
            if (!ValidateProvisioningToken(httpContext, configuration, log, "law-firm-users-resend-invite"))
                return Results.Unauthorized();

            var membership = await db.UserOrganizationMemberships
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.UserId == userId && m.OrganizationId == organizationId && m.IsActive, ct);
            if (membership is null)
                return Results.NotFound(new { error = $"User '{userId}' is not an active member of organization '{organizationId}'." });

            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (user is null)
                return Results.NotFound(new { error = $"User '{userId}' not found." });

            var pending = await db.UserInvitations
                .Where(i => i.UserId == userId && i.Status == UserInvitation.Statuses.Pending)
                .ToListAsync(ct);
            if (pending.Count == 0)
                return Results.Conflict(new { error = "This user does not have a pending invitation." });

            foreach (var invitation in pending)
                invitation.Revoke();

            var tenantId = pending[0].TenantId;
            var rawToken = Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N");
            var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
            var newInvite = UserInvitation.Create(userId, tenantId, tokenHash, UserInvitation.PortalOrigins.TenantPortal);
            db.UserInvitations.Add(newInvite);

            await db.SaveChangesAsync(ct);

            _ = auditClient.IngestAsync(new IngestAuditEventRequest
            {
                EventType     = "careconnect.lawfirmuser.invite_resent",
                EventCategory = EventCategory.Administrative,
                SourceSystem  = "identity-service",
                SourceService = "law-firm-user-management",
                Visibility    = VisibilityScope.Tenant,
                Severity      = SeverityLevel.Info,
                OccurredAtUtc = DateTimeOffset.UtcNow,
                Scope = new AuditEventScopeDto { ScopeType = ScopeType.Tenant, TenantId = tenantId.ToString() },
                Actor = new AuditEventActorDto { Type = ActorType.System, Name = "law-firm-user-management" },
                Entity = new AuditEventEntityDto { Type = "User", Id = user.Id.ToString() },
                Action      = "LawFirmUserInviteResent",
                Description = $"Invitation resent for user '{user.Email}' in law firm organization {organizationId}.",
                IdempotencyKey = IdempotencyKey.For("identity-service", "careconnect.lawfirmuser.invite_resent", newInvite.Id.ToString()),
                Tags = ["careconnect", "law-firm-user-management", "invite"],
            });

            var tenant = await db.Tenants.FindAsync([tenantId], ct);
            var activationLink = TenantPortalUrlHelper.Build(tenant, "accept-invite", rawToken, notifOptions.Value);
            if (activationLink is null)
            {
                log.LogError(
                    "Law-firm user resend invite for {UserId} ({Email}, org={OrganizationId}): portal URL is not configured.",
                    user.Id, user.Email, organizationId);
                return Results.Problem(
                    "Invitation refreshed but email could not be sent: portal URL is not configured.",
                    statusCode: 503);
            }

            var displayName = $"{user.FirstName} {user.LastName}".Trim();
            var (emailConfigured, emailSuccess, emailError) = await emailClient.SendCareConnectLawFirmInviteEmailAsync(
                user.Email, displayName, activationLink, tenantId, ct);

            if (!emailConfigured)
                return Results.Problem(
                    "Invitation refreshed but email could not be sent: the Notifications service is not configured.",
                    statusCode: 503);

            if (!emailSuccess)
                return Results.Problem(
                    $"Invitation refreshed but email could not be sent: {emailError}",
                    statusCode: 502);

            if (!env.IsProduction())
                return Results.Ok(new { invitationId = newInvite.Id, inviteToken = rawToken, activationLink });

            return Results.Ok(new { invitationId = newInvite.Id });
        });

        // ── POST /.../{userId}/product-roles ──────────────────────────────────
        group.MapPost("/{userId:guid}/product-roles", async (
            HttpContext                  httpContext,
            Guid                         organizationId,
            Guid                         userId,
            AssignOrgProductRoleRequest  body,
            IdentityDbContext            db,
            IAuditEventClient            auditClient,
            IConfiguration               configuration,
            ILoggerFactory               loggerFactory,
            CancellationToken            ct) =>
        {
            var log = loggerFactory.CreateLogger("Identity.Api.LawFirmUsers.AssignRole");
            if (!ValidateProvisioningToken(httpContext, configuration, log, "law-firm-users-assign-role"))
                return Results.Unauthorized();

            var roleCode = (body.RoleCode ?? string.Empty).Trim().ToUpperInvariant();
            if (!AssignableRoleCodes.Contains(roleCode))
                return Results.BadRequest(new
                {
                    error = $"roleCode must be one of: {string.Join(", ", AssignableRoleCodes)}.",
                });

            if (body.TenantId == Guid.Empty)
                return Results.BadRequest(new { error = "tenantId is required." });

            var membership = await db.UserOrganizationMemberships
                .FirstOrDefaultAsync(m => m.UserId == userId && m.OrganizationId == organizationId && m.IsActive, ct);
            if (membership is null)
                return Results.NotFound(new { error = $"User '{userId}' is not an active member of organization '{organizationId}'." });

            var org = await db.Organizations.AsNoTracking().FirstOrDefaultAsync(o => o.Id == organizationId, ct);
            if (org is null)
                return Results.NotFound(new { error = $"Organization '{organizationId}' not found." });
            if (org.TenantId is { } orgTenantId && orgTenantId != body.TenantId)
                return Results.BadRequest(new { error = "tenantId does not match the organization's tenant." });

            var tenantId = body.TenantId;

            var alreadyAssigned = await db.UserRoleAssignments.AnyAsync(
                a => a.UserId == userId && a.TenantId == tenantId && a.ProductCode == ProductCodes.SynqCareConnect
                  && a.RoleCode == roleCode && a.AssignmentStatus == AssignmentStatus.Active, ct);
            if (alreadyAssigned)
                return Results.Conflict(new { error = $"Role '{roleCode}' is already actively assigned to user '{userId}'." });

            var hasProductAccess = await db.UserProductAccessRecords.AnyAsync(
                a => a.UserId == userId && a.TenantId == tenantId && a.ProductCode == ProductCodes.SynqCareConnect
                  && a.AccessStatus == AccessStatus.Granted, ct);
            if (!hasProductAccess)
                db.UserProductAccessRecords.Add(UserProductAccess.Create(tenantId, userId, ProductCodes.SynqCareConnect, organizationId));

            var assignment = UserRoleAssignment.Create(tenantId, userId, roleCode, ProductCodes.SynqCareConnect, organizationId);
            db.UserRoleAssignments.Add(assignment);
            await db.SaveChangesAsync(ct);

            _ = auditClient.IngestAsync(new IngestAuditEventRequest
            {
                EventType     = "careconnect.lawfirmuser.role_assigned",
                EventCategory = EventCategory.Administrative,
                SourceSystem  = "identity-service",
                SourceService = "law-firm-user-management",
                Visibility    = VisibilityScope.Tenant,
                Severity      = SeverityLevel.Info,
                OccurredAtUtc = DateTimeOffset.UtcNow,
                Scope = new AuditEventScopeDto { ScopeType = ScopeType.Tenant, TenantId = tenantId.ToString() },
                Actor = new AuditEventActorDto { Type = ActorType.System, Name = "law-firm-user-management" },
                Entity = new AuditEventEntityDto { Type = "User", Id = userId.ToString() },
                Action      = "LawFirmUserRoleAssigned",
                Description = $"Role '{roleCode}' assigned to user '{userId}' in organization {organizationId}.",
                IdempotencyKey = IdempotencyKey.For("identity-service", "careconnect.lawfirmuser.role_assigned", assignment.Id.ToString()),
                Tags = ["careconnect", "law-firm-user-management", "role-assignment"],
            });

            return Results.Created(
                $"/api/internal/organizations/{organizationId}/users/{userId}/product-roles/{assignment.Id}",
                new { assignmentId = assignment.Id, userId, roleCode });
        });

        // ── DELETE /.../{userId}/product-roles/{assignmentId} ─────────────────
        group.MapDelete("/{userId:guid}/product-roles/{assignmentId:guid}", async (
            HttpContext       httpContext,
            Guid              organizationId,
            Guid              userId,
            Guid              assignmentId,
            IdentityDbContext db,
            IAuditEventClient auditClient,
            IConfiguration    configuration,
            ILoggerFactory    loggerFactory,
            CancellationToken ct) =>
        {
            var log = loggerFactory.CreateLogger("Identity.Api.LawFirmUsers.RevokeRole");
            if (!ValidateProvisioningToken(httpContext, configuration, log, "law-firm-users-revoke-role"))
                return Results.Unauthorized();

            var membership = await db.UserOrganizationMemberships
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.UserId == userId && m.OrganizationId == organizationId && m.IsActive, ct);
            if (membership is null)
                return Results.NotFound(new { error = $"User '{userId}' is not an active member of organization '{organizationId}'." });

            var assignment = await db.UserRoleAssignments.FirstOrDefaultAsync(
                a => a.Id == assignmentId
                  && a.UserId == userId
                  && a.ProductCode == ProductCodes.SynqCareConnect
                  && a.OrganizationId == organizationId
                  && a.AssignmentStatus == AssignmentStatus.Active, ct);
            if (assignment is null)
                return Results.NotFound(new { error = $"Active role assignment '{assignmentId}' not found for user '{userId}' in organization '{organizationId}'." });

            if (!AssignableRoleCodes.Contains(assignment.RoleCode))
                return Results.Forbid();

            assignment.Remove();
            await db.SaveChangesAsync(ct);

            _ = auditClient.IngestAsync(new IngestAuditEventRequest
            {
                EventType     = "careconnect.lawfirmuser.role_revoked",
                EventCategory = EventCategory.Administrative,
                SourceSystem  = "identity-service",
                SourceService = "law-firm-user-management",
                Visibility    = VisibilityScope.Tenant,
                Severity      = SeverityLevel.Info,
                OccurredAtUtc = DateTimeOffset.UtcNow,
                Scope = new AuditEventScopeDto { ScopeType = ScopeType.Tenant, TenantId = assignment.TenantId.ToString() },
                Actor = new AuditEventActorDto { Type = ActorType.System, Name = "law-firm-user-management" },
                Entity = new AuditEventEntityDto { Type = "User", Id = userId.ToString() },
                Action      = "LawFirmUserRoleRevoked",
                Description = $"Role '{assignment.RoleCode}' revoked from user '{userId}' in organization {organizationId}.",
                IdempotencyKey = IdempotencyKey.For("identity-service", "careconnect.lawfirmuser.role_revoked", assignment.Id.ToString()),
                Tags = ["careconnect", "law-firm-user-management", "role-assignment"],
            });

            return Results.NoContent();
        });
    }

    private static async Task<IResult> SetUserActiveStateAsync(
        HttpContext httpContext,
        Guid organizationId,
        Guid userId,
        bool activate,
        IdentityDbContext db,
        IAuditEventClient auditClient,
        IDeviceSessionService deviceSessionService,
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var operation = activate ? "law-firm-users-activate" : "law-firm-users-deactivate";
        var log = loggerFactory.CreateLogger($"Identity.Api.LawFirmUsers.{(activate ? "Activate" : "Deactivate")}");
        if (!ValidateProvisioningToken(httpContext, configuration, log, operation))
            return Results.Unauthorized();

        var membership = await db.UserOrganizationMemberships
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.UserId == userId && m.OrganizationId == organizationId && m.IsActive, ct);
        if (membership is null)
            return Results.NotFound(new { error = $"User '{userId}' is not an active member of organization '{organizationId}'." });

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return Results.NotFound(new { error = $"User '{userId}' not found." });

        var changed = activate ? user.Activate() : user.Deactivate();
        if (!changed)
            return Results.NoContent();

        await db.SaveChangesAsync(ct);

        if (!activate)
            await deviceSessionService.RevokeAllForUserAsync(user.Id, "LawFirmAdminDeactivated", ct);

        _ = auditClient.IngestAsync(new IngestAuditEventRequest
        {
            EventType     = activate ? "careconnect.lawfirmuser.activated" : "careconnect.lawfirmuser.deactivated",
            EventCategory = EventCategory.Administrative,
            SourceSystem  = "identity-service",
            SourceService = "law-firm-user-management",
            Visibility    = VisibilityScope.Tenant,
            Severity      = activate ? SeverityLevel.Info : SeverityLevel.Warn,
            OccurredAtUtc = DateTimeOffset.UtcNow,
            Scope = new AuditEventScopeDto { ScopeType = ScopeType.Organization, OrganizationId = organizationId.ToString() },
            Actor = new AuditEventActorDto { Type = ActorType.System, Name = "law-firm-user-management" },
            Entity = new AuditEventEntityDto { Type = "User", Id = user.Id.ToString() },
            Action      = activate ? "LawFirmUserActivated" : "LawFirmUserDeactivated",
            Description = $"User '{user.Email}' {(activate ? "activated" : "deactivated")} in organization {organizationId}.",
            IdempotencyKey = IdempotencyKey.For(
                "identity-service",
                activate ? "careconnect.lawfirmuser.activated" : "careconnect.lawfirmuser.deactivated",
                $"{user.Id}:{DateTime.UtcNow.Ticks}"),
            Tags = ["careconnect", "law-firm-user-management", "lifecycle"],
        });

        return Results.NoContent();
    }

    private static bool ValidateProvisioningToken(
        HttpContext    httpContext,
        IConfiguration configuration,
        ILogger        log,
        string         operation)
    {
        var secret        = configuration["TenantService:ProvisioningSecret"];
        var incomingToken = httpContext.Request.Headers["X-Provisioning-Token"].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(secret))
            return true; // dev mode — skip check

        if (!string.Equals(incomingToken, secret, StringComparison.Ordinal))
        {
            log.LogWarning(
                "[LawFirmUserManagement] {Operation}: rejected — invalid X-Provisioning-Token from {RemoteIp}",
                operation, httpContext.Connection.RemoteIpAddress);
            return false;
        }

        return true;
    }

    // ── DTOs ───────────────────────────────────────────────────────────────

    private record OrgUserRoleAssignment(Guid AssignmentId, string RoleCode);

    private record OrgUserListItem(
        Guid   UserId,
        string Email,
        string FirstName,
        string LastName,
        bool   IsActive,
        string Status,
        IReadOnlyList<OrgUserRoleAssignment> Roles);

    private record InviteOrgUserRequest(
        Guid    TenantId,
        string  Email,
        string  FirstName,
        string  LastName,
        string? RoleCode = null);

    private record AssignOrgProductRoleRequest(Guid TenantId, string RoleCode);
}
