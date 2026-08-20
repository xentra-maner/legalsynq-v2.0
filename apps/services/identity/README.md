# Identity Service

Authentication, user management, organisations, roles, product access, and RBAC policy evaluation.

**Port:** 5001

## Responsibilities

- JWT issuance and validation
- User create / invite / activate / deactivate
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
| `POST` | `/api/internal/tenant-provisioning/provision` | Internal: full tenant provision |
| `GET` | `/api/internal/users/account-exists` | Internal: trusted product services can check whether an email already belongs to an Identity account |
| `GET` | `/api/internal/users/{userId}/display` | Internal: trusted product services can resolve a tenant-scoped user's first/last display name from `idt_Users`; optional `organizationId` also accepts active org membership in that tenant |
| `GET` | `/api/internal/users/tenant-owner/display` | Internal: trusted product services can resolve the tenant owner's first/last display name from `idt_Tenants.OwnerUserId` and `idt_Users` |
| `POST` | `/api/admin/products/{code}/provision` | Enable/disable product for tenant |
| `POST` | `/api/admin/organizations/synqlien-buyer` | Internal: create/resolve a tenant-scoped `LIEN_OWNER` org for SynqLien public buyer activation |
| `GET` | `/api/admin/organizations?tenantId={tenantId}&orgType=LAW_FIRM` | Internal/admin: list law firm organizations for CareConnect referral portal selection; tenant-scoped requests include global law firms |
| `POST` | `/api/admin/organizations/{id}/self-register` | Internal: CareConnect self-enrollment creates or links an active Identity user; accepts optional user `title` |
| `POST` | `/api/admin/organizations/{id}/synqlien-buyer-self-register` | Internal: create a SynqLien buyer user and grant `SYNQ_LIENS:SYNQLIEN_BUYER`; returns `409 ACCOUNT_ALREADY_EXISTS` for existing emails |
| `GET` | `/api/tenants/current/branding` | Anonymous branding by tenant code |

## Database

`IdentityDb` (MySQL) — all tables prefixed `idt_`.

Biometric device sessions are installed by EF migration
`20260810113000_AddBiometricDeviceSessions`; startup does not create these tables manually.

`idt_Users` includes an optional `Title` column (`varchar(50)`) for professional titles captured during
CareConnect portal enrollment and exposed on user DTOs. Existing rows may leave it `NULL`.

CareConnect seeds `CARECONNECT_NETWORK_MANAGER` for law-firm provider network management, including
provider search, map, and provider-management capabilities. `LAW_FIRM` and `LIEN_OWNER` organization
eligibility is seeded for `SYNQ_CARECONNECT` so law-firm-scoped CareConnect users can be provisioned
without tenant-wide user-management access.

`20260728000001_SeedSynqLienSellWorkflowPermission` maps
`SYNQ_LIENS.lien:sell` to `SYNQLIEN_SELLER`. This is the explicit Flow
capability for seller workflow access; it supplements the lien-sale API
permissions seeded by `20260627000002_SeedSynqLienSalePermissions`.

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
