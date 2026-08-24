using BuildingBlocks.Authorization;
using BuildingBlocks.Authorization.Filters;
using BuildingBlocks.Context;
using CareConnect.Application.Interfaces;
using LegalSynq.AuditClient;
using LegalSynq.AuditClient.DTOs;
using LegalSynq.AuditClient.Enums;
using AuditVisibility = LegalSynq.AuditClient.Enums.VisibilityScope;

namespace CareConnect.Api.Endpoints;

// LSV3-1083 — Law Firm Company Super Admin/Manager.
// Access: CARECONNECT_REFERRER_ADMIN product role, or PlatformAdmin / TenantAdmin bypass.
// A REFERRER_ADMIN always operates on their own organization — there is deliberately no
// org-id route parameter, so "manage someone else's firm" is not an expressible request.
public static class LawFirmUserEndpoints
{
    public static void MapLawFirmUserEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/law-firm-users")
            .RequireAuthorization(Policies.AuthenticatedUser);

        group.MapGet("/", async (
            ILawFirmUserService service,
            ICurrentRequestContext ctx,
            CancellationToken ct) =>
        {
            var orgId = RequireOrgId(ctx);
            var users = await service.ListUsersAsync(orgId, ctx.OrgId, IsSystemAdmin(ctx), ct);
            return Results.Ok(users);
        })
        .RequireProductRole(ProductCodes.SynqCareConnect, ProductRoleCodes.CareConnectReferrerAdmin);

        group.MapPost("/invite", async (
            InviteLawFirmUserRequest request,
            ILawFirmUserService service,
            ICurrentRequestContext ctx,
            IAuditEventClient auditClient,
            HttpContext http,
            CancellationToken ct) =>
        {
            var orgId = RequireOrgId(ctx);
            var tenantId = RequireTenantId(ctx);
            var result = await service.InviteUserAsync(
                orgId, tenantId, request.Email, request.FirstName, request.LastName, request.RoleCode,
                ctx.OrgId, IsSystemAdmin(ctx), ct);

            _ = EmitLawFirmUserAuditAsync(auditClient,
                eventType: "careconnect.lawfirmuser.invited", action: "LawFirmUserInvited",
                description: $"User '{result.Email}' invited to organization {orgId}.",
                tenantId: ctx.TenantId, actorUserId: ctx.UserId, organizationId: orgId,
                correlationId: CorrelationId(http));

            return Results.Ok(result);
        })
        .RequireProductRole(ProductCodes.SynqCareConnect, ProductRoleCodes.CareConnectReferrerAdmin);

        group.MapPost("/{userId:guid}/activate", async (
            Guid userId,
            ILawFirmUserService service,
            ICurrentRequestContext ctx,
            IAuditEventClient auditClient,
            HttpContext http,
            CancellationToken ct) =>
        {
            var orgId = RequireOrgId(ctx);
            await service.ActivateUserAsync(orgId, userId, ctx.OrgId, IsSystemAdmin(ctx), ct);

            _ = EmitLawFirmUserAuditAsync(auditClient,
                eventType: "careconnect.lawfirmuser.activated", action: "LawFirmUserActivated",
                description: $"User '{userId}' activated in organization {orgId}.",
                tenantId: ctx.TenantId, actorUserId: ctx.UserId, organizationId: orgId,
                correlationId: CorrelationId(http));

            return Results.NoContent();
        })
        .RequireProductRole(ProductCodes.SynqCareConnect, ProductRoleCodes.CareConnectReferrerAdmin);

        group.MapPost("/{userId:guid}/deactivate", async (
            Guid userId,
            ILawFirmUserService service,
            ICurrentRequestContext ctx,
            IAuditEventClient auditClient,
            HttpContext http,
            CancellationToken ct) =>
        {
            var orgId = RequireOrgId(ctx);
            await service.DeactivateUserAsync(orgId, userId, ctx.OrgId, IsSystemAdmin(ctx), ct);

            _ = EmitLawFirmUserAuditAsync(auditClient,
                eventType: "careconnect.lawfirmuser.deactivated", action: "LawFirmUserDeactivated",
                description: $"User '{userId}' deactivated in organization {orgId}.",
                tenantId: ctx.TenantId, actorUserId: ctx.UserId, organizationId: orgId,
                correlationId: CorrelationId(http));

            return Results.NoContent();
        })
        .RequireProductRole(ProductCodes.SynqCareConnect, ProductRoleCodes.CareConnectReferrerAdmin);

        group.MapPost("/{userId:guid}/resend-invite", async (
            Guid userId,
            ILawFirmUserService service,
            ICurrentRequestContext ctx,
            IAuditEventClient auditClient,
            HttpContext http,
            CancellationToken ct) =>
        {
            var orgId = RequireOrgId(ctx);
            await service.ResendInviteAsync(orgId, userId, ctx.OrgId, IsSystemAdmin(ctx), ct);

            _ = EmitLawFirmUserAuditAsync(auditClient,
                eventType: "careconnect.lawfirmuser.invite_resent", action: "LawFirmUserInviteResent",
                description: $"Invitation resent for user '{userId}' in organization {orgId}.",
                tenantId: ctx.TenantId, actorUserId: ctx.UserId, organizationId: orgId,
                correlationId: CorrelationId(http));

            return Results.NoContent();
        })
        .RequireProductRole(ProductCodes.SynqCareConnect, ProductRoleCodes.CareConnectReferrerAdmin);

        group.MapPost("/{userId:guid}/roles", async (
            Guid userId,
            AssignLawFirmUserRoleRequest request,
            ILawFirmUserService service,
            ICurrentRequestContext ctx,
            IAuditEventClient auditClient,
            HttpContext http,
            CancellationToken ct) =>
        {
            var orgId = RequireOrgId(ctx);
            var tenantId = RequireTenantId(ctx);
            var assignmentId = await service.AssignRoleAsync(orgId, tenantId, userId, request.RoleCode, ctx.OrgId, IsSystemAdmin(ctx), ct);

            _ = EmitLawFirmUserAuditAsync(auditClient,
                eventType: "careconnect.lawfirmuser.role_assigned", action: "LawFirmUserRoleAssigned",
                description: $"Role '{request.RoleCode}' assigned to user '{userId}' in organization {orgId}.",
                tenantId: ctx.TenantId, actorUserId: ctx.UserId, organizationId: orgId,
                correlationId: CorrelationId(http));

            return Results.Created($"/api/law-firm-users/{userId}/roles/{assignmentId}", new { assignmentId });
        })
        .RequireProductRole(ProductCodes.SynqCareConnect, ProductRoleCodes.CareConnectReferrerAdmin);

        group.MapDelete("/{userId:guid}/roles/{assignmentId:guid}", async (
            Guid userId,
            Guid assignmentId,
            ILawFirmUserService service,
            ICurrentRequestContext ctx,
            IAuditEventClient auditClient,
            HttpContext http,
            CancellationToken ct) =>
        {
            var orgId = RequireOrgId(ctx);
            await service.RevokeRoleAsync(orgId, userId, assignmentId, ctx.OrgId, IsSystemAdmin(ctx), ct);

            _ = EmitLawFirmUserAuditAsync(auditClient,
                eventType: "careconnect.lawfirmuser.role_revoked", action: "LawFirmUserRoleRevoked",
                description: $"Role assignment '{assignmentId}' revoked from user '{userId}' in organization {orgId}.",
                tenantId: ctx.TenantId, actorUserId: ctx.UserId, organizationId: orgId,
                correlationId: CorrelationId(http));

            return Results.NoContent();
        })
        .RequireProductRole(ProductCodes.SynqCareConnect, ProductRoleCodes.CareConnectReferrerAdmin);
    }

    private static Guid RequireOrgId(ICurrentRequestContext ctx) =>
        ctx.OrgId ?? throw new InvalidOperationException("Caller has no organization association.");

    private static Guid RequireTenantId(ICurrentRequestContext ctx) =>
        ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");

    private static string CorrelationId(HttpContext http) =>
        http.Items["CorrelationId"]?.ToString() ?? http.TraceIdentifier;

    private static bool IsSystemAdmin(ICurrentRequestContext ctx) =>
        ctx.IsPlatformAdmin || ctx.Roles.Contains(Roles.TenantAdmin, StringComparer.OrdinalIgnoreCase);

    private static Task EmitLawFirmUserAuditAsync(
        IAuditEventClient auditClient,
        string            eventType,
        string            action,
        string            description,
        Guid?             tenantId,
        Guid?             actorUserId,
        Guid              organizationId,
        string            correlationId)
    {
        try
        {
            return auditClient.IngestAsync(new IngestAuditEventRequest
            {
                EventType     = eventType,
                EventCategory = EventCategory.Business,
                SourceSystem  = "care-connect",
                SourceService = "law-firm-user-management",
                Visibility    = AuditVisibility.Tenant,
                Severity      = SeverityLevel.Info,
                OccurredAtUtc = DateTimeOffset.UtcNow,
                Scope = new AuditEventScopeDto
                {
                    ScopeType      = ScopeType.Organization,
                    TenantId       = tenantId?.ToString(),
                    OrganizationId = organizationId.ToString(),
                    UserId         = actorUserId?.ToString(),
                },
                Actor = new AuditEventActorDto
                {
                    Type = actorUserId.HasValue ? ActorType.User : ActorType.System,
                    Id   = actorUserId?.ToString() ?? "system",
                },
                Entity = new AuditEventEntityDto
                {
                    Type = "Organization",
                    Id   = organizationId.ToString(),
                },
                Action        = action,
                Description   = description,
                Outcome       = "success",
                CorrelationId = correlationId,
                IdempotencyKey = IdempotencyKey.ForWithTimestamp(DateTimeOffset.UtcNow, "care-connect", eventType, organizationId.ToString()),
                Tags = ["careconnect", "law-firm-user-management"],
            });
        }
        catch (Exception)
        {
            return Task.CompletedTask;
        }
    }
}

public sealed record InviteLawFirmUserRequest(string Email, string FirstName, string LastName, string? RoleCode = null);

public sealed record AssignLawFirmUserRoleRequest(string RoleCode);
