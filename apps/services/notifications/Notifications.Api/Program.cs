using System.Security.Claims;
using System.Text;
using System.Text.Json;
using BuildingBlocks.Authentication.ServiceTokens;
using BuildingBlocks.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Notifications.Api.Authorization;
using Notifications.Api.Endpoints;
using Notifications.Api.Middleware;
using Notifications.Infrastructure;
using Notifications.Infrastructure.Data;

var builder = WebApplication.CreateBuilder(args);

// ── JWT Authentication ────────────────────────────────────────────────────────

var jwtSection = builder.Configuration.GetSection("Jwt");
var signingKey = jwtSection["SigningKey"]
    ?? throw new InvalidOperationException("Jwt:SigningKey is not configured.");

// LS-NOTIF-CORE-021: service token signing key (shared platform secret).
// Preferred from FLOW_SERVICE_TOKEN_SECRET env var, then ServiceTokens:SigningKey config.
// Fails fast at startup if neither is set — avoids silently disabling signature validation.
var serviceTokenKey =
    Environment.GetEnvironmentVariable(ServiceTokenAuthenticationDefaults.SecretEnvVar)
    ?? builder.Configuration[$"{ServiceTokenOptions.SectionName}:SigningKey"]
    ?? throw new InvalidOperationException(
        $"Service token signing key is not configured. "
        + $"Set env var '{ServiceTokenAuthenticationDefaults.SecretEnvVar}' "
        + $"or config key '{ServiceTokenOptions.SectionName}:SigningKey'.");

// LS-NOTIF-CORE-021 — two JwtBearer schemes coexist (same pattern as Flow.Api):
//   - "Bearer"       (default): user tokens issued by Identity (iss=legalsynq-identity).
//   - "ServiceToken"           : HS256 M2M tokens minted by ServiceTokenIssuer (iss=legalsynq-service-tokens).
// A policy scheme ("MultiAuth") peeks at the inbound token's `iss` claim and
// forwards to whichever bearer is appropriate, so service tokens are never
// mistakenly validated against the user-token scheme (and vice-versa).
const string MultiScheme = "MultiAuth";

// Single source of truth for service-token accepted audiences.
// Used by both the routing selector and the ServiceToken scheme's ValidAudiences.
// "legalsynq-platform" remains accepted during the transition because some
// deployed producers still mint service JWTs with the platform audience.
string[] serviceTokenAudiences = ["notifications-service", "flow-service", "legalsynq-services", "legalsynq-platform"];

// Reused across requests — avoids per-request allocations inside ForwardDefaultSelector.
var tokenReader = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();

builder.Services
    .AddAuthentication(MultiScheme)
    .AddPolicyScheme(MultiScheme, MultiScheme, options =>
    {
        options.ForwardDefaultSelector = ctx =>
        {
            var auth = ctx.Request.Headers.Authorization.ToString();
            if (!string.IsNullOrWhiteSpace(auth) &&
                auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var raw = auth["Bearer ".Length..].Trim();
                try
                {
                    var parsed = tokenReader.ReadJwtToken(raw);
                    if (parsed.Issuer == ServiceTokenAuthenticationDefaults.DefaultIssuer)
                        return ServiceTokenAuthenticationDefaults.Scheme;
                }
                catch
                {
                    // Not a parseable JWT — fall through to user scheme.
                }
            }
            return JwtBearerDefaults.AuthenticationScheme;
        };
    })
    // ── Scheme 1: user JWTs from Identity ────────────────────────────────
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
            RoleClaimType            = "role",
            ClockSkew                = TimeSpan.Zero,
        };
    })
    // ── Scheme 2: service-to-service JWTs (LS-NOTIF-CORE-021) ────────────
    // Accepts tokens minted by ServiceTokenIssuer from any producer service.
    // Validates: issuer=legalsynq-service-tokens, audience=notifications-service
    // OR flow-service / legalsynq-services / legalsynq-platform during the
    // cross-service transition, subject=service:*
    .AddJwtBearer(ServiceTokenAuthenticationDefaults.Scheme, options =>
    {
        options.MapInboundClaims    = false;
        options.RequireHttpsMetadata = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer           = true,
            ValidateAudience         = true,
            ValidateLifetime         = true,
            ValidateIssuerSigningKey = true,
            RequireSignedTokens      = true,
            RequireExpirationTime    = true,
            ValidIssuer              = ServiceTokenAuthenticationDefaults.DefaultIssuer,
            // Accept notifications-service (new preferred) + flow-service
            // (Flow's existing issuer defaults) + legalsynq-services (future)
            // + legalsynq-platform (legacy deployed producer configs).
            ValidAudiences           = serviceTokenAudiences,
            IssuerSigningKey         = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(serviceTokenKey)) { KeyId = ServiceTokenAuthenticationDefaults.ServiceTokenKeyId },
            NameClaimType            = "sub",
            RoleClaimType            = "role",
            ClockSkew                = TimeSpan.FromSeconds(30),
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = ctx =>
            {
                var sub = ctx.Principal?.FindFirst("sub")?.Value;
                if (string.IsNullOrWhiteSpace(sub) ||
                    !sub.StartsWith("service:", StringComparison.Ordinal))
                {
                    ctx.Fail("Service token must have a subject starting with 'service:'.");
                }
                return Task.CompletedTask;
            },
            OnAuthenticationFailed = ctx =>
            {
                var log = ctx.HttpContext.RequestServices
                    .GetService<ILoggerFactory>()
                    ?.CreateLogger(ServiceTokenAuthenticationDefaults.Scheme);
                log?.LogWarning(ctx.Exception,
                    "ServiceToken authentication failed. Path={Path}",
                    ctx.HttpContext.Request.Path);
                return Task.CompletedTask;
            },
        };
    });

// ── HTTP context accessor (required by ServiceSubmissionHandler) ──────────────
builder.Services.AddHttpContextAccessor();

// ── Authorization ─────────────────────────────────────────────────────────────
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(Policies.AuthenticatedUser, policy =>
        policy.RequireAuthenticatedUser());

    options.AddPolicy(Policies.NotificationInboxUser, policy =>
        policy
            .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .RequireAssertion(context => NotificationInboxAuthorization.IsUserPrincipal(context.User)));

    options.AddPolicy(Policies.AdminOnly, policy =>
        policy.RequireRole(Roles.PlatformAdmin));

    // LS-NOTIF-CORE-021 — service submission gate on POST /v1/notifications.
    // Tries both the user JWT scheme and the ServiceToken scheme;
    // the custom handler also allows legacy X-Tenant-Id header requests.
    options.AddPolicy(Policies.ServiceSubmission, policy =>
        policy
            .AddAuthenticationSchemes(
                JwtBearerDefaults.AuthenticationScheme,
                ServiceTokenAuthenticationDefaults.Scheme)
            .AddRequirements(new ServiceSubmissionRequirement()));
});

// Register the custom authorization handler for ServiceSubmission.
builder.Services.AddSingleton<IAuthorizationHandler, ServiceSubmissionHandler>();

// ── Application services ─────────────────────────────────────────────────────

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddEndpointsApiExplorer();

var app = builder.Build();

// ── Database startup ──────────────────────────────────────────────────────────

using (var startupScope = app.Services.CreateScope())
{
    var db = startupScope.ServiceProvider.GetRequiredService<NotificationsDbContext>();

    try
    {
        await SchemaRenamer.RenameSchemaAsync(db, app.Logger);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Schema rename step failed — tables/columns may already be renamed");
    }

    try
    {
        await SeedNotificationsMigrationHistoryIfNeededAsync(db, app.Logger);
        await db.Database.MigrateAsync();
        app.Logger.LogInformation("Notifications database migrated successfully");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Could not apply Notifications database migrations on startup — schema may be out of sync.");
    }

    try
    {
        await BuildingBlocks.Diagnostics.MigrationCoverageProbe.RunAsync(db, app.Logger);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Migration coverage self-test could not run");
    }
}

// ── Platform provider seeding ─────────────────────────────────────────────────
// On every startup, ensure the platform-level SendGrid provider config exists.
// This is stored with the sentinel TenantId 00000000-0000-0000-0000-000000000001
// so the control center can list/use it without a real tenant context.
try
{
    using var seedScope = app.Services.CreateScope();
    await SeedPlatformSendGridProviderAsync(
        seedScope.ServiceProvider,
        app.Configuration,
        app.Logger);
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "Platform SendGrid provider seeding failed — providers page may show empty");
}

// ── Support email template seeding ────────────────────────────────────────────
// On every startup, ensure the four global support email templates exist so the
// support service can deliver email notifications without manual DB setup.
try
{
    using var seedScope = app.Services.CreateScope();
    await SeedSupportEmailTemplatesAsync(seedScope.ServiceProvider, app.Logger);
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "Support email template seeding failed — support notifications may not render");
}

// ── Middleware pipeline ───────────────────────────────────────────────────────
// Order matters: Authentication → Authorization → custom middleware → endpoints.
// TenantMiddleware is placed AFTER UseAuthentication so it can read context.User
// to extract tenant_id from JWT claims for authenticated requests.

app.UseMiddleware<RawBodyMiddleware>();
app.UseMiddleware<InternalTokenMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<TenantMiddleware>();

// ── Endpoints ─────────────────────────────────────────────────────────────────

app.MapHealthEndpoints();
app.MapNotificationEndpoints();
app.MapUserInboxEndpoints();
app.MapAdminNotificationEndpoints();
app.MapTemplateEndpoints();
app.MapGlobalTemplateEndpoints();
app.MapProviderEndpoints();
app.MapWebhookEndpoints();
app.MapBillingEndpoints();
app.MapContactEndpoints();
app.MapBrandingEndpoints();
app.MapInternalEndpoints();
app.MapSmsPreferenceEndpoints();
app.MapSmsReconciliationEndpoints();
app.MapSmsActivityEndpoints();
app.MapSmsDashboardEndpoints();
app.MapSmsCostEndpoints();
app.MapSmsAlertEndpoints();
app.MapSmsEscalationEndpoints();
app.MapSmsRoutingEndpoints();
app.MapSmsOptimizationEndpoints(); // LS-NOTIF-SMS-015
app.MapSmsRecipientIntelligenceEndpoints(); // LS-NOTIF-SMS-016
app.MapSmsGovernanceEndpoints();             // LS-NOTIF-SMS-017
app.MapSmsTemplateGovernanceEndpoints();     // LS-NOTIF-SMS-018
app.MapSmsGovernanceDynamicRuleEndpoints();  // LS-NOTIF-SMS-019
app.MapSmsGovernanceLifecycleEndpoints();   // LS-NOTIF-SMS-020
app.MapSmsGovernanceReleaseEndpoints();     // LS-NOTIF-SMS-021
app.MapSmsGovernanceRolloutEndpoints();       // LS-NOTIF-SMS-022
app.MapSmsGovernanceTenantScopingEndpoints(); // LS-NOTIF-SMS-023
app.MapGovernanceFederationEndpoints();       // LS-NOTIF-SMS-024
app.MapGovernanceRuntimeEndpoints();          // LS-NOTIF-SMS-025

app.Run();

// ── Migration-history seed ───────────────────────────────────────────────────
// ntf_Notifications (and related tables) were created before EF migration
// tracking was introduced. On a database that already has the schema but an
// empty __EFMigrationsHistory, MigrateAsync fails with "table already exists".
// This function seeds the history for the three pre-tracking migrations so
// MigrateAsync only executes genuinely pending migrations.
static async Task SeedNotificationsMigrationHistoryIfNeededAsync(NotificationsDbContext db, ILogger logger)
{
    var alreadyApplied = new[]
    {
        ("20260418043535_InitialCreate",          "8.0.2"),
        ("20260419000001_AddRetryFields",          "8.0.2"),
        ("20260419000002_AddCategoryAndSeverity",  "8.0.2"),
    };

    // Guard: only seed when the base schema already exists.
    // On a fresh database ntf_Notifications does not exist — seeding would mark
    // InitialCreate as applied without running it, causing MigrateAsync to skip
    // table creation and then crash on missing tables.
    try
    {
        var conn   = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) await conn.OpenAsync();
        try
        {
            using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText =
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES " +
                "WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = 'ntf_Notifications'";
            var schemaParam = checkCmd.CreateParameter();
            schemaParam.ParameterName = "@schema";
            schemaParam.Value = conn.Database;
            checkCmd.Parameters.Add(schemaParam);
            var exists = Convert.ToInt32(await checkCmd.ExecuteScalarAsync()) > 0;
            if (!exists)
            {
                logger.LogInformation("Notifications: fresh database detected — skipping migration history seed");
                return;
            }
        }
        finally
        {
            if (opened) await conn.CloseAsync();
        }
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Notifications: could not check for existing schema — skipping migration history seed");
        return;
    }

    try
    {
        await db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS `__EFMigrationsHistory` (" +
            "`MigrationId` varchar(150) CHARACTER SET utf8mb4 NOT NULL," +
            "`ProductVersion` varchar(32) CHARACTER SET utf8mb4 NOT NULL," +
            "PRIMARY KEY (`MigrationId`)) CHARACTER SET=utf8mb4;");

        foreach (var (id, ver) in alreadyApplied)
        {
            var inserted = await db.Database.ExecuteSqlRawAsync(
                "INSERT IGNORE INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`) VALUES ({0}, {1})",
                id, ver);
            if (inserted > 0)
                logger.LogInformation("Notifications: seeded migration history for {MigrationId}", id);
        }
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Notifications: could not seed migration history — proceeding anyway");
    }
}

// ── Platform SendGrid provider seeder ─────────────────────────────────────────
// Ensures a single platform-level SendGrid config exists so the control-center
// "Test Outbound Message" page and the providers list work for platform admins
// without any manual setup step.
static async Task SeedPlatformSendGridProviderAsync(
    IServiceProvider services,
    IConfiguration configuration,
    ILogger logger)
{
    var sgApiKey = configuration["SENDGRID_API_KEY"];
    if (string.IsNullOrWhiteSpace(sgApiKey))
    {
        logger.LogInformation("SENDGRID_API_KEY not set — skipping platform provider seed");
        return;
    }

    var repo = services.GetRequiredService<Notifications.Application.Interfaces.ITenantProviderConfigRepository>();

    var platformId   = Notifications.Application.Constants.PlatformProvider.PlatformTenantId;
    var existing     = await repo.GetByTenantAndChannelAsync(platformId, "email");
    var alreadyHasSg = existing.Any(c => c.ProviderType.Equals("sendgrid", StringComparison.OrdinalIgnoreCase));

    if (alreadyHasSg)
    {
        logger.LogInformation("Platform SendGrid provider config already exists — skipping seed");
        return;
    }

    var fromEmail = configuration["SENDGRID_FROM_EMAIL"] ?? "noreply@legalsynq.com";
    var fromName  = configuration["SENDGRID_FROM_NAME"]  ?? "LegalSynq";

    var config = new Notifications.Domain.TenantProviderConfig
    {
        Id              = Guid.CreateVersion7(),
        TenantId        = platformId,
        Channel         = "email",
        ProviderType    = "sendgrid",
        DisplayName     = "SendGrid (Platform Default)",
        CredentialsJson = JsonSerializer.Serialize(new { apiKey = sgApiKey }),
        SettingsJson    = JsonSerializer.Serialize(new { fromEmail, fromName }),
        Status          = "active",
        ValidationStatus = "valid",
        HealthStatus    = "unknown",
        Priority        = 1,
    };

    await repo.CreateAsync(config);
    logger.LogInformation("Platform SendGrid provider config seeded with id={Id}", config.Id);
}

// ─── Support email template seeder ────────────────────────────────────────────

static async Task SeedSupportEmailTemplatesAsync(IServiceProvider services, ILogger logger)
{
    var templateRepo = services.GetRequiredService<Notifications.Application.Interfaces.ITemplateRepository>();
    var versionRepo  = services.GetRequiredService<Notifications.Application.Interfaces.ITemplateVersionRepository>();

    var templates = new[]
    {
        new SupportEmailTemplateSeed(
            Key:         "support-ticket-created-email",
            Name:        "Support: Ticket Created",
            Subject:     "Support Ticket {{ticket_number}} Submitted: {{title}}",
            HtmlBody:    """
                         <p>Hi,</p>
                         <p>Your support ticket has been received and is being reviewed by our team.</p>
                         <table cellpadding="4" cellspacing="0" style="margin:0">
                           <tr><td style="padding-right:16px"><strong>Ticket</strong></td><td>{{ticket_number}}</td></tr>
                           <tr><td style="padding-right:16px"><strong>Subject</strong></td><td>{{title}}</td></tr>
                           <tr><td style="padding-right:16px"><strong>Priority</strong></td><td>{{priority}}</td></tr>
                           <tr><td style="padding-right:16px"><strong>Status</strong></td><td>{{status}}</td></tr>
                         </table>
                         <p style="margin-top:24px">
                           <a href="{{deeplink_url}}" style="background:#2563eb;color:#fff;padding:10px 20px;border-radius:6px;text-decoration:none;font-weight:600">View Ticket</a>
                         </p>
                         """,
            TextBody:    "Your support ticket {{ticket_number}} has been submitted.\nSubject: {{title}}\nPriority: {{priority}}\n\nView it here: {{deeplink_url}}"),

        new SupportEmailTemplateSeed(
            Key:         "support-ticket-status-changed-email",
            Name:        "Support: Ticket Status Changed",
            Subject:     "Ticket {{ticket_number}} Status Updated: {{new_status}}",
            HtmlBody:    """
                         <p>Hi,</p>
                         <p>The status of your support ticket has been updated.</p>
                         <table cellpadding="4" cellspacing="0" style="margin:0">
                           <tr><td style="padding-right:16px"><strong>Ticket</strong></td><td>{{ticket_number}}</td></tr>
                           <tr><td style="padding-right:16px"><strong>Subject</strong></td><td>{{title}}</td></tr>
                           <tr><td style="padding-right:16px"><strong>Previous Status</strong></td><td>{{previous_status}}</td></tr>
                           <tr><td style="padding-right:16px"><strong>New Status</strong></td><td>{{new_status}}</td></tr>
                         </table>
                         <p style="margin-top:24px">
                           <a href="{{deeplink_url}}" style="background:#2563eb;color:#fff;padding:10px 20px;border-radius:6px;text-decoration:none;font-weight:600">View Ticket</a>
                         </p>
                         """,
            TextBody:    "Ticket {{ticket_number}} status changed from {{previous_status}} to {{new_status}}.\n\nView it here: {{deeplink_url}}"),

        new SupportEmailTemplateSeed(
            Key:         "support-ticket-comment-added-email",
            Name:        "Support: New Reply",
            Subject:     "New Reply on Ticket {{ticket_number}}: {{title}}",
            HtmlBody:    """
                         <p>Hi,</p>
                         <p>A new reply has been posted on your support ticket.</p>
                         <table cellpadding="4" cellspacing="0" style="margin:0 0 16px">
                           <tr><td style="padding-right:16px"><strong>Ticket</strong></td><td>{{ticket_number}}</td></tr>
                           <tr><td style="padding-right:16px"><strong>Subject</strong></td><td>{{title}}</td></tr>
                           <tr><td style="padding-right:16px"><strong>From</strong></td><td>{{author_display}}</td></tr>
                         </table>
                         <div style="margin:16px 0;padding:16px 20px;background:#f8fafc;border-left:4px solid #2563eb;border-radius:4px;color:#374151;white-space:pre-wrap;word-break:break-word">{{comment_body}}</div>
                         <p style="margin-top:24px">
                           <a href="{{deeplink_url}}" style="background:#2563eb;color:#fff;padding:10px 20px;border-radius:6px;text-decoration:none;font-weight:600">View Ticket</a>
                         </p>
                         """,
            TextBody:    "New reply on ticket {{ticket_number}}: {{title}}\nFrom: {{author_display}}\n\n{{comment_body}}\n\nView it here: {{deeplink_url}}"),

        new SupportEmailTemplateSeed(
            Key:         "support-ticket-assigned-email",
            Name:        "Support: Ticket Assigned",
            Subject:     "Support Ticket {{ticket_number}} Has Been Assigned to You",
            HtmlBody:    """
                         <p>Hi,</p>
                         <p>A support ticket has been assigned to you.</p>
                         <table cellpadding="4" cellspacing="0" style="margin:0">
                           <tr><td style="padding-right:16px"><strong>Ticket</strong></td><td>{{ticket_number}}</td></tr>
                           <tr><td style="padding-right:16px"><strong>Subject</strong></td><td>{{title}}</td></tr>
                           <tr><td style="padding-right:16px"><strong>Priority</strong></td><td>{{priority}}</td></tr>
                         </table>
                         <p style="margin-top:24px">
                           <a href="{{deeplink_url}}" style="background:#2563eb;color:#fff;padding:10px 20px;border-radius:6px;text-decoration:none;font-weight:600">View Ticket</a>
                         </p>
                         """,
            TextBody:    "Support ticket {{ticket_number}} ({{title}}) has been assigned to you.\n\nView it here: {{deeplink_url}}"),

        new SupportEmailTemplateSeed(
            Key:         "support-ticket-updated-email",
            Name:        "Support: Ticket Updated",
            Subject:     "Support Ticket {{ticket_number}} Updated",
            HtmlBody:    """
                         <p>Hi,</p>
                         <p>Your support ticket has been updated.</p>
                         <table cellpadding="4" cellspacing="0" style="margin:0">
                           <tr><td style="padding-right:16px"><strong>Ticket</strong></td><td>{{ticket_number}}</td></tr>
                           <tr><td style="padding-right:16px"><strong>Subject</strong></td><td>{{title}}</td></tr>
                           <tr><td style="padding-right:16px"><strong>Status</strong></td><td>{{status}}</td></tr>
                         </table>
                         <p style="margin-top:24px">
                           <a href="{{deeplink_url}}" style="background:#2563eb;color:#fff;padding:10px 20px;border-radius:6px;text-decoration:none;font-weight:600">View Ticket</a>
                         </p>
                         """,
            TextBody:    "Ticket {{ticket_number}} ({{title}}) has been updated. Status: {{status}}.\n\nView it here: {{deeplink_url}}"),
    };

    foreach (var seed in templates)
    {
        var existing = await templateRepo.FindByKeyAsync(seed.Key, "email", null);
        Guid templateId;

        if (existing != null)
        {
            templateId = existing.Id;
            // Template record exists — check whether a published version was also created.
            var existingVersion = await versionRepo.FindPublishedByTemplateIdAsync(templateId);
            if (existingVersion != null)
            {
                // Update in-place when the seed content has changed (e.g. new tokens added).
                if (existingVersion.SubjectTemplate != seed.Subject
                    || existingVersion.BodyTemplate  != seed.HtmlBody
                    || existingVersion.TextTemplate  != seed.TextBody)
                {
                    existingVersion.SubjectTemplate = seed.Subject;
                    existingVersion.BodyTemplate    = seed.HtmlBody;
                    existingVersion.TextTemplate    = seed.TextBody;
                    await versionRepo.UpdateAsync(existingVersion);
                    logger.LogInformation("Updated support email template: {Key}", seed.Key);
                }
                else
                {
                    logger.LogDebug("Support email template already fully seeded, skipping: {Key}", seed.Key);
                }
                continue;
            }
            logger.LogDebug("Support email template exists but has no published version, creating version: {Key}", seed.Key);
        }
        else
        {
            var template = await templateRepo.CreateAsync(new Notifications.Domain.Template
            {
                Id          = Guid.CreateVersion7(),
                TenantId    = null,
                TemplateKey = seed.Key,
                Channel     = "email",
                Name        = seed.Name,
                Description = $"Auto-seeded global template for support event {seed.Key}",
                Status      = "active",
                Scope       = "global",
                ProductType = "support",
            });
            templateId = template.Id;
        }

        await versionRepo.CreateAsync(new Notifications.Domain.TemplateVersion
        {
            Id              = Guid.CreateVersion7(),
            TemplateId      = templateId,
            VersionNumber   = 1,
            SubjectTemplate = seed.Subject,
            BodyTemplate    = seed.HtmlBody,
            TextTemplate    = seed.TextBody,
            EditorType      = "html",
            IsPublished     = true,
            PublishedBy     = "system-seed",
            PublishedAt     = DateTime.UtcNow,
        });

        logger.LogInformation("Seeded support email template: {Key}", seed.Key);
    }
}

file sealed record SupportEmailTemplateSeed(
    string Key,
    string Name,
    string Subject,
    string HtmlBody,
    string TextBody);
