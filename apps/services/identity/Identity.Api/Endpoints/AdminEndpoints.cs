using System.Security.Claims;
using System.Text.Json;
using BuildingBlocks.Authorization.Filters;
using PermCodes = BuildingBlocks.Authorization.PermissionCodes;
using ProductRoleCodes = BuildingBlocks.Authorization.ProductRoleCodes;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace Identity.Api.Endpoints;

/// <summary>
/// Admin endpoints consumed by the LegalSynq Control Center and, for
/// tenant-scoped operations, by the Tenant Portal.
/// All routes are prefixed /api/admin/... and are accessed via the YARP
/// gateway under /identity/api/admin/...
///
/// Authorization is enforced at both the gateway layer (JWT validation)
/// AND at the endpoint level:
///   • Platform-wide operations (tenant management, settings, policy/permission
///     catalog mutations, etc.) require the <c>PlatformAdmin</c> role.
///   • Tenant-scoped user management operations require
///     <c>TENANT.users:manage</c> or <c>TENANT.users:view</c>, which the
///     RequirePermissionFilter grants to TenantAdmin and PlatformAdmin
///     callers automatically.
/// </summary>
public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder routes)
    {
        // ── Tenants ──────────────────────────────────────────────────────────
        // All tenant-management routes are platform-wide operations restricted
        // to PlatformAdmin.  A TenantAdmin must not be able to enumerate, create,
        // or mutate other tenants.
        routes.MapGet("/api/admin/tenants",                      ListTenants)
            .RequireAuthorization(pb => pb.RequireRole("PlatformAdmin"));
        // DEPRECATED [TENANT-B12] — Tenant creation is now owned by the Tenant service.
        // New canonical entry point: POST /tenant/api/v1/admin/tenants
        // This endpoint is kept for backward compatibility only. Do not add new callers.
        routes.MapPost("/api/admin/tenants",                     CreateTenant)
            .RequireAuthorization(pb => pb.RequireRole("PlatformAdmin"));
        routes.MapGet("/api/admin/tenants/check-code",           CheckTenantCode)        // CC2-INT-B09
            .RequireAuthorization(pb => pb.RequireRole("PlatformAdmin"));
        routes.MapPost("/api/admin/tenants/self-provision",      SelfProvisionTenant)    // CC2-INT-B09
            .RequireAuthorization(pb => pb.RequireRole("PlatformAdmin"));
        routes.MapGet("/api/admin/tenants/{id:guid}",            GetTenant)
            .RequireAuthorization(pb => pb.RequireRole("PlatformAdmin"));
        // DEPRECATED [TENANT-B12] — Entitlement toggle is now owned by the Tenant service.
        // New canonical entry point: POST /tenant/api/v1/admin/tenants/{id}/entitlements/{productCode}
        // This endpoint is kept for backward compatibility only. Do not add new callers.
        routes.MapPost("/api/admin/tenants/{id:guid}/entitlements/{productCode}", UpdateEntitlement)
            .RequireAuthorization(pb => pb.RequireRole("PlatformAdmin"));
        routes.MapPatch("/api/admin/tenants/{id:guid}/session-settings", UpdateTenantSessionSettings)
            .RequireAuthorization(pb => pb.RequireRole("PlatformAdmin"));
        routes.MapPatch("/api/admin/tenants/{id:guid}/logo",             SetTenantLogo)
            .RequireAuthorization(pb => pb.RequireRole("PlatformAdmin"));
        routes.MapDelete("/api/admin/tenants/{id:guid}/logo",            ClearTenantLogo)
            .RequireAuthorization(pb => pb.RequireRole("PlatformAdmin"));
        routes.MapPatch("/api/admin/tenants/{id:guid}/logo-white",       SetTenantLogoWhite)
            .RequireAuthorization(pb => pb.RequireRole("PlatformAdmin"));
        routes.MapDelete("/api/admin/tenants/{id:guid}/logo-white",      ClearTenantLogoWhite)
            .RequireAuthorization(pb => pb.RequireRole("PlatformAdmin"));
        routes.MapPost("/api/admin/tenants/{id:guid}/provisioning/retry", RetryProvisioning)
            .RequireAuthorization(pb => pb.RequireRole("PlatformAdmin"));
        routes.MapPost("/api/admin/tenants/{id:guid}/verification/retry", RetryVerification)
            .RequireAuthorization(pb => pb.RequireRole("PlatformAdmin"));

        // ── PUM-B03: Tenant User Management ──────────────────────────────────
        // Cross-tenant user management is a platform-level operation.
        routes.MapGet   ("/api/admin/tenants/{tenantId:guid}/users",                                   ListTenantUsers)
            .RequireAuthorization(pb => pb.RequireRole("PlatformAdmin"));
        routes.MapPost  ("/api/admin/tenants/{tenantId:guid}/users",                                   AssignUserToTenant)
            .RequireAuthorization(pb => pb.RequireRole("PlatformAdmin"));
        routes.MapDelete("/api/admin/tenants/{tenantId:guid}/users/{userId:guid}",                     RemoveUserFromTenant)
            .RequireAuthorization(pb => pb.RequireRole("PlatformAdmin"));
        routes.MapPost  ("/api/admin/tenants/{tenantId:guid}/users/{userId:guid}/roles",               AssignTenantRole)
            .RequireAuthorization(pb => pb.RequireRole("PlatformAdmin"));
        routes.MapDelete("/api/admin/tenants/{tenantId:guid}/users/{userId:guid}/roles/{assignmentId:guid}", RevokeTenantRole)
            .RequireAuthorization(pb => pb.RequireRole("PlatformAdmin"));

        // ── Infrastructure DNS ──────────────────────────────────────────
        routes.MapPost("/api/admin/dns/provision", ProvisionInfraSubdomain)
            .RequireAuthorization(pb => pb.RequireRole("PlatformAdmin"));

        // ── Users ─────────────────────────────────────────────────────────
        // TenantAdmin/PlatformAdmin — scoped to caller's tenant for non-PlatformAdmin callers.
        routes.MapGet("/api/admin/users",           ListUsers)
            .RequirePermission(PermCodes.TenantUsersView);
        routes.MapGet("/api/admin/users/{id:guid}", GetUser)
            .RequirePermission(PermCodes.TenantUsersView);

        // ── Roles ──────────────────────────────────────────────────────────
        routes.MapGet("/api/admin/roles",           ListRoles);
        routes.MapGet("/api/admin/roles/{id:guid}", GetRole);

        // ── Products catalog (tenant-accessible) ────────────────────────
        routes.MapGet("/api/admin/products",        ListProducts);

        // ── Audit Logs ────────────────────────────────────────────────────
        routes.MapGet("/api/admin/audit",           ListAudit)
            .RequireAuthorization(pb => pb.RequireRole("PlatformAdmin"));

        // ── Platform Settings (static seed — no DB table yet) ─────────────
        routes.MapGet("/api/admin/settings",            ListSettings)
            .RequireAuthorization(pb => pb.RequireRole("PlatformAdmin"));
        routes.MapPatch("/api/admin/settings/{key}",    UpdateSetting)
            .RequireAuthorization(pb => pb.RequireRole("PlatformAdmin"));

        // ── Support Cases (not yet persisted — empty stubs) ───────────────
        routes.MapGet("/api/admin/support",             ListSupport)
            .RequireAuthorization(pb => pb.RequireRole("PlatformAdmin"));
        routes.MapGet("/api/admin/support/{id}",        GetSupport)
            .RequireAuthorization(pb => pb.RequireRole("PlatformAdmin"));
        routes.MapPost("/api/admin/support",            CreateSupport)
            .RequireAuthorization(pb => pb.RequireRole("PlatformAdmin"));
        routes.MapPost("/api/admin/support/{id}/notes", AddSupportNote)
            .RequireAuthorization(pb => pb.RequireRole("PlatformAdmin"));
        routes.MapPatch("/api/admin/support/{id}/status", UpdateSupportStatus)
            .RequireAuthorization(pb => pb.RequireRole("PlatformAdmin"));

        // ── LSCC-010: Provider auto-provisioning — minimal org creation ──────
        // Internal service-to-service endpoint.  Token-gated at the gateway.
        // Creates a minimal PROVIDER Organization for a CareConnect provider.
        // Idempotent: returns the existing org if already provisioned.
        routes.MapGet("/api/admin/organizations",                                    ListOrganizations);
        routes.MapPost("/api/admin/organizations",                                   AdminEndpointsLscc010.CreateProviderOrganization);
        // SYNQLIEN-BUYER-ORG: Public buyer activation — ensure Identity LIEN_OWNER org exists.
        routes.MapPost("/api/admin/organizations/synqlien-buyer",                    AdminEndpointsLscc010.CreateSynqLienBuyerOrganization);
        routes.MapGet("/api/admin/organizations/{id:guid}",                          AdminEndpointsLscc010.GetOrganizationById);
        // CC2-INT-B04: M2M user provisioning for provider activation (no permission gate — internal only)
        routes.MapPost("/api/admin/organizations/{id:guid}/provision-user",          AdminEndpointsLscc010.ProvisionProviderUser);
        // CC2-ENROLL: Self-enrollment — creates active user with direct password (no invitation email)
        routes.MapPost("/api/admin/organizations/{id:guid}/self-register",           AdminEndpointsLscc010.SelfRegisterUser);
        // SYNQLIEN-BUYER-ENROLL: Public buyer offer account activation — creates/links SYNQLIEN_BUYER access.
        routes.MapPost("/api/admin/organizations/{id:guid}/synqlien-buyer-self-register", AdminEndpointsLscc010.SelfRegisterSynqLienBuyer);
        // CC2-ENROLL-FIRM: Law firm self-enrollment — creates a LAW_FIRM org (keyed on tenantId + email)
        routes.MapPost("/api/admin/organizations/law-firm",                          AdminEndpointsLscc010.CreateLawFirmOrganization);
        routes.MapPut("/api/admin/organizations/{id:guid}", UpdateOrganization);
        routes.MapPatch("/api/admin/organizations/{id:guid}/provider-mode", UpdateOrganizationProviderMode);

        // ── Platform Foundation — Phase 1-6 ──────────────────────────────
        routes.MapGet("/api/admin/organization-types",             ListOrganizationTypes);
        routes.MapGet("/api/admin/organization-types/{id:guid}",   GetOrganizationType);

        routes.MapGet("/api/admin/relationship-types",             ListRelationshipTypes);
        routes.MapGet("/api/admin/relationship-types/{id:guid}",   GetRelationshipType);

        routes.MapGet("/api/admin/organization-relationships",     ListOrganizationRelationships);
        routes.MapGet("/api/admin/organization-relationships/{id:guid}", GetOrganizationRelationship);
        routes.MapPost("/api/admin/organization-relationships",    CreateOrganizationRelationship)
            .RequireAuthorization(pb => pb.RequireRole("PlatformAdmin"));
        routes.MapDelete("/api/admin/organization-relationships/{id:guid}", DeactivateOrganizationRelationship)
            .RequireAuthorization(pb => pb.RequireRole("PlatformAdmin"));

        routes.MapGet("/api/admin/product-org-type-rules",          ListProductOrgTypeRules);
        // Two URL variants served by the same handler — client uses the short form.
        routes.MapGet("/api/admin/product-relationship-type-rules", ListProductRelationshipTypeRules);
        routes.MapGet("/api/admin/product-rel-type-rules",          ListProductRelationshipTypeRules);

        // ── Legacy coverage (Phase G) ────────────────────────────────────────
        routes.MapGet("/api/admin/legacy-coverage", GetLegacyCoverage)
            .RequireAuthorization(pb => pb.RequireRole("PlatformAdmin"));

        // ── Platform readiness summary (Phase 8) ─────────────────────────────
        routes.MapGet("/api/admin/platform-readiness", GetPlatformReadiness)
            .RequireAuthorization(pb => pb.RequireRole("PlatformAdmin"));

        // ── User lifecycle ────────────────────────────────────────────────────
        // Step 27 (Phase B): user deactivation — emits identity.user.deactivated.
        // LS-ID-TNT-012: TenantAdmin/PlatformAdmin bypass via RequirePermissionFilter;
        // StandardUsers with explicit TENANT.users:manage grant are also allowed.
        routes.MapPatch("/api/admin/users/{id:guid}/deactivate",            DeactivateUser)
            .RequirePermission(PermCodes.TenantUsersManage);

        // UIX-002: activate user
        routes.MapPost("/api/admin/users/{id:guid}/activate",               ActivateUser)
            .RequirePermission(PermCodes.TenantUsersManage);

        // UIX-002: invite user
        routes.MapPost("/api/admin/users/invite",                           InviteUser)
            .RequirePermission(PermCodes.TenantInvitationsManage);

        // PUM-B06: invite platform internal user (LegalSynq staff)
        routes.MapPost("/api/admin/platform-users/invite",                  InvitePlatformUser)
            .RequirePermission(PermCodes.TenantUsersManage);

        // UIX-002: resend invite
        routes.MapPost("/api/admin/users/{id:guid}/resend-invite",          ResendInvite)
            .RequirePermission(PermCodes.TenantInvitationsManage);

        // UIX-002: cancel invite
        routes.MapPost("/api/admin/users/{id:guid}/cancel-invite",          CancelInvite)
            .RequirePermission(PermCodes.TenantInvitationsManage);

        // Admin can edit a user's primary phone number on file.
        routes.MapPatch("/api/admin/users/{id:guid}/phone",                 UpdateUserPhone)
            .RequirePermission(PermCodes.TenantUsersManage);

        // UIX-003-03: security / session admin actions
        // All security actions require at minimum TenantAdmin or PlatformAdmin privilege
        // (enforced via TENANT.users:manage, which has TenantAdmin+PlatformAdmin bypass).
        routes.MapPost("/api/admin/users/{id:guid}/lock",                   LockUser)
            .RequirePermission(PermCodes.TenantUsersManage);
        routes.MapPost("/api/admin/users/{id:guid}/unlock",                 UnlockUser)
            .RequirePermission(PermCodes.TenantUsersManage);
        routes.MapPost("/api/admin/users/{id:guid}/reset-password",         AdminResetPassword)
            .RequirePermission(PermCodes.TenantUsersManage);
        routes.MapPost("/api/admin/users/{id:guid}/set-password",           AdminSetPassword)
            .RequirePermission(PermCodes.TenantUsersManage);
        routes.MapPost("/api/admin/users/{id:guid}/force-logout",           ForceLogout)
            .RequirePermission(PermCodes.TenantUsersManage);
        routes.MapGet("/api/admin/users/{id:guid}/security",                GetUserSecurity)
            .RequirePermission(PermCodes.TenantUsersManage);

        // UIX-004: user activity audit trail (queries local AuditLogs by EntityId)
        routes.MapGet("/api/admin/users/{id:guid}/activity",                GetUserActivity)
            .RequirePermission(PermCodes.TenantUsersView);

        // LSCC-01-003: Admin CareConnect provider provisioning
        routes.MapGet("/api/admin/users/{id:guid}/careconnect-readiness",   GetCareConnectReadiness);
        routes.MapPost("/api/admin/users/{id:guid}/provision-careconnect",  ProvisionForCareConnect);

        // ── Role assignment ───────────────────────────────────────────────────
        // LS-ID-TNT-012: role assignment gated on TENANT.roles:assign.
        routes.MapPost("/api/admin/users/{id:guid}/roles",                  AssignRole)
            .RequirePermission(PermCodes.TenantRolesAssign);
        routes.MapDelete("/api/admin/users/{id:guid}/roles/{roleId:guid}",  RevokeRole)
            .RequirePermission(PermCodes.TenantRolesAssign);

        // UIX-002-C: assignable roles with eligibility metadata
        routes.MapGet("/api/admin/users/{id:guid}/assignable-roles",        GetAssignableRoles);

        // Phase I: scoped role summary for a user (non-global scope visibility)
        routes.MapGet("/api/admin/users/{id:guid}/scoped-roles",            GetScopedRoles);

        // ── PUM-B04: User product access management ────────────────────────────
        routes.MapGet   ("/api/admin/users/{id:guid}/products",                                          ListUserProductAccess);
        routes.MapPost  ("/api/admin/users/{id:guid}/products",                                          GrantUserProductAccess);
        routes.MapDelete("/api/admin/users/{id:guid}/products/{productKey}",                             RevokeUserProductAccess);
        routes.MapGet   ("/api/admin/users/{id:guid}/products/{productKey}/access",                      CheckUserProductAccess);
        routes.MapPost  ("/api/admin/users/{id:guid}/products/{productKey}/roles",                       AssignUserProductRole);
        routes.MapDelete("/api/admin/users/{id:guid}/products/{productKey}/roles/{assignmentId:guid}",   RevokeUserProductRole);

        // ── PUM-B05: External customer user management ─────────────────────────
        routes.MapPost("/api/admin/external-users",                                                      CreateExternalUser);
        routes.MapGet ("/api/admin/external-users",                                                      ListExternalUsers);
        routes.MapGet ("/api/admin/external-users/{userId:guid}",                                        GetExternalUser);
        routes.MapGet ("/api/admin/external-users/{userId:guid}/products/{productKey}/access",           CheckExternalUserProductAccess);

        // ── Memberships ───────────────────────────────────────────────────────
        // UIX-002: assign user to organization, set primary, remove (scaffold)
        // LS-ID-TNT-012: membership mutations gated on TENANT.users:manage.
        routes.MapPost("/api/admin/users/{id:guid}/memberships",                                   AssignMembership)
            .RequirePermission(PermCodes.TenantUsersManage);
        routes.MapPost("/api/admin/users/{id:guid}/memberships/{membershipId:guid}/set-primary",   SetPrimaryMembership)
            .RequirePermission(PermCodes.TenantUsersManage);
        routes.MapDelete("/api/admin/users/{id:guid}/memberships/{membershipId:guid}",             RemoveMembership)
            .RequirePermission(PermCodes.TenantUsersManage);


        // ── Permissions catalog ───────────────────────────────────────────────
        // UIX-002: read-only capability/permission catalog
        routes.MapGet("/api/admin/permissions",                         ListPermissions);

        // ── Role permission management (UIX-005) ──────────────────────────────
        routes.MapGet("/api/admin/roles/{id:guid}/permissions",                              GetRolePermissions);
        routes.MapPost("/api/admin/roles/{id:guid}/permissions",                             AssignRolePermission);
        routes.MapDelete("/api/admin/roles/{id:guid}/permissions/{permissionId:guid}",        RevokeRolePermission);

        // ── User effective permissions (UIX-005) ─────────────────────────────
        routes.MapGet("/api/admin/users/{id:guid}/permissions",                              GetUserEffectivePermissions);

        // ── Authorization debug (LS-COR-AUT-008) ───────────────────────────
        routes.MapGet("/api/admin/users/{id:guid}/access-debug",                             GetAccessDebug);

        // ── Permission catalog management (LS-COR-AUT-010) ────────────────────
        routes.MapGet("/api/admin/permissions/by-product/{productCode}",                     ListPermissionsByProduct);
        routes.MapPost("/api/admin/permissions",                                             CreatePermission)
            .RequireAuthorization(pb => pb.RequireRole("PlatformAdmin"));
        routes.MapPatch("/api/admin/permissions/{id:guid}",                                  UpdatePermission)
            .RequireAuthorization(pb => pb.RequireRole("PlatformAdmin"));
        routes.MapDelete("/api/admin/permissions/{id:guid}",                                 DeactivatePermission)
            .RequireAuthorization(pb => pb.RequireRole("PlatformAdmin"));

        // ── LS-COR-AUT-011: ABAC Policy Management ─────────────────────────
        routes.MapGet("/api/admin/policies",                                                 ListPolicies);
        routes.MapGet("/api/admin/policies/{id:guid}",                                       GetPolicy);
        routes.MapPost("/api/admin/policies",                                                CreatePolicy)
            .RequireAuthorization(pb => pb.RequireRole("PlatformAdmin"));
        routes.MapPatch("/api/admin/policies/{id:guid}",                                     UpdatePolicy)
            .RequireAuthorization(pb => pb.RequireRole("PlatformAdmin"));
        routes.MapDelete("/api/admin/policies/{id:guid}",                                    DeactivatePolicy)
            .RequireAuthorization(pb => pb.RequireRole("PlatformAdmin"));

        // Policy rules
        routes.MapGet("/api/admin/policies/{policyId:guid}/rules",                           ListPolicyRules);
        routes.MapPost("/api/admin/policies/{policyId:guid}/rules",                          CreatePolicyRule)
            .RequireAuthorization(pb => pb.RequireRole("PlatformAdmin"));
        routes.MapPatch("/api/admin/policies/{policyId:guid}/rules/{ruleId:guid}",           UpdatePolicyRule)
            .RequireAuthorization(pb => pb.RequireRole("PlatformAdmin"));
        routes.MapDelete("/api/admin/policies/{policyId:guid}/rules/{ruleId:guid}",          DeletePolicyRule)
            .RequireAuthorization(pb => pb.RequireRole("PlatformAdmin"));

        // Permission ↔ Policy mappings
        routes.MapGet("/api/admin/permission-policies",                                      ListPermissionPolicies);
        routes.MapPost("/api/admin/permission-policies",                                     CreatePermissionPolicy)
            .RequireAuthorization(pb => pb.RequireRole("PlatformAdmin"));
        routes.MapDelete("/api/admin/permission-policies/{id:guid}",                         DeactivatePermissionPolicy)
            .RequireAuthorization(pb => pb.RequireRole("PlatformAdmin"));

        // Policy evaluation debug
        routes.MapGet("/api/admin/policies/supported-fields",                                GetSupportedFields);

        // ── LS-COR-AUT-011D: Authorization Simulation ───────────────────────
        routes.MapPost("/api/admin/authorization/simulate",                                  AdminEndpointsLscc010.SimulateAuthorization)
            .RequireAuthorization(pb => pb.RequireRole("PlatformAdmin"));

        // ── Membership lookup (notifications fan-out) ────────────────────────
        // Internal service-to-service endpoint used by the notifications service
        // to resolve role- or org-addressed recipients to concrete users.
        routes.MapGet("/api/admin/membership-lookup", MembershipLookup);

        // ── Notifications cache invalidation status ──────────────────────────
        // Operator-facing snapshot of identity → notifications invalidation
        // counters (configured?, attempted, succeeded, failed, last failure).
        // Surfaces obvious mis-configurations (wrong BaseUrl or shared token →
        // failures climb while succeeded stays 0) without grepping logs.
        routes.MapGet("/api/admin/notifications-cache/status",
            (INotificationsCacheClientDiagnostics diagnostics) =>
                Results.Ok(diagnostics.GetSnapshot()));

        return routes;
    }

    // =========================================================================
    // TENANTS
    // =========================================================================

    private static async Task<IResult> ListTenants(
        IdentityDbContext db,
        int    page     = 1,
        int    pageSize = 20,
        string search   = "")
    {
        var q = db.Tenants
            .Include(t => t.Organizations)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(t => t.Name.Contains(search) || t.Code.Contains(search));

        var total = await q.CountAsync();

        var tenants = await q
            .OrderBy(t => t.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(t => new
            {
                id                 = t.Id,
                code               = t.Code,
                displayName        = t.Name,
                type               = t.Organizations.OrderBy(o => o.CreatedAtUtc).Select(o => o.OrgType).FirstOrDefault() ?? "LAW_FIRM",
                status             = t.IsActive ? "Active" : "Inactive",
                primaryContactName = db.UserTenants
                    .Where(ut => ut.TenantId == t.Id && ut.IsActive)
                    .OrderBy(ut => ut.JoinedAtUtc)
                    .Select(ut => ut.User.FirstName + " " + ut.User.LastName)
                    .FirstOrDefault() ?? "",
                isActive           = t.IsActive,
                userCount          = db.UserTenants.Count(ut => ut.TenantId == t.Id && ut.IsActive),
                orgCount           = t.Organizations.Count,
                createdAtUtc       = t.CreatedAtUtc,
                subdomain          = t.Subdomain,
                provisioningStatus = t.ProvisioningStatus.ToString(),
            })
            .ToListAsync();

        return Results.Ok(new
        {
            items      = tenants,
            totalCount = total,
            page,
            pageSize,
        });
    }

    private static async Task<IResult> GetTenant(Guid id, IdentityDbContext db, IDnsService dnsService)
    {
        var t = await db.Tenants
            .Include(t => t.Organizations)
            .Include(t => t.TenantProducts)
                .ThenInclude(tp => tp.Product)
            .FirstOrDefaultAsync(t => t.Id == id);

        if (t is null) return Results.NotFound();

        var firstUser = await db.UserTenants
            .Where(ut => ut.TenantId == t.Id && ut.IsActive)
            .OrderBy(ut => ut.JoinedAtUtc)
            .Select(ut => ut.User)
            .FirstOrDefaultAsync();

        // Always return ALL active platform products so the entitlements panel
        // is never empty for newly created tenants. Products not yet in TenantProducts
        // are returned with enabled=false so they can be toggled on.
        var allProducts = await db.Products
            .Where(p => p.IsActive)
            .OrderBy(p => p.Name)
            .ToListAsync();

        var entitlements = allProducts.Select(p =>
        {
            var tp = t.TenantProducts.FirstOrDefault(x => x.ProductId == p.Id);
            // Return the frontend ProductCode ('SynqFund') rather than the raw DB code ('SYNQ_FUND')
            // so the mapper and PRODUCT_META lookup in the Control Center panel work without transforms.
            var frontendCode = DbToFrontendProductCode.TryGetValue(p.Code, out var fc) ? fc : p.Name;
            return new
            {
                productCode  = frontendCode,
                productName  = p.Name,
                enabled      = tp?.IsEnabled ?? false,
                status       = (tp?.IsEnabled ?? false) ? "Active" : "Disabled",
                enabledAtUtc = tp?.EnabledAtUtc,
            };
        }).ToList();

        var defaultOrg = t.Organizations.OrderBy(o => o.CreatedAtUtc).FirstOrDefault();
        return Results.Ok(new
        {
            id                    = t.Id,
            code                  = t.Code,
            displayName           = t.Name,
            type                  = defaultOrg?.OrgType ?? "LAW_FIRM",
            status                = t.IsActive ? "Active" : "Inactive",
            primaryContactName    = firstUser is null ? "" : $"{firstUser.FirstName} {firstUser.LastName}",
            email                 = firstUser?.Email,
            isActive              = t.IsActive,
            userCount             = await db.UserTenants.CountAsync(ut => ut.TenantId == t.Id && ut.IsActive),
            activeUserCount       = await db.UserTenants.CountAsync(ut => ut.TenantId == t.Id && ut.IsActive && ut.User.IsActive),
            orgCount              = t.Organizations.Count,
            linkedOrgCount        = t.Organizations.Count,
            createdAtUtc          = t.CreatedAtUtc,
            updatedAtUtc          = t.UpdatedAtUtc,
            sessionTimeoutMinutes = t.SessionTimeoutMinutes,
            logoDocumentId        = t.LogoDocumentId,
            logoWhiteDocumentId   = t.LogoWhiteDocumentId,
            productEntitlements   = entitlements,
            subdomain                       = t.Subdomain,
            provisioningStatus              = t.ProvisioningStatus.ToString(),
            lastProvisioningAttemptUtc       = t.LastProvisioningAttemptUtc,
            provisioningFailureReason       = t.ProvisioningFailureReason,
            provisioningFailureStage        = t.ProvisioningFailureStage.ToString(),
            hostname                        = t.Subdomain != null
                ? dnsService.BuildHostname(t.Subdomain)
                : (string?)null,
            verificationAttemptCount        = t.VerificationAttemptCount,
            lastVerificationAttemptUtc      = t.LastVerificationAttemptUtc,
            nextVerificationRetryAtUtc      = t.NextVerificationRetryAtUtc,
            isVerificationRetryExhausted    = t.IsVerificationRetryExhausted,
        });
    }

    /// <summary>
    /// POST /api/admin/tenants
    ///
    /// Creates a new tenant and a default admin user in a single atomic transaction.
    /// Returns the new tenant details and a one-time temporary password for the admin user.
    ///
    /// Validations:
    ///   - Tenant code must be unique (case-insensitive).
    ///   - Admin email must not already exist.
    ///   - Code: 2–12 alphanumeric characters (uppercased automatically).
    /// </summary>
    private static async Task<IResult> CreateTenant(
        CreateTenantRequest         body,
        IdentityDbContext           db,
        IPasswordHasher             passwordHasher,
        IAuditEventClient           auditClient,
        ITenantProvisioningService  provisioningService,
        IProductProvisioningService productProvisioningEngine,
        ILoggerFactory              loggerFactory,
        ITenantSyncAdapter          syncAdapter,
        CancellationToken           ct)
    {
        // ── Validate inputs ───────────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(body.Name))
            return Results.BadRequest(new { error = "Tenant name is required." });

        if (string.IsNullOrWhiteSpace(body.Code))
            return Results.BadRequest(new { error = "Tenant code is required." });

        var code = SlugGenerator.Normalize(body.Code);
        var (slugValid, slugError) = SlugGenerator.Validate(code);
        if (!slugValid)
            return Results.BadRequest(new { error = slugError });

        if (string.IsNullOrWhiteSpace(body.AdminEmail))
            return Results.BadRequest(new { error = "Admin email is required." });

        if (string.IsNullOrWhiteSpace(body.AdminFirstName))
            return Results.BadRequest(new { error = "Admin first name is required." });

        if (string.IsNullOrWhiteSpace(body.AdminLastName))
            return Results.BadRequest(new { error = "Admin last name is required." });

        // ── Uniqueness checks ──────────────────────────────────────────────────
        var codeExists = await db.Tenants.AnyAsync(t => t.Code == code, ct);
        if (codeExists)
            return Results.Conflict(new { error = $"A tenant with code '{code}' already exists." });

        var emailNorm = body.AdminEmail.ToLowerInvariant().Trim();
        var emailExists = await db.Users.AnyAsync(u => u.Email == emailNorm, ct);
        if (emailExists)
            return Results.Conflict(new { error = $"A user with email '{emailNorm}' already exists." });

        // ── Resolve organization type ──────────────────────────────────────────
        var orgTypeId = body.OrgType switch
        {
            "PROVIDER"   => new Guid("70000000-0000-0000-0000-000000000003"),
            "FUNDER"     => new Guid("70000000-0000-0000-0000-000000000004"),
            "LIEN_OWNER" => new Guid("70000000-0000-0000-0000-000000000005"),
            _            => new Guid("70000000-0000-0000-0000-000000000002"),
        };

        // ── Find TenantAdmin role ──────────────────────────────────────────────
        var tenantAdminRole = await db.Roles.FirstOrDefaultAsync(r => r.Name == "TenantAdmin", ct);
        if (tenantAdminRole is null)
            return Results.Problem("TenantAdmin role not found. Contact platform support.", statusCode: 500);

        // ── Generate a one-time temporary password ─────────────────────────────
        var tempPassword = GenerateTemporaryPassword();
        var passwordHash = passwordHasher.Hash(tempPassword);

        // ── Create tenant + org + user + membership + role assignment ──────────
        var tenant = Tenant.Create(body.Name.Trim(), code);
        if (!string.IsNullOrWhiteSpace(body.AddressLine1) || !string.IsNullOrWhiteSpace(body.City))
        {
            tenant.SetAddress(
                addressLine1:   body.AddressLine1,
                city:           body.City,
                state:          body.State,
                postalCode:     body.PostalCode,
                latitude:       body.Latitude,
                longitude:      body.Longitude,
                geoPointSource: body.GeoPointSource ?? "nominatim");
        }
        db.Tenants.Add(tenant);

        var org = Organization.Create(
            tenantId:         tenant.Id,
            name:             body.Name.Trim(),
            organizationTypeId: orgTypeId,
            displayName:      body.Name.Trim());
        db.Organizations.Add(org);

        var user = User.Create(
            tenantId:     tenant.Id,
            email:        emailNorm,
            passwordHash: passwordHash,
            firstName:    body.AdminFirstName.Trim(),
            lastName:     body.AdminLastName.Trim());
        db.Users.Add(user);
        db.UserTenants.Add(UserTenant.Create(user.Id, tenant.Id));

        var membership = UserOrganizationMembership.Create(
            userId:         user.Id,
            organizationId: org.Id,
            memberRole:     MemberRole.Admin);
        db.UserOrganizationMemberships.Add(membership);

        var sra = ScopedRoleAssignment.Create(
            userId:    user.Id,
            roleId:    tenantAdminRole.Id,
            scopeType: ScopedRoleAssignment.ScopeTypes.Global,
            tenantId:  tenant.Id);
        db.ScopedRoleAssignments.Add(sra);

        // ── Mark tenant + org owner (the admin user just created) ─────────────
        tenant.SetOwner(user.Id);
        org.SetOwner(user.Id);

        await db.SaveChangesAsync(ct);

        // ── TENANT-B07: Dual-write to Tenant service (feature-flagged) ────────
        var log = loggerFactory.CreateLogger("Identity.Api.AdminEndpoints");
        try
        {
            await syncAdapter.SyncAsync(new IdentityTenantSyncRequest(
                TenantId:            tenant.Id,
                Code:                tenant.Code,
                DisplayName:         tenant.Name,
                Status:              "Active",
                Subdomain:           tenant.Subdomain,
                LogoDocumentId:      tenant.LogoDocumentId,
                LogoWhiteDocumentId: tenant.LogoWhiteDocumentId,
                SourceCreatedAtUtc:  tenant.CreatedAtUtc,
                SourceUpdatedAtUtc:  tenant.UpdatedAtUtc,
                EventType:           "Create"), ct);
        }
        catch (Exception syncEx)
        {
            log.LogError(syncEx,
                "[TenantDualWrite] Strict sync failure in CreateTenant for TenantId={TenantId}, aborting.",
                tenant.Id);
            return Results.Problem("Tenant sync failed (strict mode active).", statusCode: 502);
        }

        // ── Provision subdomain (DNS + TenantDomain record) ──────────────────
        var provResult = await provisioningService.ProvisionAsync(tenant, ct);

        if (provResult.Success)
        {
            log.LogInformation("Subdomain provisioned for tenant {TenantCode}: {Hostname}",
                tenant.Code, provResult.Hostname);
        }
        else
        {
            log.LogWarning("Subdomain provisioning failed for tenant {TenantCode}: {Reason}",
                tenant.Code, provResult.ErrorMessage);
        }

        // ── Product provisioning (if products specified) ────────────────────────
        var productResults = new List<ProvisionProductResult>();
        if (body.Products is { Count: > 0 })
        {
            foreach (var rawCode in body.Products)
            {
                var dbCode = await ResolveExistingDbProductCodeAsync(rawCode, db, ct)
                    ?? GetDbProductCodeCandidates(rawCode).First();
                try
                {
                    var pr = await productProvisioningEngine.ProvisionAsync(
                        new ProvisionProductRequest(tenant.Id, dbCode, true), ct);
                    productResults.Add(pr);
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Product provisioning for {ProductCode} failed during tenant onboarding", dbCode);
                }
            }
        }

        // ── Audit events ────────────────────────────────────────────────────────
        var now = DateTimeOffset.UtcNow;
        _ = auditClient.IngestAsync(new IngestAuditEventRequest
        {
            EventType     = "platform.admin.tenant.created",
            EventCategory = EventCategory.Administrative,
            SourceSystem  = "identity-service",
            SourceService = "admin-api",
            Visibility    = VisibilityScope.Platform,
            Severity      = SeverityLevel.Info,
            OccurredAtUtc = now,
            Scope         = new AuditEventScopeDto { ScopeType = ScopeType.Platform },
            Actor         = new AuditEventActorDto { Type = ActorType.System },
            Entity        = new AuditEventEntityDto { Type = "Tenant", Id = tenant.Id.ToString() },
            Action        = "TenantCreated",
            Description   = $"Tenant '{tenant.Name}' ({tenant.Code}) created with default admin '{emailNorm}'.",
            After         = JsonSerializer.Serialize(new { tenantId = tenant.Id, code = tenant.Code, adminEmail = emailNorm, subdomain = tenant.Subdomain }),
            IdempotencyKey = IdempotencyKey.For("identity-service", "platform.admin.tenant.created", tenant.Id.ToString()),
            Tags = ["tenant-management", "onboarding"],
        });

        if (provResult.Success)
        {
            _ = auditClient.IngestAsync(new IngestAuditEventRequest
            {
                EventType     = "platform.admin.tenant.provisioning.succeeded",
                EventCategory = EventCategory.Administrative,
                SourceSystem  = "identity-service",
                SourceService = "admin-api",
                Visibility    = VisibilityScope.Platform,
                Severity      = SeverityLevel.Info,
                OccurredAtUtc = now,
                Scope         = new AuditEventScopeDto { ScopeType = ScopeType.Platform },
                Actor         = new AuditEventActorDto { Type = ActorType.System },
                Entity        = new AuditEventEntityDto { Type = "Tenant", Id = tenant.Id.ToString() },
                Action        = "ProvisioningSucceeded",
                Description   = $"Subdomain '{tenant.Subdomain}' provisioned for tenant '{tenant.Code}'.",
                After         = JsonSerializer.Serialize(new { hostname = provResult.Hostname, subdomain = tenant.Subdomain }),
                IdempotencyKey = IdempotencyKey.For("identity-service", "platform.admin.tenant.provisioning.succeeded", tenant.Id.ToString()),
                Tags = ["tenant-management", "provisioning"],
            });
        }
        else
        {
            _ = auditClient.IngestAsync(new IngestAuditEventRequest
            {
                EventType     = "platform.admin.tenant.provisioning.failed",
                EventCategory = EventCategory.Administrative,
                SourceSystem  = "identity-service",
                SourceService = "admin-api",
                Visibility    = VisibilityScope.Platform,
                Severity      = SeverityLevel.Warn,
                OccurredAtUtc = now,
                Scope         = new AuditEventScopeDto { ScopeType = ScopeType.Platform },
                Actor         = new AuditEventActorDto { Type = ActorType.System },
                Entity        = new AuditEventEntityDto { Type = "Tenant", Id = tenant.Id.ToString() },
                Action        = "ProvisioningFailed",
                Description   = $"Subdomain provisioning failed for tenant '{tenant.Code}': {provResult.ErrorMessage}",
                After         = JsonSerializer.Serialize(new { subdomain = tenant.Subdomain, error = provResult.ErrorMessage }),
                IdempotencyKey = IdempotencyKey.For("identity-service", "platform.admin.tenant.provisioning.failed", tenant.Id.ToString()),
                Tags = ["tenant-management", "provisioning"],
            });
        }

        return Results.Created(
            $"/api/admin/tenants/{tenant.Id}",
            new
            {
                tenantId            = tenant.Id,
                displayName         = tenant.Name,
                code                = tenant.Code,
                status              = "Active",
                adminUserId         = user.Id,
                adminEmail          = user.Email,
                temporaryPassword   = tempPassword,
                subdomain           = tenant.Subdomain,
                provisioningStatus  = tenant.ProvisioningStatus.ToString(),
                hostname            = provResult.Hostname,
                addressLine1        = tenant.AddressLine1,
                city                = tenant.City,
                state               = tenant.State,
                postalCode          = tenant.PostalCode,
                latitude            = tenant.Latitude,
                longitude           = tenant.Longitude,
                geoPointSource      = tenant.GeoPointSource,
                productsProvisioned = productResults.Select(p => new
                {
                    productCode = p.ProductCode,
                    enabled     = p.Enabled,
                    orgProductsCreated = p.OrganizationProductsCreated,
                }).ToList(),
            });
    }

    // ── BLK-ID-01: RETIRED ────────────────────────────────────────────────────
    /// <summary>
    /// GET /api/admin/tenants/check-code?code=xxx
    ///
    /// [RETIRED — BLK-ID-01]
    /// Tenant code validation has moved to the Tenant service.
    /// Use: GET /tenant/api/v1/tenants/check-code?code={code}
    ///
    /// Returns 410 Gone to all callers so they receive a clear, actionable error.
    /// </summary>
#pragma warning disable CS1998
    private static async Task<IResult> CheckTenantCode(
        string            code,
        IdentityDbContext db,
        CancellationToken ct)
    {
        // BLK-ID-01 SAFEGUARD: Tenant code validation is no longer owned by Identity.
        // Callers must migrate to the Tenant service endpoint.
        return Results.Json(
            new
            {
                error  = "This endpoint has been retired.",
                reason = "Tenant code validation has moved to the Tenant service.",
                tenantServiceEndpoint = "GET /tenant/api/v1/tenants/check-code?code={code}",
            },
            statusCode: 410);
    }
#pragma warning restore CS1998

    // ── BLK-ID-01: RETIRED ────────────────────────────────────────────────────
    /// <summary>
    /// POST /api/admin/tenants/self-provision
    ///
    /// [RETIRED — BLK-ID-01]
    /// Tenant creation has moved to the Tenant service.
    /// Use: POST /tenant/api/v1/admin/tenants
    ///
    /// Returns 410 Gone to all callers so they receive a clear, actionable error.
    /// The CareConnect onboarding flow migration to Tenant service is a future block.
    /// </summary>
#pragma warning disable CS1998
    private static async Task<IResult> SelfProvisionTenant(
        SelfProvisionTenantRequest       body,
        IdentityDbContext                db,
        IProductProvisioningService      productProvisioningEngine,
        ITenantProvisioningService       provisioningService,
        IAuditEventClient                auditClient,
        ILoggerFactory                   loggerFactory,
        ITenantSyncAdapter               syncAdapter,
        CancellationToken                ct)
    {
        // BLK-ID-01 SAFEGUARD: Tenant creation is no longer supported in Identity service.
        // All tenant creation must go through the Tenant service.
        // Callers should migrate to: POST /tenant/api/v1/admin/tenants
        return Results.Json(
            new
            {
                error  = "This endpoint has been retired.",
                reason = "Tenant creation is no longer supported in Identity service. Use Tenant service.",
                tenantServiceEndpoints = new
                {
                    checkCode    = "GET /tenant/api/v1/tenants/check-code?code={code}",
                    createTenant = "POST /tenant/api/v1/admin/tenants",
                },
            },
            statusCode: 410);
    }
#pragma warning restore CS1998

    private static async Task<IResult> RetryProvisioning(
        Guid                       id,
        IdentityDbContext          db,
        ITenantProvisioningService provisioningService,
        IAuditEventClient          auditClient,
        ILoggerFactory             loggerFactory,
        CancellationToken          ct)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null)
            return Results.NotFound(new { error = "Tenant not found." });

        if (tenant.ProvisioningStatus == ProvisioningStatus.Active)
            return Results.Ok(new { success = true, provisioningStatus = "Active", hostname = (string?)null, error = (string?)null });

        if (tenant.ProvisioningStatus == ProvisioningStatus.InProgress)
            return Results.Conflict(new { error = "Provisioning is already in progress." });

        var log = loggerFactory.CreateLogger("Identity.Api.AdminEndpoints");
        log.LogInformation("Provisioning retry requested for tenant {TenantCode}", tenant.Code);

        var result = await provisioningService.RetryProvisioningAsync(tenant, ct);

        var now = DateTimeOffset.UtcNow;
        _ = auditClient.IngestAsync(new IngestAuditEventRequest
        {
            EventType     = result.Success
                ? "platform.admin.tenant.provisioning.retry.succeeded"
                : "platform.admin.tenant.provisioning.retry.failed",
            EventCategory = EventCategory.Administrative,
            SourceSystem  = "identity-service",
            SourceService = "admin-api",
            Visibility    = VisibilityScope.Platform,
            Severity      = result.Success ? SeverityLevel.Info : SeverityLevel.Warn,
            OccurredAtUtc = now,
            Scope         = new AuditEventScopeDto { ScopeType = ScopeType.Platform },
            Actor         = new AuditEventActorDto { Type = ActorType.System },
            Entity        = new AuditEventEntityDto { Type = "Tenant", Id = tenant.Id.ToString() },
            Action        = result.Success ? "ProvisioningRetrySucceeded" : "ProvisioningRetryFailed",
            Description   = result.Success
                ? $"Provisioning retry succeeded for tenant '{tenant.Code}': {result.Hostname}"
                : $"Provisioning retry failed for tenant '{tenant.Code}': {result.ErrorMessage}",
            After         = JsonSerializer.Serialize(new { hostname = result.Hostname, error = result.ErrorMessage }),
            IdempotencyKey = IdempotencyKey.For("identity-service", result.Success ? "provisioning.retry.succeeded" : "provisioning.retry.failed", $"{tenant.Id}:{now.Ticks}"),
            Tags = ["tenant-management", "provisioning", "retry"],
        });

        return Results.Ok(new
        {
            success            = result.Success,
            provisioningStatus = tenant.ProvisioningStatus.ToString(),
            hostname           = result.Hostname,
            error              = result.ErrorMessage,
        });
    }

    private static async Task<IResult> RetryVerification(
        Guid                       id,
        IdentityDbContext          db,
        IVerificationRetryService  retryService,
        IDnsService                dnsService,
        IAuditEventClient          auditClient,
        ILoggerFactory             loggerFactory,
        CancellationToken          ct)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null)
            return Results.NotFound(new { error = "Tenant not found." });

        if (tenant.ProvisioningStatus == ProvisioningStatus.Active)
            return Results.Ok(new { success = true, provisioningStatus = "Active", hostname = (string?)null, error = (string?)null });

        if (tenant.Subdomain is null)
            return Results.BadRequest(new { error = "Tenant has no subdomain assigned. Run provisioning first." });

        if (tenant.ProvisioningStatus == ProvisioningStatus.InProgress)
            return Results.Conflict(new { error = "Provisioning is already in progress." });

        var log = loggerFactory.CreateLogger("Identity.Api.AdminEndpoints");
        log.LogInformation("Verification retry requested (admin) for tenant {TenantCode}", tenant.Code);

        var hostname = dnsService.BuildHostname(tenant.Subdomain);

        tenant.ResetVerificationRetryState();
        await db.SaveChangesAsync(ct);

        var outcome = await retryService.ExecuteRetryAsync(tenant, hostname, ct);

        var now = DateTimeOffset.UtcNow;
        _ = auditClient.IngestAsync(new IngestAuditEventRequest
        {
            EventType     = outcome.Succeeded
                ? "platform.admin.tenant.verification.retry.succeeded"
                : "platform.admin.tenant.verification.retry.failed",
            EventCategory = EventCategory.Administrative,
            SourceSystem  = "identity-service",
            SourceService = "admin-api",
            Visibility    = VisibilityScope.Platform,
            Severity      = outcome.Succeeded ? SeverityLevel.Info : SeverityLevel.Warn,
            OccurredAtUtc = now,
            Scope         = new AuditEventScopeDto { ScopeType = ScopeType.Platform },
            Actor         = new AuditEventActorDto { Type = ActorType.System },
            Entity        = new AuditEventEntityDto { Type = "Tenant", Id = tenant.Id.ToString() },
            Action        = outcome.Succeeded ? "VerificationRetrySucceeded" : "VerificationRetryFailed",
            Description   = outcome.Succeeded
                ? $"Verification retry succeeded for tenant '{tenant.Code}': {hostname}"
                : $"Verification retry failed for tenant '{tenant.Code}' (attempt {outcome.AttemptNumber}): {outcome.LastFailureReason}",
            After         = JsonSerializer.Serialize(new
            {
                hostname,
                error = outcome.LastFailureReason,
                stage = outcome.LastFailureStage.ToString(),
                attempt = outcome.AttemptNumber,
                stillRetrying = outcome.StillRetrying,
                exhausted = outcome.Exhausted,
            }),
            IdempotencyKey = IdempotencyKey.For("identity-service",
                outcome.Succeeded ? "verification.retry.succeeded" : "verification.retry.failed",
                $"{tenant.Id}:{now.Ticks}"),
            Tags = ["tenant-management", "verification", "retry"],
        });

        if (outcome.Succeeded)
            log.LogInformation("Verification retry succeeded for tenant {TenantCode}: {Hostname}", tenant.Code, hostname);

        return Results.Ok(new
        {
            success            = outcome.Succeeded,
            provisioningStatus = tenant.ProvisioningStatus.ToString(),
            hostname           = hostname,
            error              = outcome.LastFailureReason,
            failureStage       = outcome.LastFailureStage.ToString(),
            attemptNumber      = outcome.AttemptNumber,
            stillRetrying      = outcome.StillRetrying,
            exhausted          = outcome.Exhausted,
            nextRetryAtUtc     = outcome.NextRetryAtUtc,
        });
    }

    private static async Task<IResult> ProvisionInfraSubdomain(
        InfraSubdomainRequest    body,
        IDnsService              dns,
        IAuditEventClient        auditClient,
        ILoggerFactory           loggerFactory,
        CancellationToken        ct)
    {
        if (string.IsNullOrWhiteSpace(body.Subdomain))
            return Results.BadRequest(new { error = "Subdomain is required." });

        var slug = body.Subdomain.Trim().ToLowerInvariant();
        var log = loggerFactory.CreateLogger("Identity.Api.AdminEndpoints");

        log.LogInformation("Infrastructure DNS provisioning requested for subdomain {Slug}", slug);

        var success = await dns.CreateSubdomainAsync(slug, ct);
        var hostname = dns.BuildHostname(slug);

        var now = DateTimeOffset.UtcNow;
        _ = auditClient.IngestAsync(new IngestAuditEventRequest
        {
            EventType     = success ? "platform.admin.infra.dns.created" : "platform.admin.infra.dns.failed",
            EventCategory = EventCategory.Administrative,
            SourceSystem  = "identity-service",
            SourceService = "admin-api",
            Visibility    = VisibilityScope.Platform,
            Severity      = success ? SeverityLevel.Info : SeverityLevel.Warn,
            OccurredAtUtc = now,
            Scope         = new AuditEventScopeDto { ScopeType = ScopeType.Platform },
            Actor         = new AuditEventActorDto { Type = ActorType.System },
            Entity        = new AuditEventEntityDto { Type = "InfraSubdomain", Id = slug },
            Action        = success ? "InfraDnsCreated" : "InfraDnsFailed",
            Description   = success
                ? $"Infrastructure subdomain '{hostname}' provisioned successfully."
                : $"Infrastructure subdomain '{hostname}' provisioning failed.",
            IdempotencyKey = IdempotencyKey.For("identity-service", "infra.dns", $"{slug}:{now.Ticks}"),
            Tags = ["infrastructure", "dns"],
        });

        return success
            ? Results.Ok(new { success = true, hostname, subdomain = slug })
            : Results.Problem($"DNS provisioning failed for '{hostname}'. Check Route53 configuration.", statusCode: 502);
    }

    /// <summary>
    /// Generates a secure random temporary password: 12 characters,
    /// mixing uppercase, lowercase, digits, and symbols.
    /// </summary>
    private static string GenerateTemporaryPassword()
    {
        const string upper   = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower   = "abcdefghjkmnpqrstuvwxyz";
        const string digits  = "23456789";
        const string symbols = "!@#$%&*";
        var all = upper + lower + digits + symbols;

        var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
        var bytes = new byte[16];
        rng.GetBytes(bytes);

        var chars = new char[12];
        chars[0]  = upper  [bytes[0]  % upper.Length];
        chars[1]  = lower  [bytes[1]  % lower.Length];
        chars[2]  = digits [bytes[2]  % digits.Length];
        chars[3]  = symbols[bytes[3]  % symbols.Length];
        for (int i = 4; i < 12; i++)
            chars[i] = all[bytes[i] % all.Length];

        // Shuffle
        rng.GetBytes(bytes);
        for (int i = chars.Length - 1; i > 0; i--)
        {
            int j = bytes[i] % (i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars);
    }

    private static async Task<IResult> UpdateTenantSessionSettings(
        Guid id,
        IdentityDbContext db,
        SessionSettingsRequest body)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == id);
        if (tenant is null) return Results.NotFound();

        try
        {
            tenant.SetSessionTimeout(body.SessionTimeoutMinutes);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return Results.Problem(ex.Message, statusCode: 400);
        }

        await db.SaveChangesAsync();

        return Results.Ok(new
        {
            tenantId              = tenant.Id,
            sessionTimeoutMinutes = tenant.SessionTimeoutMinutes,
            updatedAtUtc          = tenant.UpdatedAtUtc,
        });
    }

    // ── Tenant logo ───────────────────────────────────────────────────────────

    private record SetLogoRequest(string? DocumentId);

    /// <summary>
    /// Calls PUT /documents/{documentId}/logo-registration on the Documents service
    /// so that IsPublishedAsLogo is set to true on the document. This allows
    /// /public/logo/{id} to serve the logo without authentication.
    ///
    /// Forwarded headers:
    ///   Authorization        — the admin caller's JWT
    ///   X-Admin-Target-Tenant — the target tenant ID so Documents resolves the
    ///                           correct tenant scope (platform-admin bypass path)
    /// Non-fatal: a failure here is logged as a warning and does not roll back
    /// the Identity logo assignment.
    /// </summary>
    private static async Task RegisterLogoInDocumentsAsync(
        IHttpClientFactory httpClientFactory,
        HttpContext        httpContext,
        Guid               documentId,
        Guid               tenantId,
        CancellationToken  ct)
    {
        try
        {
            var authHeader = httpContext.Request.Headers["Authorization"].FirstOrDefault();
            var client     = httpClientFactory.CreateClient("DocumentsInternal");

            using var req = new HttpRequestMessage(HttpMethod.Put, $"/documents/{documentId}/logo-registration");
            if (!string.IsNullOrEmpty(authHeader))
                req.Headers.TryAddWithoutValidation("Authorization", authHeader);
            req.Headers.TryAddWithoutValidation("X-Admin-Target-Tenant", tenantId.ToString());

            var res = await client.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                var body = await res.Content.ReadAsStringAsync(ct);
                Console.Error.WriteLine(
                    $"[Identity] RegisterLogoInDocumentsAsync: Documents returned {(int)res.StatusCode} for document {documentId} / tenant {tenantId}. Body: {body}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[Identity] RegisterLogoInDocumentsAsync: non-fatal error for document {documentId} / tenant {tenantId}: {ex.Message}");
        }
    }

    /// <summary>
    /// PATCH /api/admin/tenants/{id}/logo
    /// Sets the tenant's logo by storing the document ID of an already-uploaded image.
    /// The caller is responsible for uploading the image to the Documents service first.
    /// </summary>
    private static async Task<IResult> SetTenantLogo(
        Guid                id,
        SetLogoRequest      body,
        ClaimsPrincipal     caller,
        HttpContext         httpContext,
        IHttpClientFactory  httpClientFactory,
        IdentityDbContext   db,
        IAuditEventClient   auditClient,
        ITenantSyncAdapter  syncAdapter,
        CancellationToken   ct)
    {
        // TENANT-B10: This endpoint is deprecated. The authoritative write path
        // is now PATCH /tenant/api/v1/admin/tenants/{id}/logo (Tenant service).
        httpContext.Response.Headers.Append("X-Deprecated",    "true");
        httpContext.Response.Headers.Append("X-Deprecated-By", "TENANT-B10");
        Console.Error.WriteLine($"[Identity][DEPRECATED] PATCH /api/admin/tenants/{id}/logo called. Use Tenant service endpoint. (TENANT-B10)");

        if (string.IsNullOrWhiteSpace(body.DocumentId))
            return Results.BadRequest(new { error = "documentId is required." });

        if (!Guid.TryParse(body.DocumentId, out var documentId))
            return Results.BadRequest(new { error = "documentId must be a valid UUID." });

        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null) return Results.NotFound();

        tenant.SetLogo(documentId);
        await db.SaveChangesAsync(ct);

        // Register the document as the published logo in the Documents service so that
        // /public/logo/{id} (which requires IsPublishedAsLogo=true) can serve it.
        await RegisterLogoInDocumentsAsync(httpClientFactory, httpContext, documentId, tenant.Id, ct);

        // ── TENANT-B07: Dual-write logo update to Tenant service ──────────────
        _ = syncAdapter.SyncAsync(new IdentityTenantSyncRequest(
            TenantId:            tenant.Id,
            Code:                tenant.Code,
            DisplayName:         tenant.Name,
            Status:              tenant.IsActive ? "Active" : "Inactive",
            Subdomain:           tenant.Subdomain,
            LogoDocumentId:      tenant.LogoDocumentId,
            LogoWhiteDocumentId: tenant.LogoWhiteDocumentId,
            SourceCreatedAtUtc:  tenant.CreatedAtUtc,
            SourceUpdatedAtUtc:  tenant.UpdatedAtUtc,
            EventType:           "Update"));

        var callerId     = caller.FindFirstValue(ClaimTypes.NameIdentifier) ?? caller.FindFirstValue("sub");
        var callerEmail  = caller.FindFirstValue(ClaimTypes.Email) ?? caller.FindFirstValue("email");
        var callerTenant = caller.FindFirstValue("tenant_id");
        var now          = DateTimeOffset.UtcNow;

        _ = auditClient.IngestAsync(new IngestAuditEventRequest
        {
            EventType     = "identity.tenant.logo_set",
            EventCategory = EventCategory.Administrative,
            SourceSystem  = "identity-service",
            SourceService = "admin-api",
            Visibility    = VisibilityScope.Platform,
            Severity      = SeverityLevel.Info,
            OccurredAtUtc = now,
            Scope = new AuditEventScopeDto
            {
                ScopeType = ScopeType.Tenant,
                TenantId  = id.ToString(),
            },
            Actor = new AuditEventActorDto
            {
                Id   = callerId,
                Type = ActorType.User,
                Name = callerEmail,
            },
            Entity      = new AuditEventEntityDto { Type = "Tenant", Id = id.ToString() },
            Action      = "LogoSet",
            Description = $"Admin set logo for tenant {id} (document {documentId}).",
            Metadata    = JsonSerializer.Serialize(new { tenantId = id, documentId, callerTenant }),
            IdempotencyKey = IdempotencyKey.ForWithTimestamp(now, "identity-service", "identity.tenant.logo_set", id.ToString()),
            Tags = ["tenant", "logo", "branding"],
        });

        return Results.Ok(new { tenantId = tenant.Id, logoDocumentId = documentId, updatedAtUtc = tenant.UpdatedAtUtc });
    }

    /// <summary>
    /// DELETE /api/admin/tenants/{id}/logo
    /// Clears the tenant's logo, reverting to the platform default (LegalSynq) branding.
    /// </summary>
    private static async Task<IResult> ClearTenantLogo(
        Guid                id,
        ClaimsPrincipal     caller,
        HttpContext         httpContext,
        IdentityDbContext   db,
        IAuditEventClient   auditClient,
        ITenantSyncAdapter  syncAdapter,
        CancellationToken   ct)
    {
        // TENANT-B10: This endpoint is deprecated. The authoritative write path
        // is now DELETE /tenant/api/v1/admin/tenants/{id}/logo (Tenant service).
        httpContext.Response.Headers.Append("X-Deprecated",    "true");
        httpContext.Response.Headers.Append("X-Deprecated-By", "TENANT-B10");
        Console.Error.WriteLine($"[Identity][DEPRECATED] DELETE /api/admin/tenants/{id}/logo called. Use Tenant service endpoint. (TENANT-B10)");

        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null) return Results.NotFound();

        tenant.ClearLogo();
        await db.SaveChangesAsync(ct);

        // ── TENANT-B11: Sync logo-clear to Tenant service (fixes B10 gap) ────
        _ = syncAdapter.SyncAsync(new IdentityTenantSyncRequest(
            TenantId:            tenant.Id,
            Code:                tenant.Code,
            DisplayName:         tenant.Name,
            Status:              tenant.IsActive ? "Active" : "Inactive",
            Subdomain:           tenant.Subdomain,
            LogoDocumentId:      null,
            LogoWhiteDocumentId: tenant.LogoWhiteDocumentId,
            SourceCreatedAtUtc:  tenant.CreatedAtUtc,
            SourceUpdatedAtUtc:  tenant.UpdatedAtUtc,
            EventType:           "Update"));

        var callerId     = caller.FindFirstValue(ClaimTypes.NameIdentifier) ?? caller.FindFirstValue("sub");
        var callerEmail  = caller.FindFirstValue(ClaimTypes.Email) ?? caller.FindFirstValue("email");
        var now          = DateTimeOffset.UtcNow;

        _ = auditClient.IngestAsync(new IngestAuditEventRequest
        {
            EventType     = "identity.tenant.logo_cleared",
            EventCategory = EventCategory.Administrative,
            SourceSystem  = "identity-service",
            SourceService = "admin-api",
            Visibility    = VisibilityScope.Platform,
            Severity      = SeverityLevel.Info,
            OccurredAtUtc = now,
            Scope = new AuditEventScopeDto
            {
                ScopeType = ScopeType.Tenant,
                TenantId  = id.ToString(),
            },
            Actor = new AuditEventActorDto
            {
                Id   = callerId,
                Type = ActorType.User,
                Name = callerEmail,
            },
            Entity      = new AuditEventEntityDto { Type = "Tenant", Id = id.ToString() },
            Action      = "LogoCleared",
            Description = $"Admin cleared logo for tenant {id}.",
            IdempotencyKey = IdempotencyKey.ForWithTimestamp(now, "identity-service", "identity.tenant.logo_cleared", id.ToString()),
            Tags = ["tenant", "logo", "branding"],
        });

        return Results.NoContent();
    }

    private static async Task<IResult> SetTenantLogoWhite(
        Guid                id,
        SetLogoRequest      body,
        ClaimsPrincipal     caller,
        HttpContext         httpContext,
        IHttpClientFactory  httpClientFactory,
        IdentityDbContext   db,
        IAuditEventClient   auditClient,
        ITenantSyncAdapter  syncAdapter,
        CancellationToken   ct)
    {
        // TENANT-B10: This endpoint is deprecated. The authoritative write path
        // is now PATCH /tenant/api/v1/admin/tenants/{id}/logo-white (Tenant service).
        httpContext.Response.Headers.Append("X-Deprecated",    "true");
        httpContext.Response.Headers.Append("X-Deprecated-By", "TENANT-B10");
        Console.Error.WriteLine($"[Identity][DEPRECATED] PATCH /api/admin/tenants/{id}/logo-white called. Use Tenant service endpoint. (TENANT-B10)");

        if (string.IsNullOrWhiteSpace(body.DocumentId))
            return Results.BadRequest(new { error = "documentId is required." });

        if (!Guid.TryParse(body.DocumentId, out var documentId))
            return Results.BadRequest(new { error = "documentId must be a valid UUID." });

        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null) return Results.NotFound();

        tenant.SetLogoWhite(documentId);
        await db.SaveChangesAsync(ct);

        // Register the white logo document as published so /public/logo/{id} can serve it.
        await RegisterLogoInDocumentsAsync(httpClientFactory, httpContext, documentId, tenant.Id, ct);

        // ── TENANT-B07: Dual-write logo-white update to Tenant service ─────────
        _ = syncAdapter.SyncAsync(new IdentityTenantSyncRequest(
            TenantId:            tenant.Id,
            Code:                tenant.Code,
            DisplayName:         tenant.Name,
            Status:              tenant.IsActive ? "Active" : "Inactive",
            Subdomain:           tenant.Subdomain,
            LogoDocumentId:      tenant.LogoDocumentId,
            LogoWhiteDocumentId: tenant.LogoWhiteDocumentId,
            SourceCreatedAtUtc:  tenant.CreatedAtUtc,
            SourceUpdatedAtUtc:  tenant.UpdatedAtUtc,
            EventType:           "Update"));

        var callerId     = caller.FindFirstValue(ClaimTypes.NameIdentifier) ?? caller.FindFirstValue("sub");
        var callerEmail  = caller.FindFirstValue(ClaimTypes.Email) ?? caller.FindFirstValue("email");
        var now          = DateTimeOffset.UtcNow;

        _ = auditClient.IngestAsync(new IngestAuditEventRequest
        {
            EventType     = "identity.tenant.logo_white_set",
            EventCategory = EventCategory.Administrative,
            SourceSystem  = "identity-service",
            SourceService = "admin-api",
            Visibility    = VisibilityScope.Platform,
            Severity      = SeverityLevel.Info,
            OccurredAtUtc = now,
            Scope = new AuditEventScopeDto
            {
                ScopeType = ScopeType.Tenant,
                TenantId  = id.ToString(),
            },
            Actor = new AuditEventActorDto
            {
                Id   = callerId,
                Type = ActorType.User,
                Name = callerEmail,
            },
            Entity      = new AuditEventEntityDto { Type = "Tenant", Id = id.ToString() },
            Action      = "LogoWhiteSet",
            Description = $"Admin set white/reversed logo for tenant {id} (document {documentId}).",
            Metadata    = JsonSerializer.Serialize(new { tenantId = id, documentId }),
            IdempotencyKey = IdempotencyKey.ForWithTimestamp(now, "identity-service", "identity.tenant.logo_white_set", id.ToString()),
            Tags = ["tenant", "logo", "branding"],
        });

        return Results.Ok(new { logoWhiteDocumentId = documentId });
    }

    private static async Task<IResult> ClearTenantLogoWhite(
        Guid                id,
        ClaimsPrincipal     caller,
        HttpContext         httpContext,
        IdentityDbContext   db,
        IAuditEventClient   auditClient,
        ITenantSyncAdapter  syncAdapter,
        CancellationToken   ct)
    {
        // TENANT-B10: This endpoint is deprecated. The authoritative write path
        // is now DELETE /tenant/api/v1/admin/tenants/{id}/logo-white (Tenant service).
        httpContext.Response.Headers.Append("X-Deprecated",    "true");
        httpContext.Response.Headers.Append("X-Deprecated-By", "TENANT-B10");
        Console.Error.WriteLine($"[Identity][DEPRECATED] DELETE /api/admin/tenants/{id}/logo-white called. Use Tenant service endpoint. (TENANT-B10)");

        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null) return Results.NotFound();

        tenant.ClearLogoWhite();
        await db.SaveChangesAsync(ct);

        // ── TENANT-B11: Sync logo-white-clear to Tenant service (fixes B10 gap) ──
        _ = syncAdapter.SyncAsync(new IdentityTenantSyncRequest(
            TenantId:            tenant.Id,
            Code:                tenant.Code,
            DisplayName:         tenant.Name,
            Status:              tenant.IsActive ? "Active" : "Inactive",
            Subdomain:           tenant.Subdomain,
            LogoDocumentId:      tenant.LogoDocumentId,
            LogoWhiteDocumentId: null,
            SourceCreatedAtUtc:  tenant.CreatedAtUtc,
            SourceUpdatedAtUtc:  tenant.UpdatedAtUtc,
            EventType:           "Update"));

        var callerId     = caller.FindFirstValue(ClaimTypes.NameIdentifier) ?? caller.FindFirstValue("sub");
        var callerEmail  = caller.FindFirstValue(ClaimTypes.Email) ?? caller.FindFirstValue("email");
        var now          = DateTimeOffset.UtcNow;

        _ = auditClient.IngestAsync(new IngestAuditEventRequest
        {
            EventType     = "identity.tenant.logo_white_cleared",
            EventCategory = EventCategory.Administrative,
            SourceSystem  = "identity-service",
            SourceService = "admin-api",
            Visibility    = VisibilityScope.Platform,
            Severity      = SeverityLevel.Info,
            OccurredAtUtc = now,
            Scope = new AuditEventScopeDto
            {
                ScopeType = ScopeType.Tenant,
                TenantId  = id.ToString(),
            },
            Actor = new AuditEventActorDto
            {
                Id   = callerId,
                Type = ActorType.User,
                Name = callerEmail,
            },
            Entity      = new AuditEventEntityDto { Type = "Tenant", Id = id.ToString() },
            Action      = "LogoWhiteCleared",
            Description = $"Admin cleared white/reversed logo for tenant {id}.",
            IdempotencyKey = IdempotencyKey.ForWithTimestamp(now, "identity-service", "identity.tenant.logo_white_cleared", id.ToString()),
            Tags = ["tenant", "logo", "branding"],
        });

        return Results.NoContent();
    }

    // Maps the frontend ProductCode (TypeScript) → the DB product Code column.
    // Keeps the two representations decoupled without touching the DB schema.
    private static readonly Dictionary<string, string> FrontendToDbProductCode = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SynqFund"]      = "SYNQ_FUND",
        ["SynqLien"]      = "SYNQ_LIENS",
        ["CareConnect"]   = "SYNQ_CARECONNECT",
        ["Xenia"]         = "SYNQ_AI",
        ["SynqAI"]        = "SYNQ_AI",
        ["SynqInsights"]  = "SYNQ_INSIGHTS",
    };

    // Maps the DB product Code column → the frontend ProductCode (TypeScript).
    private static readonly Dictionary<string, string> DbToFrontendProductCode = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SYNQ_FUND"]        = "SynqFund",
        ["SYNQ_LIENS"]       = "SynqLien",
        ["SYNQ_CARECONNECT"] = "CareConnect",
        ["SYNQ_AI"]          = "Xenia",
        ["XENIA"]           = "Xenia",
        ["SYNQ_INSIGHTS"]    = "SynqInsights",
    };

    private static IEnumerable<string> GetDbProductCodeCandidates(string productCode)
    {
        if (string.Equals(productCode, "Xenia", StringComparison.OrdinalIgnoreCase)
            || string.Equals(productCode, "SynqAI", StringComparison.OrdinalIgnoreCase)
            || string.Equals(productCode, "SYNQ_AI", StringComparison.OrdinalIgnoreCase)
            || string.Equals(productCode, "XENIA", StringComparison.OrdinalIgnoreCase))
        {
            yield return "SYNQ_AI";
            yield return "XENIA";
            yield break;
        }

        if (FrontendToDbProductCode.TryGetValue(productCode, out var mapped))
        {
            yield return mapped;
            yield break;
        }

        yield return productCode.Trim().ToUpperInvariant();
    }

    private static async Task<string?> ResolveExistingDbProductCodeAsync(
        string productCode,
        IdentityDbContext db,
        CancellationToken ct = default,
        bool requireActive = false)
    {
        foreach (var candidate in GetDbProductCodeCandidates(productCode))
        {
            var exists = requireActive
                ? await db.Products.AnyAsync(p => p.Code == candidate && p.IsActive, ct)
                : await db.Products.AnyAsync(p => p.Code == candidate, ct);
            if (exists) return candidate;
        }

        return null;
    }

    private static async Task<IResult> UpdateEntitlement(
        Guid   id,
        string productCode,
        IdentityDbContext db,
        EntitlementRequest body,
        IAuditEventClient auditClient,
        IProductProvisioningService provisioningEngine,
        CancellationToken ct)
    {
        var tenantExists = await db.Tenants.AnyAsync(t => t.Id == id);
        if (!tenantExists) return Results.NotFound();

        var dbCode = await ResolveExistingDbProductCodeAsync(productCode, db, ct);
        if (dbCode is null)
            return Results.NotFound(new { error = $"Product '{productCode}' not found." });

        var result = await provisioningEngine.ProvisionAsync(
            new ProvisionProductRequest(id, dbCode, body.Enabled));

        var auditNow = DateTimeOffset.UtcNow;
        _ = auditClient.IngestAsync(new IngestAuditEventRequest
        {
            EventType     = "platform.admin.tenant.entitlement.updated",
            EventCategory = EventCategory.Administrative,
            SourceSystem  = "identity-service",
            SourceService = "admin-api",
            Visibility    = VisibilityScope.Platform,
            Severity      = SeverityLevel.Warn,
            OccurredAtUtc = auditNow,
            Scope         = new AuditEventScopeDto { ScopeType = ScopeType.Tenant, TenantId = id.ToString() },
            Actor         = new AuditEventActorDto { Type = ActorType.System },
            Entity        = new AuditEventEntityDto { Type = "Tenant", Id = id.ToString() },
            Action        = "EntitlementUpdated",
            Description   = $"Product entitlement '{productCode}' {(body.Enabled ? "enabled" : "disabled")} for tenant {id}.",
            After         = JsonSerializer.Serialize(new { productCode, enabled = body.Enabled }),
            IdempotencyKey = IdempotencyKey.ForWithTimestamp(auditNow, "identity-service", "platform.admin.tenant.entitlement.updated", id.ToString(), productCode),
            Tags = ["entitlement", "tenant-admin"],
        });

        return Results.Ok(new
        {
            tenantId    = id,
            productCode,
            enabled     = body.Enabled,
            status      = body.Enabled ? "Active" : "Disabled",
            provisioningResult = new
            {
                tenantProductCreated       = result.TenantProductCreated,
                organizationProductsCreated = result.OrganizationProductsCreated,
                organizationProductsUpdated = result.OrganizationProductsUpdated,
                handlerExecuted            = result.HandlerResult is not null,
            },
        });
    }

    // =========================================================================
    // USERS
    // =========================================================================

    private static async Task<IResult> ListUsers(
        IdentityDbContext db,
        ClaimsPrincipal   caller,
        int    page     = 1,
        int    pageSize = 20,
        string search   = "",
        string tenantId = "",
        string status   = "",
        string userType = "",
        string isActive = "")
    {
        var q = db.Users.AsQueryable();

        // ── Tenant scoping: TenantAdmin is always restricted to their own tenant ──
        // PlatformAdmin may pass an explicit tenantId filter or see all.
        var callerTenantId = caller.FindFirstValue("tenant_id");
        var isPlatformAdmin = caller.IsInRole("PlatformAdmin");

        if (!isPlatformAdmin && callerTenantId is not null && Guid.TryParse(callerTenantId, out var callerTid))
        {
            // TenantAdmin: always scope to own tenant — ignore any tenantId param
            q = q.Where(u => db.UserTenants.Any(ut => ut.UserId == u.Id && ut.TenantId == callerTid && ut.IsActive));
        }
        else if (!string.IsNullOrWhiteSpace(tenantId) && Guid.TryParse(tenantId, out var tid))
        {
            // PlatformAdmin with explicit tenant filter
            q = q.Where(u => db.UserTenants.Any(ut => ut.UserId == u.Id && ut.TenantId == tid && ut.IsActive));
        }

        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(u =>
                u.Email.Contains(search) ||
                u.FirstName.Contains(search) ||
                u.LastName.Contains(search));

        // ── Status filter ──────────────────────────────────────────────────────
        var statusNorm = status.ToLowerInvariant().Trim();
        if (statusNorm == "active")
            q = q.Where(u => u.IsActive);
        else if (statusNorm == "inactive")
            q = q.Where(u => !u.IsActive &&
                !db.UserInvitations.Any(i => i.UserId == u.Id && i.Status == UserInvitation.Statuses.Pending));
        else if (statusNorm == "invited")
            q = q.Where(u => !u.IsActive &&
                db.UserInvitations.Any(i => i.UserId == u.Id && i.Status == UserInvitation.Statuses.Pending));

        // ── PUM-B01: isActive filter (boolean — distinct from the status string filter) ──
        var isActiveTrimmed = isActive.Trim().ToLowerInvariant();
        if (isActiveTrimmed == "true")
            q = q.Where(u => u.IsActive);
        else if (isActiveTrimmed == "false")
            q = q.Where(u => !u.IsActive);

        // ── PUM-B01: userType filter ───────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(userType) &&
            Enum.TryParse<Identity.Domain.UserType>(userType, ignoreCase: true, out var parsedUserType))
        {
            q = q.Where(u => u.UserType == parsedUserType);
        }

        var total = await q.CountAsync();

        // Step 6 Phase B: role resolved via ScopedRoleAssignments (GLOBAL-scoped, primary).
        // Correlated subquery; EF Core translates to a single SQL query.
        var users = await q
            .OrderBy(u => u.Email)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(u => new
            {
                id         = u.Id,
                firstName  = u.FirstName,
                lastName   = u.LastName,
                email      = u.Email,
                userType   = u.UserType.ToString(),
                role       = db.ScopedRoleAssignments
                               .Where(s => s.UserId == u.Id && s.IsActive
                                        && s.ScopeType == ScopedRoleAssignment.ScopeTypes.Global)
                               .Select(s => s.Role!.Name)
                               .FirstOrDefault() ?? "User",
                status     = u.IsActive
                    ? "Active"
                    : db.UserInvitations.Any(i => i.UserId == u.Id && i.Status == UserInvitation.Statuses.Pending)
                        ? "Invited"
                        : "Inactive",
                isActive   = u.IsActive,
                primaryOrg = db.UserOrganizationMemberships
                               .Where(m => m.UserId == u.Id && m.IsPrimary && m.IsActive)
                               .Select(m => m.Organization.DisplayName ?? m.Organization.Name)
                               .FirstOrDefault(),
                groupCount = db.AccessGroupMemberships.Count(am => am.UserId == u.Id && am.MembershipStatus == MembershipStatus.Active),
                tenantId   = db.UserTenants
                               .Where(ut => ut.UserId == u.Id && ut.IsActive)
                               .OrderBy(ut => ut.JoinedAtUtc)
                               .Select(ut => (Guid?)ut.TenantId)
                               .FirstOrDefault(),
                tenantCode = db.UserTenants
                               .Where(ut => ut.UserId == u.Id && ut.IsActive)
                               .OrderBy(ut => ut.JoinedAtUtc)
                               .Select(ut => ut.Tenant.Code)
                               .FirstOrDefault(),
                createdAtUtc = u.CreatedAtUtc,
            })
            .ToListAsync();

        return Results.Ok(new
        {
            items      = users,
            totalCount = total,
            page,
            pageSize,
        });
    }

    /// <summary>
    /// Resolve role- or organization-addressed recipients to concrete users.
    /// Used by the notifications service when fanning out a Role/Org envelope.
    ///
    /// Filters (all combinations supported except "neither"):
    ///   tenantId  — required, scopes the lookup.
    ///   roleKey   — match against Role.Name (case-insensitive) via active GLOBAL
    ///               ScopedRoleAssignments.
    ///   orgId     — restrict to users with an active UserOrganizationMembership
    ///               in that organization.
    ///
    /// Returns: { items: [{ userId, email, organizationId? }] }
    /// </summary>
    private static async Task<IResult> MembershipLookup(
        IdentityDbContext db,
        ClaimsPrincipal   caller,
        string            tenantId  = "",
        string            roleKey   = "",
        string            orgId     = "",
        CancellationToken ct        = default)
    {
        if (!Guid.TryParse(tenantId, out var tid))
            return Results.BadRequest(new { error = "tenantId is required and must be a GUID." });

        Guid? oid = null;
        if (!string.IsNullOrWhiteSpace(orgId))
        {
            if (!Guid.TryParse(orgId, out var parsedOid))
                return Results.BadRequest(new { error = "orgId must be a GUID." });
            oid = parsedOid;
        }

        var roleKeyTrimmed = roleKey?.Trim();
        var hasRoleFilter  = !string.IsNullOrWhiteSpace(roleKeyTrimmed);
        var hasOrgFilter   = oid.HasValue;

        if (!hasRoleFilter && !hasOrgFilter)
            return Results.BadRequest(new { error = "Provide at least one of roleKey or orgId." });

        // Tenant scope: TenantAdmins may only resolve within their own tenant.
        // PlatformAdmins (and unauthenticated internal callers — gateway-trusted)
        // may resolve any tenant. Service-to-service callers (no claims) are allowed
        // because gateway/network policy fronts this endpoint.
        var callerTenantId = caller.FindFirstValue("tenant_id");
        if (callerTenantId is not null && Guid.TryParse(callerTenantId, out var callerTid))
        {
            var isPlatformAdmin = caller.IsInRole("PlatformAdmin");
            if (!isPlatformAdmin && callerTid != tid)
                return Results.Forbid();
        }

        var q = db.Users.AsNoTracking()
            .Where(u => db.UserTenants.Any(ut => ut.UserId == u.Id && ut.TenantId == tid && ut.IsActive) && u.IsActive);

        if (hasRoleFilter)
        {
            // Explicit lower-case compare so case-insensitivity is guaranteed
            // regardless of column collation (MySQL default _ci is permissive,
            // but other deployments / future migrations may differ).
            var roleKeyLower = roleKeyTrimmed!.ToLowerInvariant();
            q = q.Where(u => db.ScopedRoleAssignments.Any(s =>
                s.UserId    == u.Id    &&
                s.IsActive             &&
                s.ScopeType == ScopedRoleAssignment.ScopeTypes.Global &&
                s.Role.Name.ToLower() == roleKeyLower));
        }

        if (hasOrgFilter)
        {
            q = q.Where(u => db.UserOrganizationMemberships.Any(m =>
                m.UserId         == u.Id     &&
                m.IsActive                   &&
                m.OrganizationId == oid!.Value));
        }

        var items = await q
            .OrderBy(u => u.Email)
            .Select(u => new
            {
                userId         = u.Id,
                email          = u.Email,
                phone          = u.Phone,
                organizationId = oid,
            })
            .ToListAsync(ct);

        return Results.Ok(new { items, totalCount = items.Count });
    }

    private static async Task<IResult> GetUser(
        Guid              id,
        ClaimsPrincipal   caller,
        IdentityDbContext db,
        CancellationToken ct)
    {
        var isPlatformAdmin = caller.IsInRole("PlatformAdmin");
        var callerTenantId  = caller.FindFirstValue("tenant_id");

        var u = await db.Users
            .Include(u => u.ScopedRoleAssignments.Where(s => s.IsActive && s.ScopeType == ScopedRoleAssignment.ScopeTypes.Global))
                .ThenInclude(s => s.Role)
            .Include(u => u.OrganizationMemberships.Where(m => m.IsActive))
                .ThenInclude(m => m.Organization)
            .FirstOrDefaultAsync(u => u.Id == id, ct);

        if (u is null) return Results.NotFound();

        // Non-PlatformAdmins may only view users within their own tenant.
        if (!isPlatformAdmin)
        {
            if (callerTenantId is null || !Guid.TryParse(callerTenantId, out var callerTid) ||
                !await db.UserTenants.AnyAsync(ut => ut.UserId == u.Id && ut.TenantId == callerTid && ut.IsActive, ct))
                return Results.Forbid();
        }

        var hasPendingInvite = await db.UserInvitations.AnyAsync(
            i => i.UserId == id && i.Status == UserInvitation.Statuses.Pending, ct);

        var groupMemberships = await (
            from am in db.AccessGroupMemberships
            join ag in db.AccessGroups on am.GroupId equals ag.Id
            where am.UserId == id && am.MembershipStatus == MembershipStatus.Active
            select new { groupId = am.GroupId, groupName = ag.Name, joinedAtUtc = am.AddedAtUtc }
        ).ToListAsync(ct);

        var status = u.IsActive ? "Active" : (hasPendingInvite ? "Invited" : "Inactive");

        var userOpTenantId = await GetOperationalTenantIdAsync(caller, u.Id, db, ct);
        var userTenant = await GetTenantSummaryAsync(db, userOpTenantId, ct);

        return Results.Ok(new
        {
            id                = u.Id,
            firstName         = u.FirstName,
            lastName          = u.LastName,
            email             = u.Email,
            userType          = u.UserType.ToString(),
            role              = u.ScopedRoleAssignments.Select(s => s.Role.Name).FirstOrDefault() ?? "User",
            roles             = u.ScopedRoleAssignments.Select(s => new { roleId = s.RoleId, roleName = s.Role.Name, assignmentId = s.Id }),
            status,
            isActive          = u.IsActive,
            tenantId          = userOpTenantId,
            tenantCode        = userTenant?.Code,
            tenantDisplayName = userTenant?.Name,
            createdAtUtc      = u.CreatedAtUtc,
            updatedAtUtc      = u.UpdatedAtUtc,
            lastLoginAtUtc    = u.LastLoginAtUtc,
            isLocked          = u.IsLocked,
            lockedAtUtc       = u.LockedAtUtc,
            sessionVersion    = u.SessionVersion,
            avatarDocumentId  = u.AvatarDocumentId,
            phone             = u.Phone,
            memberships = u.OrganizationMemberships.Select(m => new
            {
                membershipId   = m.Id,
                organizationId = m.OrganizationId,
                orgName        = m.Organization.DisplayName ?? m.Organization.Name,
                memberRole     = m.MemberRole,
                isPrimary      = m.IsPrimary,
                joinedAtUtc    = m.JoinedAtUtc,
            }),
            groups            = groupMemberships,
            groupCount        = groupMemberships.Count,
        });
    }

    // =========================================================================
    // USER LIFECYCLE
    // =========================================================================

    /// <summary>
    /// PATCH /api/admin/users/{id}/deactivate
    ///
    /// Sets the user's IsActive flag to false and emits the canonical
    /// identity.user.deactivated audit event (HIPAA-required lifecycle record).
    ///
    /// Idempotent: if the user is already inactive, returns 204 without re-emitting.
    /// Returns 404 if the user does not exist.
    /// </summary>
    private static async Task<IResult> DeactivateUser(
        Guid                      id,
        ClaimsPrincipal           caller,
        IdentityDbContext         db,
        IAuditEventClient         auditClient,
        IDeviceSessionService     deviceSessionService,
        INotificationsCacheClient notificationsCache,
        CancellationToken         ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);

        if (user is null) return Results.NotFound();

        // UIX-003-01: TenantAdmin may only deactivate users within their own tenant.
        if (await IsCrossTenantAccessAsync(caller, user.Id, db, ct)) return Results.Forbid();

        var opTenantId = await GetOperationalTenantIdAsync(caller, user.Id, db, ct);

        // LS-ID-TNT-005: Backend last-admin protection.
        // Block deactivation if the target user is the only remaining active TenantAdmin
        // for this tenant.  This guard applies to all callers (tenant UI, platform admin
        // tools, direct API calls, self-targeted actions) — not just the frontend path.
        var isTenantAdmin = await db.ScopedRoleAssignments
            .AnyAsync(s => s.IsActive
                        && s.UserId == user.Id
                        && s.ScopeType == ScopedRoleAssignment.ScopeTypes.Global
                        && s.Role!.Name == "TenantAdmin", ct);
        if (isTenantAdmin)
        {
            var otherActiveAdmins = await CountOtherActiveTenantAdmins(db, user.Id, opTenantId, ct);
            if (otherActiveAdmins == 0)
                return Results.UnprocessableEntity(new
                {
                    error = "This action is not allowed because the user is the last active tenant administrator.",
                    code  = "LAST_ACTIVE_ADMIN",
                });
        }

        // Deactivate() is idempotent — returns false if already inactive.
        var changed = user.Deactivate();
        if (!changed) return Results.NoContent();

        await db.SaveChangesAsync(ct);
        await deviceSessionService.RevokeAllForUserAsync(user.Id, "AccountDeactivated", ct);

        // Canonical audit: identity.user.deactivated — fire-and-observe.
        var now = DateTimeOffset.UtcNow;
        _ = auditClient.IngestAsync(new IngestAuditEventRequest
        {
            EventType     = "identity.user.deactivated",
            EventCategory = EventCategory.Administrative,
            SourceSystem  = "identity-service",
            SourceService = "admin-api",
            Visibility    = VisibilityScope.Tenant,
            Severity      = SeverityLevel.Warn,
            OccurredAtUtc = now,
            Scope = new AuditEventScopeDto
            {
                ScopeType = ScopeType.Tenant,
                TenantId  = opTenantId.ToString(),
            },
            Actor = new AuditEventActorDto
            {
                Type = ActorType.System,
                Name = "admin-api",
            },
            Entity = new AuditEventEntityDto { Type = "User", Id = user.Id.ToString() },
            Action      = "UserDeactivated",
            Description = $"User '{user.Email}' deactivated in tenant {opTenantId}.",
            Before      = JsonSerializer.Serialize(new { isActive = true,  email = user.Email }),
            After       = JsonSerializer.Serialize(new { isActive = false, email = user.Email }),
            IdempotencyKey = IdempotencyKey.For("identity-service", "identity.user.deactivated", user.Id.ToString()),
            Tags = ["user-management", "lifecycle", "deactivation"],
        });

        // Membership state changed for this tenant — drop the notifications
        // service's cached role/org member lists so role-addressed alerts
        // immediately stop including the deactivated user.
        notificationsCache.InvalidateTenant(
            opTenantId,
            eventType: "identity.user.deactivated",
            reason:    $"user {user.Id} deactivated");

        return Results.NoContent();
    }

    /// <summary>
    /// PATCH /api/admin/users/{id}/phone
    ///
    /// Lets a tenant admin set or clear the user's primary phone number.
    /// Body: { "phone": "+15551234567" } — pass null/empty to clear.
    /// Phones are normalised to E.164 before persisting.
    /// Returns 200 with { phone } on success (including idempotent no-ops),
    /// 400 on invalid input, 403 cross-tenant, 404 unknown user.
    /// Emits identity.admin.user_phone_updated audit event when the value
    /// actually changes.
    /// </summary>
    private static async Task<IResult> UpdateUserPhone(
        Guid               id,
        UpdatePhoneRequest body,
        ClaimsPrincipal    caller,
        IdentityDbContext  db,
        IAuditEventClient  auditClient,
        CancellationToken  ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return Results.NotFound();
        if (await IsCrossTenantAccessAsync(caller, user.Id, db, ct)) return Results.Forbid();

        var opTenantId = await GetOperationalTenantIdAsync(caller, user.Id, db, ct);
        var (ok, normalised, error) = PhoneNumber.TryNormalise(body.Phone);
        // Return both `message` (consumed by the Control Center api client)
        // and `error` (consumed by the tenant portal's PhoneEditor) so the
        // same upstream payload satisfies both BFFs without translation.
        if (!ok) return Results.BadRequest(new { error, message = error });

        var before  = user.Phone;
        var changed = user.SetPhone(normalised);
        if (!changed) return Results.Ok(new { phone = user.Phone });

        await db.SaveChangesAsync(ct);

        var callerIdStr = caller.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier)
                       ?? caller.FindFirstValue("sub");
        var now = DateTimeOffset.UtcNow;
        _ = auditClient.IngestAsync(new IngestAuditEventRequest
        {
            EventType     = "identity.admin.user_phone_updated",
            EventCategory = EventCategory.Administrative,
            SourceSystem  = "identity-service",
            SourceService = "admin-api",
            Visibility    = VisibilityScope.Tenant,
            Severity      = SeverityLevel.Info,
            OccurredAtUtc = now,
            Scope = new AuditEventScopeDto
            {
                ScopeType = ScopeType.Tenant,
                TenantId  = opTenantId.ToString(),
            },
            Actor = new AuditEventActorDto
            {
                Id   = callerIdStr,
                Type = ActorType.User,
                Name = "admin",
            },
            Entity      = new AuditEventEntityDto { Type = "User", Id = user.Id.ToString() },
            Action      = normalised is null ? "PhoneCleared" : "PhoneUpdated",
            Description = normalised is null
                ? $"Admin cleared phone for user '{user.Email}'."
                : $"Admin updated phone for user '{user.Email}'.",
            Before         = JsonSerializer.Serialize(new { phone = before }),
            After          = JsonSerializer.Serialize(new { phone = normalised }),
            IdempotencyKey = IdempotencyKey.ForWithTimestamp(
                now, "identity-service", "identity.admin.user_phone_updated", user.Id.ToString()),
            Tags = ["user-management", "phone"],
        });

        return Results.Ok(new { phone = user.Phone });
    }

    private record UpdatePhoneRequest(string? Phone);

    // =========================================================================
    // UIX-003-03: SECURITY / SESSION ADMIN ACTIONS
    // =========================================================================

    /// <summary>
    /// POST /api/admin/users/{id}/lock
    ///
    /// Administratively locks a user account. Locked users cannot authenticate.
    /// Also increments SessionVersion, invalidating all active JWTs.
    /// Idempotent: 204 if already locked.
    /// Emits identity.user.locked audit event.
    /// </summary>
    private static async Task<IResult> LockUser(
        Guid                      id,
        ClaimsPrincipal           caller,
        IdentityDbContext         db,
        IAuditEventClient         auditClient,
        IDeviceSessionService     deviceSessionService,
        INotificationsCacheClient notificationsCache,
        CancellationToken         ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);

        if (user is null) return Results.NotFound();
        if (await IsCrossTenantAccessAsync(caller, user.Id, db, ct)) return Results.Forbid();

        var opTenantId = await GetOperationalTenantIdAsync(caller, user.Id, db, ct);
        var callerIdStr = caller.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier)
                       ?? caller.FindFirstValue("sub");
        var lockingAdminId = Guid.TryParse(callerIdStr, out var aid) ? (Guid?)aid : null;

        var changed = user.Lock(lockingAdminId);
        if (!changed) return Results.NoContent();

        await db.SaveChangesAsync(ct);
        await deviceSessionService.RevokeAllForUserAsync(user.Id, "AccountLocked", ct);

        var now = DateTimeOffset.UtcNow;
        _ = auditClient.IngestAsync(new IngestAuditEventRequest
        {
            EventType     = "identity.user.locked",
            EventCategory = EventCategory.Security,
            SourceSystem  = "identity-service",
            SourceService = "admin-api",
            Visibility    = VisibilityScope.Tenant,
            Severity      = SeverityLevel.Warn,
            OccurredAtUtc = now,
            Scope = new AuditEventScopeDto
            {
                ScopeType = ScopeType.Tenant,
                TenantId  = opTenantId.ToString(),
            },
            Actor = new AuditEventActorDto
            {
                Id   = callerIdStr,
                Type = ActorType.User,
                Name = "admin",
            },
            Entity = new AuditEventEntityDto { Type = "User", Id = user.Id.ToString() },
            Action      = "UserLocked",
            Description = $"User '{user.Email}' locked by admin in tenant {opTenantId}.",
            Before      = JsonSerializer.Serialize(new { isLocked = false, email = user.Email }),
            After       = JsonSerializer.Serialize(new { isLocked = true,  email = user.Email, lockedAt = now }),
            IdempotencyKey = IdempotencyKey.For("identity-service", "identity.user.locked", user.Id.ToString()),
            Tags = ["user-management", "security", "lock"],
        });

        // Membership state changed for this tenant — drop the notifications
        // service's cached role/org member lists so role-addressed alerts
        // immediately stop including the locked user.
        notificationsCache.InvalidateTenant(
            opTenantId,
            eventType: "identity.user.locked",
            reason:    $"user {user.Id} locked");

        return Results.NoContent();
    }

    /// <summary>
    /// POST /api/admin/users/{id}/unlock
    ///
    /// Unlocks an administratively locked account.
    /// Idempotent: 204 if already unlocked.
    /// Emits identity.user.unlocked audit event.
    /// </summary>
    private static async Task<IResult> UnlockUser(
        Guid                      id,
        ClaimsPrincipal           caller,
        IdentityDbContext         db,
        IAuditEventClient         auditClient,
        INotificationsCacheClient notificationsCache,
        CancellationToken         ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);

        if (user is null) return Results.NotFound();
        if (await IsCrossTenantAccessAsync(caller, user.Id, db, ct)) return Results.Forbid();

        var opTenantId = await GetOperationalTenantIdAsync(caller, user.Id, db, ct);
        var changed = user.Unlock();
        if (!changed) return Results.NoContent();

        await db.SaveChangesAsync(ct);

        var callerIdStr = caller.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier)
                       ?? caller.FindFirstValue("sub");

        var now = DateTimeOffset.UtcNow;
        _ = auditClient.IngestAsync(new IngestAuditEventRequest
        {
            EventType     = "identity.user.unlocked",
            EventCategory = EventCategory.Security,
            SourceSystem  = "identity-service",
            SourceService = "admin-api",
            Visibility    = VisibilityScope.Tenant,
            Severity      = SeverityLevel.Info,
            OccurredAtUtc = now,
            Scope = new AuditEventScopeDto
            {
                ScopeType = ScopeType.Tenant,
                TenantId  = opTenantId.ToString(),
            },
            Actor = new AuditEventActorDto
            {
                Id   = callerIdStr,
                Type = ActorType.User,
                Name = "admin",
            },
            Entity = new AuditEventEntityDto { Type = "User", Id = user.Id.ToString() },
            Action      = "UserUnlocked",
            Description = $"User '{user.Email}' unlocked by admin in tenant {opTenantId}.",
            Before      = JsonSerializer.Serialize(new { isLocked = true,  email = user.Email }),
            After       = JsonSerializer.Serialize(new { isLocked = false, email = user.Email }),
            IdempotencyKey = IdempotencyKey.For("identity-service", "identity.user.unlocked", user.Id.ToString()),
            Tags = ["user-management", "security", "unlock"],
        });

        // Membership state changed for this tenant — drop the notifications
        // service's cached role/org member lists so role-addressed alerts
        // immediately resume including the unlocked user.
        notificationsCache.InvalidateTenant(
            opTenantId,
            eventType: "identity.user.unlocked",
            reason:    $"user {user.Id} unlocked");

        return Results.NoContent();
    }

    /// <summary>
    /// POST /api/admin/users/{id}/force-logout
    ///
    /// Revokes all active sessions for a user by incrementing their SessionVersion.
    /// All existing JWTs containing an older session_version will be rejected by auth/me.
    /// Emits identity.user.force_logout audit event.
    /// </summary>
    private static async Task<IResult> ForceLogout(
        Guid              id,
        ClaimsPrincipal   caller,
        IdentityDbContext db,
        IAuditEventClient auditClient,
        IDeviceSessionService deviceSessionService,
        CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);

        if (user is null) return Results.NotFound();
        if (await IsCrossTenantAccessAsync(caller, user.Id, db, ct)) return Results.Forbid();

        var opTenantId = await GetOperationalTenantIdAsync(caller, user.Id, db, ct);
        user.IncrementSessionVersion();
        await db.SaveChangesAsync(ct);
        await deviceSessionService.RevokeAllForUserAsync(user.Id, "AdministrativeForceLogout", ct);

        var callerIdStr = caller.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier)
                       ?? caller.FindFirstValue("sub");

        var now = DateTimeOffset.UtcNow;
        _ = auditClient.IngestAsync(new IngestAuditEventRequest
        {
            EventType     = "identity.user.force_logout",
            EventCategory = EventCategory.Security,
            SourceSystem  = "identity-service",
            SourceService = "admin-api",
            Visibility    = VisibilityScope.Tenant,
            Severity      = SeverityLevel.Warn,
            OccurredAtUtc = now,
            Scope = new AuditEventScopeDto
            {
                ScopeType = ScopeType.Tenant,
                TenantId  = opTenantId.ToString(),
            },
            Actor = new AuditEventActorDto
            {
                Id   = callerIdStr,
                Type = ActorType.User,
                Name = "admin",
            },
            Entity = new AuditEventEntityDto { Type = "User", Id = user.Id.ToString() },
            Action      = "ForceLogout",
            Description = $"All sessions revoked for user '{user.Email}' in tenant {opTenantId}.",
            Metadata    = JsonSerializer.Serialize(new { newSessionVersion = user.SessionVersion }),
            IdempotencyKey = IdempotencyKey.ForWithTimestamp(now, "identity-service", "identity.user.force_logout", user.Id.ToString()),
            Tags = ["user-management", "security", "session", "force-logout"],
        });

        return Results.NoContent();
    }

    /// <summary>
    /// POST /api/admin/users/{id}/reset-password
    ///
    /// Admin-triggers a password reset for a user. Creates a PasswordResetToken
    /// (24-hour expiry), then dispatches a reset-link email via the notifications
    /// service when configured (LS-ID-TNT-006). Falls back to the env-gated
    /// resetToken response when notifications is not set up (dev only).
    ///
    /// Any previous pending reset tokens for this user are revoked first (idempotent).
    /// Emits identity.user.password_reset_triggered audit event.
    /// </summary>
    private static async Task<IResult> AdminResetPassword(
        Guid                                  id,
        ClaimsPrincipal                       caller,
        IdentityDbContext                     db,
        IAuditEventClient                     auditClient,
        ILoggerFactory                        loggerFactory,
        IWebHostEnvironment                   env,
        IOptions<NotificationsServiceOptions> notificationsOptions,
        INotificationsEmailClient             notificationsEmail,
        CancellationToken                     ct)
    {
        var logger = loggerFactory.CreateLogger(nameof(AdminEndpoints));

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);

        if (user is null) return Results.NotFound();
        if (await IsCrossTenantAccessAsync(caller, user.Id, db, ct)) return Results.Forbid();

        var opTenantId = await GetOperationalTenantIdAsync(caller, user.Id, db, ct);

        // Revoke any existing pending reset tokens for this user.
        var existingTokens = await db.PasswordResetTokens
            .Where(t => t.UserId == id && t.Status == PasswordResetToken.Statuses.Pending)
            .ToListAsync(ct);
        foreach (var old in existingTokens) old.Revoke();

        // Generate a new cryptographically random reset token.
        var rawToken  = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var tokenHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(rawToken)));

        var callerIdStr = caller.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier)
                       ?? caller.FindFirstValue("sub");
        var triggeredByAdminId = Guid.TryParse(callerIdStr, out var aid) ? (Guid?)aid : null;

        var resetToken = PasswordResetToken.Create(id, opTenantId, tokenHash, triggeredByAdminId);
        db.PasswordResetTokens.Add(resetToken);
        await db.SaveChangesAsync(ct);

        // LS-ID-TNT-005: Log the raw token ONLY in non-production environments.
        // In production we never expose the raw token — email delivery is future work.
        if (!env.IsProduction())
        {
            logger.LogInformation(
                "[LS-ID-TNT-005] Password reset triggered for user {UserId} ({Email}) in tenant {TenantId}. " +
                "Reset token (NON-PRODUCTION ONLY — never expose in production): {RawToken}. " +
                "Token expires at {ExpiresAt:O}.",
                user.Id, user.Email, opTenantId, rawToken, resetToken.ExpiresAtUtc);
        }
        else
        {
            logger.LogInformation(
                "[UIX-003-03] Admin password reset triggered for user {UserId} in tenant {TenantId}. " +
                "Token expires at {ExpiresAt:O}.",
                user.Id, opTenantId, resetToken.ExpiresAtUtc);
        }

        var now = DateTimeOffset.UtcNow;
        _ = auditClient.IngestAsync(new IngestAuditEventRequest
        {
            EventType     = "identity.user.password_reset_triggered",
            EventCategory = EventCategory.Security,
            SourceSystem  = "identity-service",
            SourceService = "admin-api",
            Visibility    = VisibilityScope.Tenant,
            Severity      = SeverityLevel.Info,
            OccurredAtUtc = now,
            Scope = new AuditEventScopeDto
            {
                ScopeType = ScopeType.Tenant,
                TenantId  = opTenantId.ToString(),
            },
            Actor = new AuditEventActorDto
            {
                Id   = callerIdStr,
                Type = ActorType.User,
                Name = "admin",
            },
            Entity = new AuditEventEntityDto { Type = "User", Id = user.Id.ToString() },
            Action      = "PasswordResetTriggered",
            Description = $"Admin-triggered password reset for user '{user.Email}' in tenant {opTenantId}.",
            IdempotencyKey = IdempotencyKey.ForWithTimestamp(now, "identity-service", "identity.user.password_reset_triggered", user.Id.ToString()),
            Tags = ["user-management", "security", "password-reset"],
        });

        // LS-ID-TNT-016-01: Build tenant-subdomain-aware reset link.
        var resetTenant = await db.Tenants.FindAsync([opTenantId], ct);
        var resetLink   = TenantPortalUrlHelper.Build(resetTenant, "reset-password", rawToken, notificationsOptions.Value);
        if (resetLink is not null)
        {
            var displayName = $"{user.FirstName} {user.LastName}".Trim();
            if (string.IsNullOrWhiteSpace(displayName)) displayName = user.Email;

            var (emailConfigured, delivered, deliveryError) =
                await notificationsEmail.SendPasswordResetEmailAsync(user.Email, displayName, resetLink, opTenantId, ct);

            if (emailConfigured)
            {
                if (delivered)
                    return Results.Ok(new { message = $"Password reset email sent to {user.Email}." });

                // Real delivery failure — return 502 so the caller knows not to claim success.
                // The reset token is retained; the admin can retry.
                return Results.Json(
                    new
                    {
                        message = "Failed to deliver the password reset email. Please try again or contact your platform administrator.",
                        error   = deliveryError,
                    },
                    statusCode: 502);
            }
        }

        // Fallback: notifications not configured (BaseUrl or PortalBaseUrl missing).
        // LS-ID-TNT-005: Expose raw token in non-production so admins can complete the flow
        // without needing a working email provider during development.
        if (!env.IsProduction())
        {
            return Results.Ok(new
            {
                message    = "Password reset initiated. Use the reset token below to complete the flow (non-production only).",
                resetToken = rawToken,
            });
        }

        // In production, missing configuration is a hard error — return 503 so the caller
        // knows the email was never dispatched rather than silently claiming success.
        return Results.Json(
            new
            {
                message = "Password reset could not be initiated because the notifications service is not configured. " +
                          "Ensure NotificationsService:BaseUrl and NotificationsService:PortalBaseUrl are set.",
            },
            statusCode: 503);
    }

    /// <summary>
    /// POST /api/admin/users/{id}/set-password
    ///
    /// Admin-sets a new password directly for a user. The password must be at
    /// least 8 characters. The user's session version is bumped so all existing
    /// sessions are invalidated.
    ///
    /// Access: PlatformAdmin only. Tenant-scoped for TenantAdmin callers.
    /// Emits identity.user.password_set_by_admin audit event.
    /// </summary>
    private static async Task<IResult> AdminSetPassword(
        Guid              id,
        SetPasswordRequest body,
        ClaimsPrincipal   caller,
        IdentityDbContext db,
        IPasswordHasher   passwordHasher,
        IAuditEventClient auditClient,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.NewPassword) || body.NewPassword.Length < 8)
            return Results.BadRequest(new { error = "Password must be at least 8 characters." });

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);

        if (user is null) return Results.NotFound();
        if (await IsCrossTenantAccessAsync(caller, user.Id, db, ct)) return Results.Forbid();

        var opTenantId = await GetOperationalTenantIdAsync(caller, user.Id, db, ct);
        var hash = passwordHasher.Hash(body.NewPassword);
        user.SetPassword(hash);

        await db.SaveChangesAsync(ct);

        var now = DateTimeOffset.UtcNow;
        _ = auditClient.IngestAsync(new IngestAuditEventRequest
        {
            EventType     = "identity.user.password_set_by_admin",
            EventCategory = EventCategory.Security,
            SourceSystem  = "identity-service",
            SourceService = "admin-api",
            Visibility    = VisibilityScope.Tenant,
            Severity      = SeverityLevel.Warn,
            OccurredAtUtc = now,
            Scope = new AuditEventScopeDto
            {
                ScopeType = ScopeType.Tenant,
                TenantId  = opTenantId.ToString(),
            },
            Actor = new AuditEventActorDto
            {
                Type = ActorType.User,
                Name = "admin",
            },
            Entity = new AuditEventEntityDto { Type = "User", Id = user.Id.ToString() },
            Action      = "PasswordSetByAdmin",
            Description = $"Admin directly set a new password for user '{user.Email}' in tenant {opTenantId}.",
            IdempotencyKey = IdempotencyKey.ForWithTimestamp(now, "identity-service", "identity.user.password_set_by_admin", user.Id.ToString()),
            Tags = ["user-management", "security", "password-set"],
        });

        return Results.Ok(new { message = "Password updated successfully." });
    }

    /// <summary>
    /// GET /api/admin/users/{id}/security
    ///
    /// Returns a security summary for the user: lock state, last login,
    /// session version, and recent security/admin audit events.
    /// Read-only. Tenant-scoped for TenantAdmin callers.
    /// </summary>
    private static async Task<IResult> GetUserSecurity(
        Guid              id,
        ClaimsPrincipal   caller,
        IdentityDbContext db,
        CancellationToken ct)
    {
        var u = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);

        if (u is null) return Results.NotFound();
        if (await IsCrossTenantAccessAsync(caller, u.Id, db, ct)) return Results.Forbid();

        var hasPendingInvite = await db.UserInvitations
            .AnyAsync(i => i.UserId == id && i.Status == UserInvitation.Statuses.Pending, ct);

        var recentResets = await db.PasswordResetTokens
            .Where(t => t.UserId == id)
            .OrderByDescending(t => t.CreatedAtUtc)
            .Take(5)
            .Select(t => new
            {
                id          = t.Id,
                status      = t.Status,
                createdAt   = t.CreatedAtUtc,
                expiresAt   = t.ExpiresAtUtc,
                usedAt      = t.UsedAtUtc,
            })
            .ToListAsync(ct);

        return Results.Ok(new
        {
            userId           = u.Id,
            email            = u.Email,
            isLocked         = u.IsLocked,
            lockedAtUtc      = u.LockedAtUtc,
            lastLoginAtUtc   = u.LastLoginAtUtc,
            sessionVersion   = u.SessionVersion,
            isActive         = u.IsActive,
            hasPendingInvite,
            recentPasswordResets = recentResets,
            // Recent security/admin events are fetched by the CC frontend via the
            // Audit service query API (/audit/entity/User/{userId}) to avoid coupling
            // the identity service to the audit DB.
        });
    }

    /// <summary>
    /// UIX-004: GET /api/admin/users/{id}/activity
    ///
    /// Returns a paged list of local audit log entries (AuditLogs table) for
    /// the specified user. Covers admin actions emitted by the Identity service:
    /// lock, unlock, force-logout, password reset, role assignment, membership changes.
    ///
    /// For richer canonical events (login, logout, invite-accepted, etc.) the CC
    /// queries the Audit service directly via /audit-service/audit/events?targetId=.
    ///
    /// Scope: TenantAdmin sees only users in their tenant. PlatformAdmin sees all.
    /// </summary>
    private static async Task<IResult> GetUserActivity(
        Guid              id,
        ClaimsPrincipal   caller,
        IdentityDbContext db,
        int    page     = 1,
        int    pageSize = 20,
        string category = "",
        CancellationToken ct = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        page     = Math.Max(page, 1);

        var u = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == id, ct);

        if (u is null) return Results.NotFound();
        if (await IsCrossTenantAccessAsync(caller, u.Id, db, ct)) return Results.Forbid();

        var idStr = id.ToString();
        var q = db.AuditLogs
            .AsNoTracking()
            .Where(a => a.EntityId == idStr);

        if (!string.IsNullOrWhiteSpace(category))
            q = q.Where(a => a.EntityType == category);

        var total = await q.CountAsync(ct);

        // Materialize with raw MetadataJson string first — EF Core cannot translate
        // JsonSerializer.Deserialize (it has optional parameters) inside an expression tree.
        var rawRows = await q
            .OrderByDescending(a => a.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new
            {
                a.Id,
                a.ActorName,
                a.ActorType,
                a.Action,
                a.EntityType,
                a.EntityId,
                a.MetadataJson,
                a.CreatedAtUtc,
            })
            .ToListAsync(ct);

        // Deserialize metadata in-memory after materialization.
        var rows = rawRows.Select(a => new
        {
            id           = a.Id,
            actorName    = a.ActorName,
            actorType    = a.ActorType,
            action       = a.Action,
            entityType   = a.EntityType,
            entityId     = a.EntityId,
            metadata     = a.MetadataJson is not null
                ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(a.MetadataJson)
                : null,
            createdAtUtc = a.CreatedAtUtc,
        }).ToList();

        return Results.Ok(new
        {
            items      = rows,
            totalCount = total,
            page,
            pageSize,
        });
    }

    // =========================================================================
    // LSCC-01-003: CareConnect provider provisioning
    // =========================================================================

    private const string CareConnectProductCode       = "SYNQ_CARECONNECT";
    private static readonly Guid CcProductId          = new("10000000-0000-0000-0000-000000000003");
    private static readonly Guid CcReceiverRoleId      = new("50000000-0000-0000-0000-000000000002");
    private static readonly Guid CcReferrerRoleId      = new("50000000-0000-0000-0000-000000000001");

    private static async Task EnsureCareConnectUserProductAccessAsync(
        Guid tenantId,
        Guid userId,
        IProductProvisioningService provisioningEngine,
        IUserProductAccessService userProductAccessService,
        Guid? actorUserId,
        CancellationToken ct)
    {
        await provisioningEngine.ProvisionAsync(
            new ProvisionProductRequest(tenantId, CareConnectProductCode, true),
            ct);

        await userProductAccessService.GrantAsync(
            tenantId,
            userId,
            CareConnectProductCode,
            actorUserId,
            ct);
    }

    /// <summary>
    /// GET /api/admin/users/{id}/careconnect-readiness
    /// Returns a diagnostic snapshot of the four conditions required before a user's
    /// provider organization can receive CareConnect referrals.
    /// </summary>
    private static async Task<IResult> GetCareConnectReadiness(
        Guid              id,
        ClaimsPrincipal   caller,
        IdentityDbContext db,
        CancellationToken ct)
    {
        var u = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == id, ct);

        if (u is null) return Results.NotFound();
        if (await IsCrossTenantAccessAsync(caller, u.Id, db, ct)) return Results.Forbid();

        var opTenantId = await GetOperationalTenantIdAsync(caller, u.Id, db, ct);

        // ── Primary org membership ────────────────────────────────────────────
        var membership = await db.UserOrganizationMemberships
            .AsNoTracking()
            .Include(m => m.Organization)
            .Where(m => m.UserId == id && m.IsPrimary && m.IsActive)
            .FirstOrDefaultAsync(ct);

        bool hasPrimaryOrg = membership is not null;
        var  orgId         = membership?.OrganizationId;
        var  orgType       = membership?.Organization?.OrgType;

        // ── Tenant-level CareConnect entitlement ─────────────────────────────
        bool tenantHasCareConnect = await db.Set<Identity.Domain.TenantProduct>()
            .AsNoTracking()
            .AnyAsync(tp => tp.TenantId == opTenantId
                         && tp.ProductId == CcProductId
                         && tp.IsEnabled, ct);

        // ── Org-level CareConnect entitlement ────────────────────────────────
        bool orgHasCareConnect = false;
        if (orgId.HasValue)
        {
            orgHasCareConnect = await db.OrganizationProducts
                .AsNoTracking()
                .AnyAsync(op => op.OrganizationId == orgId.Value
                              && op.ProductId == CcProductId
                              && op.IsEnabled, ct);
        }

        // ── CareConnect role (RECEIVER or REFERRER) ───────────────────────────
        bool hasCareConnectRole = await db.ScopedRoleAssignments
            .AsNoTracking()
            .AnyAsync(s => s.UserId == id
                        && s.IsActive
                        && s.ScopeType == ScopedRoleAssignment.ScopeTypes.Global
                        && (s.RoleId == CcReceiverRoleId || s.RoleId == CcReferrerRoleId), ct);

        bool isFullyProvisioned = hasPrimaryOrg && tenantHasCareConnect && orgHasCareConnect && hasCareConnectRole;

        return Results.Ok(new
        {
            userId                = id,
            hasPrimaryOrg,
            primaryOrgId          = orgId,
            primaryOrgType        = orgType,
            tenantHasCareConnect,
            orgHasCareConnect,
            hasCareConnectRole,
            isFullyProvisioned,
        });
    }

    /// <summary>
    /// POST /api/admin/users/{id}/provision-careconnect
    /// Idempotent. Ensures:
    ///   1. Tenant has SYNQ_CARECONNECT TenantProduct (enabled).
    ///   2. User's primary org has SYNQ_CARECONNECT OrganizationProduct (enabled).
    ///   3. User has the CARECONNECT_RECEIVER ScopedRoleAssignment (global).
    /// Returns a summary of what was done vs already in place.
    /// </summary>
    private static async Task<IResult> ProvisionForCareConnect(
        Guid              id,
        ClaimsPrincipal   caller,
        IdentityDbContext db,
        IProductProvisioningService provisioningEngine,
        IUserProductAccessService userProductAccessService,
        CancellationToken ct)
    {
        var callerId = caller.FindFirstValue(ClaimTypes.NameIdentifier) is { } cid
                       && Guid.TryParse(cid, out var cGuid) ? cGuid : (Guid?)null;

        var u = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == id, ct);

        if (u is null) return Results.NotFound();
        if (await IsCrossTenantAccessAsync(caller, u.Id, db, ct)) return Results.Forbid();

        var opTenantId = await GetOperationalTenantIdAsync(caller, u.Id, db, ct);
        var membership = await db.UserOrganizationMemberships
            .AsNoTracking()
            .Include(m => m.Organization)
            .Where(m => m.UserId == id && m.IsPrimary && m.IsActive)
            .FirstOrDefaultAsync(ct);

        if (membership is null)
            return Results.UnprocessableEntity(new
            {
                error = "User does not have an active primary organization membership. " +
                        "Link the user to a PROVIDER org first.",
                code  = "NO_PRIMARY_ORG",
            });

        var org = await db.Organizations
            .Include(o => o.OrganizationProducts)
            .FirstOrDefaultAsync(o => o.Id == membership.OrganizationId, ct);

        if (org is null)
            return Results.UnprocessableEntity(new
            {
                error = "Primary organization record not found.",
                code  = "ORG_NOT_FOUND",
            });

        var provResult = await provisioningEngine.ProvisionAsync(
            new ProvisionProductRequest(opTenantId, CareConnectProductCode, true), ct);

        await userProductAccessService.GrantAsync(
            opTenantId,
            id,
            CareConnectProductCode,
            callerId,
            ct);

        bool roleAdded = false;
        var existingRole = await db.ScopedRoleAssignments
            .FirstOrDefaultAsync(s => s.UserId == id
                                   && s.IsActive
                                   && s.ScopeType == ScopedRoleAssignment.ScopeTypes.Global
                                   && (s.RoleId == CcReceiverRoleId || s.RoleId == CcReferrerRoleId), ct);

        if (existingRole is null)
        {
            var sra = ScopedRoleAssignment.Create(
                userId:           id,
                roleId:           CcReceiverRoleId,
                scopeType:        ScopedRoleAssignment.ScopeTypes.Global,
                tenantId:         opTenantId,
                assignedByUserId: callerId);
            db.ScopedRoleAssignments.Add(sra);
            roleAdded = true;
            await db.SaveChangesAsync(ct);
        }

        return Results.Ok(new
        {
            userId             = id,
            organizationId     = org.Id,
            organizationName   = org.DisplayName ?? org.Name,
            tenantProductAdded = provResult.TenantProductCreated,
            orgProductAdded    = provResult.OrganizationProductsCreated > 0 || provResult.OrganizationProductsUpdated > 0,
            roleAdded,
            isFullyProvisioned = true,
        });
    }

    // =========================================================================
    // ROLES
    // =========================================================================

    // =========================================================================
    // PRODUCTS CATALOG
    // =========================================================================

    /// <summary>
    /// GET /api/admin/products
    ///
    /// Returns the global active product catalog. Accessible to TenantAdmins
    /// so they can reference product names when managing user and group access.
    /// </summary>
    private static async Task<IResult> ListProducts(IdentityDbContext db, CancellationToken ct)
    {
        var products = await db.Products
            .Where(p => p.IsActive)
            .OrderBy(p => p.Name)
            .Select(p => new { code = p.Code, name = p.Name, description = p.Description, isActive = p.IsActive })
            .ToListAsync(ct);

        return Results.Ok(products);
    }

    // =========================================================================

    private static async Task<IResult> ListRoles(
        IdentityDbContext db,
        int    page     = 1,
        int    pageSize = 20,
        string scope    = "")
    {
        var q = db.Roles.AsQueryable();
        if (!string.IsNullOrWhiteSpace(scope))
            q = q.Where(r => r.Scope == scope);

        var total = await q.CountAsync();

        // UIX-005: materialize roles first, then join capability counts
        // (EF Core LINQ restriction: cannot call Count inside Select on nav collections)
        var roleList = await q
            .OrderBy(r => r.IsSystemRole ? 0 : 1)
            .ThenBy(r => r.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var roleIds = roleList.Select(r => r.Id).ToList();

        var userCounts = await db.ScopedRoleAssignments
            .Where(s => roleIds.Contains(s.RoleId) && s.IsActive
                     && s.ScopeType == ScopedRoleAssignment.ScopeTypes.Global)
            .GroupBy(s => s.RoleId)
            .Select(g => new { roleId = g.Key, count = g.Count() })
            .ToListAsync();

        var capCounts = await db.RolePermissionAssignments
            .Where(a => roleIds.Contains(a.RoleId))
            .GroupBy(a => a.RoleId)
            .Select(g => new { roleId = g.Key, count = g.Count() })
            .ToListAsync();

        var userCountMap = userCounts.ToDictionary(x => x.roleId, x => x.count);
        var capCountMap  = capCounts.ToDictionary(x => x.roleId, x => x.count);

        // UIX-002-C: resolve product metadata for non-system roles
        var productRoles = await db.ProductRoles
            .Include(pr => pr.Product)
            .Include(pr => pr.OrgTypeRules)
                .ThenInclude(r => r.OrganizationType)
            .Where(pr => pr.IsActive)
            .ToListAsync();
        var prLookup = productRoles.ToDictionary(pr => pr.Code, StringComparer.OrdinalIgnoreCase);

        var roles = roleList.Select(r =>
        {
            prLookup.TryGetValue(r.Name, out var pr);
            var isProductRole = !r.IsSystemRole && pr is not null;
            return new
            {
                id              = r.Id,
                name            = r.Name,
                description     = r.Description ?? "",
                isSystemRole    = r.IsSystemRole,
                scope           = r.Scope,
                isProductRole,
                productCode     = isProductRole ? pr!.Product.Code : (string?)null,
                productName     = isProductRole ? pr!.Product.Name : (string?)null,
                allowedOrgTypes = isProductRole
                    ? pr!.OrgTypeRules.Where(rule => rule.IsActive).Select(rule => rule.OrganizationType.Code).ToArray()
                    : null,
                userCount       = userCountMap.GetValueOrDefault(r.Id, 0),
                permissionCount = capCountMap.GetValueOrDefault(r.Id, 0),
                permissions     = Array.Empty<string>(),
            };
        });

        return Results.Ok(new
        {
            items      = roles,
            totalCount = total,
            page,
            pageSize,
        });
    }

    private static async Task<IResult> GetRole(Guid id, IdentityDbContext db)
    {
        var r = await db.Roles
            .FirstOrDefaultAsync(r => r.Id == id);

        if (r is null) return Results.NotFound();

        var userCount = await db.ScopedRoleAssignments
            .CountAsync(s => s.RoleId == id && s.IsActive
                          && s.ScopeType == ScopedRoleAssignment.ScopeTypes.Global);

        var permAssignments = await db.RolePermissionAssignments
            .Where(a => a.RoleId == id)
            .Include(a => a.Permission)
            .ThenInclude(c => c.Product)
            .OrderBy(a => a.Permission.Product.Name)
            .ThenBy(a => a.Permission.Code)
            .ToListAsync();

        var resolvedPermissions = permAssignments.Select(a => new
        {
            id          = a.PermissionId,
            key         = a.Permission.Code,
            description = a.Permission.Description ?? a.Permission.Name,
            name        = a.Permission.Name,
            productId   = a.Permission.ProductId,
            productName = a.Permission.Product.Name,
        }).ToList();

        return Results.Ok(new
        {
            id                  = r.Id,
            name                = r.Name,
            description         = r.Description ?? "",
            isSystemRole        = r.IsSystemRole,
            scope               = r.Scope,
            userCount,
            permissionCount     = permAssignments.Count,
            permissions         = resolvedPermissions.Select(p => p.key).ToArray(),
            resolvedPermissions,
            createdAtUtc        = r.CreatedAtUtc,
            updatedAtUtc        = r.UpdatedAtUtc,
        });
    }

    // =========================================================================
    // AUDIT LOGS
    // =========================================================================

    private static async Task<IResult> ListAudit(
        IdentityDbContext db,
        int    page       = 1,
        int    pageSize   = 20,
        string search     = "",
        string entityType = "",
        string actorType  = "")
    {
        var q = db.AuditLogs.AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(a =>
                a.Action.Contains(search)     ||
                a.EntityId.Contains(search)   ||
                a.ActorName.Contains(search));

        if (!string.IsNullOrWhiteSpace(entityType))
            q = q.Where(a => a.EntityType == entityType);

        if (!string.IsNullOrWhiteSpace(actorType))
            q = q.Where(a => a.ActorType == actorType);

        var total = await q.CountAsync();

        var raw = await q
            .OrderByDescending(a => a.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new
            {
                a.Id,
                a.ActorName,
                a.ActorType,
                a.Action,
                a.EntityType,
                a.EntityId,
                a.MetadataJson,
                a.CreatedAtUtc,
            })
            .ToListAsync();

        var logs = raw.Select(a => new
        {
            id           = a.Id,
            actorName    = a.ActorName,
            actorType    = a.ActorType,
            action       = a.Action,
            entityType   = a.EntityType,
            entityId     = a.EntityId,
            metadata     = a.MetadataJson is not null
                ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(a.MetadataJson)
                : null,
            createdAtUtc = a.CreatedAtUtc,
        }).ToList();

        return Results.Ok(new
        {
            items      = logs,
            totalCount = total,
            page,
            pageSize,
        });
    }

    // =========================================================================
    // PLATFORM SETTINGS — persisted to the Tenant DB `platform_settings` table.
    //
    // The table is created lazily on first write (CREATE TABLE IF NOT EXISTS).
    // Both Identity.Api and Support.Api connect to the same Tenant DB, so a
    // change saved here is immediately visible to notification dispatch without
    // a service restart or re-deployment.
    // =========================================================================

    private static readonly List<PlatformSettingDto> _settings =
    [
        new("maintenance_mode",       "Maintenance Mode",         false, "boolean", "Put the platform into maintenance mode. All non-admin users will see a maintenance page.", false),
        new("max_users_per_tenant",   "Max Users Per Tenant",     100,   "number",  "Maximum number of users allowed per tenant. 0 = unlimited.",                              true),
        new("session_timeout_minutes","Session Timeout (minutes)", 60,   "number",  "Idle session timeout in minutes.",                                                        true),
        new("allow_self_registration","Allow Self-Registration",  false, "boolean", "Allow users to register without an invitation.",                                          true),
        new("default_product_code",   "Default Product",          "SynqFund", "string", "Product assigned to new tenants by default.",                                        true),
        new("support_email",          "Support Email",            "support@legalsynq.com", "string", "Email address displayed in the support footer.",                        true),
        new("require_availability_check","Require Availability Check", false, "boolean", "When enabled, law firms must verify provider availability before creating a referral. When disabled, referrals can be sent to any provider.", true),
        new("google_maps_enabled",       "Google Maps",                false, "boolean", "Use Google Maps as the default map provider across CareConnect views. Requires a Google Maps API key to be configured at build time.", true),
        // LS-ID-TNT-016-01: Platform-wide base URL configuration for portal links and email links.
        new("platform.portalBaseDomain", "Portal Base Domain",    "", "string", "Base domain for tenant-subdomain portal URLs (e.g. demo.legalsynq.com). Each tenant's slug is prepended automatically.", true),
        new("platform.portalBaseUrl",    "Portal Base URL",       "", "string", "Fallback portal URL used when Portal Base Domain is not set (e.g. https://portal.legalsynq.com).",                       true),
        // LS-ID-TNT-016-02: Admin notification recipient for support ticket emails.
        new("platform.adminNotifyEmail",      "Admin Notification Email",      "", "string", "Email address that receives a copy of every support ticket notification (created, updated, commented). Leave blank to disable.",                               true),
        // LS-ID-TNT-016-03: Control Center base URL for admin deeplinks in support emails.
        new("platform.controlCenterBaseUrl",  "Control Center Base URL",       "", "string", "Base URL of the Control Center (e.g. https://cc.legalsynq.com). Admin support ticket emails will link here instead of the tenant portal.",                    true),
    ];

    // Synchronise one entry in the mutable display list without touching others.
    private static void SyncSettingDisplay(string key, object? value)
    {
        var idx = _settings.FindIndex(s => s.key == key);
        if (idx >= 0) _settings[idx] = _settings[idx] with { value = value };
    }

    /// <summary>
    /// Reads a value from the Tenant DB <c>platform_settings</c> table.
    /// Returns null when the table/row doesn't exist yet, or on any error.
    /// </summary>
    private static async Task<string?> ReadPlatformSettingAsync(string key, IConfiguration config)
    {
        var cs = config.GetConnectionString("TenantDb");
        if (string.IsNullOrWhiteSpace(cs)) return null;
        try
        {
            await using var conn = new MySqlConnection(cs);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT `setting_value` FROM `platform_settings` WHERE `setting_key` = @k LIMIT 1";
            cmd.Parameters.AddWithValue("@k", key);
            return await cmd.ExecuteScalarAsync() as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Upserts a value into the Tenant DB <c>platform_settings</c> table,
    /// creating the table first if it doesn't exist yet.
    /// Failures are swallowed so a DB issue never blocks a settings PATCH.
    /// </summary>
    private static async Task PersistPlatformSettingAsync(string key, string value, IConfiguration config)
    {
        var cs = config.GetConnectionString("TenantDb");
        if (string.IsNullOrWhiteSpace(cs)) return;
        try
        {
            await using var conn = new MySqlConnection(cs);
            await conn.OpenAsync();

            await using (var createCmd = conn.CreateCommand())
            {
                createCmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS `platform_settings` (
                        `setting_key`   VARCHAR(200) NOT NULL,
                        `setting_value` TEXT         NOT NULL DEFAULT '',
                        `updated_at`    DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
                        PRIMARY KEY (`setting_key`)
                    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;";
                await createCmd.ExecuteNonQueryAsync();
            }

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO `platform_settings` (`setting_key`, `setting_value`, `updated_at`)
                VALUES (@k, @v, UTC_TIMESTAMP(3))
                ON DUPLICATE KEY UPDATE `setting_value` = @v, `updated_at` = UTC_TIMESTAMP(3)";
            cmd.Parameters.AddWithValue("@k", key);
            cmd.Parameters.AddWithValue("@v", value);
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // Swallowed — a DB write failure must not break the settings endpoint.
        }
    }

    private static async Task<IResult> ListSettings(
        IOptions<NotificationsServiceOptions> notifOptions,
        IConfiguration config)
    {
        // Seed in-process NotificationsServiceOptions from the persistent DB values so
        // that the Control Center always shows the last saved value — even after a restart
        // that would otherwise reset the in-memory state to the env-var default.
        var dbDomain      = await ReadPlatformSettingAsync("platform.portalBaseDomain",    config);
        var dbUrl         = await ReadPlatformSettingAsync("platform.portalBaseUrl",       config);
        var dbAdminEmail  = await ReadPlatformSettingAsync("platform.adminNotifyEmail",    config);
        var dbCcUrl       = await ReadPlatformSettingAsync("platform.controlCenterBaseUrl",config);

        if (dbDomain is not null) notifOptions.Value.PortalBaseDomain = dbDomain;
        if (dbUrl    is not null) notifOptions.Value.PortalBaseUrl    = dbUrl;

        SyncSettingDisplay("platform.portalBaseDomain",    notifOptions.Value.PortalBaseDomain ?? "");
        SyncSettingDisplay("platform.portalBaseUrl",       notifOptions.Value.PortalBaseUrl    ?? "");
        SyncSettingDisplay("platform.adminNotifyEmail",    dbAdminEmail ?? "");
        SyncSettingDisplay("platform.controlCenterBaseUrl",dbCcUrl      ?? "");

        return Results.Ok(new
        {
            items      = _settings,
            totalCount = _settings.Count,
            page       = 1,
            pageSize   = _settings.Count,
        });
    }

    private static async Task<IResult> UpdateSetting(
        string               key,
        SettingUpdateRequest body,
        IOptions<NotificationsServiceOptions> notifOptions,
        IConfiguration config)
    {
        var idx = _settings.FindIndex(s => s.key == key);
        if (idx < 0) return Results.NotFound();

        if (!_settings[idx].editable) return Results.Problem("This setting is read-only.", statusCode: 403);

        _settings[idx] = _settings[idx] with { value = body.Value };

        // Mirror portal-URL settings to the live NotificationsServiceOptions singleton
        // (for invite/reset links sent by Identity.Api in the same process).
        if (key == "platform.portalBaseDomain")
            notifOptions.Value.PortalBaseDomain = body.Value?.ToString();
        else if (key == "platform.portalBaseUrl")
            notifOptions.Value.PortalBaseUrl = body.Value?.ToString();

        // Persist platform settings to the Tenant DB so the value survives restarts
        // and is visible to Support.Api (which reads from the same table).
        if (key is "platform.portalBaseDomain" or "platform.portalBaseUrl"
                or "platform.adminNotifyEmail"  or "platform.controlCenterBaseUrl")
            await PersistPlatformSettingAsync(key, body.Value?.ToString() ?? "", config);

        return Results.Ok(_settings[idx]);
    }

    // =========================================================================
    // SUPPORT CASES  (stub — no DB table yet; returns empty paged list)
    // =========================================================================

    private static IResult ListSupport(int page = 1, int pageSize = 20)
    {
        return Results.Ok(new
        {
            items      = Array.Empty<object>(),
            totalCount = 0,
            page,
            pageSize,
        });
    }

    private static IResult GetSupport(string id) =>
        Results.NotFound(new { error = "Support case not found." });

    private static IResult CreateSupport(CreateSupportRequest body) =>
        Results.Created("/api/admin/support/stub", new
        {
            id          = Guid.CreateVersion7(),
            title       = body.Title,
            status      = "Open",
            priority    = body.Priority ?? "Medium",
            category    = body.Category ?? "General",
            createdAtUtc = DateTime.UtcNow,
            updatedAtUtc = DateTime.UtcNow,
        });

    private static IResult AddSupportNote(string id, SupportNoteRequest body) =>
        Results.Ok(new
        {
            id           = Guid.CreateVersion7(),
            caseId       = id,
            message      = body.Message,
            createdBy    = "admin",
            createdAtUtc = DateTime.UtcNow,
        });

    private static IResult UpdateSupportStatus(string id, SupportStatusRequest body) =>
        Results.Ok(new
        {
            id           = id,
            status       = body.Status,
            updatedAtUtc = DateTime.UtcNow,
        });

    // =========================================================================
    // ORGANIZATION TYPES  (Phase 1)
    // =========================================================================

    private static async Task<IResult> ListOrganizationTypes(IdentityDbContext db)
    {
        var items = await db.OrganizationTypes
            .OrderBy(ot => ot.DisplayName)
            .Select(ot => new
            {
                id          = ot.Id,
                code        = ot.Code,
                displayName = ot.DisplayName,
                description = ot.Description,
                isSystem    = ot.IsSystem,
                isActive    = ot.IsActive,
                createdAtUtc = ot.CreatedAtUtc,
            })
            .ToListAsync();

        return Results.Ok(new { items, totalCount = items.Count });
    }

    private static async Task<IResult> GetOrganizationType(Guid id, IdentityDbContext db)
    {
        var ot = await db.OrganizationTypes.FirstOrDefaultAsync(x => x.Id == id);
        if (ot is null) return Results.NotFound();

        return Results.Ok(new
        {
            id          = ot.Id,
            code        = ot.Code,
            displayName = ot.DisplayName,
            description = ot.Description,
            isSystem    = ot.IsSystem,
            isActive    = ot.IsActive,
            createdAtUtc = ot.CreatedAtUtc,
        });
    }

    // =========================================================================
    // RELATIONSHIP TYPES  (Phase 2)
    // =========================================================================

    private static async Task<IResult> ListRelationshipTypes(IdentityDbContext db)
    {
        var items = await db.RelationshipTypes
            .OrderBy(rt => rt.DisplayName)
            .Select(rt => new
            {
                id            = rt.Id,
                code          = rt.Code,
                displayName   = rt.DisplayName,
                description   = rt.Description,
                isDirectional = rt.IsDirectional,
                isSystem      = rt.IsSystem,
                isActive      = rt.IsActive,
                createdAtUtc  = rt.CreatedAtUtc,
            })
            .ToListAsync();

        return Results.Ok(new { items, totalCount = items.Count });
    }

    private static async Task<IResult> GetRelationshipType(Guid id, IdentityDbContext db)
    {
        var rt = await db.RelationshipTypes.FirstOrDefaultAsync(x => x.Id == id);
        if (rt is null) return Results.NotFound();

        return Results.Ok(new
        {
            id            = rt.Id,
            code          = rt.Code,
            displayName   = rt.DisplayName,
            description   = rt.Description,
            isDirectional = rt.IsDirectional,
            isSystem      = rt.IsSystem,
            isActive      = rt.IsActive,
            createdAtUtc  = rt.CreatedAtUtc,
        });
    }

    // =========================================================================
    // ORGANIZATION RELATIONSHIPS  (Phase 2)
    // =========================================================================

    private static async Task<IResult> ListOrganizationRelationships(
        IdentityDbContext db,
        int    page       = 1,
        int    pageSize   = 20,
        string tenantId   = "",
        string sourceOrgId = "",
        bool   activeOnly = true)
    {
        var q = db.OrganizationRelationships
            .Include(r => r.SourceOrganization)
            .Include(r => r.TargetOrganization)
            .Include(r => r.RelationshipType)
            .AsQueryable();

        if (activeOnly)
            q = q.Where(r => r.IsActive);

        if (!string.IsNullOrWhiteSpace(tenantId) && Guid.TryParse(tenantId, out var tid))
            q = q.Where(r => r.TenantId == tid);

        if (!string.IsNullOrWhiteSpace(sourceOrgId) && Guid.TryParse(sourceOrgId, out var sid))
            q = q.Where(r => r.SourceOrganizationId == sid);

        var total = await q.CountAsync();

        var items = await q
            .OrderByDescending(r => r.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new
            {
                id                   = r.Id,
                tenantId             = r.TenantId,
                sourceOrganizationId = r.SourceOrganizationId,
                sourceOrgName        = r.SourceOrganization.DisplayName ?? r.SourceOrganization.Name,
                targetOrganizationId = r.TargetOrganizationId,
                targetOrgName        = r.TargetOrganization.DisplayName ?? r.TargetOrganization.Name,
                relationshipTypeId   = r.RelationshipTypeId,
                relationshipTypeCode = r.RelationshipType.Code,
                relationshipTypeDisplayName = r.RelationshipType.DisplayName,
                productId            = r.ProductId,
                isActive             = r.IsActive,
                establishedAtUtc     = r.EstablishedAtUtc,
                createdAtUtc         = r.CreatedAtUtc,
            })
            .ToListAsync();

        return Results.Ok(new { items, totalCount = total, page, pageSize });
    }

    private static async Task<IResult> GetOrganizationRelationship(Guid id, IdentityDbContext db)
    {
        var r = await db.OrganizationRelationships
            .Include(x => x.SourceOrganization)
            .Include(x => x.TargetOrganization)
            .Include(x => x.RelationshipType)
            .FirstOrDefaultAsync(x => x.Id == id);

        if (r is null) return Results.NotFound();

        return Results.Ok(new
        {
            id                   = r.Id,
            tenantId             = r.TenantId,
            sourceOrganizationId = r.SourceOrganizationId,
            sourceOrgName        = r.SourceOrganization.DisplayName ?? r.SourceOrganization.Name,
            targetOrganizationId = r.TargetOrganizationId,
            targetOrgName        = r.TargetOrganization.DisplayName ?? r.TargetOrganization.Name,
            relationshipTypeId   = r.RelationshipTypeId,
            relationshipTypeCode = r.RelationshipType.Code,
            productId            = r.ProductId,
            isActive             = r.IsActive,
            establishedAtUtc     = r.EstablishedAtUtc,
            createdAtUtc         = r.CreatedAtUtc,
            updatedAtUtc         = r.UpdatedAtUtc,
        });
    }

    private static async Task<IResult> CreateOrganizationRelationship(
        CreateOrgRelationshipRequest body,
        IdentityDbContext db,
        IAuditEventClient auditClient)
    {
        // Validate orgs exist
        var sourceOrg = await db.Organizations.FirstOrDefaultAsync(o => o.Id == body.SourceOrganizationId);
        if (sourceOrg is null)
            return Results.NotFound(new { error = "Source organization not found." });

        var targetOrg = await db.Organizations.FirstOrDefaultAsync(o => o.Id == body.TargetOrganizationId);
        if (targetOrg is null)
            return Results.NotFound(new { error = "Target organization not found." });
        if (!sourceOrg.TenantId.HasValue || !targetOrg.TenantId.HasValue)
            return Results.BadRequest(new { error = "Global organizations cannot be used in tenant-scoped relationships." });
        if (sourceOrg.TenantId != targetOrg.TenantId)
            return Results.BadRequest(new { error = "Organizations must belong to the same tenant." });

        var relType = await db.RelationshipTypes.FirstOrDefaultAsync(rt => rt.Id == body.RelationshipTypeId);
        if (relType is null)
            return Results.NotFound(new { error = "Relationship type not found." });

        var existing = await db.OrganizationRelationships.FirstOrDefaultAsync(r =>
            r.TenantId == sourceOrg.TenantId &&
            r.SourceOrganizationId == body.SourceOrganizationId &&
            r.TargetOrganizationId == body.TargetOrganizationId &&
            r.RelationshipTypeId == body.RelationshipTypeId);

        if (existing is not null)
            return Results.Conflict(new { error = "Relationship already exists." });

        var rel = Identity.Domain.OrganizationRelationship.Create(
            tenantId             : sourceOrg.TenantId.Value,
            sourceOrganizationId : body.SourceOrganizationId,
            targetOrganizationId : body.TargetOrganizationId,
            relationshipTypeId   : body.RelationshipTypeId,
            productId            : body.ProductId);

        db.OrganizationRelationships.Add(rel);
        await db.SaveChangesAsync();

        // Canonical audit — fire-and-observe, never gates the response.
        var auditNow = DateTimeOffset.UtcNow;
        _ = auditClient.IngestAsync(new IngestAuditEventRequest
        {
            EventType     = "platform.admin.org.relationship.created",
            EventCategory = EventCategory.Administrative,
            SourceSystem  = "identity-service",
            SourceService = "admin-api",
            Visibility    = VisibilityScope.Platform,
            Severity      = SeverityLevel.Info,
            OccurredAtUtc = auditNow,
            Scope         = new AuditEventScopeDto { ScopeType = ScopeType.Tenant, TenantId = sourceOrg.TenantId.ToString() },
            Actor         = new AuditEventActorDto { Type = ActorType.System },
            Entity        = new AuditEventEntityDto { Type = "OrganizationRelationship", Id = rel.Id.ToString() },
            Action        = "RelationshipCreated",
            Description   = $"Organization relationship created: {body.SourceOrganizationId} → {body.TargetOrganizationId} ({relType.DisplayName}).",
            After         = JsonSerializer.Serialize(new { id = rel.Id, body.SourceOrganizationId, body.TargetOrganizationId, body.RelationshipTypeId, body.ProductId }),
            IdempotencyKey = IdempotencyKey.For("identity-service", "platform.admin.org.relationship.created", rel.Id.ToString()),
            Tags = ["org-relationship", "admin"],
        });

        return Results.Created($"/api/admin/organization-relationships/{rel.Id}", new
        {
            id                   = rel.Id,
            tenantId             = rel.TenantId,
            sourceOrganizationId = rel.SourceOrganizationId,
            targetOrganizationId = rel.TargetOrganizationId,
            relationshipTypeId   = rel.RelationshipTypeId,
            productId            = rel.ProductId,
            isActive             = rel.IsActive,
            establishedAtUtc     = rel.EstablishedAtUtc,
        });
    }

    private static async Task<IResult> DeactivateOrganizationRelationship(Guid id, IdentityDbContext db, IAuditEventClient auditClient)
    {
        var rel = await db.OrganizationRelationships.FirstOrDefaultAsync(r => r.Id == id);
        if (rel is null) return Results.NotFound();

        rel.Deactivate();
        await db.SaveChangesAsync();

        // Canonical audit — fire-and-observe, never gates the response.
        var auditNow = DateTimeOffset.UtcNow;
        _ = auditClient.IngestAsync(new IngestAuditEventRequest
        {
            EventType     = "platform.admin.org.relationship.deactivated",
            EventCategory = EventCategory.Administrative,
            SourceSystem  = "identity-service",
            SourceService = "admin-api",
            Visibility    = VisibilityScope.Platform,
            Severity      = SeverityLevel.Warn,
            OccurredAtUtc = auditNow,
            Scope         = new AuditEventScopeDto { ScopeType = ScopeType.Tenant, TenantId = rel.TenantId.ToString() },
            Actor         = new AuditEventActorDto { Type = ActorType.System },
            Entity        = new AuditEventEntityDto { Type = "OrganizationRelationship", Id = id.ToString() },
            Action        = "RelationshipDeactivated",
            Description   = $"Organization relationship {id} deactivated.",
            Before        = JsonSerializer.Serialize(new { id, isActive = true }),
            IdempotencyKey = IdempotencyKey.ForWithTimestamp(auditNow, "identity-service", "platform.admin.org.relationship.deactivated", id.ToString()),
            Tags = ["org-relationship", "admin"],
        });

        return Results.Ok(new { id = rel.Id, isActive = false });
    }

    // =========================================================================
    // PRODUCT ORG-TYPE RULES  (Phase 3)
    // =========================================================================

    private static async Task<IResult> ListProductOrgTypeRules(IdentityDbContext db)
    {
        // Response: plain array — client does Array.isArray(raw) check.
        // Field names must match api-mappers.ts mapProductOrgTypeRule camelCase keys.
        var items = await db.ProductOrganizationTypeRules
            .Include(r => r.Product)
            .Include(r => r.ProductRole)
            .Include(r => r.OrganizationType)
            .Where(r => r.IsActive)
            .OrderBy(r => r.Product.Code)
                .ThenBy(r => r.ProductRole.Code)
            .Select(r => new
            {
                id                   = r.Id,
                productId            = r.ProductId,
                productCode          = r.Product.Code,
                productRoleId        = r.ProductRoleId,
                productRoleCode      = r.ProductRole.Code,
                productRoleName      = r.ProductRole.Name,
                organizationTypeId   = r.OrganizationTypeId,
                organizationTypeCode = r.OrganizationType.Code,         // mapper expects this name
                organizationTypeName = r.OrganizationType.DisplayName,
                isActive             = r.IsActive,
                createdAtUtc         = r.CreatedAtUtc,
            })
            .ToListAsync();

        return Results.Ok(items);   // plain array, not { items, totalCount }
    }

    // =========================================================================
    // PRODUCT RELATIONSHIP-TYPE RULES  (Phase 2)
    // =========================================================================

    private static async Task<IResult> ListProductRelationshipTypeRules(IdentityDbContext db)
    {
        // Registered under both /product-relationship-type-rules (canonical) and
        // /product-rel-type-rules (short alias used by the control-center client).
        var items = await db.ProductRelationshipTypeRules
            .Include(r => r.Product)
            .Include(r => r.RelationshipType)
            .Where(r => r.IsActive)
            .OrderBy(r => r.Product.Code)
                .ThenBy(r => r.RelationshipType.Code)
            .Select(r => new
            {
                id                   = r.Id,
                productId            = r.ProductId,
                productCode          = r.Product.Code,
                relationshipTypeId   = r.RelationshipTypeId,
                relationshipTypeCode = r.RelationshipType.Code,
                relationshipTypeName = r.RelationshipType.DisplayName,
                isActive             = r.IsActive,
                createdAtUtc         = r.CreatedAtUtc,
            })
            .ToListAsync();

        return Results.Ok(items);   // plain array, not { items, totalCount }
    }

    // =========================================================================
    // LEGACY COVERAGE  (Step 4 / Phase F)
    // =========================================================================

    /// <summary>
    /// GET /api/admin/legacy-coverage
    ///
    /// Returns a point-in-time snapshot of the two Phase F migration paths:
    ///
    ///   1. Eligibility rules (Phase F COMPLETE):
    ///      - EligibleOrgType column dropped (migration 20260330200003).
    ///      - legacyStringOnly = 0 always. withBothPaths = 0 always.
    ///      - All 7 restricted ProductRoles use ProductOrganizationTypeRules exclusively.
    ///      - dbCoveragePct reflects OrgTypeRule coverage over all restricted roles.
    ///
    ///   2. UserRoles → ScopedRoleAssignment dual-write adoption (ongoing):
    ///      - Tracks users with legacy UserRole records vs. GLOBAL ScopedRoleAssignments.
    ///      - Gap = usersWithLegacyRoles − usersWithScopedRoles (should reach 0 after backfill).
    ///      - Migration 20260330200002 backfills UserRoles → ScopedRoleAssignments.
    ///
    /// Used by the /legacy-coverage control center page to track cutover progress.
    /// </summary>
    private static async Task<IResult> GetLegacyCoverage(IdentityDbContext db)
    {
        // ── 1. Eligibility rules — Phase F complete ───────────────────────────
        // EligibleOrgType column removed; all eligibility driven by OrgTypeRules.

        var allActiveRoles = await db.ProductRoles
            .Where(pr => pr.IsActive)
            .Select(pr => new { pr.Id, pr.Code })
            .ToListAsync();

        // ProductRole IDs that have at least one active OrgTypeRule
        var rolesWithDbRuleList = await db.ProductOrganizationTypeRules
            .Where(r => r.IsActive)
            .Select(r => r.ProductRoleId)
            .Distinct()
            .ToListAsync();
        var rolesWithDbRules = new HashSet<Guid>(rolesWithDbRuleList);

        int withDbRuleOnly = 0;
        int unrestricted   = 0;

        foreach (var pr in allActiveRoles)
        {
            if (rolesWithDbRules.Contains(pr.Id)) withDbRuleOnly++;
            else                                  unrestricted++;
        }

        // Phase F: these are permanently 0 — column dropped, path retired.
        const int withBothPaths    = 0;
        const int legacyStringOnly = 0;

        double eligibilityCoverage = allActiveRoles.Count > 0
            ? Math.Round((double)withDbRuleOnly / allActiveRoles.Count * 100, 1)
            : 100.0;

        // ── 2. ScopedRoleAssignment adoption — Phase G complete ──────────────

        // Phase G: UserRoles table dropped. Count authoritative scoped assignments.
        var usersWithScopedRole = await db.ScopedRoleAssignments
            .Where(s => s.IsActive && s.ScopeType == ScopedRoleAssignment.ScopeTypes.Global)
            .Select(s => s.UserId)
            .Distinct()
            .CountAsync();

        var totalScopedAssignments = await db.ScopedRoleAssignments
            .CountAsync(s => s.IsActive && s.ScopeType == ScopedRoleAssignment.ScopeTypes.Global);

        return Results.Ok(new
        {
            generatedAtUtc = DateTime.UtcNow,

            eligibilityRules = new
            {
                totalActiveProductRoles  = allActiveRoles.Count,
                withDbRuleOnly,
                withBothPaths,           // Phase F+G: always 0 — EligibleOrgType column dropped
                legacyStringOnly,        // Phase F+G: always 0 — EligibleOrgType column dropped
                unrestricted,
                dbCoveragePct            = eligibilityCoverage,
            },

            roleAssignments = new
            {
                usersWithScopedRoles        = usersWithScopedRole,
                totalActiveScopedAssignments = totalScopedAssignments,
                // Phase G: UserRoles table retired. Gap metric no longer applicable.
                userRolesRetired            = true,
                dualWriteCoveragePct        = 100.0,
            },
        });
    }

    // =========================================================================
    // ROLE ASSIGNMENT  (Step 5 — dual-write admin endpoints)
    // =========================================================================

    /// <summary>
    /// POST /api/admin/users/{id}/roles
    ///
    /// Assigns a role to a user.
    ///
    /// Phase G: UserRoles table dropped — ScopedRoleAssignments is the sole store.
    /// Phase I: extended to support non-GLOBAL scope types.
    ///
    /// Body: { "roleId": "guid", "scopeType": "GLOBAL|ORGANIZATION|PRODUCT|RELATIONSHIP|TENANT",
    ///         "organizationId"?: "guid", "productId"?: "guid",
    ///         "organizationRelationshipId"?: "guid" }
    ///
    /// scopeType defaults to GLOBAL when omitted (backward compatible).
    /// ORGANIZATION scope requires organizationId.
    /// PRODUCT scope requires productId.
    /// RELATIONSHIP scope requires organizationRelationshipId.
    ///
    /// Returns 201 Created on success, 400 for scope validation errors,
    /// 404 if user or role not found, 409 Conflict if the same scoped assignment exists.
    /// </summary>
    private static async Task<IResult> AssignRole(
        Guid                      id,
        AssignRoleRequest         body,
        IdentityDbContext         db,
        IAuditEventClient         auditClient,
        INotificationsCacheClient notificationsCache,
        HttpContext               ctx)
    {
        var user = await db.Users.FindAsync(id);
        if (user is null) return Results.NotFound(new { error = $"User '{id}' not found." });

        // UIX-003-01: TenantAdmin may only assign roles within their own tenant.
        if (await IsCrossTenantAccessAsync(ctx.User, user.Id, db, ctx.RequestAborted)) return Results.Forbid();

        var opTenantId = await GetOperationalTenantIdAsync(ctx.User, user.Id, db, ctx.RequestAborted);

        // PUM-B02-R10: resolve role by roleId or roleKey (name).
        Role? role = null;
        if (body.RoleId.HasValue && body.RoleId != Guid.Empty)
            role = await db.Roles.FindAsync(body.RoleId.Value);
        else if (!string.IsNullOrWhiteSpace(body.RoleKey))
            role = await db.Roles.FirstOrDefaultAsync(r => r.Name == body.RoleKey.Trim());
        else
            return Results.BadRequest(new { error = "Either roleId or roleKey must be provided." });

        if (role is null)
        {
            var identifier = body.RoleId.HasValue ? body.RoleId.ToString() : body.RoleKey;
            return Results.NotFound(new { error = $"Role '{identifier}' not found." });
        }

        // ── PUM-B05-R07/R08: ExternalCustomer role guard ────────────────────
        // ExternalCustomer users may not receive Platform or Tenant roles.
        // Only Product-scoped roles are permitted (via the product-role endpoints).
        if (user.UserType == UserType.ExternalCustomer &&
            role.Scope is RoleScopes.Platform or RoleScopes.Tenant)
            return Results.BadRequest(new
            {
                error   = "EXTERNAL_USER_ROLE_FORBIDDEN",
                message = $"ExternalCustomer users cannot be assigned Platform or Tenant roles. " +
                          $"Role '{role.Name}' has scope '{role.Scope ?? "(none)"}'. " +
                          "Assign product-scoped roles via POST /api/admin/users/{id}/products/{productKey}/roles.",
            });

        // ── LS-ID-TNT-009: Platform-role guard (PUM-B03-R07) ────────────────
        // TenantAdmins may only assign roles with Scope == "Tenant".
        // Platform-scoped roles can only be assigned by a PlatformAdmin.
        if (role.IsSystemRole)
        {
            var callerIsPlatformAdmin = ctx.User.IsInRole("PlatformAdmin");
            if (!callerIsPlatformAdmin && role.Scope != RoleScopes.Tenant)
                return Results.BadRequest(new
                {
                    error   = "ROLE_NOT_TENANT_ASSIGNABLE",
                    message = "This role cannot be assigned by a tenant administrator. " +
                              "Only roles with Tenant scope are assignable at the tenant level.",
                });
        }

        // ── UIX-002-C: Product Role Eligibility Guardrails ──────────────────
        // If this role maps to a ProductRole (IsSystemRole == false and name matches
        // a ProductRole code), enforce org-type and product-enablement rules.
        if (!role.IsSystemRole)
        {
            var productRole = await db.ProductRoles
                .Include(pr => pr.Product)
                .Include(pr => pr.OrgTypeRules)
                    .ThenInclude(r => r.OrganizationType)
                .FirstOrDefaultAsync(pr => pr.Code == role.Name && pr.IsActive);

            if (productRole is not null)
            {
                // 1. Tenant product enablement check
                var tenantHasProduct = await db.TenantProducts
                    .AnyAsync(tp => tp.TenantId == opTenantId
                                 && tp.ProductId == productRole.ProductId
                                 && tp.IsEnabled);
                if (!tenantHasProduct)
                    return Results.BadRequest(new
                    {
                        error = "PRODUCT_NOT_ENABLED_FOR_TENANT",
                        message = $"Product '{productRole.Product.Name}' is not enabled for this user's tenant. " +
                                  "Enable the product entitlement before assigning this role.",
                    });

                // 2. Org type eligibility check
                var primaryMembership = await db.UserOrganizationMemberships
                    .Include(m => m.Organization)
                    .Where(m => m.UserId == id && m.IsActive)
                    .OrderByDescending(m => m.IsPrimary)
                    .ThenBy(m => m.JoinedAtUtc)
                    .FirstOrDefaultAsync();

                if (primaryMembership is null)
                    return Results.BadRequest(new
                    {
                        error = "NO_ORGANIZATION_MEMBERSHIP",
                        message = "User must belong to an organization before product roles can be assigned.",
                    });

                var userOrgTypeId = primaryMembership.Organization.OrganizationTypeId;
                var userOrgType   = primaryMembership.Organization.OrgType;

                if (productRole.OrgTypeRules.Count > 0)
                {
                    var orgTypeAllowed = userOrgTypeId.HasValue
                        ? productRole.OrgTypeRules.Any(r => r.IsActive && r.OrganizationTypeId == userOrgTypeId.Value)
                        : productRole.OrgTypeRules.Any(r => r.IsActive &&
                            r.OrganizationType.Code.Equals(userOrgType, StringComparison.OrdinalIgnoreCase));

                    if (!orgTypeAllowed)
                    {
                        var allowedTypes = productRole.OrgTypeRules
                            .Where(r => r.IsActive)
                            .Select(r => r.OrganizationType.Code)
                            .ToList();

                        return Results.BadRequest(new
                        {
                            error = "INVALID_ORG_TYPE_FOR_ROLE",
                            message = $"Role '{productRole.Name}' requires org type [{string.Join(", ", allowedTypes)}] " +
                                      $"but user's organization is '{userOrgType}'.",
                        });
                    }
                }

                // LS-COR-AUT-010: Product roles must be stored as UserRoleAssignment, not
                // ScopedRoleAssignment.  EffectiveAccessService reads UserRoleAssignments when
                // computing the JWT product_roles claim at login time.  Roles stored only in
                // ScopedRoleAssignments are invisible to EffectiveAccessService and will not appear
                // in the user's session — which causes the CareConnect receiver-access blocked page
                // to be shown even after the admin has assigned the role.
                // Bumping AccessVersion invalidates any existing JWT so the user is forced to
                // re-login and receives a fresh token that contains the new product role.
                var productCode = productRole.Product.Code;

                var alreadyHasProductRole = await db.UserRoleAssignments
                    .AnyAsync(a =>
                        a.TenantId         == opTenantId &&
                        a.UserId           == id &&
                        a.ProductCode      == productCode &&
                        a.RoleCode         == productRole.Code &&
                        a.AssignmentStatus == AssignmentStatus.Active);
                if (alreadyHasProductRole)
                    return Results.Conflict(new { error = "An identical product role assignment already exists for this user." });

                var productRoleAssignment = UserRoleAssignment.Create(
                    tenantId:        opTenantId,
                    userId:          id,
                    roleCode:        productRole.Code,
                    productCode:     productCode,
                    createdByUserId: body.AssignedByUserId);
                db.UserRoleAssignments.Add(productRoleAssignment);
                user.IncrementAccessVersion();
                await db.SaveChangesAsync();

                var prAuditNow = DateTimeOffset.UtcNow;
                _ = auditClient.IngestAsync(new IngestAuditEventRequest
                {
                    EventType     = "identity.role.assigned",
                    EventCategory = EventCategory.Administrative,
                    SourceSystem  = "identity-service",
                    SourceService = "admin-api",
                    Visibility    = VisibilityScope.Tenant,
                    Severity      = SeverityLevel.Info,
                    OccurredAtUtc = prAuditNow,
                    Scope  = new AuditEventScopeDto { ScopeType = ScopeType.Tenant, TenantId = opTenantId.ToString() },
                    Actor  = new AuditEventActorDto { Id = body.AssignedByUserId?.ToString(), Type = ActorType.User },
                    Entity = new AuditEventEntityDto { Type = "User", Id = id.ToString() },
                    Action      = "ProductRoleAssigned",
                    Description = $"Product role '{productRole.Code}' ({productCode}) assigned to user {id}.",
                    After       = JsonSerializer.Serialize(new { roleCode = productRole.Code, productCode, userId = id }),
                    IdempotencyKey = IdempotencyKey.For("identity-service", "identity.role.assigned", productRoleAssignment.Id.ToString()),
                    Tags = ["role-management", "access-control"],
                });

                notificationsCache.InvalidateTenant(
                    opTenantId,
                    eventType: "identity.role.assigned",
                    reason:    $"product role {productRole.Code} assigned to user {id}");

                return Results.Created(
                    $"/api/admin/users/{id}/roles/{role.Id}",
                    new
                    {
                        assignmentId  = productRoleAssignment.Id,
                        userId        = id,
                        roleId        = role.Id,
                        roleName      = role.Name,
                        roleCode      = productRole.Code,
                        productCode,
                        assignedAtUtc = prAuditNow,
                    });
            }
        }

        // LS-COR-AUT-007: ScopedRoleAssignment restricted to GLOBAL scope only.
        // Product-scoped roles use UserRoleAssignment/GroupRoleAssignment instead.
        var scopeType = body.ScopeType ?? ScopedRoleAssignment.ScopeTypes.Global;
        if (!ScopedRoleAssignment.ScopeTypes.IsValid(scopeType))
            return Results.BadRequest(new
            {
                error = "SCOPE_TYPE_RESTRICTED",
                message = $"ScopedRoleAssignment only supports GLOBAL scope. Received: '{scopeType}'. " +
                          "Use product role assignment endpoints for product-scoped roles.",
            });

        // Conflict check: same user + same role + GLOBAL scope
        var alreadyAssigned = await db.ScopedRoleAssignments
            .AnyAsync(s =>
                s.UserId     == id &&
                s.RoleId     == role.Id &&
                s.IsActive   &&
                s.ScopeType  == ScopedRoleAssignment.ScopeTypes.Global);
        if (alreadyAssigned)
            return Results.Conflict(new { error = "An identical scoped role assignment already exists for this user." });

        var now = DateTime.UtcNow;

        // LS-COR-AUT-007: GLOBAL scope only — no org/product/relationship context.
        var sra = ScopedRoleAssignment.Create(
            userId:           id,
            roleId:           role.Id,
            scopeType:        ScopedRoleAssignment.ScopeTypes.Global,
            tenantId:         opTenantId,
            assignedByUserId: body.AssignedByUserId);
        db.ScopedRoleAssignments.Add(sra);

        await db.SaveChangesAsync();

        // Canonical audit — fire-and-observe, never gates the response.
        var auditNow = DateTimeOffset.UtcNow;
        _ = auditClient.IngestAsync(new IngestAuditEventRequest
        {
            EventType     = "identity.role.assigned",
            EventCategory = EventCategory.Administrative,
            SourceSystem  = "identity-service",
            SourceService = "admin-api",
            Visibility    = VisibilityScope.Tenant,
            Severity      = SeverityLevel.Info,
            OccurredAtUtc = auditNow,
            Scope = new AuditEventScopeDto { ScopeType = ScopeType.Tenant, TenantId = opTenantId.ToString() },
            Actor = new AuditEventActorDto { Id = body.AssignedByUserId?.ToString(), Type = ActorType.User },
            Entity = new AuditEventEntityDto { Type = "User", Id = id.ToString() },
            Action      = "RoleAssigned",
            Description = $"Role '{role.Name}' ({scopeType}) assigned to user {id}.",
            After       = JsonSerializer.Serialize(new { roleId = role.Id, roleName = role.Name, scopeType, organizationId = body.OrganizationId }),
            IdempotencyKey = IdempotencyKey.For("identity-service", "identity.role.assigned", sra.Id.ToString()),
            Tags = ["role-management", "access-control"],
        });

        // Role membership for this tenant changed — invalidate the notifications
        // service's cache so the next role-addressed fan-out includes this user.
        notificationsCache.InvalidateTenant(
            opTenantId,
            eventType: "identity.role.assigned",
            reason:    $"role {role.Id} assigned to user {id}");

        return Results.Created(
            $"/api/admin/users/{id}/roles/{role.Id}",
            new
            {
                assignmentId              = sra.Id,
                userId                    = id,
                roleId                    = role.Id,
                roleName                  = role.Name,
                scopeType                 = scopeType,
                organizationId            = body.OrganizationId,
                productId                 = body.ProductId,
                organizationRelationshipId = body.OrganizationRelationshipId,
                assignedAtUtc             = now,
            });
    }

    /// <summary>
    /// GET /api/admin/users/{id}/assignable-roles
    ///
    /// UIX-002-C: Returns all roles (system + product) with eligibility metadata
    /// for a specific user. Product roles include org-type and tenant product
    /// enablement checks based on the user's primary organization.
    /// </summary>
    private static async Task<IResult> GetAssignableRoles(
        Guid              id,
        IdentityDbContext db,
        HttpContext        ctx)
    {
        var user = await db.Users.FindAsync(id);
        if (user is null) return Results.NotFound(new { error = $"User '{id}' not found." });

        if (await IsCrossTenantAccessAsync(ctx.User, user.Id, db, ctx.RequestAborted)) return Results.Forbid();

        var opTenantId = await GetOperationalTenantIdAsync(ctx.User, user.Id, db, ctx.RequestAborted);

        // Resolve user's primary org type
        var primaryMembership = await db.UserOrganizationMemberships
            .Include(m => m.Organization)
            .Where(m => m.UserId == id && m.IsActive)
            .OrderByDescending(m => m.IsPrimary)
            .ThenBy(m => m.JoinedAtUtc)
            .FirstOrDefaultAsync();

        var userOrgType   = primaryMembership?.Organization.OrgType;
        var userOrgTypeId = primaryMembership?.Organization.OrganizationTypeId;

        // Get enabled products for this tenant
        var enabledProductIds = await db.TenantProducts
            .Where(tp => tp.TenantId == opTenantId && tp.IsEnabled)
            .Select(tp => tp.ProductId)
            .ToListAsync();
        var enabledProductSet = new HashSet<Guid>(enabledProductIds);

        // Get all product roles with their rules
        var productRoles = await db.ProductRoles
            .Include(pr => pr.Product)
            .Include(pr => pr.OrgTypeRules)
                .ThenInclude(r => r.OrganizationType)
            .Where(pr => pr.IsActive)
            .ToListAsync();

        // Build code → ProductRole lookup
        var productRoleLookup = productRoles.ToDictionary(pr => pr.Code, StringComparer.OrdinalIgnoreCase);

        // All roles
        var allRoles = await db.Roles
            .OrderBy(r => r.IsSystemRole ? 0 : 1)
            .ThenBy(r => r.Name)
            .ToListAsync();

        // Currently assigned role IDs (system/tenant roles via ScopedRoleAssignment)
        var assignedRoleIds = await db.ScopedRoleAssignments
            .Where(s => s.UserId == id && s.IsActive
                     && s.ScopeType == ScopedRoleAssignment.ScopeTypes.Global)
            .Select(s => s.RoleId)
            .ToListAsync();
        var assignedSet = new HashSet<Guid>(assignedRoleIds);

        // LS-COR-AUT-010: Product roles are stored in UserRoleAssignment (not ScopedRoleAssignment).
        // Build a set of assigned product role codes so product roles show as assigned in the UI.
        var assignedProductRoleCodes = await db.UserRoleAssignments
            .Where(a => a.TenantId == opTenantId && a.UserId == id && a.AssignmentStatus == AssignmentStatus.Active)
            .Select(a => a.RoleCode)
            .ToListAsync();
        var assignedProductRoleCodeSet = new HashSet<string>(assignedProductRoleCodes, StringComparer.OrdinalIgnoreCase);

        var result = allRoles.Select(r =>
        {
            productRoleLookup.TryGetValue(r.Name, out var pr);
            var isProductRole = !r.IsSystemRole && pr is not null;
            // LS-COR-AUT-010: Product roles live in UserRoleAssignment; system/tenant roles in ScopedRoleAssignment.
            var isAssigned = isProductRole
                ? assignedProductRoleCodeSet.Contains(r.Name)
                : assignedSet.Contains(r.Id);

            string? productCode = null;
            string? productName = null;
            List<string>? allowedOrgTypes = null;
            var assignable = true;
            string? disabledReason = null;

            if (isProductRole && pr is not null)
            {
                productCode = pr.Product.Code;
                productName = pr.Product.Name;
                allowedOrgTypes = pr.OrgTypeRules
                    .Where(rule => rule.IsActive)
                    .Select(rule => rule.OrganizationType.Code)
                    .ToList();

                // Check product enablement
                if (!enabledProductSet.Contains(pr.ProductId))
                {
                    assignable = false;
                    disabledReason = $"Product '{pr.Product.Name}' is not enabled for this tenant.";
                }
                // Check org type eligibility
                else if (primaryMembership is null)
                {
                    assignable = false;
                    disabledReason = "User has no organization membership.";
                }
                else if (allowedOrgTypes.Count > 0)
                {
                    var orgTypeAllowed = userOrgTypeId.HasValue
                        ? pr.OrgTypeRules.Any(rule => rule.IsActive && rule.OrganizationTypeId == userOrgTypeId.Value)
                        : pr.OrgTypeRules.Any(rule => rule.IsActive &&
                            rule.OrganizationType.Code.Equals(userOrgType ?? "", StringComparison.OrdinalIgnoreCase));

                    if (!orgTypeAllowed)
                    {
                        assignable = false;
                        disabledReason = $"Requires org type: {string.Join(", ", allowedOrgTypes)}. User org is '{userOrgType}'.";
                    }
                }
            }

            if (isAssigned)
            {
                assignable = false;
                disabledReason = "Already assigned.";
            }

            return new
            {
                id             = r.Id,
                name           = r.Name,
                description    = r.Description ?? "",
                isSystemRole   = r.IsSystemRole,
                isProductRole,
                productCode,
                productName,
                allowedOrgTypes,
                assignable,
                disabledReason,
                isAssigned,
            };
        });

        return Results.Ok(new
        {
            items       = result,
            userOrgType = userOrgType ?? "UNKNOWN",
            tenantEnabledProducts = enabledProductIds.Count,
        });
    }

    /// <summary>
    /// GET /api/admin/users/{id}/scoped-roles
    ///
    /// Phase I: returns all active scoped role assignments for a user, grouped by
    /// scope type.  Demonstrates real non-global scope visibility at the API layer.
    ///
    /// Returns 200 with the scoped role summary, 404 if the user is not found.
    /// </summary>
    private static async Task<IResult> GetScopedRoles(
        Guid                        id,
        IScopedAuthorizationService scopedAuth,
        IdentityDbContext            db,
        CancellationToken            ct)
    {
        var exists = await db.Users.AnyAsync(u => u.Id == id, ct);
        if (!exists) return Results.NotFound(new { error = $"User '{id}' not found." });

        var summary = await scopedAuth.GetScopedRoleSummaryAsync(id, ct);

        return Results.Ok(new
        {
            userId      = summary.UserId,
            totalActive = summary.TotalActive,
            assignments = summary.Assignments.Select(a => new
            {
                assignmentId               = a.AssignmentId,
                roleName                   = a.RoleName,
                scopeType                  = a.ScopeType,
                organizationId             = a.OrganizationId,
                productId                  = a.ProductId,
                organizationRelationshipId = a.OrganizationRelationshipId,
                tenantId                   = a.TenantId,
            }),
            byScope = summary.Assignments
                .GroupBy(a => a.ScopeType)
                .ToDictionary(
                    g => g.Key.ToLowerInvariant(),
                    g => g.Count()),
        });
    }

    /// <summary>
    /// DELETE /api/admin/users/{id}/roles/{roleId}
    ///
    /// Revokes a role from a user.  Deactivates the GLOBAL ScopedRoleAssignment.
    ///
    /// Returns 204 No Content on success, 404 if user or assignment not found.
    /// </summary>
    private static async Task<IResult> RevokeRole(
        Guid                      id,
        Guid                      roleId,
        ClaimsPrincipal           caller,
        IdentityDbContext         db,
        IAuditEventClient         auditClient,
        INotificationsCacheClient notificationsCache)
    {
        var callerId = caller.FindFirstValue(ClaimTypes.NameIdentifier) ?? caller.FindFirstValue("sub");

        var user = await db.Users.FindAsync(id);
        if (user is null) return Results.NotFound(new { error = $"User '{id}' not found." });

        // UIX-003-01: TenantAdmin may only revoke roles within their own tenant.
        if (await IsCrossTenantAccessAsync(caller, user.Id, db)) return Results.Forbid();

        var opTenantId = await GetOperationalTenantIdAsync(caller, user.Id, db);

        // Phase G: deactivate the GLOBAL ScopedRoleAssignment (sole authoritative record).
        var sra = await db.ScopedRoleAssignments
            .Include(s => s.Role)
            .FirstOrDefaultAsync(s => s.UserId == id && s.RoleId == roleId
                                   && s.ScopeType == ScopedRoleAssignment.ScopeTypes.Global && s.IsActive);

        // LS-COR-AUT-010: Product roles are stored as UserRoleAssignment (not ScopedRoleAssignment).
        // If the GLOBAL ScopedRoleAssignment doesn't exist, check whether this is a product role
        // and fall back to the UserRoleAssignment table.
        if (sra is null)
        {
            var roleForRevoke = await db.Roles.FindAsync(roleId);
            if (roleForRevoke is not null && !roleForRevoke.IsSystemRole)
            {
                var productRoleForRevoke = await db.ProductRoles
                    .Include(pr => pr.Product)
                    .FirstOrDefaultAsync(pr => pr.Code == roleForRevoke.Name && pr.IsActive);

                if (productRoleForRevoke is not null)
                {
                    var ura = await db.UserRoleAssignments
                        .FirstOrDefaultAsync(a =>
                            a.TenantId         == opTenantId &&
                            a.UserId           == id &&
                            a.ProductCode      == productRoleForRevoke.Product.Code &&
                            a.RoleCode         == productRoleForRevoke.Code &&
                            a.AssignmentStatus == AssignmentStatus.Active);
                    if (ura is null)
                        return Results.NotFound(new { error = $"Role '{roleId}' is not assigned to user '{id}'." });

                    ura.Remove();
                    user.IncrementAccessVersion();
                    await db.SaveChangesAsync();

                    var urAuditNow = DateTimeOffset.UtcNow;
                    _ = auditClient.IngestAsync(new IngestAuditEventRequest
                    {
                        EventType     = "identity.role.removed",
                        EventCategory = EventCategory.Administrative,
                        SourceSystem  = "identity-service",
                        SourceService = "admin-api",
                        Visibility    = VisibilityScope.Tenant,
                        Severity      = SeverityLevel.Warn,
                        OccurredAtUtc = urAuditNow,
                        Scope  = new AuditEventScopeDto { ScopeType = ScopeType.Tenant, TenantId = opTenantId.ToString() },
                        Actor  = new AuditEventActorDto { Id = callerId, Type = ActorType.User },
                        Entity = new AuditEventEntityDto { Type = "User", Id = id.ToString() },
                        Action      = "ProductRoleRemoved",
                        Description = $"Product role '{productRoleForRevoke.Code}' ({productRoleForRevoke.Product.Code}) removed from user {id}.",
                        Before      = JsonSerializer.Serialize(new { roleCode = productRoleForRevoke.Code, productCode = productRoleForRevoke.Product.Code }),
                        IdempotencyKey = IdempotencyKey.For("identity-service", "identity.role.removed", id.ToString(), roleId.ToString()),
                        Tags = ["role-management", "access-control"],
                    });

                    notificationsCache.InvalidateTenant(
                        opTenantId,
                        eventType: "identity.role.removed",
                        reason:    $"product role {productRoleForRevoke.Code} removed from user {id}");

                    return Results.NoContent();
                }
            }

            return Results.NotFound(new { error = $"Role '{roleId}' is not assigned to user '{id}'." });
        }

        var roleName = sra.Role?.Name ?? roleId.ToString();

        // LS-ID-TNT-005: Backend last-admin protection.
        // Block removal of the TenantAdmin role if this user is the only remaining
        // active TenantAdmin for their tenant.  The user must currently be active
        // for their admin status to count toward the tenant minimum.
        if (roleName == "TenantAdmin" && user.IsActive)
        {
            var otherActiveAdmins = await CountOtherActiveTenantAdmins(db, id, opTenantId);
            if (otherActiveAdmins == 0)
                return Results.UnprocessableEntity(new
                {
                    error = "This action is not allowed because the user is the last active tenant administrator.",
                    code  = "LAST_ACTIVE_ADMIN",
                });
        }

        sra.Deactivate();

        await db.SaveChangesAsync();

        // Canonical audit — fire-and-observe, never gates the response.
        var auditNow = DateTimeOffset.UtcNow;
        _ = auditClient.IngestAsync(new IngestAuditEventRequest
        {
            EventType     = "identity.role.removed",
            EventCategory = EventCategory.Administrative,
            SourceSystem  = "identity-service",
            SourceService = "admin-api",
            Visibility    = VisibilityScope.Tenant,
            Severity      = SeverityLevel.Warn,
            OccurredAtUtc = auditNow,
            Scope = new AuditEventScopeDto { ScopeType = ScopeType.Tenant, TenantId = opTenantId.ToString() },
            Actor = new AuditEventActorDto { Id = callerId, Type = ActorType.User },
            Entity = new AuditEventEntityDto { Type = "User", Id = id.ToString() },
            Action      = "RoleRemoved",
            Description = $"Role '{roleName}' removed from user {id}.",
            Before      = JsonSerializer.Serialize(new { roleId, roleName }),
            IdempotencyKey = IdempotencyKey.For("identity-service", "identity.role.removed", id.ToString(), roleId.ToString()),
            Tags = ["role-management", "access-control"],
        });

        // Role membership for this tenant changed — invalidate the notifications
        // service's cache so the next role-addressed fan-out drops this user.
        notificationsCache.InvalidateTenant(
            opTenantId,
            eventType: "identity.role.removed",
            reason:    $"role {roleId} removed from user {id}");

        return Results.NoContent();
    }

    /// <summary>
    /// GET /api/admin/platform-readiness
    ///
    /// Returns a cross-domain readiness summary covering Phase G completion status,
    /// OrgType consistency, product-role eligibility coverage, and role assignment
    /// depth — for the platform operations dashboard.
    ///
    /// Returns 200 with the readiness payload (never 404/500 — issues surface as
    /// degraded/false flags inside the response so the dashboard always renders).
    /// </summary>
    private static async Task<IResult> GetPlatformReadiness(
        IdentityDbContext db,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // ── 1. Phase G completion ─────────────────────────────────────────────
        // Phase G removed UserRoles / UserRoleAssignments tables and established
        // ScopedRoleAssignments (GLOBAL scope) as the sole authoritative role source.
        var totalScopedActive   = await db.ScopedRoleAssignments.CountAsync(s => s.IsActive,              ct);
        var globalScopedActive  = await db.ScopedRoleAssignments.CountAsync(s => s.IsActive && s.ScopeType == ScopedRoleAssignment.ScopeTypes.Global, ct);
        var usersWithScopedRole = await db.ScopedRoleAssignments
            .Where(s => s.IsActive)
            .Select(s => s.UserId)
            .Distinct()
            .CountAsync(ct);

        // ── 2. OrgType consistency ────────────────────────────────────────────
        var orgRows = await db.Organizations
            .Where(o => o.IsActive)
            .Select(o => new { o.OrganizationTypeId, o.OrgType })
            .ToListAsync(ct);

        var totalActiveOrgs       = orgRows.Count;
        var orgsWithTypeId        = orgRows.Count(o => o.OrganizationTypeId.HasValue);
        var orgsWithMissingTypeId = orgRows.Count(o => o.OrganizationTypeId == null && !string.IsNullOrWhiteSpace(o.OrgType));
        var orgsWithCodeMismatch  = orgRows.Count(o =>
        {
            if (o.OrganizationTypeId == null) return false;
            var code = OrgTypeMapper.TryResolveCode(o.OrganizationTypeId);
            return code is not null && !string.Equals(code, o.OrgType, StringComparison.OrdinalIgnoreCase);
        });
        var orgTypeConsistent = orgsWithMissingTypeId == 0 && orgsWithCodeMismatch == 0;

        // ── 3. ProductRole eligibility coverage ──────────────────────────────
        var totalActiveProductRoles = await db.ProductRoles.CountAsync(r => r.IsActive, ct);
        var productRolesWithOrgRule = await db.ProductOrganizationTypeRules
            .Where(r => r.IsActive)
            .Select(r => r.ProductRoleId)
            .Distinct()
            .CountAsync(ct);
        var productRolesUnrestricted = totalActiveProductRoles - productRolesWithOrgRule;
        var eligibilityCoveragePct   = totalActiveProductRoles == 0
            ? 100.0
            : Math.Round((double)productRolesWithOrgRule / totalActiveProductRoles * 100.0, 1);

        // ── 4. Org-relationship coverage ──────────────────────────────────────
        var totalOrgRelationships   = await db.OrganizationRelationships.CountAsync(ct);
        var activeOrgRelationships  = await db.OrganizationRelationships.CountAsync(r => r.IsActive, ct);

        // ── 5. Phase I: scoped assignments by scope type ──────────────────────
        // Shows how many active SRAs exist per scope level.  After Phase I,
        // non-GLOBAL counts above zero prove the schema is being exercised at runtime.
        var scopeTypeCounts = await db.ScopedRoleAssignments
            .Where(s => s.IsActive)
            .GroupBy(s => s.ScopeType)
            .Select(g => new { ScopeType = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        int ScopeCount(string t) =>
            scopeTypeCounts.FirstOrDefault(g => g.ScopeType == t)?.Count ?? 0;

        var scopedGlobal       = ScopeCount(ScopedRoleAssignment.ScopeTypes.Global);
        var scopedOrg          = ScopeCount("ORGANIZATION");
        var scopedProduct      = ScopeCount("PRODUCT");
        var scopedRelationship = ScopeCount("RELATIONSHIP");
        var scopedTenant       = ScopeCount("TENANT");

        return Results.Ok(new
        {
            generatedAtUtc = now,

            phaseGCompletion = new
            {
                userRolesRetired              = true,      // migration 200004 executed — tables dropped
                soleRoleSourceIsSra           = true,
                totalActiveScopedAssignments  = totalScopedActive,
                globalScopedAssignments       = globalScopedActive,
                usersWithScopedRole,
            },

            orgTypeCoverage = new
            {
                totalActiveOrgs,
                orgsWithOrganizationTypeId = orgsWithTypeId,
                orgsWithMissingTypeId,
                orgsWithCodeMismatch,
                consistent                 = orgTypeConsistent,
                coveragePct                = totalActiveOrgs == 0
                    ? 100.0
                    : Math.Round((double)orgsWithTypeId / totalActiveOrgs * 100.0, 1),
            },

            productRoleEligibility = new
            {
                totalActiveProductRoles,
                withOrgTypeRule     = productRolesWithOrgRule,
                unrestricted        = productRolesUnrestricted,
                coveragePct         = eligibilityCoveragePct,
            },

            orgRelationships = new
            {
                total  = totalOrgRelationships,
                active = activeOrgRelationships,
            },

            // Phase I: active SRAs by scope type — non-zero org/product/relationship
            // values confirm that real non-global scope enforcement is in use.
            scopedAssignmentsByScope = new
            {
                global       = scopedGlobal,
                organization = scopedOrg,
                product      = scopedProduct,
                relationship = scopedRelationship,
                tenant       = scopedTenant,
            },
        });
    }

    // =========================================================================
    // UIX-002: USER ACTIVATION
    // =========================================================================

    /// <summary>
    /// POST /api/admin/users/{id}/activate
    /// Sets the user's IsActive flag to true. Idempotent.
    /// Emits identity.user.activated audit event.
    /// </summary>
    private static async Task<IResult> ActivateUser(
        Guid                      id,
        ClaimsPrincipal           caller,
        IdentityDbContext         db,
        IAuditEventClient         auditClient,
        INotificationsCacheClient notificationsCache,
        CancellationToken         ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);

        if (user is null) return Results.NotFound(new { error = $"User '{id}' not found." });

        // UIX-003-01: TenantAdmin may only activate users within their own tenant.
        if (await IsCrossTenantAccessAsync(caller, user.Id, db, ct)) return Results.Forbid();

        var opTenantId = await GetOperationalTenantIdAsync(caller, user.Id, db, ct);
        var changed = user.Activate();
        if (!changed) return Results.NoContent();

        await db.SaveChangesAsync(ct);

        var now = DateTimeOffset.UtcNow;
        _ = auditClient.IngestAsync(new IngestAuditEventRequest
        {
            EventType     = "identity.user.activated",
            EventCategory = EventCategory.Administrative,
            SourceSystem  = "identity-service",
            SourceService = "admin-api",
            Visibility    = VisibilityScope.Tenant,
            Severity      = SeverityLevel.Info,
            OccurredAtUtc = now,
            Scope = new AuditEventScopeDto { ScopeType = ScopeType.Tenant, TenantId = opTenantId.ToString() },
            Actor = new AuditEventActorDto { Type = ActorType.System, Name = "admin-api" },
            Entity = new AuditEventEntityDto { Type = "User", Id = user.Id.ToString() },
            Action      = "UserActivated",
            Description = $"User '{user.Email}' activated in tenant {opTenantId}.",
            Before      = JsonSerializer.Serialize(new { isActive = false, email = user.Email }),
            After       = JsonSerializer.Serialize(new { isActive = true,  email = user.Email }),
            IdempotencyKey = IdempotencyKey.For("identity-service", "identity.user.activated", user.Id.ToString()),
            Tags = ["user-management", "lifecycle", "activation"],
        });

        // Membership state changed for this tenant — drop the notifications
        // service's cached role/org member lists so role-addressed alerts
        // immediately resume including the reactivated user.
        notificationsCache.InvalidateTenant(
            opTenantId,
            eventType: "identity.user.activated",
            reason:    $"user {user.Id} activated");

        return Results.NoContent();
    }

    // =========================================================================
    // UIX-002: INVITE USER
    // =========================================================================

    /// <summary>
    /// POST /api/admin/users/invite
    /// Creates a new inactive user record and a pending UserInvitation.
    /// The invitation token is logged to the console in non-production (no email sender yet).
    /// Returns 201 with the new userId and invitationId.
    /// </summary>
    private static async Task<IResult> InviteUser(
        InviteUserRequest                     body,
        ClaimsPrincipal                       caller,
        IdentityDbContext                     db,
        IPasswordHasher                       passwordHasher,
        IAuditEventClient                     auditClient,
        IOptions<NotificationsServiceOptions> notifOptions,
        INotificationsEmailClient             emailClient,
        IWebHostEnvironment                   env,
        ILoggerFactory                        loggerFactory,
        CancellationToken                     ct)
    {
        if (string.IsNullOrWhiteSpace(body.Email))
            return Results.BadRequest(new { error = "email is required." });
        if (string.IsNullOrWhiteSpace(body.FirstName))
            return Results.BadRequest(new { error = "firstName is required." });
        if (string.IsNullOrWhiteSpace(body.LastName))
            return Results.BadRequest(new { error = "lastName is required." });
        if (body.TenantId == Guid.Empty)
            return Results.BadRequest(new { error = "tenantId is required." });

        var tenant = await db.Tenants.FindAsync([body.TenantId], ct);
        if (tenant is null) return Results.NotFound(new { error = $"Tenant '{body.TenantId}' not found." });

        // UIX-003-01: TenantAdmin may only invite users into their own tenant.
        if (IsCrossTenantAccess(caller, body.TenantId)) return Results.Forbid();

        var emailLower = body.Email.ToLowerInvariant().Trim();
        var existingUser = await db.Users
            .FirstOrDefaultAsync(u => u.Email == emailLower, ct);

        var alreadyInTenant = existingUser is not null
            && await db.UserTenants.AnyAsync(
                ut => ut.UserId == existingUser.Id && ut.TenantId == body.TenantId && ut.IsActive, ct);
        if (alreadyInTenant)
            return Results.Conflict(new { error = $"User with email '{emailLower}' already exists in this tenant." });

        var user = existingUser;
        if (user is null)
        {
            // Create user as inactive (not yet accepted invite).
            var tempPasswordHash = passwordHasher.Hash(Guid.CreateVersion7().ToString());
            user = User.Create(body.TenantId, emailLower, tempPasswordHash, body.FirstName.Trim(), body.LastName.Trim());
            user.Deactivate();
            db.Users.Add(user);
        }

        db.UserTenants.Add(UserTenant.Create(user.Id, body.TenantId));

        var orgDetailsPresent = !string.IsNullOrWhiteSpace(body.OrganizationName);
        Guid? explicitOrganizationId = null;
        if (orgDetailsPresent)
        {
            var orgType = string.IsNullOrWhiteSpace(body.OrganizationType)
                ? OrgType.LawFirm
                : body.OrganizationType.Trim().ToUpperInvariant();

            if (!OrgType.IsValid(orgType))
                return Results.BadRequest(new { error = $"organizationType '{body.OrganizationType}' is not valid." });

            var orgName = body.OrganizationName!.Trim();
            var org = await db.Organizations
                .FirstOrDefaultAsync(o => o.TenantId == body.TenantId
                                       && o.OrgType == orgType
                                       && o.Name == orgName, ct);

            if (org is null)
            {
                org = Organization.Create(
                    tenantId:    body.TenantId,
                    name:        orgName,
                    orgType:     orgType,
                    displayName: string.IsNullOrWhiteSpace(body.OrganizationDisplayName)
                        ? orgName
                        : body.OrganizationDisplayName.Trim());
                // The invited user is the founding member of this brand-new org.
                org.SetOwner(user.Id);
                db.Organizations.Add(org);
            }

            await EnsureUserOrganizationMembershipForInviteAsync(db, user.Id, org.Id, ct);
            explicitOrganizationId = org.Id;
        }

        // Assign initial role if provided.
        if (body.RoleId.HasValue && body.RoleId.Value != Guid.Empty)
        {
            var role = await db.Roles.FindAsync([body.RoleId.Value], ct);
            if (role is not null)
            {
                var sra = ScopedRoleAssignment.Create(user.Id, role.Id, ScopedRoleAssignment.ScopeTypes.Global, tenantId: body.TenantId, assignedByUserId: body.InvitedByUserId);
                db.ScopedRoleAssignments.Add(sra);
            }
        }

        if (existingUser is { IsActive: true })
        {
            await db.SaveChangesAsync(ct);

            var existingLogger = loggerFactory.CreateLogger("AdminEndpoints.InviteUser");
            var portalUrl = TenantPortalUrlHelper.BuildBaseUrl(tenant, notifOptions.Value);
            if (portalUrl is not null)
            {
                var displayName = $"{user.FirstName} {user.LastName}".Trim();
                var (_, emailOk, accessEmailError) = await emailClient.SendTenantAccessGrantedEmailAsync(
                    emailLower, displayName, tenant.Name, portalUrl, body.TenantId, ct);
                if (!emailOk)
                    existingLogger.LogWarning(
                        "Tenant-access-granted email not sent for existing active user {UserId} ({Email}): {Error}",
                        user.Id, emailLower, accessEmailError);
            }

            return Results.Ok(new
            {
                userId = user.Id,
                invitationId = (Guid?)null,
                email = emailLower,
                isNew = false,
                organizationId = explicitOrganizationId,
            });
        }

        // Create invitation token (raw token logged; hash stored).
        var rawToken   = Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N");
        var tokenHash  = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawToken)));
        var invitation = UserInvitation.Create(user.Id, body.TenantId, tokenHash, UserInvitation.PortalOrigins.TenantPortal, body.InvitedByUserId);
        db.UserInvitations.Add(invitation);

        await db.SaveChangesAsync(ct);

        var now = DateTimeOffset.UtcNow;
        _ = auditClient.IngestAsync(new IngestAuditEventRequest
        {
            EventType     = "identity.user.invited",
            EventCategory = EventCategory.Administrative,
            SourceSystem  = "identity-service",
            SourceService = "admin-api",
            Visibility    = VisibilityScope.Tenant,
            Severity      = SeverityLevel.Info,
            OccurredAtUtc = now,
            Scope = new AuditEventScopeDto { ScopeType = ScopeType.Tenant, TenantId = body.TenantId.ToString() },
            Actor = new AuditEventActorDto { Type = ActorType.System, Name = "admin-api" },
            Entity = new AuditEventEntityDto { Type = "User", Id = user.Id.ToString() },
            Action      = "UserInvited",
            Description = $"User '{emailLower}' invited to tenant {body.TenantId}.",
            IdempotencyKey = IdempotencyKey.For("identity-service", "identity.user.invited", invitation.Id.ToString()),
            Tags = ["user-management", "invite"],
        });

        // LS-ID-TNT-016-01: Build tenant-subdomain-aware activation link.
        // tenant is already resolved above (FindAsync on body.TenantId).
        var logger         = loggerFactory.CreateLogger("AdminEndpoints.InviteUser");
        var activationLink = TenantPortalUrlHelper.Build(tenant, "accept-invite", rawToken, notifOptions.Value);

        if (activationLink is null)
        {
            logger.LogError(
                "[LS-ID-TNT-016-01] Neither PortalBaseDomain nor PortalBaseUrl is configured. " +
                "Invitation email for user {UserId} ({Email}, tenant={TenantId}) cannot be sent. " +
                "Set NotificationsService:PortalBaseDomain (or PortalBaseUrl) in configuration.",
                user.Id, emailLower, body.TenantId);
            return Results.Problem(
                "User created but invitation email could not be sent: portal URL is not configured. " +
                "Configure NotificationsService:PortalBaseDomain or NotificationsService:PortalBaseUrl " +
                "so invitation links can be generated.",
                statusCode: 503);
        }
        var displayNameStr = $"{user.FirstName} {user.LastName}".Trim();

        var (emailConfigured, emailSuccess, emailError) = await emailClient.SendInviteEmailAsync(
            emailLower, displayNameStr, activationLink, body.TenantId, ct);

        if (!emailConfigured)
        {
            logger.LogError(
                "[LS-ID-TNT-007] NotificationsService:BaseUrl is not configured. " +
                "Invitation email for user {UserId} ({Email}, tenant={TenantId}) was not sent. " +
                "Set NotificationsService:BaseUrl in configuration.",
                user.Id, emailLower, body.TenantId);
            return Results.Problem(
                "User created but invitation email could not be sent: the Notifications service is not configured. " +
                "Configure NotificationsService:BaseUrl so emails can be dispatched.",
                statusCode: 503);
        }

        if (!emailSuccess)
        {
            return Results.Problem(
                $"User created but invitation email could not be sent: {emailError}",
                statusCode: 502);
        }

        // Non-production: return raw token and activation link so the admin can hand-deliver the link.
        if (!env.IsProduction())
        {
            Console.WriteLine($"[INVITE TOKEN — dev only] userId={user.Id} token={rawToken} link={activationLink}");
            return Results.Created(
                $"/api/admin/users/{user.Id}",
                new { userId = user.Id, invitationId = invitation.Id, email = emailLower, inviteToken = rawToken, activationLink });
        }

        return Results.Created(
            $"/api/admin/users/{user.Id}",
            new { userId = user.Id, invitationId = invitation.Id, email = emailLower });
    }

    private static async Task<UserOrganizationMembership> EnsureUserOrganizationMembershipForInviteAsync(
        IdentityDbContext db,
        Guid userId,
        Guid organizationId,
        CancellationToken ct)
    {
        var existing = await db.UserOrganizationMemberships
            .FirstOrDefaultAsync(m => m.UserId == userId && m.OrganizationId == organizationId, ct);

        if (existing is not null)
            return existing;

        var orgTenantId = await db.Organizations
            .AsNoTracking()
            .Where(o => o.Id == organizationId)
            .Select(o => o.TenantId)
            .FirstOrDefaultAsync(ct);

        if (orgTenantId.HasValue)
        {
            var hasTenantMembershipInTracker = db.UserTenants.Local.Any(
                ut => ut.UserId == userId && ut.TenantId == orgTenantId.Value && ut.IsActive);

            var hasTenantMembership = hasTenantMembershipInTracker
                || await db.UserTenants.AnyAsync(
                    ut => ut.UserId == userId && ut.TenantId == orgTenantId.Value && ut.IsActive, ct);

            if (!hasTenantMembership)
                throw new InvalidOperationException(
                    $"User '{userId}' must belong to tenant '{orgTenantId.Value}' before joining tenant-scoped organization '{organizationId}'.");
        }

        var membership = UserOrganizationMembership.Create(userId, organizationId, MemberRole.Member);
        db.UserOrganizationMemberships.Add(membership);
        return membership;
    }

    /// <summary>
    /// PUM-B06: POST /api/admin/platform-users/invite
    /// Invites a new PlatformInternal (LegalSynq staff) user.
    /// Unlike InviteUser, no tenantId is required from the caller — the backend resolves
    /// the platform system tenant (first active tenant by CreatedAtUtc).
    /// </summary>
    private static async Task<IResult> InvitePlatformUser(
        InvitePlatformUserRequest             body,
        ClaimsPrincipal                       caller,
        IdentityDbContext                     db,
        IPasswordHasher                       passwordHasher,
        IAuditEventClient                     auditClient,
        IOptions<NotificationsServiceOptions> notifOptions,
        INotificationsEmailClient             emailClient,
        IWebHostEnvironment                   env,
        ILoggerFactory                        loggerFactory,
        CancellationToken                     ct)
    {
        if (string.IsNullOrWhiteSpace(body.Email))
            return Results.BadRequest(new { error = "email is required." });
        if (string.IsNullOrWhiteSpace(body.FirstName))
            return Results.BadRequest(new { error = "firstName is required." });
        if (string.IsNullOrWhiteSpace(body.LastName))
            return Results.BadRequest(new { error = "lastName is required." });

        // Resolve the platform system tenant (first active tenant by age).
        var platformTenantId = await db.Tenants
            .Where(t => t.IsActive)
            .OrderBy(t => t.CreatedAtUtc)
            .Select(t => t.Id)
            .FirstOrDefaultAsync(ct);

        if (platformTenantId == Guid.Empty)
            return Results.Problem(
                detail:     "No active tenant found — cannot create platform user.",
                statusCode: 500,
                title:      "Platform Tenant Missing");

        var emailLower = body.Email.ToLowerInvariant().Trim();

        // Reject if this email already exists anywhere in the system.
        var existing = await db.Users.AnyAsync(u => u.Email == emailLower, ct);
        if (existing)
            return Results.Conflict(new { error = $"A user with email '{emailLower}' already exists." });

        // Create user as PlatformInternal + inactive (pending invite acceptance).
        var tempPasswordHash = passwordHasher.Hash(Guid.CreateVersion7().ToString());
        var user = User.Create(
            platformTenantId,
            emailLower,
            tempPasswordHash,
            body.FirstName.Trim(),
            body.LastName.Trim(),
            userType: Identity.Domain.UserType.PlatformInternal);
        user.Deactivate();
        db.Users.Add(user);
        db.UserTenants.Add(UserTenant.Create(user.Id, platformTenantId));

        // Assign initial Platform role if provided.
        if (body.RoleId.HasValue && body.RoleId.Value != Guid.Empty)
        {
            var role = await db.Roles.FindAsync([body.RoleId.Value], ct);
            if (role is not null)
            {
                var sra = ScopedRoleAssignment.Create(
                    user.Id, role.Id,
                    ScopedRoleAssignment.ScopeTypes.Global,
                    tenantId: platformTenantId,
                    assignedByUserId: null);
                db.ScopedRoleAssignments.Add(sra);
            }
        }

        // Create invitation token.
        var rawToken   = Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N");
        var tokenHash  = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(rawToken)));
        var invitation = UserInvitation.Create(
            user.Id, platformTenantId, tokenHash,
            UserInvitation.PortalOrigins.TenantPortal,
            invitedByUserId: null);
        db.UserInvitations.Add(invitation);

        await db.SaveChangesAsync(ct);

        var now = DateTimeOffset.UtcNow;
        _ = auditClient.IngestAsync(new IngestAuditEventRequest
        {
            EventType     = "identity.platform_user.invited",
            EventCategory = EventCategory.Administrative,
            SourceSystem  = "identity-service",
            SourceService = "admin-api",
            Visibility    = VisibilityScope.Platform,
            Severity      = SeverityLevel.Info,
            OccurredAtUtc = now,
            Scope = new AuditEventScopeDto { ScopeType = ScopeType.Platform },
            Actor = new AuditEventActorDto { Type = ActorType.System, Name = "admin-api" },
            Entity = new AuditEventEntityDto { Type = "User", Id = user.Id.ToString() },
            Action      = "PlatformUserInvited",
            Description = $"Platform user '{emailLower}' invited.",
            IdempotencyKey = IdempotencyKey.For("identity-service", "identity.platform_user.invited", invitation.Id.ToString()),
            Tags = ["user-management", "platform-user", "invite"],
        });

        // Build activation link via tenant portal helper.
        var logger         = loggerFactory.CreateLogger("AdminEndpoints.InvitePlatformUser");
        var platformTenant = await db.Tenants.FindAsync([platformTenantId], ct);
        string? activationLink = null;
        if (platformTenant is not null)
        {
            activationLink = TenantPortalUrlHelper.Build(
                platformTenant, "accept-invite", rawToken, notifOptions.Value);

            // Send invite email (best-effort).
            if (!string.IsNullOrWhiteSpace(activationLink))
            {
                var displayName = $"{body.FirstName.Trim()} {body.LastName.Trim()}".Trim();
                var (_, _, emailError) = await emailClient.SendInviteEmailAsync(
                    emailLower, displayName, activationLink, platformTenantId, ct);
                if (emailError is not null)
                    logger.LogWarning(
                        "PUM-B06: Invite email to platform user {Email} failed: {Error}",
                        emailLower, emailError);
            }
        }

        if (!env.IsProduction() && activationLink is not null)
            return Results.Created(
                $"/api/admin/users/{user.Id}",
                new { userId = user.Id, invitationId = invitation.Id, email = emailLower, activationLink });

        return Results.Created(
            $"/api/admin/users/{user.Id}",
            new { userId = user.Id, invitationId = invitation.Id, email = emailLower });
    }

    /// <summary>
    /// POST /api/admin/users/{id}/resend-invite
    /// Revokes all pending invitations for the user, creates a new one.
    /// </summary>
    private static async Task<IResult> ResendInvite(
        Guid                                  id,
        ClaimsPrincipal                       caller,
        IdentityDbContext                     db,
        IAuditEventClient                     auditClient,
        IOptions<NotificationsServiceOptions> notifOptions,
        INotificationsEmailClient             emailClient,
        IWebHostEnvironment                   env,
        ILoggerFactory                        loggerFactory,
        CancellationToken                     ct)
    {
        var user = await db.Users.FindAsync([id], ct);
        if (user is null) return Results.NotFound(new { error = $"User '{id}' not found." });

        // UIX-003-01: TenantAdmin may only resend invites for users in their own tenant.
        if (await IsCrossTenantAccessAsync(caller, user.Id, db, ct)) return Results.Forbid();

        var opTenantId = await GetOperationalTenantIdAsync(caller, user.Id, db, ct);
        var pending = await db.UserInvitations
            .Where(i => i.UserId == id && i.Status == UserInvitation.Statuses.Pending)
            .ToListAsync(ct);

        foreach (var inv in pending) inv.Revoke();

        var rawToken  = Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N");
        var tokenHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawToken)));
        var newInvite = UserInvitation.Create(id, opTenantId, tokenHash, UserInvitation.PortalOrigins.TenantPortal);
        db.UserInvitations.Add(newInvite);

        await db.SaveChangesAsync(ct);

        _ = auditClient.IngestAsync(new IngestAuditEventRequest
        {
            EventType     = "identity.user.invite_resent",
            EventCategory = EventCategory.Administrative,
            SourceSystem  = "identity-service",
            SourceService = "admin-api",
            Visibility    = VisibilityScope.Tenant,
            Severity      = SeverityLevel.Info,
            OccurredAtUtc = DateTimeOffset.UtcNow,
            Scope = new AuditEventScopeDto { ScopeType = ScopeType.Tenant, TenantId = opTenantId.ToString() },
            Actor = new AuditEventActorDto { Type = ActorType.System, Name = "admin-api" },
            Entity = new AuditEventEntityDto { Type = "User", Id = id.ToString() },
            Action      = "InviteResent",
            Description = $"Invite resent for user '{user.Email}'.",
            IdempotencyKey = IdempotencyKey.For("identity-service", "identity.user.invite_resent", newInvite.Id.ToString()),
            Tags = ["user-management", "invite"],
        });

        // LS-ID-TNT-016-01: Build tenant-subdomain-aware activation link.
        var logger        = loggerFactory.CreateLogger("AdminEndpoints.ResendInvite");
        var inviteTenant  = await db.Tenants.FindAsync([opTenantId], ct);
        var activationLink = TenantPortalUrlHelper.Build(inviteTenant, "accept-invite", rawToken, notifOptions.Value);

        if (activationLink is null)
        {
            logger.LogError(
                "[LS-ID-TNT-016-01] Neither PortalBaseDomain nor PortalBaseUrl is configured. " +
                "Resend-invite email for user {UserId} ({Email}, tenant={TenantId}) cannot be sent. " +
                "Set NotificationsService:PortalBaseDomain (or PortalBaseUrl) in configuration.",
                id, user.Email, opTenantId);
            return Results.Problem(
                "Invitation refreshed but email could not be sent: portal URL is not configured. " +
                "Configure NotificationsService:PortalBaseDomain or NotificationsService:PortalBaseUrl " +
                "so invitation links can be generated.",
                statusCode: 503);
        }
        var displayNameStr = $"{user.FirstName} {user.LastName}".Trim();

        var (emailConfigured, emailSuccess, emailError) = await emailClient.SendInviteEmailAsync(
            user.Email, displayNameStr, activationLink, opTenantId, ct);

        if (!emailConfigured)
        {
            logger.LogError(
                "[LS-ID-TNT-007] NotificationsService:BaseUrl is not configured. " +
                "Resend-invite email for user {UserId} ({Email}, tenant={TenantId}) was not sent. " +
                "Set NotificationsService:BaseUrl in configuration.",
                id, user.Email, opTenantId);
            return Results.Problem(
                "Invitation refreshed but email could not be sent: the Notifications service is not configured. " +
                "Configure NotificationsService:BaseUrl so emails can be dispatched.",
                statusCode: 503);
        }

        if (!emailSuccess)
        {
            return Results.Problem(
                $"Invitation refreshed but email could not be sent: {emailError}",
                statusCode: 502);
        }

        // Non-production: return raw token and activation link for hand-delivery.
        if (!env.IsProduction())
        {
            Console.WriteLine($"[RESEND INVITE — dev only] userId={id} token={rawToken} link={activationLink}");
            return Results.Ok(new { invitationId = newInvite.Id, inviteToken = rawToken, activationLink });
        }

        return Results.Ok(new { invitationId = newInvite.Id });
    }

    /// <summary>
    /// POST /api/admin/users/{id}/cancel-invite
    /// Revokes all pending invitations for the user without creating a new one.
    /// </summary>
    private static async Task<IResult> CancelInvite(
        Guid                id,
        ClaimsPrincipal     caller,
        IdentityDbContext   db,
        IAuditEventClient   auditClient,
        CancellationToken   ct)
    {
        var user = await db.Users.FindAsync([id], ct);
        if (user is null) return Results.NotFound(new { error = $"User '{id}' not found." });

        if (await IsCrossTenantAccessAsync(caller, user.Id, db, ct)) return Results.Forbid();

        var opTenantId = await GetOperationalTenantIdAsync(caller, user.Id, db, ct);
        var pending = await db.UserInvitations
            .Where(i => i.UserId == id && i.Status == UserInvitation.Statuses.Pending)
            .ToListAsync(ct);

        if (pending.Count == 0)
            return Results.Conflict(new { error = "No pending invitations found for this user." });

        foreach (var inv in pending) inv.Revoke();

        await db.SaveChangesAsync(ct);

        _ = auditClient.IngestAsync(new IngestAuditEventRequest
        {
            EventType     = "identity.user.invite_cancelled",
            EventCategory = EventCategory.Administrative,
            SourceSystem  = "identity-service",
            SourceService = "admin-api",
            Visibility    = VisibilityScope.Tenant,
            Severity      = SeverityLevel.Info,
            OccurredAtUtc = DateTimeOffset.UtcNow,
            Scope  = new AuditEventScopeDto { ScopeType = ScopeType.Tenant, TenantId = opTenantId.ToString() },
            Actor  = new AuditEventActorDto { Type = ActorType.System, Name = "admin-api" },
            Entity = new AuditEventEntityDto { Type = "User", Id = id.ToString() },
            Action      = "InviteCancelled",
            Description = $"Invite cancelled for user '{user.Email}'.",
            IdempotencyKey = IdempotencyKey.For("identity-service", "identity.user.invite_cancelled", id.ToString()),
            Tags = ["user-management", "invite"],
        });

        return Results.NoContent();
    }

    // =========================================================================
    // UIX-002: MEMBERSHIPS
    // =========================================================================

    /// <summary>
    /// POST /api/admin/users/{id}/memberships
    /// Assigns the user to an organization with the given member role.
    /// </summary>
    private static async Task<IResult> AssignMembership(
        Guid                      id,
        AssignMembershipRequest   body,
        ClaimsPrincipal           caller,
        IdentityDbContext         db,
        INotificationsCacheClient notificationsCache,
        CancellationToken         ct)
    {
        var user = await db.Users.FindAsync([id], ct);
        if (user is null) return Results.NotFound(new { error = $"User '{id}' not found." });

        // UIX-003-01: TenantAdmin may only assign memberships within their own tenant.
        if (await IsCrossTenantAccessAsync(caller, user.Id, db, ct)) return Results.Forbid();

        var opTenantId = await GetOperationalTenantIdAsync(caller, user.Id, db, ct);
        var org = await db.Organizations.FindAsync([body.OrganizationId], ct);
        if (org is null) return Results.NotFound(new { error = $"Organization '{body.OrganizationId}' not found." });

        if (org.TenantId != opTenantId)
            return Results.BadRequest(new { error = "Organization does not belong to the user's tenant." });

        var exists = await db.UserOrganizationMemberships.AnyAsync(
            m => m.UserId == id && m.OrganizationId == body.OrganizationId, ct);
        if (exists)
            return Results.Conflict(new { error = "User is already a member of this organization." });

        var memberRole = string.IsNullOrWhiteSpace(body.MemberRole) ? MemberRole.Member : body.MemberRole;
        var membership = UserOrganizationMembership.Create(id, body.OrganizationId, memberRole, body.GrantedByUserId);

        // If this is the first/only membership, make it primary automatically.
        var hasPrimary = await db.UserOrganizationMemberships.AnyAsync(m => m.UserId == id && m.IsPrimary, ct);
        if (!hasPrimary) membership.SetPrimary();

        db.UserOrganizationMemberships.Add(membership);
        await db.SaveChangesAsync(ct);

        // Org membership for this tenant changed — refresh notifications so
        // org-addressed fan-out reflects the new member immediately.
        notificationsCache.InvalidateTenant(
            opTenantId,
            eventType: "identity.membership.changed",
            reason:    $"user {id} added to organization {body.OrganizationId}");

        return Results.Created(
            $"/api/admin/users/{id}/memberships/{membership.Id}",
            new
            {
                membershipId   = membership.Id,
                userId         = id,
                organizationId = body.OrganizationId,
                memberRole     = membership.MemberRole,
                isPrimary      = membership.IsPrimary,
            });
    }

    /// <summary>
    /// POST /api/admin/users/{id}/memberships/{membershipId}/set-primary
    /// Makes the specified membership the primary org for the user.
    /// Clears the primary flag on any other memberships.
    /// </summary>
    private static async Task<IResult> SetPrimaryMembership(
        Guid                      id,
        Guid                      membershipId,
        ClaimsPrincipal           caller,
        IdentityDbContext         db,
        INotificationsCacheClient notificationsCache,
        CancellationToken         ct)
    {
        // UIX-003-01: load user to enforce TenantAdmin tenant boundary.
        var user = await db.Users.FindAsync([id], ct);
        if (user is not null && await IsCrossTenantAccessAsync(caller, user.Id, db, ct)) return Results.Forbid();

        Guid opTenantId = user is not null
            ? await GetOperationalTenantIdAsync(caller, user.Id, db, ct)
            : Guid.Empty;

        var target = await db.UserOrganizationMemberships
            .FirstOrDefaultAsync(m => m.Id == membershipId && m.UserId == id, ct);
        if (target is null)
            return Results.NotFound(new { error = $"Membership '{membershipId}' not found for user '{id}'." });

        var others = await db.UserOrganizationMemberships
            .Where(m => m.UserId == id && m.Id != membershipId && m.IsPrimary)
            .ToListAsync(ct);

        foreach (var o in others) o.ClearPrimary();
        target.SetPrimary();

        await db.SaveChangesAsync(ct);

        // Primary org changed — org-addressed fan-out keys recipients by org,
        // so refresh notifications' membership cache for this tenant.
        if (user is not null)
        {
            notificationsCache.InvalidateTenant(
                opTenantId,
                eventType: "identity.membership.changed",
                reason:    $"primary membership for user {id} set to {membershipId}");
        }

        return Results.NoContent();
    }

    /// <summary>
    /// DELETE /api/admin/users/{id}/memberships/{membershipId}
    /// Deactivates the membership. Enforces membership safety rules:
    ///   - 409 if this is the user's last active membership.
    ///   - 409 if this is the primary membership and other memberships still exist
    ///     (caller must designate a new primary first via set-primary).
    /// </summary>
    private static async Task<IResult> RemoveMembership(
        Guid                      id,
        Guid                      membershipId,
        ClaimsPrincipal           caller,
        IdentityDbContext         db,
        INotificationsCacheClient notificationsCache,
        CancellationToken         ct)
    {
        // UIX-003-01: load user to enforce TenantAdmin tenant boundary.
        var user = await db.Users.FindAsync([id], ct);
        if (user is not null && await IsCrossTenantAccessAsync(caller, user.Id, db, ct)) return Results.Forbid();

        Guid remOpTenantId = user is not null
            ? await GetOperationalTenantIdAsync(caller, user.Id, db, ct)
            : Guid.Empty;

        var membership = await db.UserOrganizationMemberships
            .FirstOrDefaultAsync(m => m.Id == membershipId && m.UserId == id && m.IsActive, ct);
        if (membership is null)
            return Results.NotFound(new { error = $"Membership '{membershipId}' not found for user '{id}'." });

        // ── Safety rule 1: cannot remove the last active membership ───────────
        var activeMembershipCount = await db.UserOrganizationMemberships
            .CountAsync(m => m.UserId == id && m.IsActive, ct);

        if (activeMembershipCount <= 1)
            return Results.Conflict(new
            {
                error = "Cannot remove the user's last remaining organization membership. " +
                        "Assign the user to another organization first.",
                code  = "LAST_MEMBERSHIP",
            });

        // ── Safety rule 2: cannot remove primary membership while others exist ─
        // Caller must designate another primary first via set-primary.
        if (membership.IsPrimary)
            return Results.Conflict(new
            {
                error = "Cannot remove the primary membership. " +
                        "Please designate another membership as primary first.",
                code  = "PRIMARY_MEMBERSHIP",
            });

        membership.Deactivate();
        await db.SaveChangesAsync(ct);

        // Org membership removed — refresh notifications so org-addressed
        // fan-out drops this user immediately.
        if (user is not null)
        {
            notificationsCache.InvalidateTenant(
                remOpTenantId,
                eventType: "identity.membership.changed",
                reason:    $"user {id} removed from organization {membership.OrganizationId}");
        }

        return Results.NoContent();
    }

    // =========================================================================
    // UIX-002: GROUPS
    // =========================================================================


    // =========================================================================
    // UIX-002: PERMISSIONS CATALOG
    // =========================================================================

    /// <summary>
    /// GET /api/admin/permissions
    /// Returns the full capabilities catalog — all active product-scoped capabilities
    /// that represent the platform permission surface.
    /// </summary>
    private static async Task<IResult> ListPermissions(
        IdentityDbContext db,
        string productId = "",
        string search    = "",
        CancellationToken ct = default)
    {
        var q = db.Permissions
            .Include(c => c.Product)
            .Where(c => c.IsActive)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(productId) && Guid.TryParse(productId, out var pid))
            q = q.Where(c => c.ProductId == pid);

        // UIX-005: simple substring search across code, name, and description
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.ToLowerInvariant();
            q = q.Where(c =>
                c.Code.Contains(s)       ||
                c.Name.Contains(s)       ||
                (c.Description != null && c.Description.Contains(s)));
        }

        var items = await q
            .OrderBy(c => c.Product.Name)
            .ThenBy(c => c.Code)
            .Select(c => new
            {
                id          = c.Id,
                code        = c.Code,
                name        = c.Name,
                description = c.Description,
                category    = c.Category,
                productId   = c.ProductId,
                productCode = c.Product.Code,
                productName = c.Product.Name,
                isActive    = c.IsActive,
                createdAtUtc = c.CreatedAtUtc,
                updatedAtUtc = c.UpdatedAtUtc,
            })
            .ToListAsync(ct);

        return Results.Ok(new { items, totalCount = items.Count });
    }

    // =========================================================================
    // ROLE PERMISSION MANAGEMENT (UIX-005)
    // =========================================================================

    /// <summary>
    /// GET /api/admin/roles/{id}/permissions
    ///
    /// Returns all capabilities assigned to a role.
    /// Access: PlatformAdmin (any role) or TenantAdmin (own-tenant non-system roles; system roles readable).
    /// UIX-005-01: Added caller + cross-tenant boundary enforcement.
    /// </summary>
    private static async Task<IResult> GetRolePermissions(
        Guid              id,
        IdentityDbContext db,
        ClaimsPrincipal   caller,
        CancellationToken ct = default)
    {
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (role is null) return Results.NotFound();

        // Cross-tenant guard: TenantAdmin may not read non-system roles from other tenants.
        // System roles are global (readable by all authenticated admins).
        if (!role.IsSystemRole && IsCrossTenantAccess(caller, role.TenantId))
            return Results.Forbid();

        var assignments = await db.RolePermissionAssignments
            .Where(a => a.RoleId == id)
            .Include(a => a.Permission)
            .ThenInclude(c => c.Product)
            .OrderBy(a => a.Permission.Product.Name)
            .ThenBy(a => a.Permission.Code)
            .ToListAsync(ct);

        var items = assignments.Select(a => new
        {
            id               = a.PermissionId,
            code             = a.Permission.Code,
            name             = a.Permission.Name,
            description      = a.Permission.Description,
            productId        = a.Permission.ProductId,
            productName      = a.Permission.Product.Name,
            isActive         = a.Permission.IsActive,
            assignedAtUtc    = a.AssignedAtUtc,
            assignedByUserId = a.AssignedByUserId,
        }).ToList();

        return Results.Ok(new { items, totalCount = items.Count });
    }

    private record AssignRolePermissionRequest(Guid PermissionId);

    /// <summary>
    /// POST /api/admin/roles/{id}/permissions
    ///
    /// Assigns a capability to a role. Idempotent — returns 200 if already assigned.
    /// Emits a role.permission.assigned audit event.
    /// Access: PlatformAdmin only.
    /// </summary>
    private static async Task<IResult> AssignRolePermission(
        Guid                         id,
        AssignRolePermissionRequest  body,
        IdentityDbContext            db,
        ClaimsPrincipal              caller,
        IAuditEventClient            auditClient,
        ILoggerFactory               loggerFactory,
        CancellationToken            ct = default)
    {
        var logger = loggerFactory.CreateLogger("AdminEndpoints.AssignRolePermission");

        var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (role is null) return Results.NotFound(new { error = "Role not found." });

        // UIX-005-01: system roles may only be modified by PlatformAdmin
        if (role.IsSystemRole && !caller.IsInRole("PlatformAdmin"))
            return Results.Json(new { error = "System roles cannot be modified. Contact the platform administrator." }, statusCode: 403);

        // UIX-005-01: TenantAdmin may not assign permissions to roles outside their tenant
        if (IsCrossTenantAccess(caller, role.TenantId))
            return Results.Forbid();

        var permission = await db.Permissions.FirstOrDefaultAsync(c => c.Id == body.PermissionId && c.IsActive, ct);
        if (permission is null) return Results.NotFound(new { error = "Permission not found or inactive." });

        var alreadyAssigned = await db.RolePermissionAssignments
            .AnyAsync(a => a.RoleId == id && a.PermissionId == body.PermissionId, ct);

        if (alreadyAssigned)
            return Results.Ok(new { message = "Permission already assigned to role." });

        var callerIdRaw = caller.FindFirstValue(ClaimTypes.NameIdentifier);
        Guid? callerId  = Guid.TryParse(callerIdRaw, out var cid) ? cid : null;

        var assignment = RolePermissionAssignment.Create(id, body.PermissionId, callerId);
        db.RolePermissionAssignments.Add(assignment);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Role {RoleId} assigned permission {PermissionId} by {ActorId}",
            id, body.PermissionId, callerId);

        var assignAuditNow = DateTimeOffset.UtcNow;
        _ = auditClient.IngestAsync(new IngestAuditEventRequest
        {
            EventType     = "role.permission.assigned",
            EventCategory = EventCategory.Administrative,
            SourceSystem  = "identity-service",
            SourceService = "admin-api",
            Severity      = SeverityLevel.Info,
            OccurredAtUtc = assignAuditNow,
            Actor         = new AuditEventActorDto { Id = callerIdRaw ?? "system", Type = ActorType.User },
            Entity        = new AuditEventEntityDto { Type = "Role", Id = id.ToString() },
            Action        = "PermissionAssigned",
            Description   = $"Permission '{permission.Code}' assigned to role '{role.Name}'",
            Metadata      = System.Text.Json.JsonSerializer.Serialize(new
            {
                roleId       = id,
                permissionId = body.PermissionId,
                code         = permission.Code,
            }),
            IdempotencyKey = IdempotencyKey.ForWithTimestamp(assignAuditNow, "identity-service", "role.permission.assigned", id.ToString(), body.PermissionId.ToString()),
        });

        return Results.Created(
            $"/api/admin/roles/{id}/permissions/{body.PermissionId}",
            new { roleId = id, permissionId = body.PermissionId });
    }

    /// <summary>
    /// DELETE /api/admin/roles/{id}/permissions/{capabilityId}
    ///
    /// Revokes a capability from a role. Emits a role.permission.revoked audit event.
    /// Access: PlatformAdmin only.
    /// </summary>
    private static async Task<IResult> RevokeRolePermission(
        Guid              id,
        Guid              permissionId,
        IdentityDbContext db,
        ClaimsPrincipal   caller,
        IAuditEventClient auditClient,
        ILoggerFactory    loggerFactory,
        CancellationToken ct = default)
    {
        var logger = loggerFactory.CreateLogger("AdminEndpoints.RevokeRolePermission");

        var assignment = await db.RolePermissionAssignments
            .Include(a => a.Permission)
            .Include(a => a.Role)
            .FirstOrDefaultAsync(a => a.RoleId == id && a.PermissionId == permissionId, ct);

        if (assignment is null)
            return Results.NotFound(new { error = "Permission assignment not found." });

        if (assignment.Role.IsSystemRole && !caller.IsInRole("PlatformAdmin"))
            return Results.Json(new { error = "System roles cannot be modified. Contact the platform administrator." }, statusCode: 403);

        if (IsCrossTenantAccess(caller, assignment.Role.TenantId))
            return Results.Forbid();

        db.RolePermissionAssignments.Remove(assignment);
        await db.SaveChangesAsync(ct);

        var callerIdRaw = caller.FindFirstValue(ClaimTypes.NameIdentifier);

        logger.LogInformation(
            "Role {RoleId} revoked permission {PermissionId} by {ActorId}",
            id, permissionId, callerIdRaw);

        var revokeAuditNow = DateTimeOffset.UtcNow;
        _ = auditClient.IngestAsync(new IngestAuditEventRequest
        {
            EventType     = "role.permission.revoked",
            EventCategory = EventCategory.Administrative,
            SourceSystem  = "identity-service",
            SourceService = "admin-api",
            Severity      = SeverityLevel.Info,
            OccurredAtUtc = revokeAuditNow,
            Actor         = new AuditEventActorDto { Id = callerIdRaw ?? "system", Type = ActorType.User },
            Entity        = new AuditEventEntityDto { Type = "Role", Id = id.ToString() },
            Action        = "PermissionRevoked",
            Description   = $"Permission '{assignment.Permission.Code}' revoked from role '{assignment.Role.Name}'",
            Metadata      = System.Text.Json.JsonSerializer.Serialize(new
            {
                roleId       = id,
                permissionId,
                code         = assignment.Permission.Code,
            }),
            IdempotencyKey = IdempotencyKey.ForWithTimestamp(revokeAuditNow, "identity-service", "role.permission.revoked", id.ToString(), permissionId.ToString()),
        });

        return Results.NoContent();
    }

    /// <summary>
    /// GET /api/admin/users/{id}/permissions
    ///
    /// Returns the effective (union) permissions for a user, derived from all active
    /// role assignments. Each capability includes which role(s) grant it.
    /// Access: PlatformAdmin or TenantAdmin (with tenant boundary check).
    /// </summary>
    private static async Task<IResult> GetUserEffectivePermissions(
        Guid              id,
        IdentityDbContext db,
        ClaimsPrincipal   caller,
        CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return Results.NotFound();

        if (await IsCrossTenantAccessAsync(caller, user.Id, db, ct))
            return Results.Forbid();

        // Active global-scoped role assignments for this user
        var roleAssignments = await db.ScopedRoleAssignments
            .Where(s => s.UserId == id && s.IsActive
                     && s.ScopeType == ScopedRoleAssignment.ScopeTypes.Global)
            .Include(s => s.Role)
            .ToListAsync(ct);

        if (roleAssignments.Count == 0)
            return Results.Ok(new { items = Array.Empty<object>(), totalCount = 0, roleCount = 0 });

        var roleIds = roleAssignments.Select(s => s.RoleId).ToList();

        var permAssignments = await db.RolePermissionAssignments
            .Where(a => roleIds.Contains(a.RoleId))
            .Include(a => a.Permission)
            .ThenInclude(c => c.Product)
            .ToListAsync(ct);

        var permToRoles = permAssignments
            .GroupBy(a => a.PermissionId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(a => roleAssignments
                        .First(r => r.RoleId == a.RoleId).Role.Name)
                      .Distinct()
                      .ToList());

        var distinctPerms = permAssignments
            .GroupBy(a => a.PermissionId)
            .Select(g => g.First().Permission)
            .OrderBy(c => c.Product.Name)
            .ThenBy(c => c.Code)
            .ToList();

        var items = distinctPerms.Select(c => new
        {
            id          = c.Id,
            code        = c.Code,
            name        = c.Name,
            description = c.Description,
            productId   = c.ProductId,
            productName = c.Product.Name,
            isActive    = c.IsActive,
            sources     = permToRoles.GetValueOrDefault(c.Id, [])
                            .Select(roleName => new { type = "role", name = roleName })
                            .ToList(),
        }).ToList();

        return Results.Ok(new
        {
            items,
            totalCount = items.Count,
            roleCount  = roleAssignments.Count,
        });
    }

    // ── LS-COR-AUT-008: Authorization debug endpoint ──────────────────────────

    private static async Task<IResult> GetAccessDebug(
        Guid              id,
        IdentityDbContext db,
        IEffectiveAccessService effectiveAccessService,
        ClaimsPrincipal   caller,
        CancellationToken ct = default)
    {
        if (!caller.IsInRole("PlatformAdmin") && !caller.IsInRole("TenantAdmin"))
            return Results.Forbid();

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return Results.NotFound();

        if (await IsCrossTenantAccessAsync(caller, user.Id, db, ct))
            return Results.Forbid();

        var opTenantId = await GetOperationalTenantIdAsync(caller, user.Id, db, ct);
        var effectiveAccess = await effectiveAccessService.GetEffectiveAccessAsync(opTenantId, user.Id, ct);

        var groupMemberships = await db.AccessGroupMemberships
            .Where(m => m.TenantId == opTenantId && m.UserId == user.Id && m.MembershipStatus == MembershipStatus.Active)
            .Join(db.AccessGroups,
                m => m.GroupId,
                g => g.Id,
                (m, g) => new { g.Id, g.Name, g.Status, g.ScopeType, g.ProductCode })
            .ToListAsync(ct);

        var entitlements = await db.TenantProductEntitlements
            .Where(e => e.TenantId == opTenantId && e.Status == EntitlementStatus.Active)
            .Select(e => new { e.ProductCode, e.Status })
            .ToListAsync(ct);

        var scopedRoles = await db.ScopedRoleAssignments
            .Where(s => s.UserId == user.Id && s.IsActive)
            .Include(s => s.Role)
            .Select(s => new { s.Role.Name, s.ScopeType })
            .ToListAsync(ct);

        return Results.Ok(new
        {
            userId = user.Id,
            tenantId = opTenantId,
            accessVersion = user.AccessVersion,

            products = effectiveAccess.ProductSources.Select(p => new
            {
                productCode = p.ProductCode,
                source = p.Source,
                groupId = p.GroupId,
                groupName = p.GroupName,
            }),

            roles = effectiveAccess.RoleSources.Select(r => new
            {
                roleCode = r.RoleCode,
                productCode = r.ProductCode,
                source = r.Source,
                groupId = r.GroupId,
                groupName = r.GroupName,
            }),

            systemRoles = scopedRoles.Select(r => new
            {
                roleName = r.Name,
                scopeType = r.ScopeType,
            }),

            groups = groupMemberships.Select(g => new
            {
                groupId = g.Id,
                groupName = g.Name,
                status = g.Status.ToString(),
                scopeType = g.ScopeType.ToString(),
                productCode = g.ProductCode,
            }),

            entitlements = entitlements.Select(e => new
            {
                productCode = e.ProductCode,
                status = e.Status.ToString(),
            }),

            productRolesFlat = effectiveAccess.ProductRolesFlat,
            tenantRoles = effectiveAccess.TenantRoles,

            permissions = effectiveAccess.Permissions,
            permissionSources = effectiveAccess.PermissionSources.Select(p => new
            {
                permissionCode = p.PermissionCode,
                productCode = p.ProductCode,
                source = p.Source,
                viaRoleCode = p.ViaRoleCode,
                groupId = p.GroupId,
                groupName = p.GroupName,
            }),

            policies = await GetPolicyDebugForPermissions(db, effectiveAccess.Permissions, ct),
        });
    }

    private static async Task<object[]> GetPolicyDebugForPermissions(
        IdentityDbContext db,
        IReadOnlyList<string> permissions,
        CancellationToken ct)
    {
        if (permissions.Count == 0) return [];

        var permissionPolicies = await db.PermissionPolicies
            .Where(pp => permissions.Contains(pp.PermissionCode) && pp.IsActive)
            .ToListAsync(ct);

        if (permissionPolicies.Count == 0) return [];

        var policyIds = permissionPolicies.Select(pp => pp.PolicyId).Distinct().ToList();
        var policies = await db.Policies
            .Where(p => policyIds.Contains(p.Id) && p.IsActive)
            .Include(p => p.Rules)
            .ToListAsync(ct);

        return permissionPolicies
            .GroupBy(pp => pp.PermissionCode)
            .Select(g => (object)new
            {
                permission = g.Key,
                linkedPolicies = g.Select(pp =>
                {
                    var policy = policies.FirstOrDefault(p => p.Id == pp.PolicyId);
                    return policy == null ? null : new
                    {
                        policyCode = policy.PolicyCode,
                        policyName = policy.Name,
                        priority = policy.Priority,
                        rulesCount = policy.Rules.Count,
                        rules = policy.Rules.Select(r => new
                        {
                            field = r.Field,
                            op = r.Operator.ToString(),
                            value = r.Value,
                            conditionType = r.ConditionType.ToString(),
                            logicalGroup = r.LogicalGroup.ToString(),
                        }),
                    };
                }).Where(x => x != null),
            }).ToArray();
    }

    // ── LS-COR-AUT-009: Permission catalog by product code ──────────────────

    private static async Task<IResult> ListPermissionsByProduct(
        string productCode,
        IdentityDbContext db,
        ClaimsPrincipal caller,
        CancellationToken ct = default)
    {
        if (!caller.IsInRole("PlatformAdmin") && !caller.IsInRole("TenantAdmin"))
            return Results.Forbid();

        var capabilities = await db.Permissions
            .Where(c => c.IsActive && c.Product.Code == productCode)
            .Include(c => c.Product)
            .OrderBy(c => c.Code)
            .Select(c => new
            {
                id = c.Id,
                code = c.Code,
                name = c.Name,
                description = c.Description,
                category = c.Category,
                productCode = c.Product.Code,
                productName = c.Product.Name,
                isActive = c.IsActive,
                createdAtUtc = c.CreatedAtUtc,
                updatedAtUtc = c.UpdatedAtUtc,
            })
            .ToListAsync(ct);

        return Results.Ok(new { items = capabilities, totalCount = capabilities.Count });
    }

    // ── LS-COR-AUT-010: Permission catalog CRUD ─────────────────────────────

    private record CreatePermissionRequest(string Code, string Name, string? Description, string? Category, string? ProductCode = null, Guid? ProductId = null);

    private static async Task<IResult> CreatePermission(
        CreatePermissionRequest body,
        IdentityDbContext db,
        ClaimsPrincipal caller,
        IAuditEventClient auditClient,
        CancellationToken ct = default)
    {
        if (!caller.IsInRole("PlatformAdmin"))
            return Results.Forbid();

        Product? product = null;
        if (!string.IsNullOrWhiteSpace(body.ProductCode))
            product = await db.Products.FirstOrDefaultAsync(p => p.Code == body.ProductCode, ct);
        else if (body.ProductId.HasValue)
            product = await db.Products.FirstOrDefaultAsync(p => p.Id == body.ProductId.Value, ct);

        if (product is null)
            return Results.BadRequest(new { error = "Invalid product. Provide a valid productCode or productId." });

        if (!Permission.IsValidCode(body.Code))
            return Results.BadRequest(new { error = $"Permission code must follow naming convention 'PRODUCT.domain:action' (e.g. SYNQ_FUND.application:create). Got: '{body.Code}'." });

        var normalizedCode = body.Code.Trim();
        var exists = await db.Permissions.AnyAsync(c => c.Code == normalizedCode, ct);
        if (exists)
            return Results.Conflict(new { error = $"Permission code '{normalizedCode}' already exists." });

        var callerIdRaw = caller.FindFirstValue(ClaimTypes.NameIdentifier);
        Guid? callerId = Guid.TryParse(callerIdRaw, out var cid) ? cid : null;

        var permission = Permission.Create(product.Id, body.Code, body.Name, body.Description, body.Category, callerId);
        db.Permissions.Add(permission);
        await db.SaveChangesAsync(ct);

        var auditNow = DateTimeOffset.UtcNow;
        _ = auditClient.IngestAsync(new IngestAuditEventRequest
        {
            EventType     = "permission.created",
            EventCategory = EventCategory.Administrative,
            SourceSystem  = "identity-service",
            SourceService = "admin-api",
            Severity      = SeverityLevel.Info,
            OccurredAtUtc = auditNow,
            Actor         = new AuditEventActorDto { Id = callerIdRaw ?? "system", Type = ActorType.User },
            Entity        = new AuditEventEntityDto { Type = "Permission", Id = permission.Id.ToString() },
            Action        = "PermissionCreated",
            Description   = $"Permission '{normalizedCode}' created for product '{product.Code}'",
            After         = System.Text.Json.JsonSerializer.Serialize(new
            {
                id = permission.Id, code = normalizedCode, name = body.Name, productCode = product.Code, category = body.Category,
            }),
            IdempotencyKey = IdempotencyKey.ForWithTimestamp(auditNow, "identity-service", "permission.created", permission.Id.ToString()),
        });

        return Results.Created($"/api/admin/permissions/{permission.Id}", new
        {
            id = permission.Id, code = normalizedCode, name = permission.Name,
            description = permission.Description, category = permission.Category,
            productCode = product.Code, productName = product.Name,
            isActive = permission.IsActive, createdAtUtc = permission.CreatedAtUtc,
        });
    }

    private record UpdatePermissionRequest(string Name, string? Description, string? Category);

    private static async Task<IResult> UpdatePermission(
        Guid id,
        UpdatePermissionRequest body,
        IdentityDbContext db,
        ClaimsPrincipal caller,
        IAuditEventClient auditClient,
        CancellationToken ct = default)
    {
        if (!caller.IsInRole("PlatformAdmin"))
            return Results.Forbid();

        var perm = await db.Permissions.Include(c => c.Product).FirstOrDefaultAsync(c => c.Id == id, ct);
        if (perm is null)
            return Results.NotFound(new { error = "Permission not found." });

        var callerIdRaw = caller.FindFirstValue(ClaimTypes.NameIdentifier);
        Guid? callerId = Guid.TryParse(callerIdRaw, out var cid) ? cid : null;

        var before = new { name = perm.Name, description = perm.Description, category = perm.Category };
        perm.Update(body.Name, body.Description, body.Category, callerId);
        await db.SaveChangesAsync(ct);

        var auditNow = DateTimeOffset.UtcNow;
        _ = auditClient.IngestAsync(new IngestAuditEventRequest
        {
            EventType     = "permission.updated",
            EventCategory = EventCategory.Administrative,
            SourceSystem  = "identity-service",
            SourceService = "admin-api",
            Severity      = SeverityLevel.Info,
            OccurredAtUtc = auditNow,
            Actor         = new AuditEventActorDto { Id = callerIdRaw ?? "system", Type = ActorType.User },
            Entity        = new AuditEventEntityDto { Type = "Permission", Id = id.ToString() },
            Action        = "PermissionUpdated",
            Description   = $"Permission '{perm.Code}' updated",
            Before        = System.Text.Json.JsonSerializer.Serialize(before),
            After         = System.Text.Json.JsonSerializer.Serialize(new { name = body.Name, description = body.Description, category = body.Category }),
            IdempotencyKey = IdempotencyKey.ForWithTimestamp(auditNow, "identity-service", "permission.updated", id.ToString()),
        });

        return Results.Ok(new
        {
            id = perm.Id, code = perm.Code, name = perm.Name,
            description = perm.Description, category = perm.Category,
            productCode = perm.Product.Code, productName = perm.Product.Name,
            isActive = perm.IsActive, updatedAtUtc = perm.UpdatedAtUtc,
        });
    }

    private static async Task<IResult> DeactivatePermission(
        Guid id,
        IdentityDbContext db,
        ClaimsPrincipal caller,
        IAuditEventClient auditClient,
        CancellationToken ct = default)
    {
        if (!caller.IsInRole("PlatformAdmin"))
            return Results.Forbid();

        var perm = await db.Permissions.Include(c => c.Product).FirstOrDefaultAsync(c => c.Id == id, ct);
        if (perm is null)
            return Results.NotFound(new { error = "Permission not found." });

        if (!perm.IsActive)
            return Results.Ok(new { message = "Permission already deactivated." });

        var callerIdRaw = caller.FindFirstValue(ClaimTypes.NameIdentifier);
        Guid? callerId = Guid.TryParse(callerIdRaw, out var cid) ? cid : null;

        perm.Deactivate(callerId);
        await db.SaveChangesAsync(ct);

        var auditNow = DateTimeOffset.UtcNow;
        _ = auditClient.IngestAsync(new IngestAuditEventRequest
        {
            EventType     = "permission.deactivated",
            EventCategory = EventCategory.Administrative,
            SourceSystem  = "identity-service",
            SourceService = "admin-api",
            Severity      = SeverityLevel.Warn,
            OccurredAtUtc = auditNow,
            Actor         = new AuditEventActorDto { Id = callerIdRaw ?? "system", Type = ActorType.User },
            Entity        = new AuditEventEntityDto { Type = "Permission", Id = id.ToString() },
            Action        = "PermissionDeactivated",
            Description   = $"Permission '{perm.Code}' deactivated for product '{perm.Product.Code}'",
            Metadata      = System.Text.Json.JsonSerializer.Serialize(new { code = perm.Code, productCode = perm.Product.Code }),
            IdempotencyKey = IdempotencyKey.ForWithTimestamp(auditNow, "identity-service", "permission.deactivated", id.ToString()),
        });

        return Results.NoContent();
    }

    // ── UIX-003-01: Caller-based tenant boundary enforcement ─────────────────

    /// <summary>
    /// Returns <c>true</c> if the caller is a non-PlatformAdmin whose tenant differs
    /// from <paramref name="targetTenantId"/>.  PlatformAdmins are never restricted.
    /// A caller with no parseable <c>tenant_id</c> claim is treated as cross-tenant (deny).
    /// </summary>
    private static bool IsCrossTenantAccess(ClaimsPrincipal caller, Guid targetTenantId)
    {
        if (caller.IsInRole("PlatformAdmin")) return false;
        var raw = caller.FindFirstValue("tenant_id");
        return raw is null || !Guid.TryParse(raw, out var callerTid) || callerTid != targetTenantId;
    }

    /// <summary>
    /// Async version of <see cref="IsCrossTenantAccess"/> that verifies the target user
    /// is a member of the caller's tenant via <c>idt_UserTenants</c>.
    /// PlatformAdmins are never restricted.
    /// </summary>
    private static async Task<bool> IsCrossTenantAccessAsync(
        ClaimsPrincipal   caller,
        Guid              userId,
        IdentityDbContext db,
        CancellationToken ct = default)
    {
        if (caller.IsInRole("PlatformAdmin")) return false;
        var raw = caller.FindFirstValue("tenant_id");
        if (raw is null || !Guid.TryParse(raw, out var callerTid)) return true;
        return !await db.UserTenants.AnyAsync(
            ut => ut.UserId == userId && ut.TenantId == callerTid && ut.IsActive, ct);
    }

    /// <summary>
    /// Resolves the effective tenant for an operation.
    /// For TenantAdmin callers, returns their JWT <c>tenant_id</c> claim.
    /// For PlatformAdmin callers (no tenant isolation), returns the user's
    /// primary active tenant from <c>idt_UserTenants</c>.
    /// </summary>
    private static async Task<Guid> GetOperationalTenantIdAsync(
        ClaimsPrincipal   caller,
        Guid              userId,
        IdentityDbContext db,
        CancellationToken ct = default)
    {
        if (!caller.IsInRole("PlatformAdmin"))
        {
            var raw = caller.FindFirstValue("tenant_id");
            if (raw is not null && Guid.TryParse(raw, out var callerTid))
                return callerTid;
        }
        // PlatformAdmin or caller without a tenant claim: use the user's primary tenant.
        return await db.UserTenants
            .Where(ut => ut.UserId == userId && ut.IsActive)
            .OrderBy(ut => ut.JoinedAtUtc)
            .Select(ut => ut.TenantId)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// LS-ID-TNT-005: Count active TenantAdmin SRAs in <paramref name="tenantId"/>
    /// that belong to users OTHER than <paramref name="excludeUserId"/>.
    ///
    /// Used by DeactivateUser and RevokeRole to enforce the last-active-admin
    /// protection server-side.  A return value of 0 means the excluded user is
    /// the sole remaining active TenantAdmin for that tenant.
    /// </summary>
    private static Task<int> CountOtherActiveTenantAdmins(
        IdentityDbContext db,
        Guid              excludeUserId,
        Guid              tenantId,
        CancellationToken ct = default) =>
        (from sra  in db.ScopedRoleAssignments
         join role in db.Roles on sra.RoleId equals role.Id
         where sra.IsActive
            && sra.ScopeType == ScopedRoleAssignment.ScopeTypes.Global
            && sra.UserId != excludeUserId
            && role.Name == "TenantAdmin"
            && db.UserTenants.Any(ut => ut.UserId == sra.UserId && ut.TenantId == tenantId && ut.IsActive)
            && db.Users.Any(u => u.Id == sra.UserId && u.IsActive)
         select sra.Id)
        .CountAsync(ct);

    // ── UIX-003: Organizations list ──────────────────────────────────────────

    /// <summary>
    /// GET /api/admin/organizations?tenantId=
    ///
    /// Returns active organizations optionally filtered by tenantId.
    /// Used by the Control Center access-control panels to populate
    /// the "Add Membership" org selection dropdown.
    /// </summary>
    private static async Task<IResult> ListOrganizations(
        IdentityDbContext db,
        ClaimsPrincipal   caller,
        string            tenantId = "",
        string            orgType = "",
        CancellationToken ct       = default)
    {
        var isPlatformAdmin = caller.IsInRole("PlatformAdmin");
        var callerTenantId  = caller.FindFirstValue("tenant_id");

        var q = db.Organizations.AsNoTracking().AsQueryable();

        // TenantAdmin is scoped to their own tenant.
        if (!isPlatformAdmin && callerTenantId is not null && Guid.TryParse(callerTenantId, out var callerTid))
        {
            q = q.Where(o => o.TenantId == callerTid);
        }
        else if (!string.IsNullOrWhiteSpace(tenantId) && Guid.TryParse(tenantId, out var filterTid))
        {
            q = string.Equals(orgType, OrgType.LawFirm, StringComparison.OrdinalIgnoreCase)
                ? q.Where(o => o.TenantId == filterTid || o.TenantId == null)
                : q.Where(o => o.TenantId == filterTid);
        }

        if (!string.IsNullOrWhiteSpace(orgType))
            q = q.Where(o => o.OrgType == orgType);

        var items = await q
            .Where(o => o.IsActive)
            .OrderBy(o => o.DisplayName ?? o.Name)
            .Select(o => new
            {
                id           = o.Id,
                tenantId     = o.TenantId,
                name         = o.Name,
                displayName  = o.DisplayName ?? o.Name,
                orgType      = o.OrgType,
                providerMode = o.ProviderMode,
                isActive     = o.IsActive,
            })
            .ToListAsync(ct);

        return Results.Ok(new { items, totalCount = items.Count });
    }

    private static async Task<IResult> UpdateOrganization(
        Guid              id,
        UpdateOrganizationRequest body,
        IdentityDbContext db,
        ClaimsPrincipal   caller,
        CancellationToken ct)
    {
        if (!caller.IsInRole("PlatformAdmin"))
            return Results.Forbid();

        var org = await db.Organizations.FirstOrDefaultAsync(o => o.Id == id, ct);
        if (org is null) return Results.NotFound(new { error = $"Organization '{id}' not found." });

        Guid? resolvedTypeId = null;
        if (!string.IsNullOrWhiteSpace(body.OrgType))
        {
            if (!Domain.OrgType.IsValid(body.OrgType))
                return Results.BadRequest(new { error = $"Invalid OrgType: {body.OrgType}. Valid: {string.Join(", ", Domain.OrgType.All)}" });
            resolvedTypeId = OrgTypeMapper.TryResolve(body.OrgType);
        }

        org.Update(
            name:               body.Name ?? org.Name,
            displayName:        body.DisplayName ?? org.DisplayName,
            updatedByUserId:    null,
            organizationTypeId: resolvedTypeId,
            orgTypeCode:        body.OrgType);

        await db.SaveChangesAsync(ct);

        return Results.Ok(new
        {
            id           = org.Id,
            tenantId     = org.TenantId,
            name         = org.Name,
            displayName  = org.DisplayName ?? org.Name,
            orgType      = org.OrgType,
            providerMode = org.ProviderMode,
            isActive     = org.IsActive,
        });
    }

    private static async Task<IResult> UpdateOrganizationProviderMode(
        Guid              id,
        UpdateProviderModeRequest body,
        IdentityDbContext db,
        ClaimsPrincipal   caller,
        CancellationToken ct)
    {
        if (!caller.IsInRole("PlatformAdmin"))
            return Results.Forbid();

        if (string.IsNullOrWhiteSpace(body.ProviderMode) || !ProviderModes.IsValid(body.ProviderMode))
            return Results.BadRequest(new
            {
                error = new
                {
                    code    = "INVALID_PROVIDER_MODE",
                    message = $"Invalid provider mode: '{body.ProviderMode}'. Valid values: sell, manage."
                }
            });

        var org = await db.Organizations.FirstOrDefaultAsync(o => o.Id == id, ct);
        if (org is null)
            return Results.NotFound(new { error = $"Organization '{id}' not found." });

        org.SetProviderMode(body.ProviderMode);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new
        {
            id           = org.Id,
            providerMode = org.ProviderMode,
        });
    }

    // ── Request / response DTOs (private, scoped to AdminEndpoints) ─────────

    private record UpdateProviderModeRequest(string ProviderMode);

    /// <summary>
    /// PUM-B02-R10: roleId or roleKey must be provided (not both required).
    /// If both are supplied, roleId takes precedence.
    /// </summary>
    private record AssignRoleRequest(
        Guid?   RoleId                       = null,
        string? RoleKey                      = null,
        Guid?   AssignedByUserId             = null,
        /// <summary>Defaults to GLOBAL when omitted. Valid: GLOBAL.</summary>
        string? ScopeType                    = null,
        Guid?   OrganizationId               = null,
        Guid?   ProductId                    = null,
        Guid?   OrganizationRelationshipId   = null);
    private record CreateTenantRequest(
        string  Name,
        string  Code,
        string  AdminEmail,
        string  AdminFirstName,
        string  AdminLastName,
        string? OrgType = null,
        string? PreferredSubdomain = null,
        List<string>? Products = null,
        string? AddressLine1 = null,
        string? City = null,
        string? State = null,
        string? PostalCode = null,
        double? Latitude = null,
        double? Longitude = null,
        string? GeoPointSource = null);

    // CC2-INT-B09: self-provision for existing Identity user (no duplicate user created)
    private record SelfProvisionTenantRequest(
        Guid   OwnerUserId,
        string TenantName,
        string TenantCode);
    private record InfraSubdomainRequest(string Subdomain);
    private record SetPasswordRequest(string NewPassword);
    private record EntitlementRequest(bool Enabled);
    private record SessionSettingsRequest(int? SessionTimeoutMinutes);
    private record UpdateOrganizationRequest(
        string? Name        = null,
        string? DisplayName = null,
        string? OrgType     = null);
    private record CreateOrgRelationshipRequest(
        Guid  SourceOrganizationId,
        Guid  TargetOrganizationId,
        Guid  RelationshipTypeId,
        Guid? ProductId);
    private record SettingUpdateRequest(object Value);
    private record CreateSupportRequest(string Title, string? Priority, string? Category);
    private record SupportNoteRequest(string Message);
    private record SupportStatusRequest(string Status);
    private record PlatformSettingDto(
        string  key,
        string  label,
        object  value,
        string  type,
        string  description,
        bool    editable);

    // UIX-002 request DTOs
    private record InviteUserRequest(
        Guid    TenantId,
        string  Email,
        string  FirstName,
        string  LastName,
        Guid?   RoleId          = null,
        Guid?   InvitedByUserId = null,
        string? OrganizationName = null,
        string? OrganizationType = null,
        string? OrganizationDisplayName = null);

    /// <summary>PUM-B06: Payload for inviting a PlatformInternal (staff) user.</summary>
    private record InvitePlatformUserRequest(
        string  Email,
        string  FirstName,
        string  LastName,
        Guid?   RoleId = null);

    private record AssignMembershipRequest(
        Guid    OrganizationId,
        string? MemberRole      = null,
        Guid?   GrantedByUserId = null);

    // =========================================================================
    // LS-COR-AUT-011: ABAC POLICY MANAGEMENT
    // =========================================================================

    private static async Task<IResult> ListPolicies(
        IdentityDbContext db,
        ClaimsPrincipal caller,
        string productCode = "",
        string search = "",
        bool activeOnly = true,
        CancellationToken ct = default)
    {
        if (!caller.IsInRole("PlatformAdmin"))
            return Results.Forbid();

        var q = db.Policies
            .Include(p => p.Rules)
            .Include(p => p.PermissionPolicies)
            .AsQueryable();

        if (activeOnly)
            q = q.Where(p => p.IsActive);

        if (!string.IsNullOrWhiteSpace(productCode))
            q = q.Where(p => p.ProductCode == productCode);

        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(p => p.PolicyCode.Contains(search) || p.Name.Contains(search));

        var policies = await q
            .OrderBy(p => p.Priority).ThenBy(p => p.PolicyCode).ThenBy(p => p.Id)
            .Select(p => new
            {
                id = p.Id,
                policyCode = p.PolicyCode,
                name = p.Name,
                description = p.Description,
                productCode = p.ProductCode,
                isActive = p.IsActive,
                priority = p.Priority,
                effect = p.Effect.ToString(),
                rulesCount = p.Rules.Count,
                permissionCount = p.PermissionPolicies.Count(pp => pp.IsActive),
                createdAtUtc = p.CreatedAtUtc,
                updatedAtUtc = p.UpdatedAtUtc,
            })
            .ToListAsync(ct);

        return Results.Ok(new { items = policies, totalCount = policies.Count });
    }

    private static async Task<IResult> GetPolicy(
        Guid id,
        IdentityDbContext db,
        ClaimsPrincipal caller,
        CancellationToken ct = default)
    {
        if (!caller.IsInRole("PlatformAdmin"))
            return Results.Forbid();

        var policy = await db.Policies
            .Include(p => p.Rules)
            .Include(p => p.PermissionPolicies)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        if (policy is null) return Results.NotFound();

        return Results.Ok(new
        {
            id = policy.Id,
            policyCode = policy.PolicyCode,
            name = policy.Name,
            description = policy.Description,
            productCode = policy.ProductCode,
            isActive = policy.IsActive,
            priority = policy.Priority,
            effect = policy.Effect.ToString(),
            createdAtUtc = policy.CreatedAtUtc,
            updatedAtUtc = policy.UpdatedAtUtc,
            createdBy = policy.CreatedBy,
            updatedBy = policy.UpdatedBy,
            rules = policy.Rules.Select(r => new
            {
                id = r.Id,
                conditionType = r.ConditionType.ToString(),
                field = r.Field,
                op = r.Operator.ToString(),
                value = r.Value,
                logicalGroup = r.LogicalGroup.ToString(),
                createdAtUtc = r.CreatedAtUtc,
            }),
            permissionMappings = policy.PermissionPolicies.Select(pp => new
            {
                id = pp.Id,
                permissionCode = pp.PermissionCode,
                isActive = pp.IsActive,
                createdAtUtc = pp.CreatedAtUtc,
            }),
        });
    }

    private record CreatePolicyRequest(
        string PolicyCode,
        string Name,
        string ProductCode,
        string? Description = null,
        int Priority = 0,
        string Effect = "Allow");

    private static async Task<IResult> CreatePolicy(
        CreatePolicyRequest body,
        IdentityDbContext db,
        ClaimsPrincipal caller,
        IAuditEventClient auditClient,
        BuildingBlocks.Authorization.IPolicyVersionProvider policyVersionProvider,
        CancellationToken ct = default)
    {
        if (!caller.IsInRole("PlatformAdmin"))
            return Results.Forbid();

        if (!Identity.Domain.Policy.IsValidPolicyCode(body.PolicyCode))
            return Results.BadRequest(new { error = $"Policy code must follow naming convention 'PRODUCT.domain.qualifier' (e.g. SYNQ_FUND.approval.limit). Got: '{body.PolicyCode}'." });

        if (!Enum.TryParse<Identity.Domain.PolicyEffect>(body.Effect, true, out var effect))
            return Results.BadRequest(new { error = $"Invalid effect: '{body.Effect}'. Valid: Allow, Deny" });

        var exists = await db.Policies.AnyAsync(p => p.PolicyCode == body.PolicyCode.Trim(), ct);
        if (exists)
            return Results.Conflict(new { error = $"Policy code '{body.PolicyCode}' already exists." });

        var callerIdRaw = caller.FindFirstValue(ClaimTypes.NameIdentifier);
        Guid? callerId = Guid.TryParse(callerIdRaw, out var cid) ? cid : null;

        var policy = Identity.Domain.Policy.Create(
            body.PolicyCode, body.Name, body.ProductCode,
            body.Description, body.Priority, effect, callerId);

        db.Policies.Add(policy);
        await db.SaveChangesAsync(ct);
        policyVersionProvider.Increment();

        var auditNow = DateTimeOffset.UtcNow;
        _ = auditClient.IngestAsync(new IngestAuditEventRequest
        {
            EventType     = "policy.created",
            EventCategory = EventCategory.Administrative,
            SourceSystem  = "identity-service",
            SourceService = "admin-api",
            Severity      = SeverityLevel.Info,
            OccurredAtUtc = auditNow,
            Actor         = new AuditEventActorDto { Id = callerIdRaw ?? "system", Type = ActorType.User },
            Entity        = new AuditEventEntityDto { Type = "Policy", Id = policy.Id.ToString() },
            Action        = "PolicyCreated",
            Description   = $"Policy '{policy.PolicyCode}' (effect={effect}) created for product '{body.ProductCode}'",
            IdempotencyKey = IdempotencyKey.ForWithTimestamp(auditNow, "identity-service", "policy.created", policy.Id.ToString()),
        });

        return Results.Created($"/api/admin/policies/{policy.Id}", new
        {
            id = policy.Id, policyCode = policy.PolicyCode, name = policy.Name,
            description = policy.Description, productCode = policy.ProductCode,
            isActive = policy.IsActive, priority = policy.Priority,
            effect = policy.Effect.ToString(),
            createdAtUtc = policy.CreatedAtUtc,
        });
    }

    private record UpdatePolicyRequest(string Name, string? Description, int Priority, string? Effect = null);

    private static async Task<IResult> UpdatePolicy(
        Guid id,
        UpdatePolicyRequest body,
        IdentityDbContext db,
        ClaimsPrincipal caller,
        IAuditEventClient auditClient,
        BuildingBlocks.Authorization.IPolicyVersionProvider policyVersionProvider,
        CancellationToken ct = default)
    {
        if (!caller.IsInRole("PlatformAdmin"))
            return Results.Forbid();

        var policy = await db.Policies.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (policy is null)
            return Results.NotFound(new { error = "Policy not found." });

        Identity.Domain.PolicyEffect? effect = null;
        if (!string.IsNullOrWhiteSpace(body.Effect))
        {
            if (!Enum.TryParse<Identity.Domain.PolicyEffect>(body.Effect, true, out var parsedEffect))
                return Results.BadRequest(new { error = $"Invalid effect: '{body.Effect}'. Valid: Allow, Deny" });
            effect = parsedEffect;
        }

        var callerIdRaw = caller.FindFirstValue(ClaimTypes.NameIdentifier);
        Guid? callerId = Guid.TryParse(callerIdRaw, out var cid) ? cid : null;

        policy.Update(body.Name, body.Description, body.Priority, effect, callerId);
        await db.SaveChangesAsync(ct);
        policyVersionProvider.Increment();

        var auditNow = DateTimeOffset.UtcNow;
        _ = auditClient.IngestAsync(new IngestAuditEventRequest
        {
            EventType     = "policy.updated",
            EventCategory = EventCategory.Administrative,
            SourceSystem  = "identity-service",
            SourceService = "admin-api",
            Severity      = SeverityLevel.Info,
            OccurredAtUtc = auditNow,
            Actor         = new AuditEventActorDto { Id = callerIdRaw ?? "system", Type = ActorType.User },
            Entity        = new AuditEventEntityDto { Type = "Policy", Id = id.ToString() },
            Action        = "PolicyUpdated",
            Description   = $"Policy '{policy.PolicyCode}' updated (effect={policy.Effect})",
            IdempotencyKey = IdempotencyKey.ForWithTimestamp(auditNow, "identity-service", "policy.updated", id.ToString()),
        });

        return Results.Ok(new
        {
            id = policy.Id, policyCode = policy.PolicyCode, name = policy.Name,
            description = policy.Description, productCode = policy.ProductCode,
            isActive = policy.IsActive, priority = policy.Priority,
            effect = policy.Effect.ToString(),
            updatedAtUtc = policy.UpdatedAtUtc,
        });
    }

    private static async Task<IResult> DeactivatePolicy(
        Guid id,
        IdentityDbContext db,
        ClaimsPrincipal caller,
        IAuditEventClient auditClient,
        BuildingBlocks.Authorization.IPolicyVersionProvider policyVersionProvider,
        CancellationToken ct = default)
    {
        if (!caller.IsInRole("PlatformAdmin"))
            return Results.Forbid();

        var policy = await db.Policies.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (policy is null)
            return Results.NotFound(new { error = "Policy not found." });

        if (!policy.IsActive)
            return Results.Ok(new { message = "Policy already deactivated." });

        var callerIdRaw = caller.FindFirstValue(ClaimTypes.NameIdentifier);
        Guid? callerId = Guid.TryParse(callerIdRaw, out var cid) ? cid : null;

        policy.Deactivate(callerId);
        await db.SaveChangesAsync(ct);
        policyVersionProvider.Increment();

        var auditNow = DateTimeOffset.UtcNow;
        _ = auditClient.IngestAsync(new IngestAuditEventRequest
        {
            EventType     = "policy.deactivated",
            EventCategory = EventCategory.Administrative,
            SourceSystem  = "identity-service",
            SourceService = "admin-api",
            Severity      = SeverityLevel.Warn,
            OccurredAtUtc = auditNow,
            Actor         = new AuditEventActorDto { Id = callerIdRaw ?? "system", Type = ActorType.User },
            Entity        = new AuditEventEntityDto { Type = "Policy", Id = id.ToString() },
            Action        = "PolicyDeactivated",
            Description   = $"Policy '{policy.PolicyCode}' deactivated",
            IdempotencyKey = IdempotencyKey.ForWithTimestamp(auditNow, "identity-service", "policy.deactivated", id.ToString()),
        });

        return Results.NoContent();
    }

    // ── Policy Rules ────────────────────────────────────────────────────────────

    private static async Task<IResult> ListPolicyRules(
        Guid policyId,
        IdentityDbContext db,
        ClaimsPrincipal caller,
        CancellationToken ct = default)
    {
        if (!caller.IsInRole("PlatformAdmin"))
            return Results.Forbid();

        var policy = await db.Policies.Include(p => p.Rules).FirstOrDefaultAsync(p => p.Id == policyId, ct);
        if (policy is null) return Results.NotFound();

        return Results.Ok(new
        {
            policyId = policy.Id,
            policyCode = policy.PolicyCode,
            rules = policy.Rules.Select(r => new
            {
                id = r.Id,
                conditionType = r.ConditionType.ToString(),
                field = r.Field,
                op = r.Operator.ToString(),
                value = r.Value,
                logicalGroup = r.LogicalGroup.ToString(),
                createdAtUtc = r.CreatedAtUtc,
            }),
        });
    }

    private record CreatePolicyRuleRequest(
        string ConditionType,
        string Field,
        string Operator,
        string Value,
        string LogicalGroup = "And");

    private static async Task<IResult> CreatePolicyRule(
        Guid policyId,
        CreatePolicyRuleRequest body,
        IdentityDbContext db,
        ClaimsPrincipal caller,
        BuildingBlocks.Authorization.IPolicyVersionProvider policyVersionProvider,
        CancellationToken ct = default)
    {
        if (!caller.IsInRole("PlatformAdmin"))
            return Results.Forbid();

        var policy = await db.Policies.FirstOrDefaultAsync(p => p.Id == policyId, ct);
        if (policy is null)
            return Results.NotFound(new { error = "Policy not found." });

        if (!Enum.TryParse<Identity.Domain.PolicyConditionType>(body.ConditionType, true, out var conditionType))
            return Results.BadRequest(new { error = $"Invalid ConditionType: '{body.ConditionType}'. Valid: {string.Join(", ", Enum.GetNames<Identity.Domain.PolicyConditionType>())}" });

        if (!Enum.TryParse<Identity.Domain.RuleOperator>(body.Operator, true, out var op))
            return Results.BadRequest(new { error = $"Invalid Operator: '{body.Operator}'. Valid: {string.Join(", ", Enum.GetNames<Identity.Domain.RuleOperator>())}" });

        if (!Enum.TryParse<Identity.Domain.LogicalGroupType>(body.LogicalGroup, true, out var logicalGroup))
            return Results.BadRequest(new { error = $"Invalid LogicalGroup: '{body.LogicalGroup}'. Valid: And, Or" });

        if (!Identity.Domain.PolicyRule.IsFieldSupported(body.Field))
            return Results.BadRequest(new { error = $"Field '{body.Field}' is not supported. Supported: {string.Join(", ", Identity.Domain.PolicyRule.GetSupportedFields())}" });

        try
        {
            var rule = Identity.Domain.PolicyRule.Create(policyId, conditionType, body.Field, op, body.Value, logicalGroup);
            db.PolicyRules.Add(rule);
            await db.SaveChangesAsync(ct);
            policyVersionProvider.Increment();

            return Results.Created($"/api/admin/policies/{policyId}/rules/{rule.Id}", new
            {
                id = rule.Id,
                policyId = rule.PolicyId,
                conditionType = rule.ConditionType.ToString(),
                field = rule.Field,
                op = rule.Operator.ToString(),
                value = rule.Value,
                logicalGroup = rule.LogicalGroup.ToString(),
                createdAtUtc = rule.CreatedAtUtc,
            });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private record UpdatePolicyRuleRequest(
        string ConditionType,
        string Field,
        string Operator,
        string Value,
        string LogicalGroup);

    private static async Task<IResult> UpdatePolicyRule(
        Guid policyId,
        Guid ruleId,
        UpdatePolicyRuleRequest body,
        IdentityDbContext db,
        ClaimsPrincipal caller,
        BuildingBlocks.Authorization.IPolicyVersionProvider policyVersionProvider,
        CancellationToken ct = default)
    {
        if (!caller.IsInRole("PlatformAdmin"))
            return Results.Forbid();

        var rule = await db.PolicyRules.FirstOrDefaultAsync(r => r.Id == ruleId && r.PolicyId == policyId, ct);
        if (rule is null)
            return Results.NotFound(new { error = "Rule not found." });

        if (!Enum.TryParse<Identity.Domain.PolicyConditionType>(body.ConditionType, true, out var conditionType))
            return Results.BadRequest(new { error = $"Invalid ConditionType: '{body.ConditionType}'." });

        if (!Enum.TryParse<Identity.Domain.RuleOperator>(body.Operator, true, out var op))
            return Results.BadRequest(new { error = $"Invalid Operator: '{body.Operator}'." });

        if (!Enum.TryParse<Identity.Domain.LogicalGroupType>(body.LogicalGroup, true, out var logicalGroup))
            return Results.BadRequest(new { error = $"Invalid LogicalGroup: '{body.LogicalGroup}'." });

        try
        {
            rule.Update(conditionType, body.Field, op, body.Value, logicalGroup);
            await db.SaveChangesAsync(ct);
            policyVersionProvider.Increment();

            return Results.Ok(new
            {
                id = rule.Id,
                policyId = rule.PolicyId,
                conditionType = rule.ConditionType.ToString(),
                field = rule.Field,
                op = rule.Operator.ToString(),
                value = rule.Value,
                logicalGroup = rule.LogicalGroup.ToString(),
            });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> DeletePolicyRule(
        Guid policyId,
        Guid ruleId,
        IdentityDbContext db,
        ClaimsPrincipal caller,
        BuildingBlocks.Authorization.IPolicyVersionProvider policyVersionProvider,
        CancellationToken ct = default)
    {
        if (!caller.IsInRole("PlatformAdmin"))
            return Results.Forbid();

        var rule = await db.PolicyRules.FirstOrDefaultAsync(r => r.Id == ruleId && r.PolicyId == policyId, ct);
        if (rule is null)
            return Results.NotFound(new { error = "Rule not found." });

        db.PolicyRules.Remove(rule);
        await db.SaveChangesAsync(ct);
        policyVersionProvider.Increment();

        return Results.NoContent();
    }

    // ── Permission ↔ Policy Mappings ────────────────────────────────────────────

    private static async Task<IResult> ListPermissionPolicies(
        IdentityDbContext db,
        ClaimsPrincipal caller,
        string? permissionCode = null,
        Guid? policyId = null,
        CancellationToken ct = default)
    {
        if (!caller.IsInRole("PlatformAdmin"))
            return Results.Forbid();

        var q = db.PermissionPolicies
            .Include(pp => pp.Policy)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(permissionCode))
            q = q.Where(pp => pp.PermissionCode == permissionCode);

        if (policyId.HasValue)
            q = q.Where(pp => pp.PolicyId == policyId.Value);

        var items = await q
            .Select(pp => new
            {
                id = pp.Id,
                permissionCode = pp.PermissionCode,
                policyId = pp.PolicyId,
                policyCode = pp.Policy.PolicyCode,
                policyName = pp.Policy.Name,
                isActive = pp.IsActive,
                createdAtUtc = pp.CreatedAtUtc,
            })
            .ToListAsync(ct);

        return Results.Ok(new { items, totalCount = items.Count });
    }

    private record CreatePermissionPolicyRequest(string PermissionCode, Guid PolicyId);

    private static async Task<IResult> CreatePermissionPolicy(
        CreatePermissionPolicyRequest body,
        IdentityDbContext db,
        ClaimsPrincipal caller,
        IAuditEventClient auditClient,
        BuildingBlocks.Authorization.IPolicyVersionProvider policyVersionProvider,
        CancellationToken ct = default)
    {
        if (!caller.IsInRole("PlatformAdmin"))
            return Results.Forbid();

        var policy = await db.Policies.FirstOrDefaultAsync(p => p.Id == body.PolicyId, ct);
        if (policy is null)
            return Results.BadRequest(new { error = "Policy not found." });

        var permExists = await db.Permissions.AnyAsync(p => p.Code == body.PermissionCode && p.IsActive, ct);
        if (!permExists)
            return Results.BadRequest(new { error = $"Permission '{body.PermissionCode}' not found or inactive." });

        var existing = await db.PermissionPolicies
            .FirstOrDefaultAsync(pp => pp.PermissionCode == body.PermissionCode && pp.PolicyId == body.PolicyId, ct);

        if (existing != null)
        {
            if (existing.IsActive)
                return Results.Conflict(new { error = "This permission-policy mapping already exists." });

            existing.Activate();
            await db.SaveChangesAsync(ct);
            policyVersionProvider.Increment();
            return Results.Ok(new { id = existing.Id, permissionCode = existing.PermissionCode, policyId = existing.PolicyId, isActive = true, message = "Reactivated existing mapping." });
        }

        var mapping = Identity.Domain.PermissionPolicy.Create(body.PermissionCode, body.PolicyId);
        db.PermissionPolicies.Add(mapping);
        await db.SaveChangesAsync(ct);
        policyVersionProvider.Increment();

        var callerIdRaw = caller.FindFirstValue(ClaimTypes.NameIdentifier);
        var auditNow = DateTimeOffset.UtcNow;
        _ = auditClient.IngestAsync(new IngestAuditEventRequest
        {
            EventType     = "permission_policy.created",
            EventCategory = EventCategory.Administrative,
            SourceSystem  = "identity-service",
            SourceService = "admin-api",
            Severity      = SeverityLevel.Info,
            OccurredAtUtc = auditNow,
            Actor         = new AuditEventActorDto { Id = callerIdRaw ?? "system", Type = ActorType.User },
            Entity        = new AuditEventEntityDto { Type = "PermissionPolicy", Id = mapping.Id.ToString() },
            Action        = "PermissionPolicyCreated",
            Description   = $"Permission '{body.PermissionCode}' linked to policy '{policy.PolicyCode}'",
            IdempotencyKey = IdempotencyKey.ForWithTimestamp(auditNow, "identity-service", "permission_policy.created", mapping.Id.ToString()),
        });

        return Results.Created($"/api/admin/permission-policies/{mapping.Id}", new
        {
            id = mapping.Id,
            permissionCode = mapping.PermissionCode,
            policyId = mapping.PolicyId,
            policyCode = policy.PolicyCode,
            isActive = mapping.IsActive,
            createdAtUtc = mapping.CreatedAtUtc,
        });
    }

    private static async Task<IResult> DeactivatePermissionPolicy(
        Guid id,
        IdentityDbContext db,
        ClaimsPrincipal caller,
        BuildingBlocks.Authorization.IPolicyVersionProvider policyVersionProvider,
        CancellationToken ct = default)
    {
        if (!caller.IsInRole("PlatformAdmin"))
            return Results.Forbid();

        var mapping = await db.PermissionPolicies.FirstOrDefaultAsync(pp => pp.Id == id, ct);
        if (mapping is null)
            return Results.NotFound(new { error = "Permission-policy mapping not found." });

        if (!mapping.IsActive)
            return Results.Ok(new { message = "Mapping already deactivated." });

        mapping.Deactivate();
        await db.SaveChangesAsync(ct);
        policyVersionProvider.Increment();

        return Results.NoContent();
    }

    // ── Supported fields for condition builder ──────────────────────────────────

    private static IResult GetSupportedFields(ClaimsPrincipal caller)
    {
        if (!caller.IsInRole("PlatformAdmin"))
            return Results.Forbid();

        var fields = Identity.Domain.PolicyRule.GetSupportedFields();
        var operators = Enum.GetNames<Identity.Domain.RuleOperator>();
        var conditionTypes = Enum.GetNames<Identity.Domain.PolicyConditionType>();
        var logicalGroups = Enum.GetNames<Identity.Domain.LogicalGroupType>();

        var effects = Enum.GetNames<Identity.Domain.PolicyEffect>();

        return Results.Ok(new
        {
            fields = fields.ToList(),
            operators,
            conditionTypes,
            logicalGroups,
            effects,
        });
    }

    // =========================================================================
    // PUM-B03 — Tenant User Management
    // =========================================================================

    /// <summary>
    /// PUM-B03-R01/R02: GET /api/admin/tenants/{tenantId}/users
    ///
    /// Lists all users whose primary tenant is <paramref name="tenantId"/>,
    /// including their active tenant-scoped role assignments.
    ///
    /// Tenant isolation:
    ///   PlatformAdmin — may query any tenantId.
    ///   TenantAdmin   — restricted to their own tenant; any other tenantId → 403.
    /// </summary>
    private static async Task<IResult> ListTenantUsers(
        Guid              tenantId,
        IdentityDbContext db,
        ClaimsPrincipal   caller,
        int    page     = 1,
        int    pageSize = 20,
        string search   = "")
    {
        // Tenant isolation check
        if (IsCrossTenantAccess(caller, tenantId)) return Results.Forbid();

        var tenant = await db.Tenants.FindAsync(tenantId);
        if (tenant is null) return Results.NotFound(new { error = $"Tenant '{tenantId}' not found." });

        var q = db.Users
            .Where(u => db.UserTenants.Any(ut => ut.UserId == u.Id && ut.TenantId == tenantId && ut.IsActive))
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(u =>
                u.Email.Contains(search) ||
                u.FirstName.Contains(search) ||
                u.LastName.Contains(search));

        var total = await q.CountAsync();

        var users = await q
            .OrderBy(u => u.Email)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(u => new
            {
                userId       = u.Id,
                email        = u.Email,
                firstName    = u.FirstName,
                lastName     = u.LastName,
                displayName  = u.FirstName + " " + u.LastName,
                userType     = u.UserType.ToString(),
                isActive     = u.IsActive,
                tenantId     = tenantId,
                roles        = db.ScopedRoleAssignments
                                 .Where(s => s.UserId == u.Id && s.IsActive
                                          && s.ScopeType == ScopedRoleAssignment.ScopeTypes.Global
                                          && s.Role.Scope == RoleScopes.Tenant)
                                 .Select(s => new
                                 {
                                     assignmentId = s.Id,
                                     roleId       = s.RoleId,
                                     roleName     = s.Role.Name,
                                     roleScope    = s.Role.Scope,
                                     assignedAtUtc = s.AssignedAtUtc,
                                 })
                                 .ToList(),
                createdAtUtc = u.CreatedAtUtc,
                updatedAtUtc = u.UpdatedAtUtc,
                lastLoginAtUtc = u.LastLoginAtUtc,
            })
            .ToListAsync();

        return Results.Ok(new
        {
            items      = users,
            totalCount = total,
            page,
            pageSize,
        });
    }

    /// <summary>
    /// PUM-B03-R03: POST /api/admin/tenants/{tenantId}/users
    ///
    /// Verifies a user belongs to the given tenant and optionally assigns a
    /// Tenant-scoped role.
    ///
    /// Architecture note (PUM-B03-R09): tenant membership is represented by
    /// idt_UserTenants. If the supplied userId is not an active member of the
    /// target tenant this endpoint returns 409.
    ///
    /// If the user is already in the tenant and no
    /// role is requested, returns 200 with the current user state (safe no-op).
    /// </summary>
    private static async Task<IResult> AssignUserToTenant(
        Guid                      tenantId,
        AssignUserToTenantRequest body,
        IdentityDbContext         db,
        ClaimsPrincipal           caller)
    {
        if (IsCrossTenantAccess(caller, tenantId)) return Results.Forbid();

        var tenant = await db.Tenants.FindAsync(tenantId);
        if (tenant is null) return Results.NotFound(new { error = $"Tenant '{tenantId}' not found." });

        var user = await db.Users.FindAsync(body.UserId);
        if (user is null) return Results.NotFound(new { error = $"User '{body.UserId}' not found." });

        // PUM-B03-R09: active tenant-membership guard
        var userInTenant = await db.UserTenants.AnyAsync(
            ut => ut.UserId == user.Id && ut.TenantId == tenantId && ut.IsActive);
        if (!userInTenant)
            return Results.Conflict(new
            {
                error   = "USER_IN_DIFFERENT_TENANT",
                message = "This user does not belong to the specified tenant. " +
                          "Cross-tenant user membership is not supported in the current schema. " +
                          "Each user has exactly one home tenant via idt_UserTenants. " +
                          "To move a user, provision a new account in the target tenant.",
                targetTenantId = tenantId,
            });

        // Optional: assign a Tenant-scoped role
        if (body.RoleId.HasValue || !string.IsNullOrWhiteSpace(body.RoleKey))
        {
            Role? role = null;
            if (body.RoleId.HasValue && body.RoleId != Guid.Empty)
                role = await db.Roles.FindAsync(body.RoleId.Value);
            else if (!string.IsNullOrWhiteSpace(body.RoleKey))
                role = await db.Roles.FirstOrDefaultAsync(r => r.Name == body.RoleKey!.Trim());

            if (role is null)
            {
                var identifier = body.RoleId.HasValue ? body.RoleId.ToString() : body.RoleKey;
                return Results.NotFound(new { error = $"Role '{identifier}' not found." });
            }

            if (role.Scope != RoleScopes.Tenant)
                return Results.BadRequest(new
                {
                    error   = "ROLE_SCOPE_INVALID",
                    message = $"Role '{role.Name}' has scope '{role.Scope ?? "(none)"}'. " +
                              "Only Tenant-scoped roles may be assigned through tenant user management.",
                });

            // Idempotent: skip if already active
            var alreadyAssigned = await db.ScopedRoleAssignments
                .AnyAsync(s => s.UserId == user.Id && s.RoleId == role.Id
                            && s.IsActive
                            && s.ScopeType == ScopedRoleAssignment.ScopeTypes.Global);

            if (!alreadyAssigned)
            {
                var sra = ScopedRoleAssignment.Create(
                    userId:   user.Id,
                    roleId:   role.Id,
                    scopeType: ScopedRoleAssignment.ScopeTypes.Global,
                    tenantId: tenantId);
                db.ScopedRoleAssignments.Add(sra);
                await db.SaveChangesAsync();
            }
        }

        // Return current tenant role assignments for this user
        var assignments = await db.ScopedRoleAssignments
            .Where(s => s.UserId == user.Id && s.IsActive
                     && s.ScopeType == ScopedRoleAssignment.ScopeTypes.Global
                     && s.Role.Scope == RoleScopes.Tenant)
            .Select(s => new { assignmentId = s.Id, roleId = s.RoleId, roleName = s.Role.Name })
            .ToListAsync();

        return Results.Ok(new
        {
            userId    = user.Id,
            tenantId  = tenantId,
            email     = user.Email,
            firstName = user.FirstName,
            lastName  = user.LastName,
            isActive  = user.IsActive,
            roles     = assignments,
        });
    }

    /// <summary>
    /// PUM-B03-R04: DELETE /api/admin/tenants/{tenantId}/users/{userId}
    ///
    /// Soft-removes a user from a tenant by deactivating all their active
    /// Tenant-scoped ScopedRoleAssignments.
    ///
    /// Architecture note: the user account itself is not deactivated globally —
    /// only tenant-scoped role memberships are revoked.
    /// </summary>
    private static async Task<IResult> RemoveUserFromTenant(
        Guid              tenantId,
        Guid              userId,
        IdentityDbContext db,
        ClaimsPrincipal   caller)
    {
        if (IsCrossTenantAccess(caller, tenantId)) return Results.Forbid();

        var tenant = await db.Tenants.FindAsync(tenantId);
        if (tenant is null) return Results.NotFound(new { error = $"Tenant '{tenantId}' not found." });

        var user = await db.Users.FindAsync(userId);
        if (user is null) return Results.NotFound(new { error = $"User '{userId}' not found." });

        var userInTenantForRemove = await db.UserTenants.AnyAsync(
            ut => ut.UserId == user.Id && ut.TenantId == tenantId && ut.IsActive);
        if (!userInTenantForRemove)
            return Results.NotFound(new { error = $"User '{userId}' is not a member of tenant '{tenantId}'." });

        // Deactivate all active Tenant-scoped role assignments for this user
        var tenantRoles = await db.ScopedRoleAssignments
            .Include(s => s.Role)
            .Where(s => s.UserId == userId && s.IsActive
                     && s.ScopeType == ScopedRoleAssignment.ScopeTypes.Global
                     && s.Role.Scope == RoleScopes.Tenant)
            .ToListAsync();

        if (tenantRoles.Count == 0)
            return Results.Ok(new
            {
                message = "User has no active tenant-scoped role assignments. No changes made.",
                userId,
                tenantId,
                revokedCount = 0,
            });

        foreach (var sra in tenantRoles)
            sra.Deactivate();

        await db.SaveChangesAsync();

        return Results.Ok(new
        {
            message      = "Tenant-scoped role assignments revoked.",
            userId,
            tenantId,
            revokedCount = tenantRoles.Count,
        });
    }

    /// <summary>
    /// PUM-B03-R05: POST /api/admin/tenants/{tenantId}/users/{userId}/roles
    ///
    /// Assigns a Tenant-scoped role to a user within a specific tenant context.
    /// roleId takes precedence over roleKey if both are supplied.
    /// Idempotent — returns 200 if the assignment already exists and is active.
    ///
    /// Enforces:
    ///   - Tenant must exist
    ///   - User must belong to this tenant
    ///   - Role must exist and have Scope == "Tenant"
    ///   - No duplicate active assignment
    /// </summary>
    private static async Task<IResult> AssignTenantRole(
        Guid                      tenantId,
        Guid                      userId,
        AssignTenantRoleRequest   body,
        IdentityDbContext         db,
        ClaimsPrincipal           caller)
    {
        if (IsCrossTenantAccess(caller, tenantId)) return Results.Forbid();

        var tenant = await db.Tenants.FindAsync(tenantId);
        if (tenant is null) return Results.NotFound(new { error = $"Tenant '{tenantId}' not found." });

        var user = await db.Users.FindAsync(userId);
        if (user is null) return Results.NotFound(new { error = $"User '{userId}' not found." });

        var userInTenantForRole = await db.UserTenants.AnyAsync(
            ut => ut.UserId == user.Id && ut.TenantId == tenantId && ut.IsActive);
        if (!userInTenantForRole)
            return Results.Forbid();

        // PUM-B05-R08: ExternalCustomer users must not receive tenant roles
        if (user.UserType == UserType.ExternalCustomer)
            return Results.BadRequest(new
            {
                error   = "EXTERNAL_USER_ROLE_FORBIDDEN",
                message = "ExternalCustomer users cannot be assigned tenant roles. " +
                          "Use product-scoped roles via POST /api/admin/users/{id}/products/{productKey}/roles.",
            });

        // Resolve role by roleId or roleKey
        Role? role = null;
        if (body.RoleId.HasValue && body.RoleId != Guid.Empty)
            role = await db.Roles.FindAsync(body.RoleId.Value);
        else if (!string.IsNullOrWhiteSpace(body.RoleKey))
            role = await db.Roles.FirstOrDefaultAsync(r => r.Name == body.RoleKey!.Trim());
        else
            return Results.BadRequest(new { error = "Either roleId or roleKey must be provided." });

        if (role is null)
        {
            var identifier = body.RoleId.HasValue ? body.RoleId.ToString() : body.RoleKey;
            return Results.NotFound(new { error = $"Role '{identifier}' not found." });
        }

        // PUM-B03-R05: Role must be Tenant-scoped
        if (role.Scope != RoleScopes.Tenant)
            return Results.BadRequest(new
            {
                error   = "ROLE_SCOPE_INVALID",
                message = $"Role '{role.Name}' has scope '{role.Scope ?? "(none)"}'. " +
                          "Only roles with Scope == Tenant may be assigned through tenant user management.",
            });

        // Idempotent: return existing if already active
        var existing = await db.ScopedRoleAssignments
            .FirstOrDefaultAsync(s => s.UserId == userId && s.RoleId == role.Id
                                   && s.IsActive
                                   && s.ScopeType == ScopedRoleAssignment.ScopeTypes.Global);

        if (existing is not null)
            return Results.Ok(new
            {
                assignmentId = existing.Id,
                userId,
                tenantId,
                roleId       = role.Id,
                roleName     = role.Name,
                roleScope    = role.Scope,
                assignedAtUtc = existing.AssignedAtUtc,
                alreadyExisted = true,
            });

        var sra = ScopedRoleAssignment.Create(
            userId:   userId,
            roleId:   role.Id,
            scopeType: ScopedRoleAssignment.ScopeTypes.Global,
            tenantId: tenantId);
        db.ScopedRoleAssignments.Add(sra);
        await db.SaveChangesAsync();

        return Results.Created(
            $"/api/admin/tenants/{tenantId}/users/{userId}/roles/{sra.Id}",
            new
            {
                assignmentId = sra.Id,
                userId,
                tenantId,
                roleId       = role.Id,
                roleName     = role.Name,
                roleScope    = role.Scope,
                assignedAtUtc = sra.AssignedAtUtc,
                alreadyExisted = false,
            });
    }

    /// <summary>
    /// PUM-B03-R06: DELETE /api/admin/tenants/{tenantId}/users/{userId}/roles/{assignmentId}
    ///
    /// Soft-deactivates a specific Tenant-scoped ScopedRoleAssignment.
    /// Only affects the specified assignment within the specified tenant context.
    /// Platform roles and product roles are not affected.
    /// </summary>
    private static async Task<IResult> RevokeTenantRole(
        Guid              tenantId,
        Guid              userId,
        Guid              assignmentId,
        IdentityDbContext db,
        ClaimsPrincipal   caller)
    {
        if (IsCrossTenantAccess(caller, tenantId)) return Results.Forbid();

        var tenant = await db.Tenants.FindAsync(tenantId);
        if (tenant is null) return Results.NotFound(new { error = $"Tenant '{tenantId}' not found." });

        var user = await db.Users.FindAsync(userId);
        if (user is null) return Results.NotFound(new { error = $"User '{userId}' not found." });

        var userInTenantForRevoke = await db.UserTenants.AnyAsync(
            ut => ut.UserId == user.Id && ut.TenantId == tenantId && ut.IsActive);
        if (!userInTenantForRevoke)
            return Results.NotFound(new { error = $"User '{userId}' is not a member of tenant '{tenantId}'." });

        var sra = await db.ScopedRoleAssignments
            .Include(s => s.Role)
            .FirstOrDefaultAsync(s => s.Id == assignmentId
                                   && s.UserId == userId
                                   && s.IsActive
                                   && s.ScopeType == ScopedRoleAssignment.ScopeTypes.Global);

        if (sra is null)
            return Results.NotFound(new { error = $"Active role assignment '{assignmentId}' not found for user '{userId}'." });

        // Guard: only allow revoking Tenant-scoped roles through this endpoint
        if (sra.Role?.Scope != RoleScopes.Tenant)
            return Results.BadRequest(new
            {
                error   = "ROLE_SCOPE_INVALID",
                message = $"Assignment '{assignmentId}' is not a Tenant-scoped role. " +
                          "Use the platform role endpoints to manage non-Tenant roles.",
            });

        sra.Deactivate();
        await db.SaveChangesAsync();

        return Results.NoContent();
    }

    // ── PUM-B03 DTOs ─────────────────────────────────────────────────────────

    private record AssignUserToTenantRequest(
        Guid    UserId,
        Guid?   RoleId  = null,
        string? RoleKey = null);

    private record AssignTenantRoleRequest(
        Guid?   RoleId  = null,
        string? RoleKey = null);

    // =========================================================================
    // PUM-B04 — Product Access Control
    // =========================================================================

    /// <summary>
    /// Resolves an inbound productKey string to the canonical uppercase DB code.
    /// Accepts frontend aliases (e.g., "SynqFund"), raw uppercase codes, or
    /// lowercase variants.  Returns null if the product does not exist in the DB.
    /// </summary>
    private static async Task<string?> ResolveProductCode(string productKey, IdentityDbContext db, CancellationToken ct = default)
    {
        return await ResolveExistingDbProductCodeAsync(productKey, db, ct, requireActive: true);
    }

    /// <summary>
    /// PUM-B04-R05: GET /api/admin/users/{id}/products
    ///
    /// Lists all product access records for a user (all statuses: Granted and Revoked).
    /// Tenant isolation: PlatformAdmin sees any user; TenantAdmin sees own-tenant users only.
    /// </summary>
    private static async Task<IResult> ListUserProductAccess(
        Guid              id,
        IdentityDbContext db,
        ClaimsPrincipal   caller,
        CancellationToken ct)
    {
        var user = await db.Users.FindAsync([id], ct);
        if (user is null) return Results.NotFound(new { error = $"User '{id}' not found." });

        if (await IsCrossTenantAccessAsync(caller, user.Id, db, ct)) return Results.Forbid();

        var records = await db.UserProductAccessRecords
            .Where(a => a.UserId == id)
            .OrderBy(a => a.ProductCode)
            .ToListAsync(ct);

        // Enrich with product display names
        var codes    = records.Select(r => r.ProductCode).Distinct().ToList();
        var products = await db.Products
            .Where(p => codes.Contains(p.Code))
            .ToDictionaryAsync(p => p.Code, p => p.Name, ct);

        return Results.Ok(records.Select(a => new
        {
            id            = a.Id,
            userId        = a.UserId,
            tenantId      = a.TenantId,
            productCode   = a.ProductCode,
            displayName   = products.GetValueOrDefault(a.ProductCode, a.ProductCode),
            accessStatus  = a.AccessStatus.ToString(),
            isActive      = a.AccessStatus == AccessStatus.Granted,
            grantedAtUtc  = a.GrantedAtUtc,
            revokedAtUtc  = a.RevokedAtUtc,
            sourceType    = a.SourceType,
            createdAtUtc  = a.CreatedAtUtc,
            updatedAtUtc  = a.UpdatedAtUtc,
        }));
    }

    /// <summary>
    /// PUM-B04-R06/R03: POST /api/admin/users/{id}/products
    ///
    /// Grants a user access to a product.  Idempotent — re-grants if previously revoked.
    ///
    /// Admin bypass: unlike IUserProductAccessService.GrantAsync, this endpoint does NOT
    /// enforce TenantProductEntitlement — platform admins can grant access independently
    /// of tenant subscription state.  This is intentional for administrative provisioning.
    ///
    /// tenantId in the request body must match an active tenant membership for the user.
    /// If omitted, the caller/user operational tenant is used.
    /// </summary>
    private static async Task<IResult> GrantUserProductAccess(
        Guid                        id,
        GrantUserProductAccessRequest body,
        IdentityDbContext           db,
        ClaimsPrincipal             caller,
        CancellationToken           ct)
    {
        var user = await db.Users.FindAsync([id], ct);
        if (user is null) return Results.NotFound(new { error = $"User '{id}' not found." });

        if (await IsCrossTenantAccessAsync(caller, user.Id, db, ct)) return Results.Forbid();

        var opTenantId = await GetOperationalTenantIdAsync(caller, user.Id, db, ct);

        // tenantId, if supplied, must match the user's tenant membership
        if (body.TenantId.HasValue)
        {
            var memberOfSupplied = await db.UserTenants.AnyAsync(
                ut => ut.UserId == user.Id && ut.TenantId == body.TenantId.Value && ut.IsActive, ct);
            if (!memberOfSupplied)
                return Results.Conflict(new
                {
                    error   = "TENANT_MISMATCH",
                    message = "Supplied tenantId does not match any active tenant membership for this user.",
                    suppliedTenantId = body.TenantId.Value,
                });
        }

        var effectiveTenantId = body.TenantId ?? opTenantId;

        // Resolve + validate product code
        var dbCode = await ResolveProductCode(body.ProductKey, db, ct);
        if (dbCode is null)
            return Results.NotFound(new { error = $"Product '{body.ProductKey}' not found or is inactive." });

        // Idempotent: re-grant if previously revoked; return current state if already granted
        var existing = await db.UserProductAccessRecords
            .FirstOrDefaultAsync(a => a.TenantId == effectiveTenantId
                                   && a.UserId == id
                                   && a.ProductCode == dbCode, ct);

        var alreadyActive = existing?.AccessStatus == AccessStatus.Granted;

        if (existing is not null)
        {
            existing.Grant();  // idempotent if already granted
            await db.SaveChangesAsync(ct);
        }
        else
        {
            var access = UserProductAccess.Create(effectiveTenantId, id, dbCode);
            db.UserProductAccessRecords.Add(access);
            await db.SaveChangesAsync(ct);
            existing = access;
        }

        return Results.Ok(new
        {
            id            = existing.Id,
            userId        = existing.UserId,
            tenantId      = existing.TenantId,
            productCode   = existing.ProductCode,
            accessStatus  = existing.AccessStatus.ToString(),
            isActive      = existing.AccessStatus == AccessStatus.Granted,
            grantedAtUtc  = existing.GrantedAtUtc,
            revokedAtUtc  = existing.RevokedAtUtc,
            alreadyActive,
        });
    }

    /// <summary>
    /// PUM-B04-R07/R04: DELETE /api/admin/users/{id}/products/{productKey}
    ///
    /// Soft-revokes a user's access to a specific product.
    /// Optional query parameter: tenantId (defaults to caller/user operational tenant).
    /// Returns 404 if no active access record is found.
    /// </summary>
    private static async Task<IResult> RevokeUserProductAccess(
        Guid              id,
        string            productKey,
        IdentityDbContext db,
        ClaimsPrincipal   caller,
        CancellationToken ct,
        Guid?             tenantId = null)
    {
        var user = await db.Users.FindAsync([id], ct);
        if (user is null) return Results.NotFound(new { error = $"User '{id}' not found." });

        if (await IsCrossTenantAccessAsync(caller, user.Id, db, ct)) return Results.Forbid();

        var revokeOpTenantId = await GetOperationalTenantIdAsync(caller, user.Id, db, ct);
        var effectiveTenantId = tenantId ?? revokeOpTenantId;

        var dbCode = await ResolveProductCode(productKey, db, ct);
        if (dbCode is null)
            return Results.NotFound(new { error = $"Product '{productKey}' not found or is inactive." });

        var existing = await db.UserProductAccessRecords
            .FirstOrDefaultAsync(a => a.TenantId == effectiveTenantId
                                   && a.UserId == id
                                   && a.ProductCode == dbCode
                                   && a.AccessStatus == AccessStatus.Granted, ct);

        if (existing is null)
            return Results.NotFound(new { error = $"No active product access found for user '{id}' and product '{productKey}'." });

        existing.Revoke();
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    /// <summary>
    /// PUM-B04-R08: GET /api/admin/users/{id}/products/{productKey}/access
    ///
    /// Checks whether a user currently has active access to a specific product.
    /// Optional query parameter: tenantId.
    /// Always returns 200 — the hasAccess field carries the result.
    /// </summary>
    private static async Task<IResult> CheckUserProductAccess(
        Guid              id,
        string            productKey,
        IdentityDbContext db,
        ClaimsPrincipal   caller,
        CancellationToken ct,
        Guid?             tenantId = null)
    {
        var user = await db.Users.FindAsync([id], ct);
        if (user is null) return Results.NotFound(new { error = $"User '{id}' not found." });

        if (await IsCrossTenantAccessAsync(caller, user.Id, db, ct)) return Results.Forbid();

        var checkOpTenantId = await GetOperationalTenantIdAsync(caller, user.Id, db, ct);
        var effectiveTenantId = tenantId ?? checkOpTenantId;

        var dbCode = await ResolveProductCode(productKey, db, ct);
        if (dbCode is null)
            return Results.NotFound(new { error = $"Product '{productKey}' not found or is inactive." });

        var hasAccess = await db.UserProductAccessRecords
            .AnyAsync(a => a.TenantId == effectiveTenantId
                        && a.UserId == id
                        && a.ProductCode == dbCode
                        && a.AccessStatus == AccessStatus.Granted, ct);

        return Results.Ok(new
        {
            hasAccess,
            userId     = id,
            productCode = dbCode,
            tenantId   = effectiveTenantId,
        });
    }

    /// <summary>
    /// PUM-B04-R09: POST /api/admin/users/{id}/products/{productKey}/roles
    ///
    /// Assigns a product-scoped role to a user using UserRoleAssignment.
    /// The role is looked up via ProductRole.Code within the specified product.
    ///
    /// Guards:
    /// 1. User must exist and be in the caller's tenant.
    /// 2. Product must exist and be active.
    /// 3. User must have active (Granted) access to the product.
    /// 4. ProductRole must exist within this product.
    /// 5. No duplicate active assignment (same tenant + user + roleCode).
    /// </summary>
    private static async Task<IResult> AssignUserProductRole(
        Guid                        id,
        string                      productKey,
        AssignUserProductRoleRequest body,
        IdentityDbContext           db,
        ClaimsPrincipal             caller,
        CancellationToken           ct)
    {
        var user = await db.Users.FindAsync([id], ct);
        if (user is null) return Results.NotFound(new { error = $"User '{id}' not found." });

        if (await IsCrossTenantAccessAsync(caller, user.Id, db, ct)) return Results.Forbid();

        var assignRoleOpTenantId = await GetOperationalTenantIdAsync(caller, user.Id, db, ct);
        var effectiveTenantId = body.TenantId ?? assignRoleOpTenantId;

        // Resolve product
        var dbCode = await ResolveProductCode(productKey, db, ct);
        if (dbCode is null)
            return Results.NotFound(new { error = $"Product '{productKey}' not found or is inactive." });

        var product = await db.Products.FirstOrDefaultAsync(p => p.Code == dbCode && p.IsActive, ct);
        if (product is null)
            return Results.NotFound(new { error = $"Product '{productKey}' not found." });

        // Guard: user must have active product access (R09)
        var hasAccess = await db.UserProductAccessRecords
            .AnyAsync(a => a.TenantId == effectiveTenantId
                        && a.UserId == id
                        && a.ProductCode == dbCode
                        && a.AccessStatus == AccessStatus.Granted, ct);
        if (!hasAccess)
            return Results.Conflict(new
            {
                error   = "PRODUCT_ACCESS_REQUIRED",
                message = $"User '{id}' must have active access to product '{dbCode}' before a product role can be assigned. " +
                          "Grant product access first via POST /api/admin/users/{id}/products.",
            });

        // Resolve ProductRole by code or name
        if (string.IsNullOrWhiteSpace(body.RoleCode) && string.IsNullOrWhiteSpace(body.RoleName))
            return Results.BadRequest(new { error = "Either roleCode or roleName must be provided." });

        ProductRole? productRole;
        if (!string.IsNullOrWhiteSpace(body.RoleCode))
            productRole = await db.ProductRoles
                .FirstOrDefaultAsync(pr => pr.ProductId == product.Id
                                        && pr.Code == body.RoleCode.ToUpperInvariant().Trim()
                                        && pr.IsActive, ct);
        else
            productRole = await db.ProductRoles
                .FirstOrDefaultAsync(pr => pr.ProductId == product.Id
                                        && pr.Name == body.RoleName!.Trim()
                                        && pr.IsActive, ct);

        if (productRole is null)
            return Results.NotFound(new
            {
                error   = "PRODUCT_ROLE_NOT_FOUND",
                message = $"ProductRole '{body.RoleCode ?? body.RoleName}' not found for product '{dbCode}'.",
            });

        // Idempotent: check for existing active assignment
        var alreadyAssigned = await db.UserRoleAssignments
            .AnyAsync(a => a.TenantId == effectiveTenantId
                        && a.UserId == id
                        && a.ProductCode == dbCode
                        && a.RoleCode == productRole.Code
                        && a.AssignmentStatus == AssignmentStatus.Active, ct);

        if (alreadyAssigned)
            return Results.Conflict(new
            {
                error   = "DUPLICATE_PRODUCT_ROLE_ASSIGNMENT",
                message = $"Role '{productRole.Code}' is already actively assigned to user '{id}' for product '{dbCode}'.",
            });

        var assignment = UserRoleAssignment.Create(
            tenantId:        effectiveTenantId,
            userId:          id,
            roleCode:        productRole.Code,
            productCode:     dbCode);
        db.UserRoleAssignments.Add(assignment);
        await db.SaveChangesAsync(ct);

        return Results.Created(
            $"/api/admin/users/{id}/products/{productKey}/roles/{assignment.Id}",
            new
            {
                assignmentId     = assignment.Id,
                userId           = assignment.UserId,
                tenantId         = assignment.TenantId,
                productCode      = assignment.ProductCode,
                roleCode         = assignment.RoleCode,
                assignmentStatus = assignment.AssignmentStatus.ToString(),
                assignedAtUtc    = assignment.AssignedAtUtc,
            });
    }

    /// <summary>
    /// PUM-B04-R10: DELETE /api/admin/users/{id}/products/{productKey}/roles/{assignmentId}
    ///
    /// Soft-removes a specific product-scoped role assignment (UserRoleAssignment).
    /// Only affects the exact assignment specified — does not touch tenant or platform roles.
    /// </summary>
    private static async Task<IResult> RevokeUserProductRole(
        Guid              id,
        string            productKey,
        Guid              assignmentId,
        IdentityDbContext db,
        ClaimsPrincipal   caller,
        CancellationToken ct)
    {
        var user = await db.Users.FindAsync([id], ct);
        if (user is null) return Results.NotFound(new { error = $"User '{id}' not found." });

        if (await IsCrossTenantAccessAsync(caller, user.Id, db, ct)) return Results.Forbid();

        var dbCode = await ResolveProductCode(productKey, db, ct);
        if (dbCode is null)
            return Results.NotFound(new { error = $"Product '{productKey}' not found or is inactive." });

        var assignment = await db.UserRoleAssignments
            .FirstOrDefaultAsync(a => a.Id == assignmentId
                                   && a.UserId == id
                                   && a.ProductCode == dbCode
                                   && a.AssignmentStatus == AssignmentStatus.Active, ct);

        if (assignment is null)
            return Results.NotFound(new
            {
                error = $"Active product role assignment '{assignmentId}' not found for user '{id}' and product '{productKey}'.",
            });

        assignment.Remove();
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    // ── PUM-B04 DTOs ─────────────────────────────────────────────────────────

    private record GrantUserProductAccessRequest(
        string  ProductKey,
        Guid?   TenantId = null);

    private record AssignUserProductRoleRequest(
        string? RoleCode = null,
        string? RoleName = null,
        Guid?   TenantId = null);

    // =========================================================================
    // PUM-B05: EXTERNAL CUSTOMER USERS
    // =========================================================================

    /// <summary>
    /// PUM-B05-R03: POST /api/admin/external-users
    ///
    /// Creates an ExternalCustomer user linked to a tenant.
    /// Idempotent — if an ExternalCustomer with the same email+tenantId already
    /// exists, returns the existing record (200 with alreadyExisted: true).
    /// If the email exists but belongs to a non-ExternalCustomer user → 409.
    ///
    /// productKeys are optional. All supplied keys are validated BEFORE the user
    /// is saved — the operation is atomic (no partial product grants).
    ///
    /// External users receive a cryptographically random unusable password hash;
    /// they cannot authenticate via the internal login flow.
    /// </summary>
    private static async Task<IResult> CreateExternalUser(
        CreateExternalUserRequest body,
        IdentityDbContext         db,
        ClaimsPrincipal           caller,
        IPasswordHasher           passwordHasher,
        CancellationToken         ct)
    {
        if (body.TenantId == Guid.Empty)
            return Results.BadRequest(new { error = "tenantId is required." });
        if (string.IsNullOrWhiteSpace(body.Email))
            return Results.BadRequest(new { error = "email is required." });
        if (string.IsNullOrWhiteSpace(body.FirstName))
            return Results.BadRequest(new { error = "firstName is required." });
        if (string.IsNullOrWhiteSpace(body.LastName))
            return Results.BadRequest(new { error = "lastName is required." });

        if (IsCrossTenantAccess(caller, body.TenantId)) return Results.Forbid();

        var tenantExists = await db.Tenants.AnyAsync(t => t.Id == body.TenantId, ct);
        if (!tenantExists)
            return Results.NotFound(new { error = $"Tenant '{body.TenantId}' not found." });

        var emailNorm = body.Email.ToLowerInvariant().Trim();

        // Idempotent: check for existing user with same email + tenant
        var existingUser = await db.Users
            .Where(u => u.Email == emailNorm
                && db.UserTenants.Any(ut => ut.UserId == u.Id && ut.TenantId == body.TenantId && ut.IsActive))
            .Select(u => new { u.Id, u.UserType })
            .FirstOrDefaultAsync(ct);

        if (existingUser is not null)
        {
            if (existingUser.UserType != UserType.ExternalCustomer)
                return Results.Conflict(new
                {
                    error   = "CONFLICTING_USER_TYPE",
                    message = $"A non-ExternalCustomer user with email '{emailNorm}' already exists in this tenant. " +
                              $"Existing user type: {existingUser.UserType}.",
                    existingUserId = existingUser.Id,
                });

            // Return existing external user — idempotent
            return Results.Ok(new
            {
                userId        = existingUser.Id,
                tenantId      = body.TenantId,
                email         = emailNorm,
                alreadyExisted = true,
            });
        }

        // Validate ALL productKeys before saving (fail fast — no partial grants)
        var dbCodes = new List<string>();
        if (body.ProductKeys is { Count: > 0 })
        {
            foreach (var key in body.ProductKeys)
            {
                var dbCode = await ResolveProductCode(key, db, ct);
                if (dbCode is null)
                    return Results.BadRequest(new
                    {
                        error   = $"Product '{key}' not found or is inactive.",
                        message = "All productKeys must be valid and active. No user or product access was created.",
                    });
                dbCodes.Add(dbCode);
            }
        }

        // Create external user with an unusable password hash
        // (ExternalCustomers do not authenticate via the internal login flow)
        var unusableHash = passwordHasher.Hash(Guid.CreateVersion7().ToString());
        var user = User.Create(
            tenantId:     body.TenantId,
            email:        emailNorm,
            passwordHash: unusableHash,
            firstName:    body.FirstName.Trim(),
            lastName:     body.LastName.Trim(),
            userType:     UserType.ExternalCustomer);

        // Respect explicit isActive = false; default is active (true)
        if (body.IsActive == false)
            user.Deactivate();

        db.Users.Add(user);
        db.UserTenants.Add(UserTenant.Create(user.Id, body.TenantId));

        // Grant product access for each validated product code
        foreach (var dbCode in dbCodes)
        {
            var access = UserProductAccess.Create(body.TenantId, user.Id, dbCode);
            db.UserProductAccessRecords.Add(access);
        }

        await db.SaveChangesAsync(ct);

        return Results.Created(
            $"/api/admin/external-users/{user.Id}",
            new
            {
                userId         = user.Id,
                tenantId       = body.TenantId,
                email          = user.Email,
                firstName      = user.FirstName,
                lastName       = user.LastName,
                userType       = user.UserType.ToString(),
                isActive       = user.IsActive,
                createdAtUtc   = user.CreatedAtUtc,
                productAccess  = dbCodes,
                alreadyExisted = false,
            });
    }

    /// <summary>
    /// PUM-B05-R04: GET /api/admin/external-users
    ///
    /// Lists ExternalCustomer users with optional filtering.
    /// PlatformAdmin can list across all tenants; TenantAdmin sees own-tenant only.
    ///
    /// Query: tenantId, productKey, search, isActive, page, pageSize
    /// </summary>
    private static async Task<IResult> ListExternalUsers(
        IdentityDbContext db,
        ClaimsPrincipal   caller,
        CancellationToken ct,
        string  tenantId  = "",
        string  productKey = "",
        string  search    = "",
        string  isActive  = "",
        int     page      = 1,
        int     pageSize  = 20)
    {
        var isPlatformAdmin = caller.IsInRole("PlatformAdmin");
        var callerTenantId  = caller.FindFirstValue("tenant_id");

        var q = db.Users.Where(u => u.UserType == UserType.ExternalCustomer);

        // Tenant isolation
        if (!isPlatformAdmin)
        {
            if (!Guid.TryParse(callerTenantId, out var callerTid)) return Results.Forbid();
            q = q.Where(u => db.UserTenants.Any(ut => ut.UserId == u.Id && ut.TenantId == callerTid && ut.IsActive));
        }
        else if (!string.IsNullOrWhiteSpace(tenantId) && Guid.TryParse(tenantId, out var tid))
        {
            q = q.Where(u => db.UserTenants.Any(ut => ut.UserId == u.Id && ut.TenantId == tid && ut.IsActive));
        }

        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(u =>
                u.Email.Contains(search) ||
                u.FirstName.Contains(search) ||
                u.LastName.Contains(search));

        var isActiveTrimmed = isActive.Trim().ToLowerInvariant();
        if (isActiveTrimmed == "true")  q = q.Where(u => u.IsActive);
        else if (isActiveTrimmed == "false") q = q.Where(u => !u.IsActive);

        // productKey filter — join via UserProductAccessRecords
        if (!string.IsNullOrWhiteSpace(productKey))
        {
            var filterCode = await ResolveExistingDbProductCodeAsync(productKey, db, ct, requireActive: true)
                ?? productKey.ToUpperInvariant().Trim();
            q = q.Where(u => db.UserProductAccessRecords.Any(a =>
                a.UserId == u.Id && a.ProductCode == filterCode &&
                a.AccessStatus == AccessStatus.Granted));
        }

        pageSize = Math.Clamp(pageSize, 1, 100);
        page     = Math.Max(1, page);

        var total = await q.CountAsync(ct);

        var items = await q
            .OrderBy(u => u.Email)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(u => new
            {
                id           = u.Id,
                email        = u.Email,
                firstName    = u.FirstName,
                lastName     = u.LastName,
                displayName  = u.FirstName + " " + u.LastName,
                userType     = u.UserType.ToString(),
                isActive     = u.IsActive,
                tenantId     = db.UserTenants
                                 .Where(ut => ut.UserId == u.Id && ut.IsActive)
                                 .OrderBy(ut => ut.JoinedAtUtc)
                                 .Select(ut => (Guid?)ut.TenantId)
                                 .FirstOrDefault(),
                tenantCode   = db.UserTenants
                                 .Where(ut => ut.UserId == u.Id && ut.IsActive)
                                 .OrderBy(ut => ut.JoinedAtUtc)
                                 .Select(ut => ut.Tenant.Code)
                                 .FirstOrDefault(),
                createdAtUtc = u.CreatedAtUtc,
                updatedAtUtc = u.UpdatedAtUtc,
            })
            .ToListAsync(ct);

        return Results.Ok(new { items, totalCount = total, page, pageSize });
    }

    /// <summary>
    /// PUM-B05-R05: GET /api/admin/external-users/{userId}
    ///
    /// Returns full profile for a single ExternalCustomer user, including product access.
    /// Returns 400 if the user exists but is not an ExternalCustomer.
    /// Returns 404 if the user does not exist.
    /// Enforces tenant isolation.
    /// </summary>
    private static async Task<IResult> GetExternalUser(
        Guid              userId,
        IdentityDbContext db,
        ClaimsPrincipal   caller,
        CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null)
            return Results.NotFound(new { error = $"User '{userId}' not found." });

        if (user.UserType != UserType.ExternalCustomer)
            return Results.BadRequest(new
            {
                error   = "USER_TYPE_MISMATCH",
                message = $"User '{userId}' is not an ExternalCustomer (actual type: {user.UserType}). " +
                          $"Use GET /api/admin/users/{userId} for non-external users.",
            });

        if (await IsCrossTenantAccessAsync(caller, user.Id, db, ct)) return Results.Forbid();

        var extOpTenantId = await GetOperationalTenantIdAsync(caller, user.Id, db, ct);
        var extTenant = await GetTenantSummaryAsync(db, extOpTenantId, ct);

        var productAccess = await db.UserProductAccessRecords
            .Where(a => a.UserId == userId)
            .OrderBy(a => a.ProductCode)
            .ToListAsync(ct);

        var codes    = productAccess.Select(r => r.ProductCode).Distinct().ToList();
        var products = await db.Products
            .Where(p => codes.Contains(p.Code))
            .ToDictionaryAsync(p => p.Code, p => p.Name, ct);

        return Results.Ok(new
        {
            userId        = user.Id,
            tenantId      = extOpTenantId,
            tenantCode    = extTenant?.Code,
            email         = user.Email,
            firstName     = user.FirstName,
            lastName      = user.LastName,
            displayName   = $"{user.FirstName} {user.LastName}",
            userType      = user.UserType.ToString(),
            isActive      = user.IsActive,
            createdAtUtc  = user.CreatedAtUtc,
            updatedAtUtc  = user.UpdatedAtUtc,
            lastLoginAtUtc = user.LastLoginAtUtc,
            productAccess = productAccess.Select(a => new
            {
                id           = a.Id,
                productCode  = a.ProductCode,
                displayName  = products.GetValueOrDefault(a.ProductCode, a.ProductCode),
                accessStatus = a.AccessStatus.ToString(),
                isActive     = a.AccessStatus == AccessStatus.Granted,
                grantedAtUtc = a.GrantedAtUtc,
                revokedAtUtc = a.RevokedAtUtc,
            }),
        });
    }

    /// <summary>
    /// PUM-B05-R10: GET /api/admin/external-users/{userId}/products/{productKey}/access
    ///
    /// Checks whether an ExternalCustomer user has active access to a specific product.
    /// Reuses the same AccessStatus.Granted lookup as PUM-B04 CheckUserProductAccess
    /// but additionally guards that the user is of type ExternalCustomer.
    /// Always returns 200 — the hasAccess field carries the boolean result.
    /// </summary>
    private static async Task<IResult> CheckExternalUserProductAccess(
        Guid              userId,
        string            productKey,
        IdentityDbContext db,
        ClaimsPrincipal   caller,
        CancellationToken ct,
        Guid?             tenantId = null)
    {
        var user = await db.Users.FindAsync([userId], ct);
        if (user is null)
            return Results.NotFound(new { error = $"User '{userId}' not found." });

        if (user.UserType != UserType.ExternalCustomer)
            return Results.BadRequest(new
            {
                error   = "USER_TYPE_MISMATCH",
                message = $"User '{userId}' is not an ExternalCustomer (actual type: {user.UserType}). " +
                          $"Use GET /api/admin/users/{userId}/products/{productKey}/access for non-external users.",
            });

        if (await IsCrossTenantAccessAsync(caller, user.Id, db, ct)) return Results.Forbid();

        var extCheckOpTenantId = await GetOperationalTenantIdAsync(caller, user.Id, db, ct);
        var effectiveTenantId = tenantId ?? extCheckOpTenantId;

        var dbCode = await ResolveProductCode(productKey, db, ct);
        if (dbCode is null)
            return Results.NotFound(new { error = $"Product '{productKey}' not found or is inactive." });

        var hasAccess = await db.UserProductAccessRecords
            .AnyAsync(a => a.TenantId == effectiveTenantId
                        && a.UserId == userId
                        && a.ProductCode == dbCode
                        && a.AccessStatus == AccessStatus.Granted, ct);

        return Results.Ok(new
        {
            hasAccess,
            userId,
            productCode = dbCode,
            tenantId    = effectiveTenantId,
        });
    }

    // ── PUM-B05 DTO ───────────────────────────────────────────────────────────

    private record CreateExternalUserRequest(
        Guid          TenantId,
        string        Email,
        string        FirstName,
        string        LastName,
        bool?         IsActive    = null,
        List<string>? ProductKeys = null);

    private static Task<TenantSummaryProjection?> GetTenantSummaryAsync(
        IdentityDbContext db,
        Guid tenantId,
        CancellationToken ct) =>
        db.Tenants
            .AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new TenantSummaryProjection(t.Code, t.Name))
            .FirstOrDefaultAsync(ct);

    private sealed record TenantSummaryProjection(string Code, string Name);

}

/// <summary>
/// LSCC-010: Handler methods for the provider auto-provisioning org endpoints.
/// Separated as partial class extensions for file manageability.
/// </summary>
public static partial class AdminEndpointsLscc010
{
    private const string CareConnectProductCode = "SYNQ_CARECONNECT";
    private const string SynqLienProductCode = "SYNQ_LIENS";
    private const string SynqLienBuyerRoleCode = "SYNQLIEN_BUYER";

    private static async Task EnsureCareConnectUserProductAccessAsync(
        Guid tenantId,
        Guid userId,
        IProductProvisioningService provisioningEngine,
        IUserProductAccessService userProductAccessService,
        Guid? actorUserId,
        CancellationToken ct)
    {
        await provisioningEngine.ProvisionAsync(
            new ProvisionProductRequest(tenantId, CareConnectProductCode, true),
            ct);

        await userProductAccessService.GrantAsync(
            tenantId,
            userId,
            CareConnectProductCode,
            actorUserId,
            ct);
    }

    private static async Task EnsureCareConnectEnrollmentRoleAsync(
        IdentityDbContext db,
        Guid tenantId,
        Guid userId,
        Guid orgId,
        string? orgType,
        Guid? organizationTypeId,
        Guid? actorUserId,
        CancellationToken ct)
    {
        var roleCode = ResolveCareConnectEnrollmentRoleCode(orgType, organizationTypeId);
        if (roleCode is null)
            return;

        var alreadyAssigned = await db.UserRoleAssignments
            .AnyAsync(a =>
                a.TenantId == tenantId &&
                a.UserId == userId &&
                a.ProductCode == CareConnectProductCode &&
                a.RoleCode == roleCode &&
                a.OrganizationId == orgId &&
                a.AssignmentStatus == AssignmentStatus.Active,
                ct);
        if (alreadyAssigned)
            return;

        var user = await db.Users.FindAsync([userId], ct)
            ?? throw new InvalidOperationException($"User {userId} does not exist.");

        db.UserRoleAssignments.Add(UserRoleAssignment.Create(
            tenantId: tenantId,
            userId: userId,
            roleCode: roleCode,
            productCode: CareConnectProductCode,
            organizationId: orgId,
            createdByUserId: actorUserId));

        user.IncrementAccessVersion();
        await db.SaveChangesAsync(ct);
    }

    private static async Task EnsureSynqLienBuyerUserProductAccessAsync(
        Guid tenantId,
        Guid userId,
        IProductProvisioningService provisioningEngine,
        IUserProductAccessService userProductAccessService,
        Guid? actorUserId,
        CancellationToken ct)
    {
        await provisioningEngine.ProvisionAsync(
            new ProvisionProductRequest(tenantId, SynqLienProductCode, true),
            ct);

        await userProductAccessService.GrantAsync(
            tenantId,
            userId,
            SynqLienProductCode,
            actorUserId,
            ct);
    }

    private static async Task EnsureSynqLienBuyerRoleAsync(
        IdentityDbContext db,
        Guid tenantId,
        Guid userId,
        Guid orgId,
        Guid? actorUserId,
        CancellationToken ct)
    {
        var alreadyAssigned = await db.UserRoleAssignments
            .AnyAsync(a =>
                a.TenantId == tenantId &&
                a.UserId == userId &&
                a.ProductCode == SynqLienProductCode &&
                a.RoleCode == SynqLienBuyerRoleCode &&
                a.OrganizationId == orgId &&
                a.AssignmentStatus == AssignmentStatus.Active,
                ct);
        if (alreadyAssigned)
            return;

        var user = await db.Users.FindAsync([userId], ct)
            ?? throw new InvalidOperationException($"User {userId} does not exist.");

        db.UserRoleAssignments.Add(UserRoleAssignment.Create(
            tenantId: tenantId,
            userId: userId,
            roleCode: SynqLienBuyerRoleCode,
            productCode: SynqLienProductCode,
            organizationId: orgId,
            createdByUserId: actorUserId));

        user.IncrementAccessVersion();
        await db.SaveChangesAsync(ct);
    }

    private static string? ResolveCareConnectEnrollmentRoleCode(
        string? orgType,
        Guid? organizationTypeId)
    {
        var orgTypeCode = organizationTypeId.HasValue
            ? OrgTypeMapper.TryResolveCode(organizationTypeId)
            : null;
        var effectiveOrgType = orgTypeCode ?? orgType;

        return effectiveOrgType?.ToUpperInvariant() switch
        {
            OrgType.LawFirm => ProductRoleCodes.CareConnectReferrerAdmin,
            OrgType.Provider => ProductRoleCodes.CareConnectReceiver,
            _ => null,
        };
    }

    /// <summary>
    /// POST /api/admin/organizations
    /// Creates a minimal PROVIDER Organization for a CareConnect provider.
    /// Always creates a new organization — no lookup/reuse of an existing org by name.
    /// </summary>
    public static async Task<IResult> CreateProviderOrganization(
        CreateProviderOrgRequest body,
        IdentityDbContext        db,
        CancellationToken        ct)
    {
        if (body.TenantId   == Guid.Empty) return Results.BadRequest(new { error = "tenantId is required." });
        if (body.ProviderCcId == Guid.Empty) return Results.BadRequest(new { error = "providerCcId is required." });
        if (string.IsNullOrWhiteSpace(body.ProviderName)) return Results.BadRequest(new { error = "providerName is required." });

        // TENANT-B12: Rehydrate the tenant record if Identity doesn't know it yet.
        var tenantExists = await db.Tenants.AnyAsync(t => t.Id == body.TenantId, ct);
        if (!tenantExists)
        {
            try
            {
                var code      = body.TenantId.ToString("N")[..12];
                var rehydrated = Tenant.Rehydrate(id: body.TenantId, code: code);
                db.Tenants.Add(rehydrated);
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Concurrent request already inserted — clear tracker and re-verify.
                db.ChangeTracker.Clear();
                tenantExists = await db.Tenants.AnyAsync(t => t.Id == body.TenantId, ct);
                if (!tenantExists)
                    return Results.Problem(
                        $"Could not rehydrate tenant '{body.TenantId}' in Identity. " +
                        "The tenant must be provisioned before creating an organization.");
            }
        }

        var providerNameNorm = body.ProviderName.Trim();

        // Create with the clean name; fall back to a short disambiguator on
        // unique-constraint collision (two providers in the same tenant with
        // the same display name).
        var org = body.GlobalScope
            ? Organization.CreateGlobal(
                name:        providerNameNorm,
                orgType:     OrgType.Provider,
                displayName: providerNameNorm)
            : Organization.Create(
                tenantId:    body.TenantId,
                name:        providerNameNorm,
                orgType:     OrgType.Provider,
                displayName: providerNameNorm);

        db.Organizations.Add(org);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            var disambiguatedName = $"{providerNameNorm} ({body.ProviderCcId.ToString("D")[..8]})";
            org = body.GlobalScope
                ? Organization.CreateGlobal(
                    name:        disambiguatedName,
                    orgType:     OrgType.Provider,
                    displayName: providerNameNorm)
                : Organization.Create(
                    tenantId:    body.TenantId,
                    name:        disambiguatedName,
                    orgType:     OrgType.Provider,
                    displayName: providerNameNorm);
            db.Organizations.Add(org);
            await db.SaveChangesAsync(ct);
        }

        return Results.Created(
            $"/api/admin/organizations/{org.Id}",
            new CreateProviderOrgResponse(org.Id, org.DisplayName ?? org.Name, IsNew: true));
    }

    /// <summary>
    /// GET /api/admin/organizations/{id}
    /// Returns a minimal org record by ID for verification/lookup.
    /// </summary>
    public static async Task<IResult> GetOrganizationById(
        Guid              id,
        IdentityDbContext db,
        CancellationToken ct)
    {
        var org = await db.Organizations
            .AsNoTracking()
            .Where(o => o.Id == id)
            .Select(o => new { o.Id, o.TenantId, o.Name, o.OrgType, o.ProviderMode, o.IsActive, o.CreatedAtUtc, o.OwnerUserId })
            .FirstOrDefaultAsync(ct);

        return org is null ? Results.NotFound() : Results.Ok(org);
    }

    /// <summary>
    /// POST /api/admin/organizations/synqlien-buyer
    ///
    /// M2M endpoint called by SynqLien public buyer activation before user
    /// self-registration. Liens buyer organization ids are source-system ids,
    /// so this creates or resolves the corresponding Identity LIEN_OWNER org.
    /// </summary>
    public static async Task<IResult> CreateSynqLienBuyerOrganization(
        CreateSynqLienBuyerOrgRequest body,
        IdentityDbContext             db,
        CancellationToken             ct)
    {
        if (body.TenantId == Guid.Empty)
            return Results.BadRequest(new { error = "tenantId is required." });
        if (body.SourceBuyerOrgId == Guid.Empty)
            return Results.BadRequest(new { error = "sourceBuyerOrgId is required." });
        if (string.IsNullOrWhiteSpace(body.BuyerCompanyName))
            return Results.BadRequest(new { error = "buyerCompanyName is required." });

        var tenantExists = await db.Tenants.AnyAsync(t => t.Id == body.TenantId, ct);
        if (!tenantExists)
        {
            try
            {
                var code = body.TenantId.ToString("N")[..12];
                var rehydrated = Tenant.Rehydrate(id: body.TenantId, code: code, status: "Active");
                db.Tenants.Add(rehydrated);
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                tenantExists = await db.Tenants.AnyAsync(t => t.Id == body.TenantId, ct);
                if (!tenantExists)
                {
                    return Results.Problem(
                        $"Could not rehydrate tenant '{body.TenantId}' in Identity. " +
                        "The tenant must be provisioned before creating an organization.");
                }
            }
        }

        var companyName = body.BuyerCompanyName.Trim();
        var sourceName = BuildSynqLienBuyerOrganizationName(companyName, body.SourceBuyerOrgId);

        var existing = await db.Organizations
            .AsNoTracking()
            .FirstOrDefaultAsync(o =>
                o.TenantId == body.TenantId &&
                o.OrgType == OrgType.LienOwner &&
                (o.Name == sourceName || o.Name == companyName),
                ct);

        if (existing is not null)
        {
            return Results.Ok(new CreateSynqLienBuyerOrgResponse(
                existing.Id,
                existing.DisplayName ?? existing.Name,
                IsNew: false));
        }

        var org = Organization.Create(
            tenantId: body.TenantId,
            name: sourceName,
            orgType: OrgType.LienOwner,
            displayName: companyName);

        db.Organizations.Add(org);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            existing = await db.Organizations
                .AsNoTracking()
                .FirstOrDefaultAsync(o =>
                    o.TenantId == body.TenantId &&
                    o.OrgType == OrgType.LienOwner &&
                    (o.Name == sourceName || o.Name == companyName),
                    ct);

            if (existing is null)
                throw;

            return Results.Ok(new CreateSynqLienBuyerOrgResponse(
                existing.Id,
                existing.DisplayName ?? existing.Name,
                IsNew: false));
        }

        return Results.Created(
            $"/api/admin/organizations/{org.Id}",
            new CreateSynqLienBuyerOrgResponse(org.Id, org.DisplayName ?? org.Name, IsNew: true));
    }

    /// <summary>
    /// POST /api/admin/organizations/{orgId}/provision-user
    ///
    /// LSCC-010 / CC2-INT-B04: M2M endpoint called by CareConnect during auto-provisioning
    /// to create an Identity user for the person activating a provider account.
    ///
    /// Idempotent — if a user with this email already exists in the org's tenant, the
    /// existing record is returned (isNew=false). No duplicate is created.
    ///
    /// No permission guard — this is a trusted internal M2M path. It is intentionally
    /// NOT behind RequirePermission(TenantInvitationsManage) because CareConnect calls
    /// it without a user JWT. Requests arriving here have already crossed the internal
    /// service boundary (no public gateway route exists for this path).
    /// </summary>
    public static async Task<IResult> ProvisionProviderUser(
        Guid                                  id,
        ProvisionProviderUserRequest          body,
        IdentityDbContext                     db,
        IPasswordHasher                       passwordHasher,
        IProductProvisioningService           provisioningEngine,
        IUserProductAccessService             userProductAccessService,
        IAuditEventClient                     auditClient,
        IOptions<NotificationsServiceOptions> notifOptions,
        INotificationsEmailClient             emailClient,
        ILoggerFactory                        loggerFactory,
        CancellationToken                     ct)
    {
        var logger = loggerFactory.CreateLogger("AdminEndpointsLscc010.ProvisionProviderUser");

        if (string.IsNullOrWhiteSpace(body.Email))
            return Results.BadRequest(new { error = "email is required." });
        if (string.IsNullOrWhiteSpace(body.FirstName))
            return Results.BadRequest(new { error = "firstName is required." });

        var org = await db.Organizations
            .AsNoTracking()
            .Where(o => o.Id == id)
            .Select(o => new { o.Id, o.TenantId, o.Name, o.OrgType, o.OrganizationTypeId })
            .FirstOrDefaultAsync(ct);

        if (org is null)
            return Results.NotFound(new { error = $"Organization '{id}' not found." });
        if (!org.TenantId.HasValue)
            return Results.BadRequest(new { error = "provision-user requires a tenant-scoped organization." });

        var targetTenantId = org.TenantId.Value;

        var emailLower = body.Email.ToLowerInvariant().Trim();

        // ── Owner guard: tenant owners may not enroll as providers ─────────────
        var tenantOwnerUserId = await db.Tenants
            .AsNoTracking()
            .Where(t => t.Id == targetTenantId)
            .Select(t => t.OwnerUserId)
            .FirstOrDefaultAsync(ct);

        var emailUserIdForOwnerCheck = await db.Users
            .AsNoTracking()
            .Where(u => u.Email.Trim().ToLower() == emailLower)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(ct);

        if (tenantOwnerUserId.HasValue &&
            emailUserIdForOwnerCheck.HasValue &&
            emailUserIdForOwnerCheck.Value == tenantOwnerUserId.Value)
        {
            logger.LogWarning(
                "LSCC-010 ProvisionProviderUser: enrollment blocked — email {Email} belongs to tenant owner of {TenantId}.",
                emailLower, targetTenantId);
            return Results.Conflict(new
            {
                error = "This email address is associated with the account that owns this network and cannot be enrolled as a provider.",
                code  = "OWNER_ENROLLMENT_BLOCKED",
            });
        }
        // ── End owner guard ───────────────────────────────────────────────────

        // ── Multi-tenant: global email lookup ─────────────────────────────────
        var existingUser = await db.Users
            .Where(u => u.Email.Trim().ToLower() == emailLower)
            .Select(u => new { u.Id, u.IsActive })
            .FirstOrDefaultAsync(ct);

        if (existingUser is not null)
        {
            // Check if this user already belongs to the target tenant.
            var alreadyInTenant = await db.UserTenants.AnyAsync(
                    ut => ut.UserId == existingUser.Id && ut.TenantId == targetTenantId, ct);

            if (alreadyInTenant)
            {
                await EnsureCareConnectUserProductAccessAsync(
                    targetTenantId,
                    existingUser.Id,
                    provisioningEngine,
                    userProductAccessService,
                    actorUserId: null,
                    ct);

                logger.LogInformation(
                    "LSCC-010 ProvisionProviderUser: user {Email} already exists in tenant {TenantId} (org {OrgId}). Returning existing.",
                    emailLower, targetTenantId, id);
                return Results.Ok(new ProvisionProviderUserResponse(
                    existingUser.Id, InvitationId: null, IsNew: false, InvitationSent: false));
            }

            // User exists in a different tenant.
            if (existingUser.IsActive)
            {
                // Active user: add UserTenant row + org membership; send tenant-access-granted email.
                db.UserTenants.Add(UserTenant.Create(existingUser.Id, targetTenantId));

                var crossMembership = UserOrganizationMembership.Create(
                    existingUser.Id, org.Id, memberRole: MemberRole.Member);
                db.UserOrganizationMemberships.Add(crossMembership);

                await db.SaveChangesAsync(ct);

                await EnsureCareConnectUserProductAccessAsync(
                    targetTenantId,
                    existingUser.Id,
                    provisioningEngine,
                    userProductAccessService,
                    actorUserId: null,
                    ct);

                logger.LogInformation(
                    "LSCC-010 ProvisionProviderUser: existing active user {UserId} ({Email}) linked to tenant {TenantId}.",
                    existingUser.Id, emailLower, targetTenantId);

                // Best-effort tenant-access-granted notification email.
                var notificationSent = false;
                var providerTenantForNotif = await db.Tenants.FindAsync([targetTenantId], ct);
                var portalUrl = TenantPortalUrlHelper.BuildBaseUrl(providerTenantForNotif, notifOptions.Value);
                if (portalUrl is not null)
                {
                    var displayName = $"{body.FirstName} {body.LastName ?? "User"}".Trim();
                    var tenantName  = providerTenantForNotif?.Name ?? targetTenantId.ToString();
                    try
                    {
                        var (_, emailOk, _) = await emailClient.SendTenantAccessGrantedEmailAsync(
                            emailLower, displayName, tenantName, portalUrl, targetTenantId, ct);
                        notificationSent = emailOk;
                        if (!emailOk)
                            logger.LogWarning(
                                "LSCC-010 ProvisionProviderUser: tenant-access-granted email not sent for user {UserId} ({Email}).",
                                existingUser.Id, emailLower);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex,
                            "LSCC-010 ProvisionProviderUser: tenant-access-granted email threw for user {UserId} ({Email}). Non-fatal.",
                            existingUser.Id, emailLower);
                    }
                }
                else
                {
                    logger.LogWarning(
                        "LSCC-010 ProvisionProviderUser: PortalBaseDomain/PortalBaseUrl not configured — " +
                        "tenant-access-granted notification cannot be sent for user {UserId} ({Email}).",
                        existingUser.Id, emailLower);
                }

                return Results.Ok(new ProvisionProviderUserResponse(
                    existingUser.Id,
                    InvitationId:     null,
                    IsNew:            false,
                    InvitationSent:   false,
                    NotificationSent: notificationSent));
            }
            else
            {
                // Invite-pending (inactive) user: add UserTenant row + org membership; send new invite.
                db.UserTenants.Add(UserTenant.Create(existingUser.Id, targetTenantId));

                var crossMembership = UserOrganizationMembership.Create(
                    existingUser.Id, org.Id, memberRole: MemberRole.Member);
                db.UserOrganizationMemberships.Add(crossMembership);

                var rawToken   = Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N");
                var tokenHash  = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(rawToken)));
                var newInvitation = UserInvitation.Create(
                    existingUser.Id, targetTenantId, tokenHash,
                    UserInvitation.PortalOrigins.TenantPortal,
                    invitedByUserId: null);
                db.UserInvitations.Add(newInvitation);

                await db.SaveChangesAsync(ct);

                await EnsureCareConnectUserProductAccessAsync(
                    targetTenantId,
                    existingUser.Id,
                    provisioningEngine,
                    userProductAccessService,
                    actorUserId: null,
                    ct);

                var invSent       = false;
                var provTenant    = await db.Tenants.FindAsync([targetTenantId], ct);
                var activationLink = TenantPortalUrlHelper.Build(provTenant, "accept-invite", rawToken, notifOptions.Value);
                if (activationLink is not null)
                {
                    var displayName = $"{body.FirstName} {body.LastName ?? "User"}".Trim();
                    var (_, emailOk, _) = await emailClient.SendInviteEmailAsync(
                        emailLower, displayName, activationLink, targetTenantId, ct);
                    invSent = emailOk;
                    if (!emailOk)
                        logger.LogWarning(
                            "LSCC-010 ProvisionProviderUser: invite email not sent for inactive cross-tenant user {UserId} ({Email}).",
                            existingUser.Id, emailLower);
                }

                return Results.Ok(new ProvisionProviderUserResponse(
                    existingUser.Id,
                    InvitationId:     newInvitation.Id,
                    IsNew:            false,
                    InvitationSent:   invSent));
            }
        }

        // ── New user: standard provisioning path ──────────────────────────────
        var lastName = string.IsNullOrWhiteSpace(body.LastName) ? "User" : body.LastName.Trim();
        var tempHash = passwordHasher.Hash(Guid.CreateVersion7().ToString());
        var user     = User.Create(targetTenantId, emailLower, tempHash, body.FirstName.Trim(), lastName);
        user.Deactivate();
        db.Users.Add(user);

        // Create UserTenant row alongside the new user.
        db.UserTenants.Add(UserTenant.Create(user.Id, targetTenantId));

        // Create invitation
        var rawToken2  = Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N");
        var tokenHash2 = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(rawToken2)));
        var invitation = UserInvitation.Create(
            user.Id, targetTenantId, tokenHash2,
            UserInvitation.PortalOrigins.TenantPortal,
            invitedByUserId: null);
        db.UserInvitations.Add(invitation);

        await db.SaveChangesAsync(ct);

        await EnsureCareConnectUserProductAccessAsync(
            targetTenantId,
            user.Id,
            provisioningEngine,
            userProductAccessService,
            actorUserId: null,
            ct);

        // Fire-and-forget audit
        var now = DateTimeOffset.UtcNow;
        _ = auditClient.IngestAsync(new IngestAuditEventRequest
        {
            EventType     = "identity.user.provider.provisioned",
            EventCategory = EventCategory.Administrative,
            SourceSystem  = "identity-service",
            SourceService = "admin-api",
            Visibility    = VisibilityScope.Tenant,
            Severity      = SeverityLevel.Info,
            OccurredAtUtc = now,
            Scope         = new AuditEventScopeDto { ScopeType = ScopeType.Tenant, TenantId = targetTenantId.ToString() },
            Actor         = new AuditEventActorDto { Type = ActorType.System, Name = "careconnect-autoprovision" },
            Entity        = new AuditEventEntityDto { Type = "User", Id = user.Id.ToString() },
            Action        = "ProviderUserProvisioned",
            Description   = $"Provider user '{emailLower}' provisioned via CareConnect auto-provision (org {id}).",
            IdempotencyKey = IdempotencyKey.For("identity-service", "identity.user.provider.provisioned", invitation.Id.ToString()),
            Tags           = ["user-management", "provider", "autoprovision"],
        });

        // Best-effort invitation email — LS-ID-TNT-016-01: tenant-subdomain-aware link.
        var invitationSent = false;
        var providerTenant = await db.Tenants.FindAsync([targetTenantId], ct);
        var activationLinkNew = TenantPortalUrlHelper.Build(providerTenant, "accept-invite", rawToken2, notifOptions.Value);
        if (activationLinkNew is not null)
        {
            var displayName     = $"{user.FirstName} {user.LastName}".Trim();
            var (_, emailOk, _) = await emailClient.SendInviteEmailAsync(
                emailLower, displayName, activationLinkNew, targetTenantId, ct);
            invitationSent = emailOk;
            if (!emailOk)
                logger.LogWarning(
                    "LSCC-010 ProvisionProviderUser: invitation email not sent for user {UserId} ({Email}). Continuing.",
                    user.Id, emailLower);
        }
        else
        {
            logger.LogWarning(
                "LSCC-010 ProvisionProviderUser: PortalBaseDomain/PortalBaseUrl not configured — " +
                "invitation link cannot be generated for user {UserId} ({Email}).",
                user.Id, emailLower);
        }

        return Results.Created(
            $"/api/admin/users/{user.Id}",
            new ProvisionProviderUserResponse(user.Id, invitation.Id, IsNew: true, invitationSent));
    }

    // =========================================================================
    // CC2-ENROLL: Self-enrollment — direct password registration (no invitation)
    // =========================================================================

    /// <summary>
    /// POST /api/admin/organizations/{orgId}/self-register
    ///
    /// CC2-ENROLL: M2M endpoint called by CareConnect during self-enrollment.
    /// Creates an immediately ACTIVE Identity user with the caller-supplied password.
    /// No invitation email is sent — the user has already chosen their password
    /// in the enrollment form.
    ///
    /// Idempotent: if a user with this email already exists in the org's tenant
    /// the existing user is returned (isNew=false) and no duplicate is created.
    ///
    /// No permission guard — trusted internal M2M path (same policy as provision-user).
    /// </summary>
    public static async Task<IResult> SelfRegisterUser(
        Guid                    id,
        SelfRegisterUserRequest body,
        IdentityDbContext       db,
        IPasswordHasher         passwordHasher,
        IProductProvisioningService provisioningEngine,
        IUserProductAccessService userProductAccessService,
        IAuditEventClient       auditClient,
        ILoggerFactory          loggerFactory,
        CancellationToken       ct)
    {
        var logger = loggerFactory.CreateLogger("AdminEndpointsLscc010.SelfRegisterUser");

        if (string.IsNullOrWhiteSpace(body.Email))
            return Results.BadRequest(new { error = "email is required." });
        if (string.IsNullOrWhiteSpace(body.Password))
            return Results.BadRequest(new { error = "password is required." });
        if (string.IsNullOrWhiteSpace(body.FirstName))
            return Results.BadRequest(new { error = "firstName is required." });
        if (body.Title?.Trim().Length > 50)
            return Results.BadRequest(new { error = "title must be 50 characters or fewer." });

        var org = await db.Organizations
            .AsNoTracking()
            .Where(o => o.Id == id)
            .Select(o => new { o.Id, o.TenantId, o.Name, o.OrgType, o.OrganizationTypeId })
            .FirstOrDefaultAsync(ct);

        if (org is null)
            return Results.NotFound(new { error = $"Organization '{id}' not found." });

        var targetTenantId = body.TenantId ?? org.TenantId;
        if (!targetTenantId.HasValue || targetTenantId.Value == Guid.Empty)
            return Results.BadRequest(new { error = "tenantId is required for global organization self-registration." });

        var emailLower = body.Email.ToLowerInvariant().Trim();
        var (phoneOk, normalisedPhone, phoneError) = PhoneNumber.TryNormalise(body.Phone);
        if (!phoneOk)
            return Results.BadRequest(new { error = phoneError });

        // ── Owner guard: tenant owners may not enroll as providers ─────────────
        var tenantOwnerUserIdSr = await db.Tenants
            .AsNoTracking()
            .Where(t => t.Id == targetTenantId.Value)
            .Select(t => t.OwnerUserId)
            .FirstOrDefaultAsync(ct);

        var emailUserIdForOwnerCheckSr = await db.Users
            .AsNoTracking()
            .Where(u => u.Email.Trim().ToLower() == emailLower)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(ct);

        if (tenantOwnerUserIdSr.HasValue &&
            emailUserIdForOwnerCheckSr.HasValue &&
            emailUserIdForOwnerCheckSr.Value == tenantOwnerUserIdSr.Value)
        {
            logger.LogWarning(
                "CC2-ENROLL SelfRegisterUser: enrollment blocked — email {Email} belongs to tenant owner of {TenantId}.",
                emailLower, targetTenantId.Value);
            return Results.Conflict(new
            {
                error = "This email address is associated with the account that owns this network and cannot be enrolled as a provider.",
                code  = "OWNER_ENROLLMENT_BLOCKED",
            });
        }
        // ── End owner guard ───────────────────────────────────────────────────

        // ── Multi-tenant: global email lookup ─────────────────────────────────
        var existingUser = await db.Users
            .Where(u => u.Email.Trim().ToLower() == emailLower)
            .FirstOrDefaultAsync(ct);

        if (existingUser is not null)
        {
            if (!string.IsNullOrWhiteSpace(body.Title))
                existingUser.SetTitle(body.Title);
            existingUser.SetPhone(normalisedPhone);

            // Check if already in the target tenant (via UserTenants row).
            var alreadyInTenant = await db.UserTenants.AnyAsync(
                    ut => ut.UserId == existingUser.Id && ut.TenantId == targetTenantId.Value && ut.IsActive, ct);

            if (alreadyInTenant)
            {
                await EnsureUserOrganizationMembershipAsync(db, existingUser.Id, org.Id, ct);
                await db.SaveChangesAsync(ct);

                await EnsureCareConnectUserProductAccessAsync(
                    targetTenantId.Value,
                    existingUser.Id,
                    provisioningEngine,
                    userProductAccessService,
                    actorUserId: null,
                    ct);

                await EnsureCareConnectEnrollmentRoleAsync(
                    db,
                    targetTenantId.Value,
                    existingUser.Id,
                    org.Id,
                    org.OrgType,
                    org.OrganizationTypeId,
                    actorUserId: null,
                    ct);

                logger.LogInformation(
                    "CC2-ENROLL SelfRegisterUser: user {Email} already exists in tenant {TenantId} (org {OrgId}). Returning existing.",
                    emailLower, targetTenantId.Value, id);
                return Results.Ok(new SelfRegisterUserResponse(existingUser.Id, IsNew: false));
            }

            // Different tenant: verify password before linking.
            if (!passwordHasher.Verify(body.Password, existingUser.PasswordHash))
            {
                logger.LogWarning(
                    "CC2-ENROLL SelfRegisterUser: cross-tenant enrollment password mismatch for {Email}. Returning 409.",
                    emailLower);
                return Results.Conflict(new
                {
                    error = "An account with this email already exists. " +
                            "Please use your existing password to link your account to this network.",
                });
            }

            // Password matches — link existing user to the new tenant.
            db.UserTenants.Add(UserTenant.Create(existingUser.Id, targetTenantId.Value));

            await EnsureUserOrganizationMembershipAsync(db, existingUser.Id, org.Id, ct);

            await db.SaveChangesAsync(ct);

            await EnsureCareConnectUserProductAccessAsync(
                targetTenantId.Value,
                existingUser.Id,
                provisioningEngine,
                userProductAccessService,
                actorUserId: null,
                ct);

            await EnsureCareConnectEnrollmentRoleAsync(
                db,
                targetTenantId.Value,
                existingUser.Id,
                org.Id,
                org.OrgType,
                org.OrganizationTypeId,
                actorUserId: null,
                ct);

            logger.LogInformation(
                "CC2-ENROLL SelfRegisterUser: existing user {UserId} ({Email}) linked to tenant {TenantId} (org {OrgId}).",
                existingUser.Id, emailLower, targetTenantId.Value, id);

            return Results.Ok(new SelfRegisterUserResponse(existingUser.Id, IsNew: false));
        }

        // ── New user: standard self-registration path ─────────────────────────
        var lastName = body.LastName?.Trim() ?? string.Empty;
        var hash     = passwordHasher.Hash(body.Password);
        var user     = User.Create(
            targetTenantId.Value,
            emailLower,
            hash,
            body.FirstName.Trim(),
            lastName,
            title: body.Title?.Trim());
        user.SetPhone(normalisedPhone);
        // User.Create produces an active user by default — no Deactivate() call here.
        db.Users.Add(user);

        // Create UserTenant row alongside the new user.
        db.UserTenants.Add(UserTenant.Create(user.Id, targetTenantId.Value));

        await EnsureUserOrganizationMembershipAsync(db, user.Id, id, ct);

        await db.SaveChangesAsync(ct);

        await EnsureCareConnectUserProductAccessAsync(
            targetTenantId.Value,
            user.Id,
            provisioningEngine,
            userProductAccessService,
            actorUserId: null,
            ct);

        await EnsureCareConnectEnrollmentRoleAsync(
            db,
            targetTenantId.Value,
            user.Id,
            org.Id,
            org.OrgType,
            org.OrganizationTypeId,
            actorUserId: null,
            ct);

        _ = auditClient.IngestAsync(new IngestAuditEventRequest
        {
            EventType     = "identity.user.self.enrolled",
            EventCategory = EventCategory.Administrative,
            SourceSystem  = "identity-service",
            SourceService = "admin-api",
            Visibility    = VisibilityScope.Tenant,
            Severity      = SeverityLevel.Info,
            OccurredAtUtc = DateTimeOffset.UtcNow,
            Scope         = new AuditEventScopeDto { ScopeType = ScopeType.Tenant, TenantId = targetTenantId.Value.ToString() },
            Actor         = new AuditEventActorDto { Type = ActorType.System, Name = "careconnect-enrollment" },
            Entity        = new AuditEventEntityDto { Type = "User", Id = user.Id.ToString() },
            Action        = "SelfEnrolled",
            Description   = $"User '{emailLower}' self-enrolled via CareConnect enrollment form (org {id}).",
            IdempotencyKey = IdempotencyKey.For("identity-service", "identity.user.self.enrolled", user.Id.ToString()),
            Tags           = ["user-management", "provider", "self-enrollment"],
        });

        logger.LogInformation(
            "CC2-ENROLL SelfRegisterUser: user {UserId} ({Email}) created and active in org {OrgId}.",
            user.Id, emailLower, id);

        return Results.Created(
            $"/api/admin/users/{user.Id}",
            new SelfRegisterUserResponse(user.Id, IsNew: true));
    }

    // =========================================================================
    // SYNQLIEN-BUYER-ENROLL: buyer self-enrollment from public lien offer link
    // =========================================================================

    /// <summary>
    /// POST /api/admin/organizations/{orgId}/synqlien-buyer-self-register
    ///
    /// M2M endpoint called by SynqLien during public buyer account activation.
    /// Creates an active user, grants SYNQ_LIENS product access, and assigns
    /// SYNQLIEN_BUYER scoped to the buyer organization. Existing accounts are
    /// rejected so the public activation flow cannot silently reuse a login.
    /// </summary>
    public static async Task<IResult> SelfRegisterSynqLienBuyer(
        Guid                    id,
        SelfRegisterUserRequest body,
        IdentityDbContext       db,
        IPasswordHasher         passwordHasher,
        IProductProvisioningService provisioningEngine,
        IUserProductAccessService userProductAccessService,
        IAuditEventClient       auditClient,
        ILoggerFactory          loggerFactory,
        CancellationToken       ct)
    {
        var logger = loggerFactory.CreateLogger("AdminEndpointsLscc010.SelfRegisterSynqLienBuyer");

        if (string.IsNullOrWhiteSpace(body.Email))
            return Results.BadRequest(new { error = "email is required." });
        if (string.IsNullOrWhiteSpace(body.Password))
            return Results.BadRequest(new { error = "password is required." });
        if (string.IsNullOrWhiteSpace(body.FirstName))
            return Results.BadRequest(new { error = "firstName is required." });

        var org = await db.Organizations
            .AsNoTracking()
            .Where(o => o.Id == id)
            .Select(o => new { o.Id, o.TenantId, o.Name })
            .FirstOrDefaultAsync(ct);

        if (org is null)
            return Results.NotFound(new { error = $"Organization '{id}' not found." });

        var targetTenantId = body.TenantId ?? org.TenantId;
        if (!targetTenantId.HasValue || targetTenantId.Value == Guid.Empty)
            return Results.BadRequest(new { error = "tenantId is required for SynqLien buyer self-registration." });

        var emailLower = body.Email.ToLowerInvariant().Trim();
        var (phoneOk, normalisedPhone, phoneError) = PhoneNumber.TryNormalise(body.Phone);
        if (!phoneOk)
            return Results.BadRequest(new { error = phoneError });

        var tenantOwnerUserId = await db.Tenants
            .AsNoTracking()
            .Where(t => t.Id == targetTenantId.Value)
            .Select(t => t.OwnerUserId)
            .FirstOrDefaultAsync(ct);

        var emailUserIdForOwnerCheck = await db.Users
            .AsNoTracking()
            .Where(u => u.Email.Trim().ToLower() == emailLower)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(ct);

        if (tenantOwnerUserId.HasValue &&
            emailUserIdForOwnerCheck.HasValue &&
            emailUserIdForOwnerCheck.Value == tenantOwnerUserId.Value)
        {
            logger.LogWarning(
                "SynqLien buyer self-registration blocked because email {Email} belongs to tenant owner of {TenantId}.",
                emailLower,
                targetTenantId.Value);
            return Results.Conflict(new
            {
                error = "This email address is associated with the account that owns this tenant and cannot be enrolled as a SynqLien buyer.",
                code = "OWNER_ENROLLMENT_BLOCKED",
            });
        }

        var existingUser = await db.Users
            .Where(u => u.Email.Trim().ToLower() == emailLower)
            .FirstOrDefaultAsync(ct);

        if (existingUser is not null)
        {
            logger.LogInformation(
                "SynqLien buyer self-registration rejected existing user {UserId} for tenant {TenantId} org {OrgId}.",
                existingUser.Id,
                targetTenantId.Value,
                id);
            return Results.Conflict(new
            {
                error = "An account with this email already exists. Log in with your existing account instead.",
                code = "ACCOUNT_ALREADY_EXISTS",
            });
        }

        var lastName = body.LastName?.Trim() ?? string.Empty;
        var hash = passwordHasher.Hash(body.Password);
        var user = User.Create(targetTenantId.Value, emailLower, hash, body.FirstName.Trim(), lastName);
        user.SetPhone(normalisedPhone);
        db.Users.Add(user);
        db.UserTenants.Add(UserTenant.Create(user.Id, targetTenantId.Value));

        await EnsureUserOrganizationMembershipAsync(db, user.Id, id, ct);
        await db.SaveChangesAsync(ct);

        await EnsureSynqLienBuyerUserProductAccessAsync(
            targetTenantId.Value,
            user.Id,
            provisioningEngine,
            userProductAccessService,
            actorUserId: null,
            ct);

        await EnsureSynqLienBuyerRoleAsync(
            db,
            targetTenantId.Value,
            user.Id,
            org.Id,
            actorUserId: null,
            ct);

        _ = auditClient.IngestAsync(new IngestAuditEventRequest
        {
            EventType = "identity.user.synqlien-buyer.self.enrolled",
            EventCategory = EventCategory.Administrative,
            SourceSystem = "identity-service",
            SourceService = "admin-api",
            Visibility = VisibilityScope.Tenant,
            Severity = SeverityLevel.Info,
            OccurredAtUtc = DateTimeOffset.UtcNow,
            Scope = new AuditEventScopeDto { ScopeType = ScopeType.Tenant, TenantId = targetTenantId.Value.ToString() },
            Actor = new AuditEventActorDto { Type = ActorType.System, Name = "synqlien-buyer-enrollment" },
            Entity = new AuditEventEntityDto { Type = "User", Id = user.Id.ToString() },
            Action = "SelfEnrolled",
            Description = $"User '{emailLower}' self-enrolled as a SynqLien buyer (org {id}).",
            IdempotencyKey = IdempotencyKey.For("identity-service", "identity.user.synqlien-buyer.self.enrolled", user.Id.ToString()),
            Tags = ["user-management", "synqlien", "buyer", "self-enrollment"],
        });

        logger.LogInformation(
            "SynqLien buyer self-registration created user {UserId} ({Email}) in org {OrgId}.",
            user.Id,
            emailLower,
            id);

        return Results.Created(
            $"/api/admin/users/{user.Id}",
            new SelfRegisterUserResponse(user.Id, IsNew: true));
    }

    // =========================================================================
    // CC2-ENROLL-FIRM: Law firm self-enrollment — create LAW_FIRM org
    // =========================================================================

    /// <summary>
    /// POST /api/admin/organizations/law-firm
    /// Creates a minimal LAW_FIRM Organization for a law firm self-enrolling via CareConnect.
    /// Idempotent — keyed on (tenantId, email) so repeated submissions return the same org.
    /// </summary>
    public static async Task<IResult> CreateLawFirmOrganization(
        CreateLawFirmOrgRequest body,
        IdentityDbContext       db,
        CancellationToken       ct)
    {
        if (body.TenantId  == Guid.Empty)                        return Results.BadRequest(new { error = "tenantId is required." });
        if (string.IsNullOrWhiteSpace(body.FirmName))            return Results.BadRequest(new { error = "firmName is required." });
        if (string.IsNullOrWhiteSpace(body.ContactEmail))        return Results.BadRequest(new { error = "contactEmail is required." });

        // TENANT-B12: The Tenant service is the source of record for tenants.
        // If this tenant is not yet known to Identity (e.g. created via Tenant service or
        // provisioned before this dual-write convention), rehydrate a minimal record so
        // the FK constraint on Organizations.TenantId is satisfied.
        var tenantExists = await db.Tenants.AnyAsync(t => t.Id == body.TenantId, ct);
        if (!tenantExists)
        {
            try
            {
                var code      = body.TenantId.ToString("N")[..12];
                // AUTH-B01: rehydrate as Active — the real tenant exists and is live;
                // this stub satisfies the Identity FK constraint until the next full
                // tenant write-through from the Tenant service occurs.
                var rehydrated = Tenant.Rehydrate(id: body.TenantId, code: code, status: "Active");
                db.Tenants.Add(rehydrated);
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Concurrent request already inserted — clear the tracker and re-verify.
                db.ChangeTracker.Clear();

                // After clearing, confirm the tenant really is in the DB now.
                // If it still isn't (e.g. code-uniqueness clash with a different tenant row),
                // return an error rather than letting the Organization FK fail later.
                tenantExists = await db.Tenants.AnyAsync(t => t.Id == body.TenantId, ct);
                if (!tenantExists)
                    return Results.Problem(
                        $"Could not rehydrate tenant '{body.TenantId}' in Identity. " +
                        "The tenant must be provisioned before creating an organization.");
            }
        }

        // Idempotency: find an existing global LAW_FIRM org where the contact
        // email already has a membership, so re-enrollment reuses the same org.
        // Also check the legacy name format for orgs created before the normalization migration.
        var contactEmailNorm = body.ContactEmail.Trim().ToLowerInvariant();
        var firmNameNorm = body.FirmName.Trim();
        var legacyFirmName = $"{firmNameNorm} [firm:{contactEmailNorm}]";

        var existing = await db.Organizations
            .AsNoTracking()
            .Where(o => o.TenantId == null
                     && o.OrgType  == OrgType.LawFirm)
            .Where(o => o.Name == legacyFirmName
                     || o.Memberships.Any(m => m.User!.Email == contactEmailNorm))
            .FirstOrDefaultAsync(ct);

        if (existing is not null)
            return Results.Ok(new CreateLawFirmOrgResponse(existing.Id, existing.DisplayName ?? existing.Name, IsNew: false));

        var org = Organization.CreateGlobal(
            name:        firmNameNorm,
            orgType:     OrgType.LawFirm,
            displayName: firmNameNorm);

        db.Organizations.Add(org);
        await db.SaveChangesAsync(ct);

        return Results.Created(
            $"/api/admin/organizations/{org.Id}",
            new CreateLawFirmOrgResponse(org.Id, org.DisplayName ?? org.Name, IsNew: true));
    }

    private static async Task<UserOrganizationMembership> EnsureUserOrganizationMembershipAsync(
        IdentityDbContext db,
        Guid userId,
        Guid organizationId,
        CancellationToken ct,
        string memberRole = MemberRole.Member)
    {
        var existing = await db.UserOrganizationMemberships
            .FirstOrDefaultAsync(m => m.UserId == userId && m.OrganizationId == organizationId, ct);

        if (existing is not null)
            return existing;

        var org = await db.Organizations
            .AsNoTracking()
            .Where(o => o.Id == organizationId)
            .Select(o => new { o.TenantId })
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"Organization '{organizationId}' not found.");

        if (org.TenantId.HasValue)
        {
            var hasTenantMembershipInTracker = db.UserTenants.Local.Any(
                ut => ut.UserId == userId && ut.TenantId == org.TenantId.Value && ut.IsActive);

            var hasTenantMembership = hasTenantMembershipInTracker
                || await db.UserTenants.AnyAsync(
                    ut => ut.UserId == userId && ut.TenantId == org.TenantId.Value && ut.IsActive, ct);

            if (!hasTenantMembership)
                throw new InvalidOperationException(
                    $"User '{userId}' must belong to tenant '{org.TenantId.Value}' before joining tenant-scoped organization '{organizationId}'.");
        }

        var membership = UserOrganizationMembership.Create(userId, organizationId, memberRole);
        db.UserOrganizationMemberships.Add(membership);
        return membership;
    }

    private static string BuildSynqLienBuyerOrganizationName(string companyName, Guid sourceBuyerOrgId)
    {
        const int maxOrganizationNameLength = 200;
        var suffix = $" [synqlien:{sourceBuyerOrgId:D}]";
        var trimmedCompanyName = companyName.Trim();
        var maxCompanyLength = maxOrganizationNameLength - suffix.Length;
        if (trimmedCompanyName.Length > maxCompanyLength)
            trimmedCompanyName = trimmedCompanyName[..maxCompanyLength].TrimEnd();

        return trimmedCompanyName + suffix;
    }

    // Keep the request/response records accessible to the route registration above
    public record CreateLawFirmOrgRequest(
        Guid   TenantId,
        string FirmName,
        string ContactEmail);

    private record CreateLawFirmOrgResponse(
        Guid   Id,
        string Name,
        bool   IsNew);

    public record CreateProviderOrgRequest(
        Guid   TenantId,
        Guid   ProviderCcId,
        string ProviderName,
        bool   GlobalScope = false);

    private record CreateProviderOrgResponse(
        Guid   Id,
        string Name,
        bool   IsNew);

    public record CreateSynqLienBuyerOrgRequest(
        Guid    TenantId,
        Guid    SourceBuyerOrgId,
        string  BuyerCompanyName,
        string? ContactEmail = null);

    private record CreateSynqLienBuyerOrgResponse(
        Guid   Id,
        string Name,
        bool   IsNew);

    public record ProvisionProviderUserRequest(
        string  Email,
        string  FirstName,
        string? LastName = null);

    public record ProvisionProviderUserResponse(
        Guid  UserId,
        Guid? InvitationId,
        bool  IsNew,
        bool  InvitationSent,
        bool  NotificationSent = false);

    public record SelfRegisterUserRequest(
        Guid?   TenantId,
        string  Email,
        string  Password,
        string  FirstName,
        string? LastName = null,
        string? Phone = null,
        string? Title = null);

    public record SelfRegisterUserResponse(
        Guid UserId,
        bool IsNew);

    // =========================================================================
    // LS-COR-AUT-011D: AUTHORIZATION SIMULATION
    // =========================================================================

    internal static async Task<IResult> SimulateAuthorization(
        SimulateAuthorizationRequest body,
        ClaimsPrincipal caller,
        IAuthorizationSimulationService simulationService,
        IdentityDbContext db,
        IAuditEventClient auditClient,
        CancellationToken ct)
    {
        var isPlatformAdmin = caller.IsInRole("PlatformAdmin");
        var isTenantAdmin = caller.IsInRole("TenantAdmin");
        if (!isPlatformAdmin && !isTenantAdmin)
            return Results.Forbid();

        if (string.IsNullOrWhiteSpace(body.PermissionCode))
            return Results.BadRequest(new { error = "permissionCode is required." });

        if (body.TenantId == Guid.Empty)
            return Results.BadRequest(new { error = "tenantId is required." });

        if (body.UserId == Guid.Empty)
            return Results.BadRequest(new { error = "userId is required." });

        var permParts = body.PermissionCode.Trim().Split('.');
        if (permParts.Length < 2)
            return Results.BadRequest(new { error = "permissionCode must contain at least one dot separator (e.g. PRODUCT.resource:action)." });

        var tenantExists = await db.Tenants.AnyAsync(t => t.Id == body.TenantId, ct);
        if (!tenantExists)
            return Results.NotFound(new { error = "Tenant not found." });

        if (!isPlatformAdmin)
        {
            var rawTid = caller.FindFirstValue("tenant_id");
            if (rawTid is null || !Guid.TryParse(rawTid, out var callerTid) || callerTid != body.TenantId)
                return Results.Forbid();
        }

        var targetUser = await db.Users
            .Where(u => u.Id == body.UserId
                && db.UserTenants.Any(ut => ut.UserId == u.Id && ut.TenantId == body.TenantId && ut.IsActive))
            .Select(u => new { u.Id })
            .FirstOrDefaultAsync(ct);
        if (targetUser == null)
            return Results.NotFound(new { error = "User not found in the specified tenant." });

        if (body.DraftPolicy != null)
        {
            if (string.IsNullOrWhiteSpace(body.DraftPolicy.PolicyCode))
                return Results.BadRequest(new { error = "draftPolicy.policyCode is required." });
            if (string.IsNullOrWhiteSpace(body.DraftPolicy.Name))
                return Results.BadRequest(new { error = "draftPolicy.name is required." });
            if (body.DraftPolicy.Rules != null)
            {
                foreach (var rule in body.DraftPolicy.Rules)
                {
                    if (string.IsNullOrWhiteSpace(rule.Field))
                        return Results.BadRequest(new { error = "Each draft rule must have a 'field'." });
                    if (string.IsNullOrWhiteSpace(rule.Value))
                        return Results.BadRequest(new { error = $"Draft rule for field '{rule.Field}' must have a 'value'." });
                    if (!string.IsNullOrWhiteSpace(rule.Operator) && !Enum.TryParse<Identity.Domain.RuleOperator>(rule.Operator, ignoreCase: true, out _))
                        return Results.BadRequest(new { error = $"Draft rule for field '{rule.Field}' has invalid operator '{rule.Operator}'. Valid operators: {string.Join(", ", Enum.GetNames<Identity.Domain.RuleOperator>())}." });
                    if (!string.IsNullOrWhiteSpace(rule.LogicalGroup) && !rule.LogicalGroup.Equals("And", StringComparison.OrdinalIgnoreCase) && !rule.LogicalGroup.Equals("Or", StringComparison.OrdinalIgnoreCase))
                        return Results.BadRequest(new { error = $"Draft rule for field '{rule.Field}' has invalid logicalGroup '{rule.LogicalGroup}'. Valid values: And, Or." });
                }
            }
        }

        var mode = body.DraftPolicy != null ? SimulationMode.Draft : SimulationMode.Live;

        var request = new SimulationRequest
        {
            TenantId = body.TenantId,
            UserId = body.UserId,
            PermissionCode = body.PermissionCode.Trim(),
            ResourceContext = body.ResourceContext,
            RequestContext = body.RequestContext,
            Mode = mode,
            DraftPolicy = body.DraftPolicy != null ? new DraftPolicyInput
            {
                PolicyCode = body.DraftPolicy.PolicyCode,
                Name = body.DraftPolicy.Name,
                Description = body.DraftPolicy.Description,
                Priority = body.DraftPolicy.Priority,
                Effect = body.DraftPolicy.Effect ?? "Allow",
                Rules = body.DraftPolicy.Rules?.Select(r => new DraftRuleInput
                {
                    Field = r.Field,
                    Operator = r.Operator ?? "Equals",
                    Value = r.Value,
                    LogicalGroup = r.LogicalGroup ?? "And",
                }).ToList() ?? [],
            } : null,
            ExcludePolicyIds = body.ExcludePolicyIds,
        };

        var result = await simulationService.SimulateAsync(request, ct);

        var callerIdStr = caller.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier)
            ?? caller.FindFirstValue("sub") ?? "unknown";

        _ = auditClient.IngestAsync(new IngestAuditEventRequest
        {
            EventType     = "authorization.simulation.executed",
            EventCategory = LegalSynq.AuditClient.Enums.EventCategory.Administrative,
            Visibility    = LegalSynq.AuditClient.Enums.VisibilityScope.Platform,
            Severity      = LegalSynq.AuditClient.Enums.SeverityLevel.Info,
            SourceSystem  = "Identity",
            SourceService = "AdminEndpoints",
            Action        = "SimulateAuthorization",
            Description   = $"Admin {callerIdStr} simulated authorization for user {body.UserId} permission '{body.PermissionCode}' in tenant {body.TenantId}. Mode={mode}, Result={result.Allowed}",
            Outcome       = result.Allowed ? "allow" : "deny",
            Scope         = new LegalSynq.AuditClient.DTOs.AuditEventScopeDto { TenantId = body.TenantId.ToString() },
            Actor         = new LegalSynq.AuditClient.DTOs.AuditEventActorDto { Id = callerIdStr, Type = LegalSynq.AuditClient.Enums.ActorType.User },
            Entity        = new LegalSynq.AuditClient.DTOs.AuditEventEntityDto { Type = "AuthorizationSimulation", Id = body.UserId.ToString() },
            Metadata      = JsonSerializer.Serialize(new { permissionCode = body.PermissionCode, mode = mode.ToString(), allowed = result.Allowed }),
            IdempotencyKey = $"sim:{callerIdStr}:{body.UserId}:{body.PermissionCode}:{DateTime.UtcNow:yyyyMMddHHmmss}",
            Tags          = ["simulation", "authorization", mode.ToString().ToLowerInvariant()],
        });

        return Results.Ok(result);
    }

    internal record SimulateAuthorizationRequest
    {
        public Guid TenantId { get; init; }
        public Guid UserId { get; init; }
        public string PermissionCode { get; init; } = string.Empty;
        public Dictionary<string, object?>? ResourceContext { get; init; }
        public Dictionary<string, string>? RequestContext { get; init; }
        public SimulateAuthDraftPolicyInput? DraftPolicy { get; init; }
        public List<Guid>? ExcludePolicyIds { get; init; }
    }

    internal record SimulateAuthDraftPolicyInput
    {
        public string PolicyCode { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string? Description { get; init; }
        public int Priority { get; init; }
        public string? Effect { get; init; }
        public List<SimulateAuthDraftRuleInput>? Rules { get; init; }
    }

    internal record SimulateAuthDraftRuleInput
    {
        public string Field { get; init; } = string.Empty;
        public string? Operator { get; init; }
        public string Value { get; init; } = string.Empty;
        public string? LogicalGroup { get; init; }
    }
}
