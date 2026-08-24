using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Identity.Api.Endpoints;
using Identity.Domain;
using Identity.Infrastructure.Data;
using Identity.Infrastructure.Services;
using LegalSynq.AuditClient;
using LegalSynq.AuditClient.DTOs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Identity.Api.Tests;

/// <summary>
/// Covers the password-reset misconfiguration error paths in the AdminResetPassword
/// minimal-API handler (LS-ID-TNT-006).
///
/// The handler lives in the private static AdminEndpoints class and is invoked via
/// reflection so that the tests exercise the real handler logic without standing up
/// a full WebApplicationFactory.
///
/// Three paths are covered:
///   1. Production + PortalBaseUrl missing        → 503
///   2. Production + BaseUrl missing (email unconfigured) → 503
///   3. Non-production + config missing           → 200 with raw resetToken
/// </summary>
public class AdminResetPasswordTests
{
    private static readonly MethodInfo HandlerMethod =
        typeof(AdminEndpoints)
            .GetMethod("AdminResetPassword", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            "AdminResetPassword method not found on AdminEndpoints. " +
            "If the method was renamed, update this test.");

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IdentityDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseInMemoryDatabase(Guid.CreateVersion7().ToString())
            .Options;
        return new IdentityDbContext(opts);
    }

    private static (IdentityDbContext db, Guid userId) CreateDbWithUser()
    {
        var db = CreateDb();
        var tenant = Tenant.Create("Test Tenant", $"testtenant-{Guid.CreateVersion7():N}");
        db.Tenants.Add(tenant);

        var user = User.Create(tenant.Id, $"user-{Guid.CreateVersion7():N}@example.com", "hash", "Alice", "Admin");
        db.Users.Add(user);
        db.SaveChanges();

        return (db, user.Id);
    }

    private static ClaimsPrincipal PlatformAdminCaller(Guid adminId) =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, adminId.ToString()),
            new Claim(ClaimTypes.Role, "PlatformAdmin"),
        ], "test"));

    private static IWebHostEnvironment MakeEnv(bool production) =>
        new StubWebHostEnvironment(production ? "Production" : "Development");

    private static IOptions<NotificationsServiceOptions> MakeOptions(
        string? portalBaseUrl, string? baseUrl = "http://notifications:5000") =>
        Options.Create(new NotificationsServiceOptions
        {
            PortalBaseUrl = portalBaseUrl,
            BaseUrl       = baseUrl,
        });

    private static ILoggerFactory NullLoggerFactory() =>
        LoggerFactory.Create(_ => { });

    private static Task<IResult> InvokeHandler(
        Guid                                  userId,
        ClaimsPrincipal                       caller,
        IdentityDbContext                     db,
        IAuditEventClient                     auditClient,
        ILoggerFactory                        loggerFactory,
        IWebHostEnvironment                   env,
        IOptions<NotificationsServiceOptions> notificationsOptions,
        INotificationsEmailClient             notificationsEmail) =>
        (Task<IResult>)HandlerMethod.Invoke(
            null,
            [
                userId,
                caller,
                db,
                auditClient,
                loggerFactory,
                env,
                notificationsOptions,
                notificationsEmail,
                CancellationToken.None,
            ])!;

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// When PortalBaseUrl is null or empty in a production environment the handler
    /// must return 503 because it cannot construct the reset link for the email.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Returns503_WhenPortalBaseUrl_IsMissing_InProduction(string? portalBaseUrl)
    {
        var (db, userId) = CreateDbWithUser();
        var caller = PlatformAdminCaller(Guid.CreateVersion7());

        var result = await InvokeHandler(
            userId,
            caller,
            db,
            auditClient:         new NoOpAuditEventClient(),
            loggerFactory:       NullLoggerFactory(),
            env:                 MakeEnv(production: true),
            notificationsOptions: MakeOptions(portalBaseUrl: portalBaseUrl, baseUrl: "http://notifications:5000"),
            notificationsEmail:  new NeverCalledEmailClient());

        var statusResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(503, statusResult.StatusCode);
    }

    /// <summary>
    /// When PortalBaseUrl is set but the notifications BaseUrl is missing, the email
    /// client returns EmailConfigured=false. In production the handler must surface
    /// this as a 503 (hard error) rather than silently claiming success.
    /// </summary>
    [Fact]
    public async Task Returns503_WhenBaseUrl_IsMissing_InProduction()
    {
        var (db, userId) = CreateDbWithUser();
        var caller = PlatformAdminCaller(Guid.CreateVersion7());

        var emailClient = new StubEmailClient(emailConfigured: false, delivered: false, error: "BaseUrl not set");

        var result = await InvokeHandler(
            userId,
            caller,
            db,
            auditClient:         new NoOpAuditEventClient(),
            loggerFactory:       NullLoggerFactory(),
            env:                 MakeEnv(production: true),
            notificationsOptions: MakeOptions(
                portalBaseUrl: "https://portal.example.com",
                baseUrl:       null),
            notificationsEmail:  emailClient);

        var statusResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(503, statusResult.StatusCode);
    }

    /// <summary>
    /// When notifications config is missing in a non-production environment the
    /// handler must fall back to returning 200 with the raw resetToken so that
    /// developers can complete the flow without a working email provider.
    /// </summary>
    [Fact]
    public async Task Returns200WithResetToken_WhenConfigMissing_InNonProduction()
    {
        var (db, userId) = CreateDbWithUser();
        var caller = PlatformAdminCaller(Guid.CreateVersion7());

        var result = await InvokeHandler(
            userId,
            caller,
            db,
            auditClient:         new NoOpAuditEventClient(),
            loggerFactory:       NullLoggerFactory(),
            env:                 MakeEnv(production: false),
            notificationsOptions: MakeOptions(portalBaseUrl: null, baseUrl: null),
            notificationsEmail:  new NeverCalledEmailClient());

        var statusResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(200, statusResult.StatusCode);

        var body = await ExecuteResultBodyAsync(result);
        using var doc  = JsonDocument.Parse(body);
        var resetToken = doc.RootElement.GetProperty("resetToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(resetToken),
            "Non-production fallback must include a non-empty resetToken in the response body.");
    }

    private static async Task<string> ExecuteResultBodyAsync(IResult result)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(_ => { });
        await using var provider = services.BuildServiceProvider();

        var ctx = new DefaultHttpContext { RequestServices = provider };
        ctx.Response.Body = new System.IO.MemoryStream();

        await result.ExecuteAsync(ctx);

        ctx.Response.Body.Seek(0, System.IO.SeekOrigin.Begin);
        return await new System.IO.StreamReader(ctx.Response.Body).ReadToEndAsync();
    }
}

// ── Stubs & fakes ─────────────────────────────────────────────────────────────

file sealed class NoOpAuditEventClient : IAuditEventClient
{
    public Task<IngestResult> IngestAsync(IngestAuditEventRequest request, CancellationToken ct = default) =>
        Task.FromResult(new IngestResult(
            Accepted:        true,
            AuditId:         Guid.CreateVersion7().ToString(),
            RejectionReason: null,
            StatusCode:      202));

    public Task<BatchIngestResult> IngestBatchAsync(BatchIngestRequest request, CancellationToken ct = default) =>
        Task.FromResult(new BatchIngestResult(Submitted: 0, Accepted: 0, Rejected: 0, Results: []));
}

file sealed class NeverCalledEmailClient : INotificationsEmailClient
{
    public Task<(bool EmailConfigured, bool Success, string? Error)> SendPasswordResetEmailAsync(
        string toEmail, string displayName, string resetLink, Guid tenantId, CancellationToken ct = default) =>
        throw new InvalidOperationException(
            "SendPasswordResetEmailAsync should not be called when PortalBaseUrl is not configured.");

    public Task<(bool EmailConfigured, bool Success, string? Error)> SendInviteEmailAsync(
        string toEmail, string displayName, string activationLink, Guid tenantId, CancellationToken ct = default) =>
        throw new InvalidOperationException("SendInviteEmailAsync should not be called in this test.");

    public Task<(bool EmailConfigured, bool Success, string? Error)> SendCareConnectLawFirmInviteEmailAsync(
        string toEmail, string displayName, string activationLink, Guid tenantId, CancellationToken ct = default) =>
        throw new InvalidOperationException("SendCareConnectLawFirmInviteEmailAsync should not be called in this test.");

    public Task<(bool EmailConfigured, bool Success, string? Error)> SendTenantRegistrationApprovedEmailAsync(
        string toEmail, string displayName, string tenantName, string activationLink, int expiryHours, Guid tenantId, CancellationToken ct = default) =>
        throw new InvalidOperationException("SendTenantRegistrationApprovedEmailAsync should not be called in this test.");

    public Task<(bool EmailConfigured, bool Success, string? Error)> SendTenantAccessGrantedEmailAsync(
        string toEmail, string displayName, string tenantName, string portalUrl, Guid tenantId, CancellationToken ct = default) =>
        throw new InvalidOperationException("SendTenantAccessGrantedEmailAsync should not be called in this test.");
}

file sealed class StubEmailClient(bool emailConfigured, bool delivered, string? error)
    : INotificationsEmailClient
{
    public Task<(bool EmailConfigured, bool Success, string? Error)> SendPasswordResetEmailAsync(
        string toEmail, string displayName, string resetLink, Guid tenantId, CancellationToken ct = default) =>
        Task.FromResult((emailConfigured, delivered, error));

    public Task<(bool EmailConfigured, bool Success, string? Error)> SendInviteEmailAsync(
        string toEmail, string displayName, string activationLink, Guid tenantId, CancellationToken ct = default) =>
        Task.FromResult((emailConfigured, delivered, error));

    public Task<(bool EmailConfigured, bool Success, string? Error)> SendCareConnectLawFirmInviteEmailAsync(
        string toEmail, string displayName, string activationLink, Guid tenantId, CancellationToken ct = default) =>
        Task.FromResult((emailConfigured, delivered, error));

    public Task<(bool EmailConfigured, bool Success, string? Error)> SendTenantRegistrationApprovedEmailAsync(
        string toEmail, string displayName, string tenantName, string activationLink, int expiryHours, Guid tenantId, CancellationToken ct = default) =>
        Task.FromResult((emailConfigured, delivered, error));

    public Task<(bool EmailConfigured, bool Success, string? Error)> SendTenantAccessGrantedEmailAsync(
        string toEmail, string displayName, string tenantName, string portalUrl, Guid tenantId, CancellationToken ct = default) =>
        Task.FromResult((emailConfigured, delivered, error));
}

file sealed class StubWebHostEnvironment(string environmentName) : IWebHostEnvironment
{
    public string EnvironmentName { get; set; } = environmentName;
    public string ApplicationName { get; set; } = "Identity.Api.Tests";
    public string WebRootPath { get; set; } = string.Empty;
    public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } =
        new Microsoft.Extensions.FileProviders.NullFileProvider();
    public string ContentRootPath { get; set; } = string.Empty;
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
        new Microsoft.Extensions.FileProviders.NullFileProvider();
}
