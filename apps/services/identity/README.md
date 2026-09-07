# Identity Service

Authentication, user management, organisations, roles, product access, and RBAC policy evaluation.

**Port:** 5001

## Responsibilities

- JWT issuance and validation
- User create / invite / edit profile / activate / deactivate
- Tenant custom role management (create / edit / delete, permission assignment)
- Tenant provisioning (canonical create, downstream to Tenant service)
- Organisation and multi-org membership
- Product enablement / disablement per tenant
- Role assignment (system, tenant, product, scoped)
- Access group management (group-inherited role assignments)
- Permission model (tenant permission catalog + effective resolution)
- Policy evaluation engine (attribute-based with Redis or in-memory caching)
- Audit event publication for all identity operations
- Commerce lifecycle notifications on product enable/disable (ECO-02)

## Layer Structure

```
Identity.Api/            Endpoints, middleware, Program.cs (port 5001)
Identity.Application/    Interfaces, DTOs, services (AuthService, UserService)
Identity.Domain/         Tenant, User, Organization, Product, TenantProduct,
                         ProductRole, Permission, RolePermissionAssignment,
                         AccessGroup, GroupMembership, UserProductAccess, ...
Identity.Infrastructure/ DbContext (IdentityDb), repositories, EF migrations,
                         ProductProvisioningService, TenantProvisioningService,
                         JwtTokenService, BcryptPasswordHasher, Route53DnsService
Identity.Api.Tests/      Integration and unit tests
```

## Key Endpoints

| Method | Path | Description |
|---|---|---|
| `POST` | `/api/auth/login` | Authenticate, returns JWT |
| `GET` | `/api/auth/me` | Validate current session |
| `POST` | `/api/auth/session/refresh` | Rotate a biometric device session refresh token |
| `POST` | `/api/auth/logout` | Stateless web logout or biometric device-session revocation when refresh credentials are supplied |
| `GET` | `/api/auth/device-sessions` | List the authenticated user's active device sessions |
| `POST` | `/api/users` | Create user |
| `GET` | `/api/users` | List users (tenant-scoped) |
| `GET` | `/api/admin/users` | Tenant-portal User Management: paginated, tenant-scoped user list (`page`, `pageSize`, `search`, `status`); items include `primaryOrg`, `createdAtUtc`, and `updatedAtUtc`. `TENANT.users:view`. |
| `POST` | `/api/admin/users` | Tenant-portal User Management: create an **active** user with an admin-set password (min 8 chars) plus optional `roleId` and `organizationId`, in one transaction. `409` if the email already exists on the platform. `TENANT.users:manage`. Emits `identity.user.created`. |
| `PATCH` | `/api/admin/users/{id}` | Tenant-portal User Management: edit `firstName`/`lastName` (supplied together), `email` (bumps session version, `409` on conflict), and optional `title`. `TENANT.users:manage`. Emits `identity.user.profile_updated` when a field changes. |
| `GET` | `/api/admin/roles` | Role list. For non-PlatformAdmin callers it is **tenant-scoped** (own-tenant custom roles only — a fresh tenant lists none); PlatformAdmin sees all, optionally filtered by `scope`/`tenantId`. Items include `permissions` (codes), `permissionCount`, `userCount`, `createdAtUtc`, `updatedAtUtc`. `TENANT.roles:view`. |
| `POST` | `/api/admin/roles` | Tenant-portal Role Management: create a custom role (`Scope = "Tenant"`) with `{ name, description?, permissionCodes[] }`. `409` on duplicate name in the tenant, `400` on unknown permission, `403` when a non-admin grants a permission they don't hold. `TENANT.roles:manage`. Emits `identity.role.created`. |
| `PUT` | `/api/admin/roles/{id}` | Tenant-portal Role Management: rename/re-describe a role and **replace** its permission set; bumps the access version of current holders. `409 SYSTEM_ROLE` for system roles, `409 ROLE_NAME_CONFLICT`, `403` cross-tenant. `TENANT.roles:manage`. Emits `identity.role.updated`. |
| `DELETE` | `/api/admin/roles/{id}` | Tenant-portal Role Management: hard-delete a custom role + its permission assignments. `409 SYSTEM_ROLE`, `409 ROLE_IN_USE` when assigned to any active user. `TENANT.roles:manage`. Emits `identity.role.deleted`. |
| `POST` | `/api/internal/tenant-provisioning/provision` | Internal: full tenant provision |
| `GET` | `/api/internal/users/account-exists` | Internal: trusted product services can check whether an email already belongs to an Identity account |
| `GET` | `/api/internal/users/{userId}/display` | Internal: trusted product services can resolve a tenant-scoped user's first/last display name from `idt_Users`; optional `organizationId` also accepts active org membership in that tenant |
| `GET` | `/api/internal/users/tenant-owner/display` | Internal: trusted product services can resolve the tenant owner's first/last display name from `idt_Tenants.OwnerUserId` and `idt_Users` |
| `POST` | `/api/admin/products/{code}/provision` | Enable/disable product for tenant |
| `POST` | `/api/admin/organizations/synqlien-buyer` | Internal: create/resolve a tenant-scoped `LIEN_OWNER` org for SynqLien public buyer activation |
| `GET` | `/api/admin/organizations?tenantId={tenantId}&orgType=LAW_FIRM` | Internal/admin: list law firm organizations for CareConnect referral portal selection; tenant-scoped requests include global law firms |
| `POST` | `/api/admin/organizations/{id}/self-register` | Internal: CareConnect self-enrollment creates or links an active Identity user; accepts optional user `title` |
| `POST` | `/api/admin/organizations/{id}/synqlien-buyer-self-register` | Internal: create a SynqLien buyer user and grant `SYNQ_LIENS:SYNQLIEN_BUYER`; returns `409 FUNDING_COMPANY_USER_ALREADY_EXISTS` when the funding-company organization already has an active member, or `409 ACCOUNT_ALREADY_EXISTS` for existing emails |
| `GET` | `/api/tenants/current/branding` | Anonymous branding by tenant code |
| `GET`/`POST` | `/api/internal/organizations/{organizationId}/users[/invite\|/{userId}/resend-invite\|/{userId}/activate\|/{userId}/deactivate\|/{userId}/product-roles]` | Internal (provisioning token, not public JWT): list/invite/resend pending invite/activate/deactivate a law-firm organization's users and assign/revoke their `CARECONNECT_REFERRER`/`CARECONNECT_REFERRER_ADMIN` roles (LSV3-1083). Users with pending invitations are listed as `Invited` even though their account is not active yet. Called by CareConnect's `/api/law-firm-users` on behalf of a caller already verified to hold `CARECONNECT_REFERRER_ADMIN` for that org; every route re-derives org membership itself, treating the caller's own ownership check as advisory only. |

### SynqLien user management

Identity exposes this surface only to the Liens service at `/api/internal/synqlien/user-management`. Calls require an audience-bound service token (`aud=identity-service`, `svc=liens-service`); tenant and actor come from that token and organization comes from the trusted `X-Organization-Id` header. The public API surface is owned by Liens.

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/internal/synqlien/user-management/users[/{userId}]` | `SYNQ_LIENS.users:view` |
| `GET` | `/api/internal/synqlien/user-management/options` | `SYNQ_LIENS.users:view` |
| `POST` | `/api/internal/synqlien/user-management/invitations` | `SYNQ_LIENS.invitations:manage` |
| `POST`/`DELETE` | `/api/internal/synqlien/user-management/users/{userId}/invitations/...` | `SYNQ_LIENS.invitations:manage` |
| `PATCH`/`PUT`/`POST` | `/api/internal/synqlien/user-management/users/{userId}/...` | `SYNQ_LIENS.users:manage` |
| `GET`/`POST`/`PUT`/`DELETE` | `/api/internal/synqlien/user-management/roles[/{roleId}]` | `SYNQ_LIENS.roles:view/manage` |

The list accepts `search`, `status`, `roleId`, `department`, `page`, `pageSize` (maximum 100), and `sort=NAME_ASC|NAME_DESC|LAST_LOGIN_DESC`. Status precedence is `LOCKED`, `INVITED`, `INACTIVE`, then `ACTIVE`.

Management roles are organization-scoped and separate from commercial Seller/Buyer/Holder personas. A Seller persona alone grants no user-management authority. New law firms receive protected starter roles on first user-management access: Administrator, Quality Assurance, and View Only; custom roles can be added, but a manager cannot delegate permissions they do not hold. Pending invitations store the intended role/profile but activate no membership, product access, or role until acceptance. Inactive Identity accounts must be restored by an Identity administrator rather than reactivated by a product invitation. Self-deactivation, self-role changes, deletion of protected/in-use roles, and removal of the last Administrator are rejected.

## Database

`IdentityDb` (MySQL) — all tables prefixed `idt_`.

For environments where EF migrations cannot run, apply the SynqLien
organization user-management migration manually while Identity and Liens are
stopped:

```bash
mysql --host=<host> --user=<user> --password <identity_database> \
  < scripts/apply-identity-synqlien-user-management.sql
```

The script is idempotent and can repair the missing immediate predecessor
`20260824124500_AddCareConnectReferrerAdminReferralCapabilities` when
`20260824120000_MigrateCareConnectLawFirmReferrerToAdmin` is already recorded.
It records `20260903020000_AddSynqLienUserManagement` in
`__EFMigrationsHistory` only when its schema, backfill, capabilities, starter
roles, and owner assignments are valid. Its final status must be `READY`
before restarting either service.

Run `scripts/add-synq-selling-product.sql` against `IdentityDb` to idempotently
add or reactivate the `SYNQ_SELLING` catalog product before enabling it for tenants.

Biometric device sessions are installed by EF migration
`20260810113000_AddBiometricDeviceSessions`; startup does not create these tables manually.

`idt_Users` includes an optional `Title` column (`varchar(50)`) for professional titles captured during
CareConnect portal enrollment and exposed on user DTOs. Existing rows may leave it `NULL`.

CareConnect seeds `CARECONNECT_NETWORK_MANAGER` for law-firm provider network management, including
provider search, map, and provider-management capabilities. `LAW_FIRM` and `LIEN_OWNER` organization
eligibility is seeded for `SYNQ_CARECONNECT` so law-firm-scoped CareConnect users can be provisioned
without tenant-wide user-management access.

`20260821093425_AddCareConnectReferrerAdminRole` seeds `CARECONNECT_REFERRER_ADMIN` — the same
network/provider capabilities as `CARECONNECT_NETWORK_MANAGER`, but with `LAW_FIRM`-only organization
eligibility (no `LIEN_OWNER` row), so a law firm's own admin can be granted network self-management
without also becoming eligible for the lien-company-oriented role (LSV3-1084).
`20260824120000_MigrateCareConnectLawFirmReferrerToAdmin` upgrades active law-firm
`CARECONNECT_REFERRER` assignments to `CARECONNECT_REFERRER_ADMIN` and increments affected users'
`AccessVersion` so stale JWT access claims are rejected/refreshed.
`20260824124500_AddCareConnectReferrerAdminReferralCapabilities` backfills the admin role's
direct referral create/read/cancel and appointment read permissions, then increments affected
admin users' `AccessVersion` so refreshed JWT permission claims include the new capabilities.

`20260728000001_SeedSynqLienSellWorkflowPermission` maps
`SYNQ_LIENS.lien:sell` to `SYNQLIEN_SELLER`. This is the explicit Flow
capability for seller workflow access; it supplements the lien-sale API
permissions seeded by `20260627000002_SeedSynqLienSalePermissions`.

`20260903020000_AddSynqLienUserManagement` adds organization profile fields, pending invitation grants, organization-aware product/role indexes, dedicated access-role tables, management capabilities, the three starter roles, and owner bootstrap. Legacy tenant-scoped SynqLien grants are converted into explicit grants for each active organization membership and the ambiguous grants are removed. Existing Sellers are not mass-promoted to management roles. SynqLien activation/deactivation changes only product access in the current organization.

## External Integrations

- **AWS Route53** — DNS record management for tenant subdomain provisioning; create/delete waits for Route53 changes to become `INSYNC` before verification continues
- **Notifications service** — transactional email delivery (tenant-registration acceptance before DNS/product provisioning, invite, password reset). Registration acceptance embeds the LegalSynq logo as an inline attachment so it does not depend on the not-yet-provisioned tenant hostname.
- **Tenant service** — dual-write sync for tenant data consistency
- **Documents service** — logo registration after tenant logo upload
- **Audit service** — all identity events published via `LegalSynq.AuditClient`
- **Commerce** — `ICommerceLifecycleNotifier` called on product enable/disable (`Enabled: false` by default)

## Config (`appsettings.json`)

```json
{
  "Jwt": { "Issuer": "legalsynq-identity", "Audience": "legalsynq-platform" },
  "AuditClient": { "BaseUrl": "http://127.0.0.1:5007" },
  "Route53": {
    "BaseDomain": "demo.legalsynq.com",
    "ChangeWaitTimeoutSeconds": 120,
    "ChangeWaitPollSeconds": 5
  },
  "NotificationsService": { "BaseUrl": "", "PortalBaseDomain": "" },
  "TenantService": { "InternalUrl": "http://127.0.0.1:5005" },
  "CommerceIntegration": { "Enabled": false, "BaseUrl": "http://127.0.0.1:5030" }
}
```
