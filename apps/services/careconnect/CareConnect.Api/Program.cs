using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using BuildingBlocks;
using BuildingBlocks.Authorization;
using BuildingBlocks.Authentication.ServiceTokens;
using BuildingBlocks.Context;
using BuildingBlocks.FlowClient;
using CareConnect.Api.Endpoints;
using CareConnect.Api.Middleware;
using CareConnect.Api.Options;
using CareConnect.Application.DTOs;
using CareConnect.Application.Interfaces;
using CareConnect.Infrastructure;
using CareConnect.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// JWT Authentication
var jwtSection = builder.Configuration.GetSection("Jwt");
var signingKey = jwtSection["SigningKey"]
    ?? throw new InvalidOperationException("Jwt:SigningKey is not configured.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer           = true,
            ValidateAudience         = true,
            ValidateLifetime         = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer              = jwtSection["Issuer"],
            ValidAudience            = jwtSection["Audience"],
            IssuerSigningKey         = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)) { KeyId = ServiceTokenAuthenticationDefaults.UserTokenKeyId },
            RoleClaimType            = "role"
        };
    })
    // M2M service-token bearer — validates HS256 tokens minted by platform services.
    // Secret is read from FLOW_SERVICE_TOKEN_SECRET env var (see ServiceTokenAuthenticationDefaults).
    .AddServiceTokenBearer(builder.Configuration);

// Authorization policies
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(Policies.AuthenticatedUser, policy =>
        policy.RequireAuthenticatedUser());

    options.AddPolicy(Policies.PlatformOrTenantAdmin, policy =>
        policy.RequireRole(Roles.PlatformAdmin, Roles.TenantAdmin));

    // Internal M2M endpoints — only accept service tokens (not user JWTs).
    options.AddPolicy("ServiceOnly", policy =>
        policy
            .AddAuthenticationSchemes(ServiceTokenAuthenticationDefaults.Scheme)
            .RequireRole(ServiceTokenAuthenticationDefaults.ServiceRole));
});

// Infrastructure (DbContext + repositories + services)
builder.Services.AddInfrastructure(builder.Configuration);
// LS-FLOW-MERGE-P4 — shared Flow HTTP adapter (bearer pass-through, retry, 503 mapping).
builder.Services.AddFlowClient(builder.Configuration, serviceName: "careconnect");

// Upload validation limits — bound from "AttachmentUpload" section of appsettings.json.
builder.Services.Configure<AttachmentUploadOptions>(
    builder.Configuration.GetSection(AttachmentUploadOptions.SectionName));

// Set Kestrel's request body size limit and ASP.NET's multipart body length limit
// well above the configured upload ceiling so that oversized-but-realistic uploads
// always reach our handler and receive a custom 400 error, rather than being cut
// off by the framework with a bare 413/400.  The application-level check in
// AttachmentEndpoints is the authoritative gate.
// A hard backstop of 512 MB still protects against truly absurd payloads.
{
    var uploadSection = builder.Configuration.GetSection(AttachmentUploadOptions.SectionName);
    var configuredMax = uploadSection.GetValue<long?>("MaxFileSizeBytes")
                        ?? new AttachmentUploadOptions().MaxFileSizeBytes;
    const long backstopBytes = 512L * 1024 * 1024;
    var effectiveLimit = Math.Max(configuredMax * 10, backstopBytes);

    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        kestrel.Limits.MaxRequestBodySize = effectiveLimit;
    });

    // ASP.NET Core's multipart parser enforces its own separate length limit
    // (default ~128 MB). Align it with the same backstop so it doesn't reject
    // uploads before endpoint code can return a meaningful error.
    builder.Services.Configure<FormOptions>(form =>
    {
        form.MultipartBodyLengthLimit = effectiveLimit;
    });
}

// Request context
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentRequestContext, CurrentRequestContext>();

// BLK-SEC-05: Forwarded-headers trust — the YARP gateway is the only upstream
// proxy in this deployment. Trusting X-Forwarded-For rewrites
// context.Connection.RemoteIpAddress to the real client IP, which is then used
// as the rate-limiter partition key so per-client limits work correctly.
//
// ReverseProxy:KnownProxyIp (env: ReverseProxy__KnownProxyIp) should be set to
// the gateway pod/container IP in production. Without it, the middleware falls
// back to ASP.NET Core's default of trusting loopback only, which is safe but
// means X-Forwarded-For is ignored when the proxy is non-loopback.
builder.Services.Configure<ForwardedHeadersOptions>(opts =>
{
    opts.ForwardedHeaders = ForwardedHeaders.XForwardedFor;

    var knownProxyIp = builder.Configuration["ReverseProxy:KnownProxyIp"];
    if (!string.IsNullOrWhiteSpace(knownProxyIp) && IPAddress.TryParse(knownProxyIp, out var proxyIp))
    {
        opts.KnownProxies.Add(proxyIp);
    }
    // No else-branch: without a configured proxy IP, ASP.NET Core's default
    // loopback-only trust applies. This is safe in all environments.
});

// CC2-INT-B08: Rate limiting for the public referral endpoint.
// Fixed window: 10 submissions per minute per IP address.
// Rejected requests receive 429 Too Many Requests.
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("public-referral-limit", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit          = 10,
                Window               = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit           = 0,
            }));

    // BLK-SEC-04: Rate limiting for public network read endpoints.
    // Sliding window: 60 requests per minute per IP, split into 4 segments of 15 s.
    // Prevents high-volume enumeration of the provider directory.
    options.AddPolicy("public-read-limit", context =>
        RateLimitPartition.GetSlidingWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit          = 60,
                Window               = TimeSpan.FromMinutes(1),
                SegmentsPerWindow    = 4,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit           = 0,
            }));

    // BLK-SEC-04b: Tighter limit for /api/public/referrer-status.
    // This endpoint probes whether an email address is a registered referrer;
    // 60/min would allow bulk email enumeration. 20/min is sufficient for the
    // post-referral UX (one check per submission) while blocking enumeration attempts.
    options.AddPolicy("referrer-status-limit", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit          = 20,
                Window               = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit           = 0,
            }));

    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

// ── BLK-OPS-01: Production fail-fast (supersedes BLK-SEC-01 inline checks) ────
// Validates all required secrets and service URLs before any requests are accepted.
// Uses RuntimeConfigValidator for consistent error messages and placeholder detection.
if (!builder.Environment.IsDevelopment())
{
    var v = new RuntimeConfigValidator(builder.Configuration, "careconnect");
    v
        // JWT signing key must be real — not a placeholder
        .RequireNotPlaceholder("Jwt:SigningKey")
        // Trust boundary secret — must match Gateway and Web BFF
        .RequireNonEmpty("PublicTrustBoundary:InternalRequestSecret")
        .RequireNotPlaceholder("PublicTrustBoundary:InternalRequestSecret")
        // Service URLs — must be absolute URLs in production
        .RequireAbsoluteUrl("TenantService:BaseUrl")
        .RequireAbsoluteUrl("IdentityService:BaseUrl")
        // Provisioning tokens — required for CareConnect → Tenant and Identity calls
        .RequireNonEmpty("TenantService:ProvisioningToken")
        .RequireNonEmpty("IdentityService:ProvisioningToken")
        // Database connection string — must not contain placeholder password
        .RequireConnectionString("ConnectionStrings:CareConnectDb");
}

var app = builder.Build();

var referralRuntimeOptions = ReferralRuntimeOptions.FromConfiguration(builder.Configuration);
app.Logger.LogInformation(
    "Referral token configuration loaded. SecretConfigured={SecretConfigured} UsingDevFallbackSecret={UsingDevFallbackSecret} AppBaseUrl={AppBaseUrl} AppBaseDomainConfigured={AppBaseDomainConfigured}",
    !string.IsNullOrWhiteSpace(builder.Configuration["ReferralToken:Secret"]),
    referralRuntimeOptions.UsingDevelopmentFallbackSecret,
    referralRuntimeOptions.AppBaseUrl,
    !string.IsNullOrWhiteSpace(referralRuntimeOptions.AppBaseDomain));

// Auto-migrate — apply pending EF Core migrations on startup in all environments.
// CareConnect uses MySQL (RDS) and the __EFMigrationsHistory table tracks which
// migrations have already been applied, so this is safe and idempotent.
// Fail fast if migrations cannot be applied — serving traffic with an incompatible
// schema causes silent 500s and process crashes that are harder to diagnose.
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<CareConnectDbContext>();

    // ── Schema divergence repair (CC2-INT-B07) ────────────────────────────
    // Guards against a known RDS state where __EFMigrationsHistory records
    // migrations as applied but the actual DDL was never executed.
    // Uses idempotent DDL (CREATE TABLE IF NOT EXISTS / ADD COLUMN IF NOT EXISTS)
    // to guarantee the B06+ schema objects exist before handing off to EF.
    // Wrapped in try/catch so a transient DB error during repair does not
    // prevent EF Core Migrate() from running (schema repair is advisory).
    try
    {
        await EnsureSchemaObjectsAsync(db, app.Logger);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "EnsureSchemaObjects schema repair failed — proceeding with EF Core Migrate()");
    }

    db.Database.Migrate();
    app.Logger.LogInformation("CareConnect database migrations applied successfully.");
}

// ── Referral Attribution seed (idempotent) ────────────────────────────────
// Configured via ReferralAttributionSeed:TenantCode in appsettings/secrets — empty
// (the default) is a no-op, so this never runs unless an environment explicitly
// configures the tenant that should receive the initial attribution (e.g. the
// CareConnect tenant that hired Cam Perry). Resolves the tenant by code via the
// Tenant service rather than a hardcoded GUID, so the same config works across
// dev/staging/prod without editing code per environment. Safe to run on every
// startup — ReferralAttributionService.SeedAsync checks-then-creates on
// (TenantId, Code) and no-ops if already seeded.
{
    var seedTenantCode = builder.Configuration["ReferralAttributionSeed:TenantCode"];
    if (!string.IsNullOrWhiteSpace(seedTenantCode))
    {
        try
        {
            using var scope = app.Services.CreateScope();
            var tenantClient = scope.ServiceProvider.GetRequiredService<ITenantServiceClient>();
            var attributionService = scope.ServiceProvider.GetRequiredService<IReferralAttributionService>();

            var seedTenantId = await tenantClient.ResolveTenantIdByCodeAsync(seedTenantCode, CancellationToken.None);
            if (seedTenantId is null)
            {
                app.Logger.LogWarning(
                    "ReferralAttributionSeed: tenant code '{TenantCode}' did not resolve — skipping seed.", seedTenantCode);
            }
            else
            {
                await attributionService.SeedAsync(seedTenantId.Value, new CreateReferralAttributionRequest
                {
                    FirstName = builder.Configuration["ReferralAttributionSeed:FirstName"] ?? "Cam",
                    LastName = builder.Configuration["ReferralAttributionSeed:LastName"] ?? "Perry",
                    Code = builder.Configuration["ReferralAttributionSeed:Code"] ?? "CAM_PERRY",
                    IsActive = true,
                    DisplayOrder = int.TryParse(builder.Configuration["ReferralAttributionSeed:DisplayOrder"], out var order) ? order : 1,
                }, CancellationToken.None);
                app.Logger.LogInformation(
                    "ReferralAttributionSeed: seed check completed for tenant '{TenantId}' (code '{TenantCode}').", seedTenantId, seedTenantCode);
            }
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "ReferralAttributionSeed: seed failed (non-fatal) for tenant code '{TenantCode}'.", seedTenantCode);
        }
    }
}

// ── Migration coverage self-test ─────────────────────────────────────────
// Compares every EF-mapped column against the live schema and logs an ERROR
// if any are missing. Guards against the regression behind Task #58 —
// a migration committed without its [Migration] attribute (or otherwise
// un-applied) leaves the EF model and the live schema out of sync, which
// previously surfaced only as runtime "Unknown column" SQL errors.
try
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<CareConnectDbContext>();
    await BuildingBlocks.Diagnostics.MigrationCoverageProbe.RunAsync(db, app.Logger);
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "Migration coverage self-test could not run");
}

// ── Phase H startup diagnostic: provider/facility Identity linkage health ─────
try
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<CareConnectDbContext>();

    var totalProviders           = await db.Providers.CountAsync(p => p.IsActive);
    var providersWithoutOrgLink  = await db.Providers.CountAsync(p => p.IsActive && p.OrganizationId == null);
    var totalFacilities          = await db.Facilities.CountAsync(f => f.IsActive);
    var facilitiesWithoutOrgLink = await db.Facilities.CountAsync(f => f.IsActive && f.OrganizationId == null);

    if (providersWithoutOrgLink > 0)
        app.Logger.LogWarning(
            "Linkage health: {Count}/{Total} active Provider(s) have no Identity Organization link (OrganizationId is null). " +
            "These providers cannot participate in cross-service org-scoped authorization.",
            providersWithoutOrgLink, totalProviders);
    else
        app.Logger.LogInformation(
            "Linkage health: all {Total} active Provider(s) have an Identity Organization link.",
            totalProviders);

    if (facilitiesWithoutOrgLink > 0)
        app.Logger.LogWarning(
            "Linkage health: {Count}/{Total} active Facility(ies) have no Identity Organization link (OrganizationId is null). " +
            "These facilities cannot participate in cross-service org-scoped authorization.",
            facilitiesWithoutOrgLink, totalFacilities);
    else
        app.Logger.LogInformation(
            "Linkage health: all {Total} active Facility(ies) have an Identity Organization link.",
            totalFacilities);
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex,
        "CareConnect Phase H startup diagnostic skipped — could not query the database at startup.");
}

app.UseMiddleware<CorrelationIdMiddleware>();    // BLK-OBS-01: assign X-Correlation-Id first
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.Use(async (context, next) =>
{
    // BLK-SEC-06: capture the raw physical TCP peer address before UseForwardedHeaders can
    // rewrite Connection.RemoteIpAddress from a client-supplied X-Forwarded-For header. The
    // provider-import endpoint's loopback-only gate reads this so it can't be spoofed by a
    // caller setting X-Forwarded-For: 127.0.0.1 upstream of the trusted proxy.
    context.Items[CareConnect.Api.Endpoints.NetworkEndpoints.RawRemoteIpAddressKey] = context.Connection.RemoteIpAddress;
    await next();
});
app.UseForwardedHeaders();                      // BLK-SEC-05: rewrite RemoteIpAddress from X-Forwarded-For before rate limiting
app.UseRateLimiter();                           // BLK-SEC-04: shed excess traffic before authentication runs
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<TenantClaimGuardMiddleware>(); // BLK-SEC-03: reject authenticated requests without tenant_id claim

// Health & info
app.MapGet("/health", async (CareConnectDbContext db, CancellationToken ct) =>
{
    try
    {
        // Lightweight probe: executes "SELECT 1" to confirm DB connectivity.
        var canConnect = await db.Database.CanConnectAsync(ct);
        var dbStatus   = canConnect ? "connected" : "unreachable";
        return canConnect
            ? Results.Ok(new { status = "healthy", db = dbStatus })
            : Results.Json(new { status = "degraded", db = dbStatus },
                statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (Exception)
    {
        return Results.Json(new { status = "degraded", db = "error" },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).AllowAnonymous();
app.MapGet("/info",   () => Results.Ok(new { service = "CareConnect", version = "1.0.0" })).AllowAnonymous();

// Internal service-to-service endpoints
app.MapInternalProvisionEndpoints();

// API endpoints
app.MapCareConnectIntegrityEndpoints();
app.MapProviderAdminEndpoints();
app.MapAdminDashboardEndpoints();   // LSCC-01-004: admin dashboard, blocked queue, referral monitor
app.MapPerformanceEndpoints();      // LSCC-01-005: referral performance metrics
app.MapAdminBackfillEndpoints();
app.MapActivationAdminEndpoints(); // LSCC-009
app.MapAnalyticsEndpoints();      // LSCC-011
// LS-FLOW-MERGE-P4 — product → Flow integration endpoints.
app.MapWorkflowEndpoints();
app.MapAssistantToolEndpoints();
app.MapProviderEndpoints();
app.MapReferralEndpoints();
app.MapPendingReferralRequestEndpoints();
app.MapCategoryEndpoints();
app.MapSpecialtyEndpoints();
app.MapFacilityEndpoints();
app.MapServiceOfferingEndpoints();
app.MapAvailabilityTemplateEndpoints();
app.MapSlotEndpoints();
app.MapAppointmentEndpoints();
app.MapAvailabilityExceptionEndpoints();
app.MapReferralNoteEndpoints();
app.MapAppointmentNoteEndpoints();
app.MapAttachmentEndpoints();
app.MapNotificationEndpoints();
app.MapNetworkEndpoints();             // CC2-INT-B06: provider network management
app.MapPublicNetworkEndpoints();       // CC2-INT-B07: public network surface (anonymous)
app.MapEnrollmentEndpoints();          // CC2-ENROLL: provider self-enrollment (anonymous)
app.MapReferralThreadEndpoints();      // Public referral comment thread (token-authenticated)
app.MapProviderOnboardingEndpoints();  // CC2-INT-B09: provider tenant self-onboarding
app.MapReferralAttributionEndpoints();          // Referral Attribution configuration (tenant admin)
app.MapReferralAttributionAccessCodeEndpoints(); // Referral Representative access codes (tenant admin)
app.MapPublicRepresentativeEndpoints();          // Referral Representative Portal (anonymous, code-gated)

app.Run();

// ── Schema repair helper (CC2-INT-B07) ───────────────────────────────────────
// Applies idempotent DDL to guarantee B06+ schema objects exist regardless of the
// __EFMigrationsHistory state. MySQL DDL is non-transactional, so a partially-applied
// migration can leave history rows written but tables absent.
// Notes:
//   - CREATE TABLE IF NOT EXISTS is safe cross-version.
//   - ADD COLUMN IF NOT EXISTS requires MySQL ≥ 8.0.29 and is NOT available on RDS;
//     we therefore check information_schema first, then ADD COLUMN (no IF NOT EXISTS).
//   - FK constraints are omitted from manual DDL to avoid charset/collation mismatches;
//     EF enforces referential integrity at the application layer.
static async Task EnsureSchemaObjectsAsync(
    CareConnect.Infrastructure.Data.CareConnectDbContext db,
    ILogger logger)
{
    var conn = db.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open)
        await conn.OpenAsync();

    // Resolve the actual database name from the open connection
    string dbName;
    using (var dbCmd = conn.CreateCommand())
    {
        dbCmd.CommandText = "SELECT DATABASE()";
        dbName = (string)(await dbCmd.ExecuteScalarAsync() ?? "careconnect_db");
    }

    // Helper: returns true if the table exists in the live schema
    async Task<bool> TableExists(string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT COUNT(*) FROM information_schema.tables " +
            $"WHERE table_schema='{dbName}' AND table_name='{table}'";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
    }

    // Helper: returns true if the column exists on the given table
    async Task<bool> ColumnExists(string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT COUNT(*) FROM information_schema.columns " +
            $"WHERE table_schema='{dbName}' AND table_name='{table}' AND column_name='{column}'";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
    }

    async Task<bool> IndexExists(string table, string index)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT COUNT(*) FROM information_schema.statistics " +
            $"WHERE table_schema='{dbName}' AND table_name='{table}' AND index_name='{index}'";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
    }

    async Task<bool> MigrationApplied(string migrationId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM information_schema.tables " +
            $"WHERE table_schema='{dbName}' AND table_name='__EFMigrationsHistory'";
        var historyTableExists = Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
        if (!historyTableExists)
            return false;

        using var historyCmd = conn.CreateCommand();
        historyCmd.CommandText =
            "SELECT COUNT(*) FROM `__EFMigrationsHistory` WHERE `MigrationId` = @migrationId";
        var parameter = historyCmd.CreateParameter();
        parameter.ParameterName = "@migrationId";
        parameter.Value = migrationId;
        historyCmd.Parameters.Add(parameter);
        return Convert.ToInt32(await historyCmd.ExecuteScalarAsync()) > 0;
    }

    // Helper: execute a DDL statement, log any errors but continue
    async Task<bool> Exec(string sql, string label)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
            logger.LogInformation("EnsureSchemaObjects: {Label} — applied.", label);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "EnsureSchemaObjects: {Label} — DDL failed.", label);
            return false;
        }
    }

    async Task<bool> DropIndexIfExists(string table, string index, string label)
    {
        if (!await IndexExists(table, index))
            return false;

        return await Exec($"DROP INDEX `{index}` ON `{table}`", label);
    }

    int applied = 0;

    // Only run the B06+ repair path once the prefix migration is already part of
    // the recorded history. On a clean database, pre-creating prefixed tables here
    // races the actual migrations and causes duplicate-table failures.
    if (!await MigrationApplied("20260413230000_AddTablePrefixes"))
    {
        logger.LogInformation(
            "EnsureSchemaObjects: skipping advisory B06+ repair because AddTablePrefixes is not yet recorded in migration history.");

        if (conn.State == System.Data.ConnectionState.Open)
            await conn.CloseAsync();
        return;
    }

    // ── 20260422000000_AddProviderReassignmentLog ───────────────────────────
    if (!await TableExists("cc_ReferralProviderReassignments"))
        if (await Exec("""
            CREATE TABLE `cc_ReferralProviderReassignments` (
                `Id`                char(36)    NOT NULL,
                `ReferralId`        char(36)    NOT NULL,
                `TenantId`          char(36)    NOT NULL,
                `PreviousProviderId` char(36)   NULL,
                `NewProviderId`     char(36)    NOT NULL,
                `ReassignedByUserId` char(36)  NULL,
                `ReassignedAtUtc`   datetime(6) NOT NULL,
                PRIMARY KEY (`Id`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci
            """, "cc_ReferralProviderReassignments")) applied++;

    // ── 20260422100000_AddProviderNetworks ──────────────────────────────────
    if (!await TableExists("cc_ProviderNetworks"))
        if (await Exec("""
            CREATE TABLE `cc_ProviderNetworks` (
                `Id`              char(36)      NOT NULL,
                `TenantId`        char(36)      NOT NULL,
                `Name`            varchar(200)  NOT NULL,
                `Description`     varchar(1000) NOT NULL DEFAULT '',
                `IsDeleted`       tinyint(1)    NOT NULL DEFAULT 0,
                `CreatedAtUtc`    datetime(6)   NOT NULL,
                `UpdatedAtUtc`    datetime(6)   NOT NULL,
                `CreatedByUserId` varchar(255)  NULL,
                `UpdatedByUserId` varchar(255)  NULL,
                PRIMARY KEY (`Id`),
                KEY `IX_cc_ProviderNetworks_TenantId_Name` (`TenantId`, `Name`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci
            """, "cc_ProviderNetworks")) applied++;

    if (!await TableExists("cc_NetworkProviders"))
        // FK constraints are omitted — column types/collations vary by RDS instance;
        // EF enforces referential integrity at the application layer.
        if (await Exec("""
            CREATE TABLE `cc_NetworkProviders` (
                `Id`                char(36)     NOT NULL,
                `TenantId`          char(36)     NOT NULL,
                `ProviderNetworkId` char(36)     NOT NULL,
                `ProviderId`        char(36)     NOT NULL,
                `CreatedAtUtc`      datetime(6)  NOT NULL,
                `UpdatedAtUtc`      datetime(6)  NOT NULL,
                `CreatedByUserId`   varchar(255) NULL,
                `UpdatedByUserId`   varchar(255) NULL,
                PRIMARY KEY (`Id`),
                UNIQUE KEY `IX_cc_NetworkProviders_ProviderNetworkId_ProviderId` (`ProviderNetworkId`, `ProviderId`),
                KEY `IX_cc_NetworkProviders_TenantId` (`TenantId`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci
            """, "cc_NetworkProviders")) applied++;

    // ── 20260422120000_AddProviderNpi ───────────────────────────────────────
    // ADD COLUMN IF NOT EXISTS is only available on MySQL ≥ 8.0.29; check first.
    if (!await ColumnExists("cc_Providers", "Npi"))
        if (await Exec("ALTER TABLE `cc_Providers` ADD COLUMN `Npi` varchar(20) NULL",
            "cc_Providers.Npi")) applied++;

    // ── 20260422130000_AddProviderAccessStage ───────────────────────────────
    if (!await ColumnExists("cc_Providers", "AccessStage"))
        if (await Exec("ALTER TABLE `cc_Providers` ADD COLUMN `AccessStage` varchar(20) NOT NULL DEFAULT 'URL'",
            "cc_Providers.AccessStage")) applied++;

    if (!await ColumnExists("cc_Providers", "IdentityUserId"))
        if (await Exec("ALTER TABLE `cc_Providers` ADD COLUMN `IdentityUserId` char(36) NULL",
            "cc_Providers.IdentityUserId")) applied++;

    if (!await ColumnExists("cc_Providers", "CommonPortalActivatedAtUtc"))
        if (await Exec("ALTER TABLE `cc_Providers` ADD COLUMN `CommonPortalActivatedAtUtc` datetime(6) NULL",
            "cc_Providers.CommonPortalActivatedAtUtc")) applied++;

    if (!await ColumnExists("cc_Providers", "TenantProvisionedAtUtc"))
        if (await Exec("ALTER TABLE `cc_Providers` ADD COLUMN `TenantProvisionedAtUtc` datetime(6) NULL",
            "cc_Providers.TenantProvisionedAtUtc")) applied++;

    // ── 20260803010000_AddProviderTitle ─────────────────────────────────────
    if (!await ColumnExists("cc_Providers", "Title"))
        if (await Exec("ALTER TABLE `cc_Providers` ADD COLUMN `Title` varchar(50) NULL",
            "cc_Providers.Title")) applied++;

    // ── 20260804010000_AddProviderNetworkLocations ────────────────────────
    if (await TableExists("cc_Facilities"))
    {
        if (!await ColumnExists("cc_Facilities", "Email"))
            if (await Exec("ALTER TABLE `cc_Facilities` ADD COLUMN `Email` varchar(320) NULL",
                "cc_Facilities.Email")) applied++;

        if (!await ColumnExists("cc_Facilities", "Latitude"))
            if (await Exec("ALTER TABLE `cc_Facilities` ADD COLUMN `Latitude` decimal(10,7) NULL",
                "cc_Facilities.Latitude")) applied++;

        if (!await ColumnExists("cc_Facilities", "Longitude"))
            if (await Exec("ALTER TABLE `cc_Facilities` ADD COLUMN `Longitude` decimal(10,7) NULL",
                "cc_Facilities.Longitude")) applied++;

        if (!await ColumnExists("cc_Facilities", "GeoPointSource"))
            if (await Exec("ALTER TABLE `cc_Facilities` ADD COLUMN `GeoPointSource` varchar(20) NULL",
                "cc_Facilities.GeoPointSource")) applied++;

        if (!await ColumnExists("cc_Facilities", "GeoUpdatedAtUtc"))
            if (await Exec("ALTER TABLE `cc_Facilities` ADD COLUMN `GeoUpdatedAtUtc` datetime(6) NULL",
                "cc_Facilities.GeoUpdatedAtUtc")) applied++;

        if (!await IndexExists("cc_Facilities", "IX_Facilities_TenantId_Latitude_Longitude"))
            if (await Exec("CREATE INDEX `IX_Facilities_TenantId_Latitude_Longitude` ON `cc_Facilities` (`TenantId`, `Latitude`, `Longitude`)",
                "cc_Facilities tenant geo index")) applied++;

        if (!await IndexExists("cc_Facilities", "IX_Facilities_Tenant_Address"))
            if (await Exec("CREATE INDEX `IX_Facilities_Tenant_Address` ON `cc_Facilities` (`TenantId`, `AddressLine1`, `City`, `State`, `PostalCode`)",
                "cc_Facilities tenant address index")) applied++;
    }

    if (await TableExists("cc_NetworkProviders"))
    {
        if (!await ColumnExists("cc_NetworkProviders", "FacilityId"))
            if (await Exec("ALTER TABLE `cc_NetworkProviders` ADD COLUMN `FacilityId` char(36) NULL",
                "cc_NetworkProviders.FacilityId")) applied++;

        if (!await ColumnExists("cc_NetworkProviders", "IsActive"))
            if (await Exec("ALTER TABLE `cc_NetworkProviders` ADD COLUMN `IsActive` tinyint(1) NOT NULL DEFAULT 1",
                "cc_NetworkProviders.IsActive")) applied++;

        if (!await ColumnExists("cc_NetworkProviders", "AcceptingReferrals"))
            if (await Exec("ALTER TABLE `cc_NetworkProviders` ADD COLUMN `AcceptingReferrals` tinyint(1) NOT NULL DEFAULT 1",
                "cc_NetworkProviders.AcceptingReferrals")) applied++;
    }

    if (await TableExists("cc_Referrals") && !await ColumnExists("cc_Referrals", "FacilityId"))
        if (await Exec("ALTER TABLE `cc_Referrals` ADD COLUMN `FacilityId` char(36) NULL",
            "cc_Referrals.FacilityId")) applied++;

    if (await TableExists("cc_Providers") &&
        await TableExists("cc_Facilities") &&
        await TableExists("cc_ProviderFacilities"))
    {
        if (await Exec("""
            INSERT INTO `cc_Facilities`
                (`Id`, `TenantId`, `Name`, `AddressLine1`, `City`, `State`, `PostalCode`, `Email`, `Phone`, `IsActive`,
                 `Latitude`, `Longitude`, `GeoPointSource`, `GeoUpdatedAtUtc`, `CreatedAtUtc`, `UpdatedAtUtc`, `CreatedByUserId`, `UpdatedByUserId`)
            SELECT
                UUID(),
                p.`TenantId`,
                COALESCE(NULLIF(TRIM(p.`OrganizationName`), ''), NULLIF(TRIM(p.`Name`), ''), 'Provider location'),
                COALESCE(NULLIF(TRIM(p.`AddressLine1`), ''), 'Unknown address'),
                COALESCE(NULLIF(TRIM(p.`City`), ''), 'Unknown'),
                COALESCE(NULLIF(TRIM(p.`State`), ''), 'NA'),
                COALESCE(NULLIF(TRIM(p.`PostalCode`), ''), '00000'),
                p.`Email`,
                p.`Phone`,
                p.`IsActive`,
                p.`Latitude`,
                p.`Longitude`,
                p.`GeoPointSource`,
                p.`GeoUpdatedAtUtc`,
                COALESCE(p.`CreatedAtUtc`, UTC_TIMESTAMP(6)),
                COALESCE(p.`UpdatedAtUtc`, UTC_TIMESTAMP(6)),
                p.`CreatedByUserId`,
                p.`UpdatedByUserId`
            FROM `cc_Providers` p
            WHERE NOT EXISTS (
                SELECT 1
                FROM `cc_Facilities` f
                WHERE f.`TenantId` = p.`TenantId`
                  AND UPPER(TRIM(f.`Name`)) = UPPER(TRIM(COALESCE(NULLIF(TRIM(p.`OrganizationName`), ''), NULLIF(TRIM(p.`Name`), ''), 'Provider location')))
                  AND UPPER(TRIM(f.`AddressLine1`)) = UPPER(TRIM(COALESCE(NULLIF(TRIM(p.`AddressLine1`), ''), 'Unknown address')))
                  AND UPPER(TRIM(f.`City`)) = UPPER(TRIM(COALESCE(NULLIF(TRIM(p.`City`), ''), 'Unknown')))
                  AND UPPER(TRIM(f.`State`)) = UPPER(TRIM(COALESCE(NULLIF(TRIM(p.`State`), ''), 'NA')))
                  AND UPPER(TRIM(f.`PostalCode`)) = UPPER(TRIM(COALESCE(NULLIF(TRIM(p.`PostalCode`), ''), '00000')))
            )
            """, "cc_Facilities provider backfill")) applied++;

        if (await Exec("""
            INSERT IGNORE INTO `cc_ProviderFacilities`
                (`ProviderId`, `FacilityId`, `IsPrimary`)
            SELECT
                p.`Id`,
                MIN(f.`Id`) AS `FacilityId`,
                1 AS `IsPrimary`
            FROM `cc_Providers` p
            INNER JOIN `cc_Facilities` f
                ON f.`TenantId` = p.`TenantId`
               AND UPPER(TRIM(f.`Name`)) = UPPER(TRIM(COALESCE(NULLIF(TRIM(p.`OrganizationName`), ''), NULLIF(TRIM(p.`Name`), ''), 'Provider location')))
               AND UPPER(TRIM(f.`AddressLine1`)) = UPPER(TRIM(COALESCE(NULLIF(TRIM(p.`AddressLine1`), ''), 'Unknown address')))
               AND UPPER(TRIM(f.`City`)) = UPPER(TRIM(COALESCE(NULLIF(TRIM(p.`City`), ''), 'Unknown')))
               AND UPPER(TRIM(f.`State`)) = UPPER(TRIM(COALESCE(NULLIF(TRIM(p.`State`), ''), 'NA')))
               AND UPPER(TRIM(f.`PostalCode`)) = UPPER(TRIM(COALESCE(NULLIF(TRIM(p.`PostalCode`), ''), '00000')))
            GROUP BY p.`Id`
            """, "cc_ProviderFacilities provider backfill")) applied++;

        if (await Exec("""
            UPDATE `cc_ProviderFacilities` pf
            INNER JOIN (
                SELECT `ProviderId`, MIN(`FacilityId`) AS `PrimaryFacilityId`
                FROM `cc_ProviderFacilities`
                GROUP BY `ProviderId`
            ) selected ON selected.`ProviderId` = pf.`ProviderId`
            SET pf.`IsPrimary` = CASE WHEN pf.`FacilityId` = selected.`PrimaryFacilityId` THEN 1 ELSE 0 END
            """, "cc_ProviderFacilities primary backfill")) applied++;
    }

    if (await TableExists("cc_NetworkProviders") &&
        await TableExists("cc_Providers") &&
        await TableExists("cc_ProviderFacilities"))
    {
        if (await Exec("""
            UPDATE `cc_NetworkProviders` np
            INNER JOIN `cc_Providers` p ON p.`Id` = np.`ProviderId`
            INNER JOIN (
                SELECT `ProviderId`, MIN(`FacilityId`) AS `FacilityId`
                FROM `cc_ProviderFacilities`
                GROUP BY `ProviderId`
            ) pf ON pf.`ProviderId` = np.`ProviderId`
            SET
                np.`FacilityId` = pf.`FacilityId`,
                np.`IsActive` = p.`IsActive`,
                np.`AcceptingReferrals` = p.`AcceptingReferrals`
            WHERE np.`FacilityId` IS NULL
            """, "cc_NetworkProviders facility/status backfill")) applied++;

        if (await Exec("""
            SET @networkProviderFacilityNotNull = IF(
                (SELECT COUNT(*) FROM `cc_NetworkProviders` WHERE `FacilityId` IS NULL) = 0,
                'ALTER TABLE `cc_NetworkProviders` MODIFY COLUMN `FacilityId` char(36) NOT NULL',
                'SELECT 1');
            PREPARE stmt FROM @networkProviderFacilityNotNull; EXECUTE stmt; DEALLOCATE PREPARE stmt
            """, "cc_NetworkProviders.FacilityId not-null repair")) applied++;

        if (!await IndexExists("cc_NetworkProviders", "IX_NetworkProviders_ProviderNetworkId_ProviderId_Tmp"))
            if (await Exec("CREATE INDEX `IX_NetworkProviders_ProviderNetworkId_ProviderId_Tmp` ON `cc_NetworkProviders` (`ProviderNetworkId`, `ProviderId`)",
                "cc_NetworkProviders temporary provider lookup index")) applied++;

        if (await DropIndexIfExists("cc_NetworkProviders", "IX_cc_NetworkProviders_ProviderNetworkId_ProviderId",
            "cc_NetworkProviders old unique provider index drop")) applied++;

        if (await DropIndexIfExists("cc_NetworkProviders", "IX_NetworkProviders_ProviderNetworkId_ProviderId",
            "cc_NetworkProviders old named provider index drop")) applied++;

        if (!await IndexExists("cc_NetworkProviders", "IX_NetworkProviders_ProviderNetworkId_ProviderId"))
            if (await Exec("CREATE INDEX `IX_NetworkProviders_ProviderNetworkId_ProviderId` ON `cc_NetworkProviders` (`ProviderNetworkId`, `ProviderId`)",
                "cc_NetworkProviders provider lookup index")) applied++;

        if (await DropIndexIfExists("cc_NetworkProviders", "IX_NetworkProviders_ProviderNetworkId_ProviderId_Tmp",
            "cc_NetworkProviders temporary provider lookup index drop")) applied++;

        if (!await IndexExists("cc_NetworkProviders", "IX_NetworkProviders_ProviderNetworkId_ProviderId_FacilityId"))
            if (await Exec("CREATE UNIQUE INDEX `IX_NetworkProviders_ProviderNetworkId_ProviderId_FacilityId` ON `cc_NetworkProviders` (`ProviderNetworkId`, `ProviderId`, `FacilityId`)",
                "cc_NetworkProviders provider facility unique index")) applied++;

        if (!await IndexExists("cc_NetworkProviders", "IX_NetworkProviders_FacilityId"))
            if (await Exec("CREATE INDEX `IX_NetworkProviders_FacilityId` ON `cc_NetworkProviders` (`FacilityId`)",
                "cc_NetworkProviders facility index")) applied++;
    }

    if (await TableExists("cc_Referrals") && !await IndexExists("cc_Referrals", "IX_Referrals_TenantId_FacilityId"))
        if (await Exec("CREATE INDEX `IX_Referrals_TenantId_FacilityId` ON `cc_Referrals` (`TenantId`, `FacilityId`)",
            "cc_Referrals tenant facility index")) applied++;

    // ── 20260429120000_AddReferralComments ──────────────────────────────────
    if (!await TableExists("cc_ReferralComments"))
        if (await Exec("""
            CREATE TABLE `cc_ReferralComments` (
                `Id`         char(36) COLLATE ascii_general_ci NOT NULL,
                `TenantId`   char(36) COLLATE ascii_general_ci NOT NULL,
                `ReferralId` char(36) COLLATE ascii_general_ci NOT NULL,
                `SenderType` varchar(20)   NOT NULL,
                `SenderName` varchar(200)  NOT NULL,
                `Message`    varchar(4000) NOT NULL,
                `CreatedAt`  datetime(6)   NOT NULL,
                PRIMARY KEY (`Id`),
                KEY `IX_ReferralComments_TenantId_ReferralId_CreatedAt` (`TenantId`, `ReferralId`, `CreatedAt`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci
            """, "cc_ReferralComments")) applied++;

    // ── 20260719000000_AddReferralMessageAttachments ───────────────────────
    if (await TableExists("cc_ReferralAttachments"))
    {
        if (!await ColumnExists("cc_ReferralAttachments", "ReferralCommentId"))
            if (await Exec("ALTER TABLE `cc_ReferralAttachments` ADD COLUMN `ReferralCommentId` char(36) COLLATE ascii_general_ci NULL",
                "cc_ReferralAttachments.ReferralCommentId")) applied++;

        if (!await IndexExists("cc_ReferralAttachments", "IX_cc_ReferralAttachments_ReferralCommentId"))
            if (await Exec("CREATE INDEX `IX_cc_ReferralAttachments_ReferralCommentId` ON `cc_ReferralAttachments` (`ReferralCommentId`)",
                "cc_ReferralAttachments.ReferralCommentId index")) applied++;

        if (!await IndexExists("cc_ReferralAttachments", "IX_cc_ReferralAttachments_ReferralComment"))
            if (await Exec("CREATE INDEX `IX_cc_ReferralAttachments_ReferralComment` ON `cc_ReferralAttachments` (`TenantId`, `ReferralId`, `ReferralCommentId`, `CreatedAtUtc`)",
                "cc_ReferralAttachments referral-comment index")) applied++;
    }

    // ── 20260803000000_AddProviderSpecialties ──────────────────────────────
    if (!await MigrationApplied("20260803000000_AddProviderSpecialties"))
    {
        logger.LogInformation(
            "EnsureSchemaObjects: skipping AddProviderSpecialties repair because migration is not yet recorded in migration history.");
    }
    else
    {
        if (!await TableExists("cc_Specialties"))
        {
            if (await Exec("""
                CREATE TABLE `cc_Specialties` (
                    `Id`           char(36)      NOT NULL,
                    `Name`         varchar(200)  NOT NULL,
                    `Code`         varchar(50)   NOT NULL,
                    `Description`  varchar(1000) NULL,
                    `IsActive`     tinyint(1)    NOT NULL DEFAULT 1,
                    `CreatedAtUtc` datetime(6)   NOT NULL,
                    `UpdatedAtUtc` datetime(6)   NOT NULL,
                    PRIMARY KEY (`Id`),
                    UNIQUE KEY `IX_cc_Specialties_Code` (`Code`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci
                """, "cc_Specialties")) applied++;
        }

        if (await TableExists("cc_Specialties"))
        {
            if (!await IndexExists("cc_Specialties", "IX_cc_Specialties_Code"))
                if (await Exec("CREATE UNIQUE INDEX `IX_cc_Specialties_Code` ON `cc_Specialties` (`Code`)",
                    "cc_Specialties.Code index")) applied++;

            if (await Exec("""
                INSERT IGNORE INTO `cc_Specialties`
                    (`Id`, `Name`, `Code`, `Description`, `IsActive`, `CreatedAtUtc`, `UpdatedAtUtc`)
                VALUES
                    ('41000000-0000-0000-0000-000000000001', 'Pain',             'PAIN',             NULL, 1, '2024-01-01 00:00:00', '2024-01-01 00:00:00'),
                    ('41000000-0000-0000-0000-000000000007', 'Spine',            'SPINE',            NULL, 1, '2024-01-01 00:00:00', '2024-01-01 00:00:00'),
                    ('41000000-0000-0000-0000-000000000004', 'Physical Therapy', 'PHYSICAL_THERAPY', NULL, 1, '2024-01-01 00:00:00', '2024-01-01 00:00:00'),
                    ('41000000-0000-0000-0000-000000000006', 'Neuro',            'NEURO',            NULL, 1, '2024-01-01 00:00:00', '2024-01-01 00:00:00'),
                    ('41000000-0000-0000-0000-000000000005', 'Imaging',          'IMAGING',          NULL, 1, '2024-01-01 00:00:00', '2024-01-01 00:00:00'),
                    ('41000000-0000-0000-0000-000000000002', 'Chiropractor',     'CHIROPRACTOR',     NULL, 1, '2024-01-01 00:00:00', '2024-01-01 00:00:00'),
                    ('41000000-0000-0000-0000-000000000009', 'Extremities',      'EXTREMITIES',      NULL, 1, '2024-01-01 00:00:00', '2024-01-01 00:00:00')
                """, "cc_Specialties seed data")) applied++;

            if (await Exec("""
                UPDATE `cc_Specialties`
                SET `Name` = CASE `Id`
                        WHEN '41000000-0000-0000-0000-000000000001' THEN 'Pain'
                        WHEN '41000000-0000-0000-0000-000000000002' THEN 'Chiropractor'
                        WHEN '41000000-0000-0000-0000-000000000006' THEN 'Neuro'
                        ELSE `Name`
                    END,
                    `Code` = CASE `Id`
                        WHEN '41000000-0000-0000-0000-000000000001' THEN 'PAIN'
                        WHEN '41000000-0000-0000-0000-000000000002' THEN 'CHIROPRACTOR'
                        WHEN '41000000-0000-0000-0000-000000000006' THEN 'NEURO'
                        ELSE `Code`
                    END,
                    `UpdatedAtUtc` = '2024-01-01 00:00:00'
                WHERE (`Id` = '41000000-0000-0000-0000-000000000001' AND `Name` = 'Pain Doctors' AND `Code` = 'PAIN_DOCTORS')
                   OR (`Id` = '41000000-0000-0000-0000-000000000002' AND `Name` = 'Chiropractors' AND `Code` = 'CHIROPRACTORS')
                   OR (`Id` = '41000000-0000-0000-0000-000000000006' AND `Name` = 'Neurology' AND `Code` = 'NEUROLOGY')
                """, "cc_Specialties renamed defaults repair")) applied++;

            if (await Exec("""
                UPDATE `cc_Specialties`
                SET `IsActive` = 0,
                    `UpdatedAtUtc` = '2024-01-01 00:00:00'
                WHERE ((`Id` = '41000000-0000-0000-0000-000000000003' AND `Name` = 'Orthopedics' AND `Code` = 'ORTHOPEDICS')
                    OR (`Id` = '41000000-0000-0000-0000-000000000008' AND `Name` = 'Surgery Center' AND `Code` = 'SURGERY_CENTER'))
                  AND `UpdatedAtUtc` = '2024-01-01 00:00:00'
                """, "cc_Specialties removed defaults deactivation")) applied++;
        }

        if (!await TableExists("cc_ProviderSpecialties"))
        {
            if (await Exec("""
                CREATE TABLE `cc_ProviderSpecialties` (
                    `ProviderId`  char(36)   NOT NULL,
                    `SpecialtyId` char(36)   NOT NULL,
                    `IsPrimary`   tinyint(1) NOT NULL DEFAULT 0,
                    PRIMARY KEY (`ProviderId`, `SpecialtyId`),
                    KEY `IX_cc_ProviderSpecialties_SpecialtyId` (`SpecialtyId`),
                    KEY `IX_cc_ProviderSpecialties_ProviderId_IsPrimary` (`ProviderId`, `IsPrimary`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci
                """, "cc_ProviderSpecialties")) applied++;
        }

        if (await TableExists("cc_ProviderSpecialties"))
        {
            if (!await ColumnExists("cc_ProviderSpecialties", "IsPrimary"))
                if (await Exec("ALTER TABLE `cc_ProviderSpecialties` ADD COLUMN `IsPrimary` tinyint(1) NOT NULL DEFAULT 0",
                    "cc_ProviderSpecialties.IsPrimary")) applied++;

            if (!await IndexExists("cc_ProviderSpecialties", "IX_cc_ProviderSpecialties_SpecialtyId"))
                if (await Exec("CREATE INDEX `IX_cc_ProviderSpecialties_SpecialtyId` ON `cc_ProviderSpecialties` (`SpecialtyId`)",
                    "cc_ProviderSpecialties.SpecialtyId index")) applied++;

            if (!await IndexExists("cc_ProviderSpecialties", "IX_cc_ProviderSpecialties_ProviderId_IsPrimary"))
                if (await Exec("CREATE INDEX `IX_cc_ProviderSpecialties_ProviderId_IsPrimary` ON `cc_ProviderSpecialties` (`ProviderId`, `IsPrimary`)",
                    "cc_ProviderSpecialties.ProviderId.IsPrimary index")) applied++;

            if (await TableExists("cc_ProviderCategories") && await TableExists("cc_Categories") && await TableExists("cc_Specialties"))
            {
                if (await Exec("""
                    INSERT IGNORE INTO `cc_ProviderSpecialties`
                        (`ProviderId`, `SpecialtyId`, `IsPrimary`)
                    SELECT
                        pc.`ProviderId`,
                        CASE c.`Code`
                            WHEN 'PAIN' THEN '41000000-0000-0000-0000-000000000001'
                            WHEN 'PT' THEN '41000000-0000-0000-0000-000000000004'
                            WHEN 'IMG' THEN '41000000-0000-0000-0000-000000000005'
                            WHEN 'NEURO' THEN '41000000-0000-0000-0000-000000000006'
                            WHEN 'SPINE' THEN '41000000-0000-0000-0000-000000000007'
                            WHEN 'CHIRO' THEN '41000000-0000-0000-0000-000000000002'
                        END AS `SpecialtyId`,
                        0
                    FROM `cc_ProviderCategories` pc
                    INNER JOIN `cc_Categories` c ON c.`Id` = pc.`CategoryId`
                    WHERE c.`Code` IN ('PAIN', 'CHIRO', 'PT', 'IMG', 'NEURO', 'SPINE')
                    """, "cc_ProviderSpecialties category backfill")) applied++;

                if (await Exec("""
                    UPDATE `cc_ProviderSpecialties` ps
                    INNER JOIN (
                        SELECT `ProviderId`, MIN(`SpecialtyId`) AS `PrimarySpecialtyId`
                        FROM `cc_ProviderSpecialties`
                        GROUP BY `ProviderId`
                    ) selected ON selected.`ProviderId` = ps.`ProviderId`
                    SET ps.`IsPrimary` = CASE WHEN ps.`SpecialtyId` = selected.`PrimarySpecialtyId` THEN 1 ELSE 0 END
                    """, "cc_ProviderSpecialties primary backfill")) applied++;
            }
        }
    }

    // ── 20260429130000_AddTreatmentTypes ────────────────────────────────────
    if (!await TableExists("cc_TreatmentTypes"))
    {
        if (await Exec("""
            CREATE TABLE `cc_TreatmentTypes` (
                `Id`           char(36)     NOT NULL,
                `Name`         varchar(150) NOT NULL,
                `Category`     varchar(100) NULL,
                `DisplayOrder` int          NOT NULL DEFAULT 0,
                `IsActive`     tinyint(1)   NOT NULL DEFAULT 1,
                PRIMARY KEY (`Id`),
                KEY `IX_cc_TreatmentTypes_Category_DisplayOrder` (`Category`, `DisplayOrder`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci
            """, "cc_TreatmentTypes")) applied++;

        // Seed default treatment types (deterministic GUIDs — idempotent on re-run via INSERT IGNORE)
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT IGNORE INTO `cc_TreatmentTypes` (`Id`, `Name`, `Category`, `DisplayOrder`, `IsActive`) VALUES
                ('a1000001-0000-0000-0000-000000000001', 'Chiropractic Care',        'Musculoskeletal',    10, 1),
                ('a1000002-0000-0000-0000-000000000001', 'Physical Therapy',         'Rehabilitation',     20, 1),
                ('a1000003-0000-0000-0000-000000000001', 'Occupational Therapy',     'Rehabilitation',     30, 1),
                ('a1000004-0000-0000-0000-000000000001', 'Orthopedic Evaluation',    'Musculoskeletal',    40, 1),
                ('a1000005-0000-0000-0000-000000000001', 'Neurology Evaluation',     'Neurological',       50, 1),
                ('a1000006-0000-0000-0000-000000000001', 'Pain Management',          'Pain',               60, 1),
                ('a1000007-0000-0000-0000-000000000001', 'MRI / Radiology',          'Diagnostic',         70, 1),
                ('a1000008-0000-0000-0000-000000000001', 'X-Ray',                    'Diagnostic',         80, 1),
                ('a1000009-0000-0000-0000-000000000001', 'Acupuncture',              'Alternative',        90, 1),
                ('a1000010-0000-0000-0000-000000000001', 'Psychological Evaluation', 'Mental Health',     100, 1),
                ('a1000011-0000-0000-0000-000000000001', 'Toxicology',               'Diagnostic',        110, 1),
                ('a1000012-0000-0000-0000-000000000001', 'Internal Medicine',        'General',           120, 1),
                ('a1000013-0000-0000-0000-000000000001', 'Podiatry',                 'Musculoskeletal',   130, 1),
                ('a1000014-0000-0000-0000-000000000001', 'Ophthalmology',            'Specialized',       140, 1),
                ('a1000015-0000-0000-0000-000000000001', 'Cardiology',               'Specialized',       150, 1),
                ('a1000016-0000-0000-0000-000000000001', 'Dermatology',              'Specialized',       160, 1),
                ('a1000017-0000-0000-0000-000000000001', 'General Referral',         'General',           999, 1)
                """;
            await cmd.ExecuteNonQueryAsync();
            logger.LogInformation("EnsureSchemaObjects: cc_TreatmentTypes — seed data inserted.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "EnsureSchemaObjects: cc_TreatmentTypes seed — failed (non-fatal).");
        }
    }

    // ── 20260821000000_AddPendingReferralProviderRecommendation ────────────
    // Pending referral provider preferences are optional, so drift here only
    // surfaces after a referral representative selects a preferred provider.
    if (await TableExists("cc_PendingReferralRequests"))
    {
        if (!await ColumnExists("cc_PendingReferralRequests", "RecommendedProviderId"))
            if (await Exec("ALTER TABLE `cc_PendingReferralRequests` ADD COLUMN `RecommendedProviderId` char(36) COLLATE ascii_general_ci NULL",
                "cc_PendingReferralRequests.RecommendedProviderId")) applied++;

        if (!await ColumnExists("cc_PendingReferralRequests", "RecommendedFacilityId"))
            if (await Exec("ALTER TABLE `cc_PendingReferralRequests` ADD COLUMN `RecommendedFacilityId` char(36) COLLATE ascii_general_ci NULL",
                "cc_PendingReferralRequests.RecommendedFacilityId")) applied++;

        if (!await ColumnExists("cc_PendingReferralRequests", "RecommendedProviderName"))
            if (await Exec("ALTER TABLE `cc_PendingReferralRequests` ADD COLUMN `RecommendedProviderName` varchar(250) NULL",
                "cc_PendingReferralRequests.RecommendedProviderName")) applied++;

        if (!await ColumnExists("cc_PendingReferralRequests", "RecommendedFacilityName"))
            if (await Exec("ALTER TABLE `cc_PendingReferralRequests` ADD COLUMN `RecommendedFacilityName` varchar(250) NULL",
                "cc_PendingReferralRequests.RecommendedFacilityName")) applied++;

        if (!await TableExists("cc_PendingReferralProviderPreferences"))
        {
            if (await Exec("""
                CREATE TABLE `cc_PendingReferralProviderPreferences` (
                    `Id`                       char(36) COLLATE ascii_general_ci NOT NULL,
                    `PendingReferralRequestId` char(36) COLLATE ascii_general_ci NOT NULL,
                    `ProviderId`               char(36) COLLATE ascii_general_ci NOT NULL,
                    `FacilityId`               char(36) COLLATE ascii_general_ci NULL,
                    `ProviderName`             varchar(250) NOT NULL,
                    `FacilityName`             varchar(250) NULL,
                    `DisplayOrder`             int NOT NULL,
                    PRIMARY KEY (`Id`),
                    KEY `IX_PendingReferralProviderPreferences_Request` (`PendingReferralRequestId`),
                    KEY `IX_PendingReferralProviderPreferences_Request_Order` (`PendingReferralRequestId`, `DisplayOrder`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci
                """, "cc_PendingReferralProviderPreferences")) applied++;
        }
        else
        {
            if (!await IndexExists("cc_PendingReferralProviderPreferences", "IX_PendingReferralProviderPreferences_Request"))
                if (await Exec("CREATE INDEX `IX_PendingReferralProviderPreferences_Request` ON `cc_PendingReferralProviderPreferences` (`PendingReferralRequestId`)",
                    "cc_PendingReferralProviderPreferences request index")) applied++;

            if (!await IndexExists("cc_PendingReferralProviderPreferences", "IX_PendingReferralProviderPreferences_Request_Order"))
                if (await Exec("CREATE INDEX `IX_PendingReferralProviderPreferences_Request_Order` ON `cc_PendingReferralProviderPreferences` (`PendingReferralRequestId`, `DisplayOrder`)",
                    "cc_PendingReferralProviderPreferences request-order index")) applied++;
        }
    }

    // ── LSV3-1084: ProviderNetwork.OwningOrganizationId ────────────────────
    // Networks created before CareConnectReferrerAdmin existed have no owner and are
    // treated as tenant-admin-owned (view-only for a ReferrerAdmin without NetworkManager).
    if (!await ColumnExists("cc_ProviderNetworks", "OwningOrganizationId"))
        if (await Exec("ALTER TABLE `cc_ProviderNetworks` ADD COLUMN `OwningOrganizationId` char(36) COLLATE ascii_general_ci NULL",
            "cc_ProviderNetworks.OwningOrganizationId")) applied++;

    logger.LogInformation("EnsureSchemaObjects: {Count} DDL change(s) applied.", applied);

    // Close the connection so that EF Core's Migrate() can manage its own
    // connection lifecycle cleanly (Pomelo may behave unexpectedly when
    // Migrate() is invoked on a DbContext whose connection is already open).
    if (conn.State == System.Data.ConnectionState.Open)
        await conn.CloseAsync();
}
