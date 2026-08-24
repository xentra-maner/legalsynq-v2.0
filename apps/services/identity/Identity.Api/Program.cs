using System.Text;
using System.Threading.RateLimiting;
using BuildingBlocks;
using BuildingBlocks.Authentication.ServiceTokens;
using Contracts;
using Identity.Api;
using Identity.Api.Endpoints;
using Identity.Infrastructure;
using Identity.Infrastructure.Data;
using Identity.Infrastructure.Persistence.Migrations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

const string ServiceName = "identity";
const string Version = "v1";

var builder = WebApplication.CreateBuilder(args);

builder.Logging
    .ClearProviders()
    .AddConsole();

builder.Services.AddInfrastructure(builder.Configuration);

// ── JWT authentication (for GET /api/auth/me) ─────────────────────────────
// Identity.Api both ISSUES and VALIDATES JWTs.
// Validation here is used only for the /auth/me endpoint (called by the Next.js BFF).
// The gateway handles JWT validation for all other downstream service routes.
var jwtSection   = builder.Configuration.GetSection("Jwt");
var signingKey   = jwtSection["SigningKey"]
    ?? throw new InvalidOperationException("Jwt:SigningKey is not configured.");
var issuer       = jwtSection["Issuer"]   ?? "legalsynq-identity";
var audience     = jwtSection["Audience"] ?? "legalsynq-platform";

// ── BLK-OPS-01: Production fail-fast (supersedes BLK-SEC-01 inline checks) ────
// Both email values must be present for invitation and password-reset emails to work.
// Failing at startup prevents silent runtime drops where emails are never sent.
if (!builder.Environment.IsDevelopment())
{
    var v = new RuntimeConfigValidator(builder.Configuration, "identity");
    v
        // JWT signing key must be real — not a placeholder
        .RequireNotPlaceholder("Jwt:SigningKey")
        // Provisioning secret gates all internal provisioning and membership endpoints
        .RequireNonEmpty("TenantService:ProvisioningSecret")
        // Notifications service — required for invitation and password-reset emails
        .RequireAbsoluteUrl("NotificationsService:BaseUrl")
        .RequireNonEmpty("NotificationsService:PortalBaseUrl")
        // LS-ID-TNT-016-01: tenant-subdomain-aware portal links require a base domain in
        // non-development environments. Missing this causes BuildBaseUrl to silently return
        // null and breaks all invite/reset email links. Set via
        // NotificationsService__PortalBaseDomain (e.g. "portal.legalsynq.com").
        .RequireNonEmpty("NotificationsService:PortalBaseDomain")
        // Database connection string
        .RequireConnectionString("ConnectionStrings:IdentityDb");
}

// ── LS-ID-TNT-016-01: Log portal URL-building mode at startup ─────────────
// NotificationsService:PortalBaseDomain (env: NotificationsService__PortalBaseDomain)
// enables tenant-subdomain-aware email links: https://{slug}.{baseDomain}/{path}?token=...
// When absent, PortalBaseUrl is used as a fallback (generic, non-subdomain URL).
// Set NotificationsService__PortalBaseDomain to your deployment base domain (e.g. example.com).
{
    var startupLogger     = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("Identity.Startup");
    var portalBaseDomain  = builder.Configuration["NotificationsService:PortalBaseDomain"];
    var portalBaseUrl     = builder.Configuration["NotificationsService:PortalBaseUrl"];

    if (!string.IsNullOrWhiteSpace(portalBaseDomain))
    {
        startupLogger.LogInformation(
            "[LS-ID-TNT-016-01] Portal URL mode: SUBDOMAIN — invite/reset links will use " +
            "https://{{slug}}.{BaseDomain}/{{path}}?token=... Set via NotificationsService__PortalBaseDomain.",
            portalBaseDomain);
    }
    else if (!string.IsNullOrWhiteSpace(portalBaseUrl))
    {
        startupLogger.LogWarning(
            "[LS-ID-TNT-016-01] Portal URL mode: FALLBACK — NotificationsService:PortalBaseDomain is not set; " +
            "invite/reset links will use the generic PortalBaseUrl ({PortalBaseUrl}). " +
            "Set NotificationsService__PortalBaseDomain to enable tenant-subdomain-aware links.",
            portalBaseUrl);
    }
    else
    {
        startupLogger.LogError(
            "[LS-ID-TNT-016-01] Portal URL mode: UNCONFIGURED — neither PortalBaseDomain nor PortalBaseUrl is set. " +
            "Invite and password-reset emails cannot be sent. " +
            "Set NotificationsService__PortalBaseDomain (required) and NotificationsService__PortalBaseUrl (fallback).");
    }
}

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;   // keep claim names as-is (sub, email, etc.)
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer           = true,
            ValidIssuer              = issuer,
            ValidateAudience         = true,
            ValidAudience            = audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey         = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)) { KeyId = ServiceTokenAuthenticationDefaults.UserTokenKeyId },
            ValidateLifetime         = true,
            ClockSkew                = TimeSpan.Zero,
            RoleClaimType            = "role",
            NameClaimType            = System.Security.Claims.ClaimTypes.NameIdentifier,
        };
    });

builder.Services.AddAuthorization();

// Resolves the real client IP from X-Forwarded-For (set by the gateway/proxy)
// before falling back to the direct TCP remote address. This prevents all
// traffic from appearing to originate from the gateway IP, which would cause
// a single bad actor to exhaust the shared limit for all legitimate callers.
static string ResolveClientIp(HttpContext ctx)
{
    var xff = ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault();
    if (!string.IsNullOrWhiteSpace(xff))
    {
        var first = xff.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
        if (!string.IsNullOrWhiteSpace(first))
            return first;
    }
    return ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

static string ResolveAuthPartition(HttpContext ctx)
{
    var account = ctx.User.FindFirst("sub")?.Value ?? "anonymous";
    var device = ctx.User.FindFirst("device_session_id")?.Value
        ?? ctx.Request.Headers["X-Device-Session-Id"].FirstOrDefault() ?? "unknown-device";
    var client = ctx.Request.Headers["X-Application-Client"].FirstOrDefault() ?? "unknown-client";
    return $"{ResolveClientIp(ctx)}:{account}:{device}:{client}";
}

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("auth-login", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ResolveAuthPartition(context),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit          = 20,
                Window               = TimeSpan.FromMinutes(5),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit           = 0,
            }));

    options.AddPolicy("auth-forgot-password", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ResolveAuthPartition(context),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit          = 5,
                Window               = TimeSpan.FromMinutes(15),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit           = 0,
            }));

    options.AddPolicy("auth-token-exchange", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ResolveAuthPartition(context),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit          = 10,
                Window               = TimeSpan.FromMinutes(5),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit           = 0,
            }));

    // ── BE-BIO-020: biometric login / device-session rate limits ────────────
    options.AddPolicy("auth-refresh", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ResolveAuthPartition(context),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit          = 30,
                Window               = TimeSpan.FromMinutes(5),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit           = 0,
            }));

    options.AddPolicy("auth-logout", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ResolveAuthPartition(context),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit          = 20,
                Window               = TimeSpan.FromMinutes(5),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit           = 0,
            }));

    options.AddPolicy("auth-logout-all", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ResolveAuthPartition(context),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit          = 5,
                Window               = TimeSpan.FromMinutes(15),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit           = 0,
            }));

    options.AddPolicy("auth-biometric-toggle", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ResolveAuthPartition(context),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit          = 20,
                Window               = TimeSpan.FromMinutes(5),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit           = 0,
            }));

    options.AddPolicy("auth-device-session-list", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ResolveAuthPartition(context),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit          = 30,
                Window               = TimeSpan.FromMinutes(5),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit           = 0,
            }));

    options.AddPolicy("auth-device-session-revoke", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ResolveAuthPartition(context),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit          = 15,
                Window               = TimeSpan.FromMinutes(5),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit           = 0,
            }));
});

var app = builder.Build();
var env = app.Environment.EnvironmentName;

app.Logger.LogInformation("Starting {Service} {Version} in {Environment}", ServiceName, Version, env);

// ── LS-ID-UIX-003 pre-migration guard ───────────────────────────────────────
// 20260401200002_AddUserSecurityFields can partially commit on MySQL: ALTER
// TABLE succeeds, then later CREATE TABLE / CREATE INDEX steps fail, leaving
// the Users security columns present without the EF history row. The next boot
// retries the ALTER TABLE and dies on "Duplicate column name".
try
{
    using var scope = app.Services.CreateScope();
    var db   = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
    var conn = db.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) conn.Open();
    using var cmd = conn.CreateCommand();

    Identity.Infrastructure.StartupMigrationGuard.ApplyIfMissing(
        cmd,
        migrationId: "20260401200002_AddUserSecurityFields",
        efVersion:   "8.0.7",
        logger:      app.Logger,
        guardLabel:  "LS-ID-UIX-003",
        prerequisite: c => RequiredTablesExist(c, "Users"),
        apply: c =>
        {
            EnsureColumn(
                c,
                tableName: "Users",
                columnName: "IsLocked",
                alterSql: "ALTER TABLE `Users` ADD `IsLocked` tinyint(1) NOT NULL DEFAULT FALSE;");
            EnsureColumn(
                c,
                tableName: "Users",
                columnName: "LockedAtUtc",
                alterSql: "ALTER TABLE `Users` ADD `LockedAtUtc` datetime(6) NULL;");
            EnsureColumn(
                c,
                tableName: "Users",
                columnName: "LockedByAdminId",
                alterSql: "ALTER TABLE `Users` ADD `LockedByAdminId` char(36) NULL;");
            EnsureColumn(
                c,
                tableName: "Users",
                columnName: "LastLoginAtUtc",
                alterSql: "ALTER TABLE `Users` ADD `LastLoginAtUtc` datetime(6) NULL;");
            EnsureColumn(
                c,
                tableName: "Users",
                columnName: "SessionVersion",
                alterSql: "ALTER TABLE `Users` ADD `SessionVersion` int NOT NULL DEFAULT 0;");

            EnsureTable(
                c,
                tableName: "PasswordResetTokens",
                createSql: @"
CREATE TABLE `PasswordResetTokens` (
    `Id` char(36) COLLATE ascii_general_ci NOT NULL,
    `UserId` char(36) COLLATE ascii_general_ci NOT NULL,
    `TenantId` char(36) COLLATE ascii_general_ci NOT NULL,
    `TriggeredByAdminId` char(36) COLLATE ascii_general_ci NULL,
    `TokenHash` varchar(64) NOT NULL,
    `Status` varchar(20) NOT NULL,
    `ExpiresAtUtc` datetime(6) NOT NULL,
    `UsedAtUtc` datetime(6) NULL,
    `RevokedAtUtc` datetime(6) NULL,
    `CreatedAtUtc` datetime(6) NOT NULL,
    CONSTRAINT `PK_PasswordResetTokens` PRIMARY KEY (`Id`),
    CONSTRAINT `FK_PasswordResetTokens_Users_UserId`
        FOREIGN KEY (`UserId`) REFERENCES `Users` (`Id`) ON DELETE CASCADE
) CHARACTER SET utf8mb4;");

            EnsureIndex(
                c,
                tableName: "PasswordResetTokens",
                indexName: "IX_PasswordResetTokens_TokenHash",
                createSql: "CREATE UNIQUE INDEX `IX_PasswordResetTokens_TokenHash` ON `PasswordResetTokens` (`TokenHash`);");
            EnsureIndex(
                c,
                tableName: "PasswordResetTokens",
                indexName: "IX_PasswordResetTokens_UserId",
                createSql: "CREATE INDEX `IX_PasswordResetTokens_UserId` ON `PasswordResetTokens` (`UserId`);");
        },
        warningMessage:
            "LS-ID-UIX-003: AddUserSecurityFields is missing from EF history. " +
            "Re-applying the migration idempotently and recording it before normal Migrate().");
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "LS-ID-UIX-003 pre-migration guard failed — proceeding with normal migration");
}

// ── LS-ID-AST-001 pre-migration guard ───────────────────────────────────────
// 20260410190647_AddAccessSourceOfTruth creates three legacy pre-prefix tables.
// If MySQL committed those CREATE TABLE statements before the migration failed,
// the next startup retries the same DDL and aborts on "table already exists".
try
{
    using var scope = app.Services.CreateScope();
    var db   = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
    var conn = db.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) conn.Open();
    using var cmd = conn.CreateCommand();

    Identity.Infrastructure.StartupMigrationGuard.ApplyIfMissing(
        cmd,
        migrationId: "20260410190647_AddAccessSourceOfTruth",
        efVersion:   "8.0.7",
        logger:      app.Logger,
        guardLabel:  "LS-ID-AST-001",
        prerequisite: c => AnyTableExists(
            c,
            "TenantProductEntitlements",
            "UserProductAccess",
            "UserRoleAssignments"),
        apply: c =>
        {
            EnsureTable(
                c,
                tableName: "TenantProductEntitlements",
                createSql: @"
CREATE TABLE `TenantProductEntitlements` (
    `Id` char(36) COLLATE ascii_general_ci NOT NULL,
    `TenantId` char(36) COLLATE ascii_general_ci NOT NULL,
    `ProductCode` varchar(50) CHARACTER SET utf8mb4 NOT NULL,
    `Status` varchar(20) CHARACTER SET utf8mb4 NOT NULL,
    `EnabledAtUtc` datetime(6) NULL,
    `DisabledAtUtc` datetime(6) NULL,
    `CreatedAtUtc` datetime(6) NOT NULL,
    `UpdatedAtUtc` datetime(6) NOT NULL,
    `CreatedByUserId` char(36) COLLATE ascii_general_ci NULL,
    `UpdatedByUserId` char(36) COLLATE ascii_general_ci NULL,
    CONSTRAINT `PK_TenantProductEntitlements` PRIMARY KEY (`Id`)
) CHARACTER SET utf8mb4;");
            EnsureColumns(
                c,
                "TenantProductEntitlements",
                ("TenantId", "ALTER TABLE `TenantProductEntitlements` ADD `TenantId` char(36) COLLATE ascii_general_ci NOT NULL;"),
                ("ProductCode", "ALTER TABLE `TenantProductEntitlements` ADD `ProductCode` varchar(50) CHARACTER SET utf8mb4 NOT NULL;"),
                ("Status", "ALTER TABLE `TenantProductEntitlements` ADD `Status` varchar(20) CHARACTER SET utf8mb4 NOT NULL;"),
                ("EnabledAtUtc", "ALTER TABLE `TenantProductEntitlements` ADD `EnabledAtUtc` datetime(6) NULL;"),
                ("DisabledAtUtc", "ALTER TABLE `TenantProductEntitlements` ADD `DisabledAtUtc` datetime(6) NULL;"),
                ("CreatedAtUtc", "ALTER TABLE `TenantProductEntitlements` ADD `CreatedAtUtc` datetime(6) NOT NULL;"),
                ("UpdatedAtUtc", "ALTER TABLE `TenantProductEntitlements` ADD `UpdatedAtUtc` datetime(6) NOT NULL;"),
                ("CreatedByUserId", "ALTER TABLE `TenantProductEntitlements` ADD `CreatedByUserId` char(36) COLLATE ascii_general_ci NULL;"),
                ("UpdatedByUserId", "ALTER TABLE `TenantProductEntitlements` ADD `UpdatedByUserId` char(36) COLLATE ascii_general_ci NULL;"));

            EnsureTable(
                c,
                tableName: "UserProductAccess",
                createSql: @"
CREATE TABLE `UserProductAccess` (
    `Id` char(36) COLLATE ascii_general_ci NOT NULL,
    `TenantId` char(36) COLLATE ascii_general_ci NOT NULL,
    `UserId` char(36) COLLATE ascii_general_ci NOT NULL,
    `ProductCode` varchar(50) CHARACTER SET utf8mb4 NOT NULL,
    `AccessStatus` varchar(20) CHARACTER SET utf8mb4 NOT NULL,
    `OrganizationId` char(36) COLLATE ascii_general_ci NULL,
    `SourceType` varchar(20) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'Direct',
    `GrantedAtUtc` datetime(6) NULL,
    `RevokedAtUtc` datetime(6) NULL,
    `CreatedAtUtc` datetime(6) NOT NULL,
    `UpdatedAtUtc` datetime(6) NOT NULL,
    `CreatedByUserId` char(36) COLLATE ascii_general_ci NULL,
    `UpdatedByUserId` char(36) COLLATE ascii_general_ci NULL,
    CONSTRAINT `PK_UserProductAccess` PRIMARY KEY (`Id`)
) CHARACTER SET utf8mb4;");
            EnsureColumns(
                c,
                "UserProductAccess",
                ("TenantId", "ALTER TABLE `UserProductAccess` ADD `TenantId` char(36) COLLATE ascii_general_ci NOT NULL;"),
                ("UserId", "ALTER TABLE `UserProductAccess` ADD `UserId` char(36) COLLATE ascii_general_ci NOT NULL;"),
                ("ProductCode", "ALTER TABLE `UserProductAccess` ADD `ProductCode` varchar(50) CHARACTER SET utf8mb4 NOT NULL;"),
                ("AccessStatus", "ALTER TABLE `UserProductAccess` ADD `AccessStatus` varchar(20) CHARACTER SET utf8mb4 NOT NULL;"),
                ("OrganizationId", "ALTER TABLE `UserProductAccess` ADD `OrganizationId` char(36) COLLATE ascii_general_ci NULL;"),
                ("SourceType", "ALTER TABLE `UserProductAccess` ADD `SourceType` varchar(20) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'Direct';"),
                ("GrantedAtUtc", "ALTER TABLE `UserProductAccess` ADD `GrantedAtUtc` datetime(6) NULL;"),
                ("RevokedAtUtc", "ALTER TABLE `UserProductAccess` ADD `RevokedAtUtc` datetime(6) NULL;"),
                ("CreatedAtUtc", "ALTER TABLE `UserProductAccess` ADD `CreatedAtUtc` datetime(6) NOT NULL;"),
                ("UpdatedAtUtc", "ALTER TABLE `UserProductAccess` ADD `UpdatedAtUtc` datetime(6) NOT NULL;"),
                ("CreatedByUserId", "ALTER TABLE `UserProductAccess` ADD `CreatedByUserId` char(36) COLLATE ascii_general_ci NULL;"),
                ("UpdatedByUserId", "ALTER TABLE `UserProductAccess` ADD `UpdatedByUserId` char(36) COLLATE ascii_general_ci NULL;"));

            EnsureTable(
                c,
                tableName: "UserRoleAssignments",
                createSql: @"
CREATE TABLE `UserRoleAssignments` (
    `Id` char(36) COLLATE ascii_general_ci NOT NULL,
    `TenantId` char(36) COLLATE ascii_general_ci NOT NULL,
    `UserId` char(36) COLLATE ascii_general_ci NOT NULL,
    `ProductCode` varchar(50) CHARACTER SET utf8mb4 NULL,
    `RoleCode` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
    `AssignmentStatus` varchar(20) CHARACTER SET utf8mb4 NOT NULL,
    `OrganizationId` char(36) COLLATE ascii_general_ci NULL,
    `SourceType` varchar(20) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'Direct',
    `AssignedAtUtc` datetime(6) NULL,
    `RemovedAtUtc` datetime(6) NULL,
    `CreatedAtUtc` datetime(6) NOT NULL,
    `UpdatedAtUtc` datetime(6) NOT NULL,
    `CreatedByUserId` char(36) COLLATE ascii_general_ci NULL,
    `UpdatedByUserId` char(36) COLLATE ascii_general_ci NULL,
    CONSTRAINT `PK_UserRoleAssignments` PRIMARY KEY (`Id`)
) CHARACTER SET utf8mb4;");
            EnsureColumns(
                c,
                "UserRoleAssignments",
                ("TenantId", "ALTER TABLE `UserRoleAssignments` ADD `TenantId` char(36) COLLATE ascii_general_ci NOT NULL;"),
                ("UserId", "ALTER TABLE `UserRoleAssignments` ADD `UserId` char(36) COLLATE ascii_general_ci NOT NULL;"),
                ("ProductCode", "ALTER TABLE `UserRoleAssignments` ADD `ProductCode` varchar(50) CHARACTER SET utf8mb4 NULL;"),
                ("RoleCode", "ALTER TABLE `UserRoleAssignments` ADD `RoleCode` varchar(100) CHARACTER SET utf8mb4 NOT NULL;"),
                ("AssignmentStatus", "ALTER TABLE `UserRoleAssignments` ADD `AssignmentStatus` varchar(20) CHARACTER SET utf8mb4 NOT NULL;"),
                ("OrganizationId", "ALTER TABLE `UserRoleAssignments` ADD `OrganizationId` char(36) COLLATE ascii_general_ci NULL;"),
                ("SourceType", "ALTER TABLE `UserRoleAssignments` ADD `SourceType` varchar(20) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'Direct';"),
                ("AssignedAtUtc", "ALTER TABLE `UserRoleAssignments` ADD `AssignedAtUtc` datetime(6) NULL;"),
                ("RemovedAtUtc", "ALTER TABLE `UserRoleAssignments` ADD `RemovedAtUtc` datetime(6) NULL;"),
                ("CreatedAtUtc", "ALTER TABLE `UserRoleAssignments` ADD `CreatedAtUtc` datetime(6) NOT NULL;"),
                ("UpdatedAtUtc", "ALTER TABLE `UserRoleAssignments` ADD `UpdatedAtUtc` datetime(6) NOT NULL;"),
                ("CreatedByUserId", "ALTER TABLE `UserRoleAssignments` ADD `CreatedByUserId` char(36) COLLATE ascii_general_ci NULL;"),
                ("UpdatedByUserId", "ALTER TABLE `UserRoleAssignments` ADD `UpdatedByUserId` char(36) COLLATE ascii_general_ci NULL;"));

            EnsureIndex(
                c,
                tableName: "TenantProductEntitlements",
                indexName: "IX_TenantProductEntitlements_TenantId_ProductCode",
                createSql: "CREATE UNIQUE INDEX `IX_TenantProductEntitlements_TenantId_ProductCode` ON `TenantProductEntitlements` (`TenantId`, `ProductCode`);");
            EnsureIndex(
                c,
                tableName: "UserProductAccess",
                indexName: "IX_UserProductAccess_TenantId_UserId_ProductCode",
                createSql: "CREATE UNIQUE INDEX `IX_UserProductAccess_TenantId_UserId_ProductCode` ON `UserProductAccess` (`TenantId`, `UserId`, `ProductCode`);");
            EnsureIndex(
                c,
                tableName: "UserRoleAssignments",
                indexName: "IX_UserRoleAssignments_TenantId_UserId_RoleCode",
                createSql: "CREATE INDEX `IX_UserRoleAssignments_TenantId_UserId_RoleCode` ON `UserRoleAssignments` (`TenantId`, `UserId`, `RoleCode`);");
        },
        warningMessage:
            "LS-ID-AST-001: AddAccessSourceOfTruth is missing from EF history. " +
            "Ensuring legacy access-source-of-truth tables/indexes exist and recording the migration before normal Migrate().");
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "LS-ID-AST-001 pre-migration guard failed — proceeding with normal migration");
}

// ── LS-ID-AVR-001 pre-migration guard ───────────────────────────────────────
// 20260410192258_AddAccessVersion adds Users.AccessVersion and recreates the
// UserProductAccess unique index. MySQL may commit the ADD COLUMN before the
// history row is written, leading to duplicate-column failures on restart.
try
{
    using var scope = app.Services.CreateScope();
    var db   = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
    var conn = db.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) conn.Open();
    using var cmd = conn.CreateCommand();

    Identity.Infrastructure.StartupMigrationGuard.ApplyIfMissing(
        cmd,
        migrationId: "20260410192258_AddAccessVersion",
        efVersion:   "8.0.7",
        logger:      app.Logger,
        guardLabel:  "LS-ID-AVR-001",
        prerequisite: c => ColumnExists(c, "Users", "AccessVersion"),
        apply: c =>
        {
            EnsureIndex(
                c,
                tableName: "UserProductAccess",
                indexName: "IX_UserProductAccess_TenantId_UserId_ProductCode",
                createSql: "CREATE UNIQUE INDEX `IX_UserProductAccess_TenantId_UserId_ProductCode` ON `UserProductAccess` (`TenantId`, `UserId`, `ProductCode`);");
        },
        warningMessage:
            "LS-ID-AVR-001: AddAccessVersion is missing from EF history. " +
            "Ensuring AccessVersion-side effects exist and recording the migration before normal Migrate().");
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "LS-ID-AVR-001 pre-migration guard failed — proceeding with normal migration");
}

// ── LS-ID-AGR-001 pre-migration guard ───────────────────────────────────────
// 20260410195442_AddAccessGroups creates four legacy pre-prefix tables and
// their indexes. Handle mixed partial states before EF retries CREATE TABLE.
try
{
    using var scope = app.Services.CreateScope();
    var db   = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
    var conn = db.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) conn.Open();
    using var cmd = conn.CreateCommand();

    Identity.Infrastructure.StartupMigrationGuard.ApplyIfMissing(
        cmd,
        migrationId: "20260410195442_AddAccessGroups",
        efVersion:   "8.0.7",
        logger:      app.Logger,
        guardLabel:  "LS-ID-AGR-001",
        prerequisite: c => AnyTableExists(
            c,
            "AccessGroupMemberships",
            "AccessGroups",
            "GroupProductAccess",
            "GroupRoleAssignments"),
        apply: c =>
        {
            EnsureTable(
                c,
                tableName: "AccessGroupMemberships",
                createSql: @"
CREATE TABLE `AccessGroupMemberships` (
    `Id` char(36) COLLATE ascii_general_ci NOT NULL,
    `TenantId` char(36) COLLATE ascii_general_ci NOT NULL,
    `GroupId` char(36) COLLATE ascii_general_ci NOT NULL,
    `UserId` char(36) COLLATE ascii_general_ci NOT NULL,
    `MembershipStatus` varchar(20) CHARACTER SET utf8mb4 NOT NULL,
    `AddedAtUtc` datetime(6) NOT NULL,
    `RemovedAtUtc` datetime(6) NULL,
    `CreatedAtUtc` datetime(6) NOT NULL,
    `UpdatedAtUtc` datetime(6) NOT NULL,
    `CreatedByUserId` char(36) COLLATE ascii_general_ci NULL,
    `UpdatedByUserId` char(36) COLLATE ascii_general_ci NULL,
    CONSTRAINT `PK_AccessGroupMemberships` PRIMARY KEY (`Id`)
) CHARACTER SET utf8mb4;");
            EnsureColumns(
                c,
                "AccessGroupMemberships",
                ("TenantId", "ALTER TABLE `AccessGroupMemberships` ADD `TenantId` char(36) COLLATE ascii_general_ci NOT NULL;"),
                ("GroupId", "ALTER TABLE `AccessGroupMemberships` ADD `GroupId` char(36) COLLATE ascii_general_ci NOT NULL;"),
                ("UserId", "ALTER TABLE `AccessGroupMemberships` ADD `UserId` char(36) COLLATE ascii_general_ci NOT NULL;"),
                ("MembershipStatus", "ALTER TABLE `AccessGroupMemberships` ADD `MembershipStatus` varchar(20) CHARACTER SET utf8mb4 NOT NULL;"),
                ("AddedAtUtc", "ALTER TABLE `AccessGroupMemberships` ADD `AddedAtUtc` datetime(6) NOT NULL;"),
                ("RemovedAtUtc", "ALTER TABLE `AccessGroupMemberships` ADD `RemovedAtUtc` datetime(6) NULL;"),
                ("CreatedAtUtc", "ALTER TABLE `AccessGroupMemberships` ADD `CreatedAtUtc` datetime(6) NOT NULL;"),
                ("UpdatedAtUtc", "ALTER TABLE `AccessGroupMemberships` ADD `UpdatedAtUtc` datetime(6) NOT NULL;"),
                ("CreatedByUserId", "ALTER TABLE `AccessGroupMemberships` ADD `CreatedByUserId` char(36) COLLATE ascii_general_ci NULL;"),
                ("UpdatedByUserId", "ALTER TABLE `AccessGroupMemberships` ADD `UpdatedByUserId` char(36) COLLATE ascii_general_ci NULL;"));

            EnsureTable(
                c,
                tableName: "AccessGroups",
                createSql: @"
CREATE TABLE `AccessGroups` (
    `Id` char(36) COLLATE ascii_general_ci NOT NULL,
    `TenantId` char(36) COLLATE ascii_general_ci NOT NULL,
    `Name` varchar(200) CHARACTER SET utf8mb4 NOT NULL,
    `Description` varchar(1000) CHARACTER SET utf8mb4 NULL,
    `Status` varchar(20) CHARACTER SET utf8mb4 NOT NULL,
    `ScopeType` varchar(20) CHARACTER SET utf8mb4 NOT NULL,
    `ProductCode` varchar(100) CHARACTER SET utf8mb4 NULL,
    `OrganizationId` char(36) COLLATE ascii_general_ci NULL,
    `CreatedAtUtc` datetime(6) NOT NULL,
    `UpdatedAtUtc` datetime(6) NOT NULL,
    `CreatedByUserId` char(36) COLLATE ascii_general_ci NULL,
    `UpdatedByUserId` char(36) COLLATE ascii_general_ci NULL,
    CONSTRAINT `PK_AccessGroups` PRIMARY KEY (`Id`)
) CHARACTER SET utf8mb4;");
            EnsureColumns(
                c,
                "AccessGroups",
                ("TenantId", "ALTER TABLE `AccessGroups` ADD `TenantId` char(36) COLLATE ascii_general_ci NOT NULL;"),
                ("Name", "ALTER TABLE `AccessGroups` ADD `Name` varchar(200) CHARACTER SET utf8mb4 NOT NULL;"),
                ("Description", "ALTER TABLE `AccessGroups` ADD `Description` varchar(1000) CHARACTER SET utf8mb4 NULL;"),
                ("Status", "ALTER TABLE `AccessGroups` ADD `Status` varchar(20) CHARACTER SET utf8mb4 NOT NULL;"),
                ("ScopeType", "ALTER TABLE `AccessGroups` ADD `ScopeType` varchar(20) CHARACTER SET utf8mb4 NOT NULL;"),
                ("ProductCode", "ALTER TABLE `AccessGroups` ADD `ProductCode` varchar(100) CHARACTER SET utf8mb4 NULL;"),
                ("OrganizationId", "ALTER TABLE `AccessGroups` ADD `OrganizationId` char(36) COLLATE ascii_general_ci NULL;"),
                ("CreatedAtUtc", "ALTER TABLE `AccessGroups` ADD `CreatedAtUtc` datetime(6) NOT NULL;"),
                ("UpdatedAtUtc", "ALTER TABLE `AccessGroups` ADD `UpdatedAtUtc` datetime(6) NOT NULL;"),
                ("CreatedByUserId", "ALTER TABLE `AccessGroups` ADD `CreatedByUserId` char(36) COLLATE ascii_general_ci NULL;"),
                ("UpdatedByUserId", "ALTER TABLE `AccessGroups` ADD `UpdatedByUserId` char(36) COLLATE ascii_general_ci NULL;"));

            EnsureTable(
                c,
                tableName: "GroupProductAccess",
                createSql: @"
CREATE TABLE `GroupProductAccess` (
    `Id` char(36) COLLATE ascii_general_ci NOT NULL,
    `TenantId` char(36) COLLATE ascii_general_ci NOT NULL,
    `GroupId` char(36) COLLATE ascii_general_ci NOT NULL,
    `ProductCode` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
    `AccessStatus` varchar(20) CHARACTER SET utf8mb4 NOT NULL,
    `GrantedAtUtc` datetime(6) NULL,
    `RevokedAtUtc` datetime(6) NULL,
    `CreatedAtUtc` datetime(6) NOT NULL,
    `UpdatedAtUtc` datetime(6) NOT NULL,
    `CreatedByUserId` char(36) COLLATE ascii_general_ci NULL,
    `UpdatedByUserId` char(36) COLLATE ascii_general_ci NULL,
    CONSTRAINT `PK_GroupProductAccess` PRIMARY KEY (`Id`)
) CHARACTER SET utf8mb4;");
            EnsureColumns(
                c,
                "GroupProductAccess",
                ("TenantId", "ALTER TABLE `GroupProductAccess` ADD `TenantId` char(36) COLLATE ascii_general_ci NOT NULL;"),
                ("GroupId", "ALTER TABLE `GroupProductAccess` ADD `GroupId` char(36) COLLATE ascii_general_ci NOT NULL;"),
                ("ProductCode", "ALTER TABLE `GroupProductAccess` ADD `ProductCode` varchar(100) CHARACTER SET utf8mb4 NOT NULL;"),
                ("AccessStatus", "ALTER TABLE `GroupProductAccess` ADD `AccessStatus` varchar(20) CHARACTER SET utf8mb4 NOT NULL;"),
                ("GrantedAtUtc", "ALTER TABLE `GroupProductAccess` ADD `GrantedAtUtc` datetime(6) NULL;"),
                ("RevokedAtUtc", "ALTER TABLE `GroupProductAccess` ADD `RevokedAtUtc` datetime(6) NULL;"),
                ("CreatedAtUtc", "ALTER TABLE `GroupProductAccess` ADD `CreatedAtUtc` datetime(6) NOT NULL;"),
                ("UpdatedAtUtc", "ALTER TABLE `GroupProductAccess` ADD `UpdatedAtUtc` datetime(6) NOT NULL;"),
                ("CreatedByUserId", "ALTER TABLE `GroupProductAccess` ADD `CreatedByUserId` char(36) COLLATE ascii_general_ci NULL;"),
                ("UpdatedByUserId", "ALTER TABLE `GroupProductAccess` ADD `UpdatedByUserId` char(36) COLLATE ascii_general_ci NULL;"));

            EnsureTable(
                c,
                tableName: "GroupRoleAssignments",
                createSql: @"
CREATE TABLE `GroupRoleAssignments` (
    `Id` char(36) COLLATE ascii_general_ci NOT NULL,
    `TenantId` char(36) COLLATE ascii_general_ci NOT NULL,
    `GroupId` char(36) COLLATE ascii_general_ci NOT NULL,
    `RoleCode` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
    `ProductCode` varchar(100) CHARACTER SET utf8mb4 NULL,
    `OrganizationId` char(36) COLLATE ascii_general_ci NULL,
    `AssignmentStatus` varchar(20) CHARACTER SET utf8mb4 NOT NULL,
    `AssignedAtUtc` datetime(6) NULL,
    `RemovedAtUtc` datetime(6) NULL,
    `CreatedAtUtc` datetime(6) NOT NULL,
    `UpdatedAtUtc` datetime(6) NOT NULL,
    `CreatedByUserId` char(36) COLLATE ascii_general_ci NULL,
    `UpdatedByUserId` char(36) COLLATE ascii_general_ci NULL,
    CONSTRAINT `PK_GroupRoleAssignments` PRIMARY KEY (`Id`)
) CHARACTER SET utf8mb4;");
            EnsureColumns(
                c,
                "GroupRoleAssignments",
                ("TenantId", "ALTER TABLE `GroupRoleAssignments` ADD `TenantId` char(36) COLLATE ascii_general_ci NOT NULL;"),
                ("GroupId", "ALTER TABLE `GroupRoleAssignments` ADD `GroupId` char(36) COLLATE ascii_general_ci NOT NULL;"),
                ("RoleCode", "ALTER TABLE `GroupRoleAssignments` ADD `RoleCode` varchar(100) CHARACTER SET utf8mb4 NOT NULL;"),
                ("ProductCode", "ALTER TABLE `GroupRoleAssignments` ADD `ProductCode` varchar(100) CHARACTER SET utf8mb4 NULL;"),
                ("OrganizationId", "ALTER TABLE `GroupRoleAssignments` ADD `OrganizationId` char(36) COLLATE ascii_general_ci NULL;"),
                ("AssignmentStatus", "ALTER TABLE `GroupRoleAssignments` ADD `AssignmentStatus` varchar(20) CHARACTER SET utf8mb4 NOT NULL;"),
                ("AssignedAtUtc", "ALTER TABLE `GroupRoleAssignments` ADD `AssignedAtUtc` datetime(6) NULL;"),
                ("RemovedAtUtc", "ALTER TABLE `GroupRoleAssignments` ADD `RemovedAtUtc` datetime(6) NULL;"),
                ("CreatedAtUtc", "ALTER TABLE `GroupRoleAssignments` ADD `CreatedAtUtc` datetime(6) NOT NULL;"),
                ("UpdatedAtUtc", "ALTER TABLE `GroupRoleAssignments` ADD `UpdatedAtUtc` datetime(6) NOT NULL;"),
                ("CreatedByUserId", "ALTER TABLE `GroupRoleAssignments` ADD `CreatedByUserId` char(36) COLLATE ascii_general_ci NULL;"),
                ("UpdatedByUserId", "ALTER TABLE `GroupRoleAssignments` ADD `UpdatedByUserId` char(36) COLLATE ascii_general_ci NULL;"));

            EnsureIndex(
                c,
                tableName: "AccessGroupMemberships",
                indexName: "IX_AccessGroupMemberships_TenantId_GroupId_UserId",
                createSql: "CREATE UNIQUE INDEX `IX_AccessGroupMemberships_TenantId_GroupId_UserId` ON `AccessGroupMemberships` (`TenantId`, `GroupId`, `UserId`);");
            EnsureIndex(
                c,
                tableName: "AccessGroups",
                indexName: "IX_AccessGroups_TenantId_Name",
                createSql: "CREATE UNIQUE INDEX `IX_AccessGroups_TenantId_Name` ON `AccessGroups` (`TenantId`, `Name`);");
            EnsureIndex(
                c,
                tableName: "GroupProductAccess",
                indexName: "IX_GroupProductAccess_TenantId_GroupId_ProductCode",
                createSql: "CREATE UNIQUE INDEX `IX_GroupProductAccess_TenantId_GroupId_ProductCode` ON `GroupProductAccess` (`TenantId`, `GroupId`, `ProductCode`);");
            EnsureIndex(
                c,
                tableName: "GroupRoleAssignments",
                indexName: "IX_GroupRoleAssignments_TenantId_GroupId_RoleCode",
                createSql: "CREATE INDEX `IX_GroupRoleAssignments_TenantId_GroupId_RoleCode` ON `GroupRoleAssignments` (`TenantId`, `GroupId`, `RoleCode`);");
        },
        warningMessage:
            "LS-ID-AGR-001: AddAccessGroups is missing from EF history. " +
            "Ensuring legacy access-group tables/indexes exist and recording the migration before normal Migrate().");
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "LS-ID-AGR-001 pre-migration guard failed — proceeding with normal migration");
}

// ── LS-ID-TNT-011 pre-migration guard ────────────────────────────────────────
// MySQL auto-commits DDL (ALTER TABLE, CREATE TABLE) immediately, even inside a
// failed migration transaction. If 20260418230627_AddTenantPermissionCatalog ran
// its DDL but failed on the data-seed step, the schema is already correct but EF
// has no record of the migration. On the next startup EF tries to re-apply it,
// the AddColumn calls fail with "Duplicate column name", and the service can never
// start. This guard detects that scenario, runs the idempotent data seeds, and
// inserts the migration record so EF's Migrate() finds nothing to do.
try
{
    using var scope = app.Services.CreateScope();
    var db    = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
    var conn  = db.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) conn.Open();
    using var cmd = conn.CreateCommand();

    Identity.Infrastructure.StartupMigrationGuard.ApplyIfMissing(
        cmd,
        migrationId: "20260418230627_AddTenantPermissionCatalog",
        efVersion:   "8.0.7",
        logger:      app.Logger,
        guardLabel:  "LS-ID-TNT-011",
        apply: c =>
        {
            // Seed SYNQ_PLATFORM product (idempotent)
            c.CommandText = @"
                INSERT IGNORE INTO `idt_Products` (`Id`, `Code`, `CreatedAtUtc`, `Description`, `IsActive`, `Name`)
                VALUES ('10000000-0000-0000-0000-000000000006','SYNQ_PLATFORM','2025-01-01 00:00:00.000000',
                        'Platform/tenant operation capabilities',1,'SynqPlatform');";
            c.ExecuteNonQuery();

            // Seed 8 TENANT.* permissions (idempotent)
            c.CommandText = @"
                INSERT IGNORE INTO `idt_Capabilities`
                    (`Id`,`ProductId`,`Code`,`Name`,`Description`,`Category`,`IsActive`,`CreatedAtUtc`,`UpdatedAtUtc`,`CreatedBy`,`UpdatedBy`)
                VALUES
                    ('60000000-0000-0000-0000-000000000030','10000000-0000-0000-0000-000000000006','TENANT.users:view',        'View Tenant Users',       'View the list of users in the tenant',                  'Users',      1,'2025-01-01 00:00:00.000000',NULL,NULL,NULL),
                    ('60000000-0000-0000-0000-000000000031','10000000-0000-0000-0000-000000000006','TENANT.users:manage',      'Manage Tenant Users',     'Create, edit, and deactivate users in the tenant',      'Users',      1,'2025-01-01 00:00:00.000000',NULL,NULL,NULL),
                    ('60000000-0000-0000-0000-000000000032','10000000-0000-0000-0000-000000000006','TENANT.groups:manage',     'Manage Access Groups',    'Create, edit, and delete tenant access groups',         'Groups',     1,'2025-01-01 00:00:00.000000',NULL,NULL,NULL),
                    ('60000000-0000-0000-0000-000000000033','10000000-0000-0000-0000-000000000006','TENANT.roles:assign',      'Assign Roles',            'Assign or revoke roles for tenant users',               'Roles',      1,'2025-01-01 00:00:00.000000',NULL,NULL,NULL),
                    ('60000000-0000-0000-0000-000000000034','10000000-0000-0000-0000-000000000006','TENANT.products:assign',   'Assign Product Access',   'Assign or revoke product access for tenant users',      'Products',   1,'2025-01-01 00:00:00.000000',NULL,NULL,NULL),
                    ('60000000-0000-0000-0000-000000000035','10000000-0000-0000-0000-000000000006','TENANT.settings:manage',   'Manage Tenant Settings',  'Update tenant configuration and preferences',           'Settings',   1,'2025-01-01 00:00:00.000000',NULL,NULL,NULL),
                    ('60000000-0000-0000-0000-000000000036','10000000-0000-0000-0000-000000000006','TENANT.audit:view',        'View Audit Logs',         'View identity and access audit events for the tenant',  'Audit',      1,'2025-01-01 00:00:00.000000',NULL,NULL,NULL),
                    ('60000000-0000-0000-0000-000000000037','10000000-0000-0000-0000-000000000006','TENANT.invitations:manage','Manage User Invitations', 'Send, resend, and revoke user invitations',             'Invitations',1,'2025-01-01 00:00:00.000000',NULL,NULL,NULL);";
            c.ExecuteNonQuery();

            // Seed role → capability assignments (idempotent). Column is CapabilityId (not PermissionId).
            c.CommandText = @"
                INSERT IGNORE INTO `idt_RoleCapabilityAssignments` (`RoleId`,`CapabilityId`,`AssignedAtUtc`,`AssignedByUserId`)
                VALUES
                    ('30000000-0000-0000-0000-000000000002','60000000-0000-0000-0000-000000000030','2025-01-01 00:00:00.000000',NULL),
                    ('30000000-0000-0000-0000-000000000002','60000000-0000-0000-0000-000000000031','2025-01-01 00:00:00.000000',NULL),
                    ('30000000-0000-0000-0000-000000000002','60000000-0000-0000-0000-000000000032','2025-01-01 00:00:00.000000',NULL),
                    ('30000000-0000-0000-0000-000000000002','60000000-0000-0000-0000-000000000033','2025-01-01 00:00:00.000000',NULL),
                    ('30000000-0000-0000-0000-000000000002','60000000-0000-0000-0000-000000000034','2025-01-01 00:00:00.000000',NULL),
                    ('30000000-0000-0000-0000-000000000002','60000000-0000-0000-0000-000000000035','2025-01-01 00:00:00.000000',NULL),
                    ('30000000-0000-0000-0000-000000000002','60000000-0000-0000-0000-000000000036','2025-01-01 00:00:00.000000',NULL),
                    ('30000000-0000-0000-0000-000000000002','60000000-0000-0000-0000-000000000037','2025-01-01 00:00:00.000000',NULL),
                    ('30000000-0000-0000-0000-000000000003','60000000-0000-0000-0000-000000000030','2025-01-01 00:00:00.000000',NULL);";
            c.ExecuteNonQuery();
        },
        prerequisite: c =>
        {
            if (!RequiredTablesExist(
                c,
                "idt_Products",
                "idt_Capabilities",
                "idt_Roles",
                "idt_RoleCapabilityAssignments"))
                return false;

            // Only proceed if the DDL column was already applied (partial-commit scenario).
            c.CommandText = @"
                SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE()
                  AND TABLE_NAME   = 'idt_Capabilities'
                  AND COLUMN_NAME  = 'Category';";
            return Convert.ToInt64(c.ExecuteScalar()) > 0;
        },
        warningMessage:
            "LS-ID-TNT-011: AddTenantPermissionCatalog DDL was partially committed " +
            "by a prior run. Seeding data idempotently and marking migration as applied.");

    conn.Close();
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "LS-ID-TNT-011 pre-migration guard failed — proceeding with normal migration");
}

// ── LS-ID-SUP-002 pre-migration guard ────────────────────────────────────────
// The three 20260426 migrations seed support roles and back-fill ScopedRole-
// Assignments. Their SQL is idempotent (INSERT IGNORE / UPDATE … WHERE), so
// re-running it is safe; however EF will attempt to apply them on EVERY startup
// until they appear in __EFMigrationsHistory. If a prior startup ran their SQL
// via the LS-ID-SUP-001 guard but the EF Migrate() call subsequently failed,
// the history never advanced and the cycle repeats.
//
// This guard runs each migration's SQL idempotently, then inserts any missing
// history rows so that EF's Migrate() finds nothing to do for these three entries.
// (LS-ID-SUP-001, the earlier belt-and-suspenders guard, has been removed now that
// these migrations are correctly recorded in __EFMigrationsHistory on every startup.)
try
{
    using var scope = app.Services.CreateScope();
    var db   = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
    var conn = db.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) conn.Open();
    using var cmd = conn.CreateCommand();

    const string PlatformTenantId  = "20000000-0000-0000-0000-000000000001";
    const string RolePlatformAdmin = "30000000-0000-0000-0000-000000000001";
    const string RoleTenantAdmin   = "30000000-0000-0000-0000-000000000002";
    const string RoleSupportAdmin     = "30000000-0000-0000-0000-000000000011";
    const string RoleSupportManager   = "30000000-0000-0000-0000-000000000012";
    const string RoleSupportAgent     = "30000000-0000-0000-0000-000000000013";
    const string RoleTenantUser       = "30000000-0000-0000-0000-000000000014";
    const string RoleExternalCustomer = "30000000-0000-0000-0000-000000000015";
    const string EfVersion = "8.0.7";

    // ── Migration 20260426000001_SeedSupportRoles ──────────────────────────
    var mig1Recorded = Identity.Infrastructure.StartupMigrationGuard.ApplyIfMissing(
        cmd,
        migrationId: "20260426000001_SeedSupportRoles",
        efVersion:   EfVersion,
        logger:      app.Logger,
        guardLabel:  "LS-ID-SUP-002",
        prerequisite: c => RequiredTablesExist(
            c,
            "idt_Roles",
            "idt_ScopedRoleAssignments",
            "idt_Users",
            "idt_UserTenants",
            "idt_UserOrganizationMemberships",
            "idt_Organizations"),
        apply: c =>
        {
            c.CommandText = $@"
INSERT IGNORE INTO `idt_Roles`
    (`Id`, `TenantId`, `Name`, `Description`, `IsSystemRole`, `Scope`, `CreatedAtUtc`, `UpdatedAtUtc`)
VALUES
    ('{RoleSupportAdmin}',    '{PlatformTenantId}', 'SupportAdmin',     'Full support administration — manages queues, SLAs and settings',         1, 'Support',  '2024-01-01 00:00:00', '2024-01-01 00:00:00'),
    ('{RoleSupportManager}',  '{PlatformTenantId}', 'SupportManager',   'Support team manager — escalations, reporting and agent oversight',        1, 'Support',  '2024-01-01 00:00:00', '2024-01-01 00:00:00'),
    ('{RoleSupportAgent}',    '{PlatformTenantId}', 'SupportAgent',     'Frontline support agent — handles and responds to tickets',                1, 'Support',  '2024-01-01 00:00:00', '2024-01-01 00:00:00'),
    ('{RoleTenantUser}',      '{PlatformTenantId}', 'TenantUser',       'Regular authenticated tenant user — read-only support access',            1, 'Tenant',   '2024-01-01 00:00:00', '2024-01-01 00:00:00'),
    ('{RoleExternalCustomer}','{PlatformTenantId}', 'ExternalCustomer', 'External customer — may view and comment on their own support tickets',   0, 'External', '2024-01-01 00:00:00', '2024-01-01 00:00:00');";
            c.ExecuteNonQuery();

            c.CommandText = $@"
INSERT IGNORE INTO `idt_ScopedRoleAssignments`
    (`Id`, `UserId`, `RoleId`, `ScopeType`, `TenantId`,
     `OrganizationId`, `OrganizationRelationshipId`, `ProductId`,
     `IsActive`, `AssignedAtUtc`, `UpdatedAtUtc`, `AssignedByUserId`)
SELECT UUID(), u.`Id`,
    CASE WHEN ut.`TenantId` = '{PlatformTenantId}' THEN '{RolePlatformAdmin}' ELSE '{RoleTenantAdmin}' END,
    'GLOBAL', ut.`TenantId`, NULL, NULL, NULL,
    1, u.`CreatedAtUtc`, u.`CreatedAtUtc`, NULL
FROM `idt_Users` u
INNER JOIN `idt_UserTenants` ut
        ON ut.`Id` = (
            SELECT ut2.`Id`
            FROM `idt_UserTenants` ut2
            WHERE ut2.`UserId` = u.`Id` AND ut2.`IsActive` = 1
            ORDER BY ut2.`JoinedAtUtc`, ut2.`Id`
            LIMIT 1
        )
WHERE u.`IsActive` = 1
  AND NOT EXISTS (
    SELECT 1 FROM `idt_ScopedRoleAssignments` sra
    WHERE sra.`UserId` = u.`Id` AND sra.`ScopeType` = 'GLOBAL' AND sra.`IsActive` = 1
  )
  -- Exclude CareConnect-only primary org users (PROVIDER = receiver, LAW_FIRM = referrer).
  AND NOT EXISTS (
    SELECT 1
    FROM   `idt_UserOrganizationMemberships` uom
    JOIN   `idt_Organizations` o ON o.`Id` = uom.`OrganizationId`
    WHERE  uom.`UserId`    = u.`Id`
      AND  uom.`IsPrimary` = 1
      AND  uom.`IsActive`  = 1
      AND  o.`OrgType`     IN ('PROVIDER', 'LAW_FIRM')
  );";
            c.ExecuteNonQuery();
        });

    // ── Migration 20260426000002_FixSupportRolesBackfill ──────────────────
    var mig2Recorded = Identity.Infrastructure.StartupMigrationGuard.ApplyIfMissing(
        cmd,
        migrationId: "20260426000002_FixSupportRolesBackfill",
        efVersion:   EfVersion,
        logger:      app.Logger,
        guardLabel:  "LS-ID-SUP-002",
        prerequisite: c => RequiredTablesExist(
            c,
            "idt_ScopedRoleAssignments",
            "idt_Users",
            "idt_UserTenants",
            "idt_UserOrganizationMemberships",
            "idt_Organizations"),
        apply: c =>
        {
            c.CommandText = $@"
INSERT IGNORE INTO `idt_ScopedRoleAssignments`
    (`Id`, `UserId`, `RoleId`, `ScopeType`, `TenantId`,
     `OrganizationId`, `OrganizationRelationshipId`, `ProductId`,
     `IsActive`, `AssignedAtUtc`, `UpdatedAtUtc`, `AssignedByUserId`)
SELECT UUID(), u.`Id`,
    CASE WHEN ut.`TenantId` = '{PlatformTenantId}' THEN '{RolePlatformAdmin}' ELSE '{RoleTenantAdmin}' END,
    'GLOBAL', ut.`TenantId`, NULL, NULL, NULL,
    1, u.`CreatedAtUtc`, u.`CreatedAtUtc`, NULL
FROM `idt_Users` u
INNER JOIN `idt_UserTenants` ut
        ON ut.`Id` = (
            SELECT ut2.`Id`
            FROM `idt_UserTenants` ut2
            WHERE ut2.`UserId` = u.`Id` AND ut2.`IsActive` = 1
            ORDER BY ut2.`JoinedAtUtc`, ut2.`Id`
            LIMIT 1
        )
WHERE u.`IsActive` = 1
  AND NOT EXISTS (
    SELECT 1 FROM `idt_ScopedRoleAssignments` sra
    WHERE sra.`UserId` = u.`Id` AND sra.`ScopeType` = 'GLOBAL' AND sra.`IsActive` = 1
  )
  -- Exclude CareConnect-only primary org users (PROVIDER = receiver, LAW_FIRM = referrer).
  AND NOT EXISTS (
    SELECT 1
    FROM   `idt_UserOrganizationMemberships` uom
    JOIN   `idt_Organizations` o ON o.`Id` = uom.`OrganizationId`
    WHERE  uom.`UserId`    = u.`Id`
      AND  uom.`IsPrimary` = 1
      AND  uom.`IsActive`  = 1
      AND  o.`OrgType`     IN ('PROVIDER', 'LAW_FIRM')
  );";
            c.ExecuteNonQuery();
        });

    // ── Migration 20260426000003_CorrectPlatformAdminRole ─────────────────
    var mig3Recorded = Identity.Infrastructure.StartupMigrationGuard.ApplyIfMissing(
        cmd,
        migrationId: "20260426000003_CorrectPlatformAdminRole",
        efVersion:   EfVersion,
        logger:      app.Logger,
        guardLabel:  "LS-ID-SUP-002",
        prerequisite: c => RequiredTablesExist(
            c,
            "idt_ScopedRoleAssignments",
            "idt_UserTenants",
            "idt_Users"),
        apply: c =>
        {
            c.CommandText = $@"
UPDATE `idt_ScopedRoleAssignments` sra
INNER JOIN `idt_UserTenants` ut ON ut.`UserId` = sra.`UserId`
SET   sra.`RoleId`       = '{RolePlatformAdmin}',
      sra.`UpdatedAtUtc` = UTC_TIMESTAMP()
WHERE ut.`TenantId`          = '{PlatformTenantId}'
  AND ut.`IsActive`          = 1
  AND sra.`ScopeType`        = 'GLOBAL'
  AND sra.`IsActive`         = 1
  AND sra.`RoleId`           = '{RoleTenantAdmin}'
  AND sra.`AssignedByUserId` IS NULL;";
            c.ExecuteNonQuery();

            c.CommandText = $@"
INSERT IGNORE INTO `idt_ScopedRoleAssignments`
    (`Id`, `UserId`, `RoleId`, `ScopeType`, `TenantId`,
     `OrganizationId`, `OrganizationRelationshipId`, `ProductId`,
     `IsActive`, `AssignedAtUtc`, `UpdatedAtUtc`, `AssignedByUserId`)
SELECT UUID(), u.`Id`, '{RolePlatformAdmin}', 'GLOBAL', ut.`TenantId`,
       NULL, NULL, NULL, 1, u.`CreatedAtUtc`, u.`CreatedAtUtc`, NULL
FROM `idt_Users` u
INNER JOIN `idt_UserTenants` ut
        ON ut.`UserId` = u.`Id`
       AND ut.`TenantId` = '{PlatformTenantId}'
       AND ut.`IsActive` = 1
WHERE 1 = 1
  AND u.`IsActive` = 1
  AND NOT EXISTS (
    SELECT 1 FROM `idt_ScopedRoleAssignments` sra2
    WHERE sra2.`UserId` = u.`Id` AND sra2.`ScopeType` = 'GLOBAL' AND sra2.`IsActive` = 1
  );";
            c.ExecuteNonQuery();
        });

    // ── Migration 20260426100001_SeedPlatformAdminUser ───────────────────
    // Inserts the platform admin user (admin@legalsynq.com / Admin123!) if not
    // already present.  The SeedAdminOrgMembership EF migration ran a SELECT-
    // based INSERT that silently inserted 0 rows when no user existed yet.
    var mig4Recorded = Identity.Infrastructure.StartupMigrationGuard.ApplyIfMissing(
        cmd,
        migrationId: "20260426100001_SeedPlatformAdminUser",
        efVersion:   EfVersion,
        logger:      app.Logger,
        guardLabel:  "LS-ID-ADM-001",
        prerequisite: c => RequiredTablesExist(
            c,
            "idt_Users",
            "idt_UserTenants",
            "idt_UserOrganizationMemberships",
            "idt_ScopedRoleAssignments"),
        apply: c =>
        {
            const string AdminUserId   = "50000000-0000-0000-0000-000000000001";
            const string AdminPwHash   = "$2a$12$/wvZFZf.T4qlqcaD9gn5GOKmjvXHCbr3/wUXu4wtRwLzj4W4XXA2a";
            const string OrgId         = "40000000-0000-0000-0000-000000000001";
            const string OrgMemId      = "40000000-0000-0000-0000-000000000003";
            const string SraId         = "90000000-0000-0000-0000-000000000001";

            c.CommandText = $@"
INSERT IGNORE INTO `idt_Users`
    (`Id`, `Email`, `PasswordHash`,
     `FirstName`, `LastName`, `IsActive`,
     `IsLocked`, `UserType`,
     `CreatedAtUtc`, `UpdatedAtUtc`)
VALUES (
    '{AdminUserId}',
    'admin@legalsynq.com', '{AdminPwHash}',
    'Platform', 'Admin', 1, 0, 'PlatformInternal',
    '2024-01-01 00:00:00', '2024-01-01 00:00:00'
);";
            c.ExecuteNonQuery();

            c.CommandText = $@"
INSERT IGNORE INTO `idt_UserTenants`
    (`Id`, `UserId`, `TenantId`, `IsActive`, `JoinedAtUtc`)
VALUES (
    UUID(), '{AdminUserId}', '{PlatformTenantId}', 1, '2024-01-01 00:00:00'
);";
            c.ExecuteNonQuery();

            c.CommandText = $@"
INSERT IGNORE INTO `idt_UserOrganizationMemberships`
    (`Id`, `UserId`, `OrganizationId`, `MemberRole`,
     `IsActive`, `JoinedAtUtc`, `GrantedByUserId`)
VALUES (
    '{OrgMemId}', '{AdminUserId}', '{OrgId}',
    'OWNER', 1, '2024-01-01 00:00:00', NULL
);";
            c.ExecuteNonQuery();

            c.CommandText = $@"
INSERT IGNORE INTO `idt_ScopedRoleAssignments`
    (`Id`, `UserId`, `RoleId`, `ScopeType`, `TenantId`,
     `OrganizationId`, `OrganizationRelationshipId`, `ProductId`,
     `IsActive`, `AssignedAtUtc`, `UpdatedAtUtc`, `AssignedByUserId`)
VALUES (
    '{SraId}', '{AdminUserId}', '{RolePlatformAdmin}', 'GLOBAL', '{PlatformTenantId}',
    NULL, NULL, NULL, 1,
    '2024-01-01 00:00:00', '2024-01-01 00:00:00', NULL
);";
            c.ExecuteNonQuery();
        });

    conn.Close();

    if (mig1Recorded && mig2Recorded && mig3Recorded && mig4Recorded)
    {
        app.Logger.LogInformation(
            "LS-ID-SUP-002: all four 20260426 migrations already recorded in EF history — no action needed.");
    }
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "LS-ID-SUP-002 pre-migration guard failed — proceeding with normal migration");
}

// ── LS-ID-CC-001: Strip wrongly-backfilled TenantAdmin from CC-only users ─
// Migration 20260426000001 assigned TenantAdmin to every user with no GLOBAL
// ScopedRoleAssignment, including PROVIDER-type org users who are CareConnect-
// only and must be gated to the CC common portal only.  This guard runs the
// corrective UPDATE as a one-time idempotent operation.  The EF migration
// 20260530000001_RemoveTenantAdminFromCcOnlyUsers contains the same logic and
// will apply it for clean DB installs; this guard covers already-running DBs.
try
{
    using var scope2 = app.Services.CreateScope();
    var db2  = scope2.ServiceProvider.GetRequiredService<IdentityDbContext>();
    var conn2 = db2.Database.GetDbConnection();
    if (conn2.State != System.Data.ConnectionState.Open) conn2.Open();
    using var cmd2 = conn2.CreateCommand();
    Identity.Infrastructure.StartupMigrationGuard.ApplyIfMissing(
        cmd2,
        migrationId: "20260530000001_RemoveTenantAdminFromCcOnlyUsers",
        efVersion:   "8.0.7",
        logger:      app.Logger,
        guardLabel:  "LS-ID-CC-001",
        prerequisite: c => RequiredTablesExist(
            c,
            "idt_ScopedRoleAssignments",
            "idt_UserOrganizationMemberships",
            "idt_Organizations"),
        apply: c =>
        {
            // SQL is defined once on the migration class to avoid duplication.
            c.CommandText = RemoveTenantAdminFromCcOnlyUsers.UpSql;
            c.ExecuteNonQuery();
        });
    // conn2 is managed by db2/scope2 — no manual close needed.
}
catch (Exception ex)
{
    // LogError: a failure here means CC-only users may still access the tenant portal.
    app.Logger.LogError(ex, "LS-ID-CC-001 CC-user GLOBAL role cleanup guard failed — proceeding");
}


try
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
    db.Database.Migrate();
    app.Logger.LogInformation("Database migrations applied");
}
catch (Exception ex)
{
    if (app.Environment.IsDevelopment())
    {
        app.Logger.LogWarning(ex, "Could not apply migrations — ensure MySQL is running and connection string is correct");
    }
    else
    {
        app.Logger.LogCritical(ex, "Identity database migrations failed — service cannot start with an out-of-sync schema.");
        throw;
    }
}

// ── Migration coverage self-test ─────────────────────────────────────────
// Compares every EF-mapped column against information_schema. If a model
// property has no backing column on the live database, log an ERROR so the
// regression is loud at boot (and visible to CI / log aggregation).
// This catches the class of bug behind Task #58: a migration committed
// without its [Migration] attribute (or otherwise un-applied) leaves the
// EF model and the live schema out of sync, which previously surfaced only
// as runtime "Unknown column" SQL errors at login. Runs after Migrate()
// so anything still missing is a true gap.
try
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
    await BuildingBlocks.Diagnostics.MigrationCoverageProbe.RunAsync(db, app.Logger);
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "Migration coverage self-test could not run");
}

// ── Startup diagnostic: Phase G authorization status ─────────────────────────
// Phase G COMPLETE: UserRoles + UserRoleAssignments tables dropped.
// Diagnostics verify OrgTypeRule coverage and ScopedRoleAssignment counts.
try
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();

    // 1. Verify all restricted ProductRoles have OrgTypeRule coverage.
    var unrestrictedRoleCount = await db.Set<Identity.Domain.ProductRole>()
        .Where(pr => pr.IsActive)
        .Where(pr => !db.Set<Identity.Domain.ProductOrganizationTypeRule>()
            .Any(r => r.ProductRoleId == pr.Id && r.IsActive))
        .CountAsync();

    if (unrestrictedRoleCount > 0)
    {
        app.Logger.LogWarning(
            "{Count} active ProductRole(s) have no ProductOrganizationTypeRule rows and are " +
            "therefore unrestricted. If restriction was intended, add OrgTypeRule seed data.",
            unrestrictedRoleCount);
    }
    else
    {
        var totalActive = await db.Set<Identity.Domain.ProductRole>().CountAsync(pr => pr.IsActive);
        app.Logger.LogInformation(
            "Phase G eligibility check passed — {Count} active ProductRole(s), all OrgTypeRule-covered.",
            totalActive);
    }

    // 2. Log ScopedRoleAssignment totals (Phase G: sole authoritative role source).
    var scopedTotal = await db.ScopedRoleAssignments
        .CountAsync(s => s.IsActive && s.ScopeType == "GLOBAL");
    var scopedUsers = await db.ScopedRoleAssignments
        .Where(s => s.IsActive && s.ScopeType == "GLOBAL")
        .Select(s => s.UserId)
        .Distinct()
        .CountAsync();
    app.Logger.LogInformation(
        "Phase G role check: {Assignments} active GLOBAL ScopedRoleAssignment(s) across {Users} user(s).",
        scopedTotal, scopedUsers);

    // 3. Org type consistency: flag organizations where OrganizationTypeId FK is null
    //    but OrgType string is populated, or where the FK code doesn't match the string.
    var orgTypeCheckRows = await db.Organizations
        .Where(o => o.IsActive)
        .Select(o => new { o.Id, o.OrgType, o.OrganizationTypeId })
        .ToListAsync();

    var orgsWithMissingTypeId = orgTypeCheckRows
        .Count(o => o.OrganizationTypeId == null && !string.IsNullOrWhiteSpace(o.OrgType));

    var orgsWithCodeMismatch = orgTypeCheckRows
        .Count(o =>
        {
            if (o.OrganizationTypeId == null) return false;
            var expectedCode = Identity.Domain.OrgTypeMapper.TryResolveCode(o.OrganizationTypeId);
            return expectedCode is not null &&
                   !string.Equals(expectedCode, o.OrgType, StringComparison.OrdinalIgnoreCase);
        });

    if (orgsWithMissingTypeId > 0)
        app.Logger.LogWarning(
            "OrgType consistency: {Count} active org(s) have OrgType string but no OrganizationTypeId. " +
            "Run a backfill migration (or update via Organization.Create) to populate the FK.",
            orgsWithMissingTypeId);

    if (orgsWithCodeMismatch > 0)
        app.Logger.LogWarning(
            "OrgType consistency: {Count} active org(s) have an OrganizationTypeId whose OrgTypeMapper " +
            "code does not match the stored OrgType string. Investigate and reconcile.",
            orgsWithCodeMismatch);

    if (orgsWithMissingTypeId == 0 && orgsWithCodeMismatch == 0)
        app.Logger.LogInformation(
            "OrgType consistency check passed — {Total} active org(s) all have consistent OrganizationTypeId and OrgType.",
            orgTypeCheckRows.Count);
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex,
        "Phase G startup diagnostic skipped — could not query the database at startup.");
}

// ── UIX-002-C: Ensure each active ProductRole has a corresponding entry in the Roles table ──
// Product roles are defined in the ProductRoles table but need corresponding entries in the
// Roles table (with IsSystemRole = false) so they can be assigned through ScopedRoleAssignment.
// This seeder is idempotent — it only creates missing entries.
try
{
    using var prScope = app.Services.CreateScope();
    var prDb = prScope.ServiceProvider.GetRequiredService<IdentityDbContext>();

    var activeProductRoles = await prDb.ProductRoles
        .Include(pr => pr.Product)
        .Where(pr => pr.IsActive)
        .ToListAsync();

    var existingRoleNames = await prDb.Roles
        .Select(r => r.Name)
        .ToListAsync();

    var existingNameSet = new HashSet<string>(existingRoleNames, StringComparer.OrdinalIgnoreCase);

    var platformTenantId = await prDb.Tenants
        .Where(t => t.IsActive)
        .OrderBy(t => t.CreatedAtUtc)
        .Select(t => t.Id)
        .FirstOrDefaultAsync();

    if (platformTenantId == Guid.Empty)
    {
        app.Logger.LogWarning("UIX-002-C: No active tenant found — skipping product role seed.");
    }

    var created = 0;
    foreach (var pr in activeProductRoles)
    {
        if (platformTenantId == Guid.Empty)
            break;

        if (existingNameSet.Contains(pr.Code))
            continue;

        var role = Identity.Domain.Role.Create(
            tenantId: platformTenantId,
            name: pr.Code,
            description: $"[Product] {pr.Name} — {pr.Description ?? pr.Product.Name}",
            isSystemRole: false);
        prDb.Roles.Add(role);
        existingNameSet.Add(pr.Code);
        created++;

        app.Logger.LogInformation(
            "UIX-002-C: Seeded Role '{RoleName}' for ProductRole {ProductRoleCode} (Product: {Product})",
            pr.Code, pr.Code, pr.Product.Name);
    }

    if (created > 0)
        await prDb.SaveChangesAsync();

    app.Logger.LogInformation(
        "UIX-002-C product role sync complete — {Created} new Role(s) seeded, {Total} product roles active.",
        created, activeProductRoles.Count);
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "UIX-002-C product role seed encountered an error");
}

// ── Dev-only: ensure OrganizationProduct rows exist ──────────────────────
if (app.Environment.IsDevelopment())
{
    try
    {
        using var fixScope = app.Services.CreateScope();
        var fixDb = fixScope.ServiceProvider.GetRequiredService<IdentityDbContext>();

        // Also ensure OrganizationProduct rows exist for every active TenantProduct + Org pair
        var tenantProducts = await fixDb.Set<Identity.Domain.TenantProduct>()
            .Where(tp => tp.IsEnabled)
            .ToListAsync();

        foreach (var tp in tenantProducts)
        {
            var orgs = await fixDb.Organizations
                .Include(o => o.OrganizationProducts)
                .Where(o => o.TenantId == tp.TenantId && o.IsActive)
                .ToListAsync();

            foreach (var org in orgs)
            {
                if (org.OrganizationProducts.Any(op => op.ProductId == tp.ProductId))
                    continue;

                var op = Identity.Domain.OrganizationProduct.Create(org.Id, tp.ProductId);
                fixDb.Set<Identity.Domain.OrganizationProduct>().Add(op);

                app.Logger.LogInformation(
                    "Dev fixup: created OrganizationProduct for org {OrgId} ({OrgName}) → product {ProductId}",
                    org.Id, org.Name, tp.ProductId);
            }
        }

        await fixDb.SaveChangesAsync();
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Dev fixup for user/org memberships encountered an error");
    }
}

// ── Middleware pipeline ───────────────────────────────────────────────────
// Security headers
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"]        = "DENY";
    ctx.Response.Headers["X-XSS-Protection"]       = "0";
    ctx.Response.Headers["Referrer-Policy"]        = "strict-origin-when-cross-origin";
    await next();
});

app.UseRateLimiter();

// UseAuthentication + UseAuthorization must precede all endpoint maps
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () =>
    Results.Ok(new HealthResponse("ok", ServiceName)));

app.MapGet("/info", () =>
    Results.Ok(new InfoResponse(ServiceName, env, Version)));

app.MapTenantEndpoints();
app.MapProductEndpoints();
app.MapUserEndpoints();
app.MapAuthEndpoints();
app.MapTenantBrandingEndpoints();
app.MapAdminEndpoints();
app.MapTenantProvisioningEndpoints();
app.MapUserMembershipEndpoints();      // BLK-ID-02
app.MapLawFirmUserManagementEndpoints(); // LSV3-1083
app.MapAccessSourceEndpoints();
app.MapGroupEndpoints();
app.MapPermissionCatalogEndpoints();
app.MapDeviceSessionEndpoints();

app.Run();

static bool RequiredTablesExist(System.Data.IDbCommand cmd, params string[] tableNames)
{
    cmd.CommandText = $@"
        SELECT COUNT(*)
        FROM information_schema.tables
        WHERE table_schema = DATABASE()
          AND table_name IN ({string.Join(", ", tableNames.Select(t => $"'{t}'"))});";

    return Convert.ToInt64(cmd.ExecuteScalar()) == tableNames.Length;
}

static bool AnyTableExists(System.Data.IDbCommand cmd, params string[] tableNames) =>
    tableNames.Any(tableName => TableExists(cmd, tableName));

static void EnsureTable(System.Data.IDbCommand cmd, string tableName, string createSql)
{
    if (TableExists(cmd, tableName))
        return;

    cmd.CommandText = createSql;
    cmd.ExecuteNonQuery();
}

static void EnsureColumn(System.Data.IDbCommand cmd, string tableName, string columnName, string alterSql)
{
    if (ColumnExists(cmd, tableName, columnName))
        return;

    cmd.CommandText = alterSql;
    cmd.ExecuteNonQuery();
}

static void EnsureColumns(
    System.Data.IDbCommand cmd,
    string tableName,
    params (string ColumnName, string AlterSql)[] columns)
{
    foreach (var (columnName, alterSql) in columns)
        EnsureColumn(cmd, tableName, columnName, alterSql);
}

static void EnsureIndex(System.Data.IDbCommand cmd, string tableName, string indexName, string createSql)
{
    if (IndexExists(cmd, tableName, indexName))
        return;

    cmd.CommandText = createSql;
    cmd.ExecuteNonQuery();
}

static bool TableExists(System.Data.IDbCommand cmd, string tableName)
{
    cmd.CommandText = $@"
        SELECT COUNT(*)
        FROM information_schema.tables
        WHERE table_schema = DATABASE()
          AND table_name = '{tableName}';";

    return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
}

static bool ColumnExists(System.Data.IDbCommand cmd, string tableName, string columnName)
{
    cmd.CommandText = $@"
        SELECT COUNT(*)
        FROM information_schema.columns
        WHERE table_schema = DATABASE()
          AND table_name = '{tableName}'
          AND column_name = '{columnName}';";

    return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
}

static bool IndexExists(System.Data.IDbCommand cmd, string tableName, string indexName)
{
    cmd.CommandText = $@"
        SELECT COUNT(*)
        FROM information_schema.statistics
        WHERE table_schema = DATABASE()
          AND table_name = '{tableName}'
          AND index_name = '{indexName}';";

    return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
}
