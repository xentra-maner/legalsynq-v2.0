using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Identity.Domain;
using Identity.Infrastructure.Data;
using Identity.Infrastructure.Services;
using LegalSynq.AuditClient;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Identity.Tests;

/// <summary>
/// Covers the invite delivery error paths added in Task #112:
///   - InviteUser returns 503 when NotificationsService:PortalBaseUrl is not configured.
///   - InviteUser returns 503 when NotificationsService:BaseUrl is not configured
///     (email client signals it is unconfigured).
///   - ResendInvite returns 503 when NotificationsService:PortalBaseUrl is not configured.
///   - ResendInvite returns 503 when NotificationsService:BaseUrl is not configured.
/// </summary>
public class InviteDeliveryErrorTests
{
    // ── Factory helpers ───────────────────────────────────────────────────────

    private static WebApplicationFactory<Program> BuildFactory(
        string? portalBaseUrl,
        (bool Configured, bool Success, string? Error) emailResult)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");

            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:IdentityDb"]        = "Server=localhost;Database=identity_test_placeholder;",
                    ["Jwt:SigningKey"]                      = "test-only-signing-key-32-chars-padded-ok",
                    ["Jwt:Issuer"]                         = "test-issuer",
                    ["Jwt:Audience"]                       = "test-audience",
                    ["NotificationsService:BaseUrl"]        = "http://localhost:19999",
                    ["NotificationsService:PortalBaseUrl"]  = portalBaseUrl ?? "",
                });
            });

            builder.ConfigureTestServices(services =>
            {
                // Remove background services so they don't race with InMemory DB.
                var hostedSvcs = services
                    .Where(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService))
                    .ToList();
                foreach (var s in hostedSvcs) services.Remove(s);

                // Replace MySQL-backed DbContext with InMemory.
                // Must remove IdentityDbContext, DbContextOptions<T>, and the
                // non-generic DbContextOptions alias that AddDbContext also registers.
                var dbDescriptors = services
                    .Where(d =>
                        d.ServiceType == typeof(IdentityDbContext) ||
                        d.ServiceType == typeof(DbContextOptions<IdentityDbContext>) ||
                        d.ServiceType == typeof(DbContextOptions))
                    .ToList();
                foreach (var d in dbDescriptors) services.Remove(d);

                var dbName = "identity-test-" + Guid.CreateVersion7();
                services.AddDbContext<IdentityDbContext>(opts =>
                    opts.UseInMemoryDatabase(dbName));

                // Add a test authentication scheme that grants PlatformAdmin role.
                services.AddAuthentication("Test")
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                services.PostConfigure<AuthenticationOptions>(opts =>
                {
                    opts.DefaultAuthenticateScheme = "Test";
                    opts.DefaultChallengeScheme    = "Test";
                });

                // Replace the notifications email client with a controllable stub.
                var existing = services
                    .Where(d => d.ServiceType == typeof(INotificationsEmailClient))
                    .ToList();
                foreach (var d in existing) services.Remove(d);
                services.AddScoped<INotificationsEmailClient>(_ =>
                    new StubNotificationsEmailClient(emailResult));
                services.RemoveAll<IAuditEventClient>();
                services.AddSingleton<IAuditEventClient, NoOpAuditEventClient>();
            });
        });
    }

    private static async Task<Guid> SeedTenantAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var tenant = Tenant.Create("Test Tenant", "TSTCO");
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    private static async Task<(Guid TenantId, Guid UserId)> SeedUserAsync(
        WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();

        var tenant = Tenant.Create("Resend Tenant", "RSNDCO");
        db.Tenants.Add(tenant);

        var user = User.Create(tenant.Id, "existing@example.com", "hash", "Existing", "User");
        db.Users.Add(user);

        await db.SaveChangesAsync();
        return (tenant.Id, user.Id);
    }

    // ── InviteUser ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task InviteUser_Returns503_WhenPortalBaseUrlNotConfigured()
    {
        using var factory = BuildFactory(
            portalBaseUrl: "",
            emailResult: (true, true, null));

        var tenantId = await SeedTenantAsync(factory);
        var client   = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/admin/users/invite", new
        {
            email     = "newuser@example.com",
            firstName = "New",
            lastName  = "User",
            tenantId  = tenantId,
        });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("PortalBaseUrl", body);
    }

    [Fact]
    public async Task InviteUser_Returns503_WhenNotificationsServiceBaseUrlNotConfigured()
    {
        using var factory = BuildFactory(
            portalBaseUrl: "https://portal.example.com",
            emailResult: (Configured: false, Success: false, Error: "NotificationsService:BaseUrl is not configured."));

        var tenantId = await SeedTenantAsync(factory);
        var client   = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/admin/users/invite", new
        {
            email     = "invited@example.com",
            firstName = "Invited",
            lastName  = "User",
            tenantId  = tenantId,
        });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Notifications service is not configured", body);
    }

    // ── ResendInvite ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ResendInvite_Returns503_WhenPortalBaseUrlNotConfigured()
    {
        using var factory = BuildFactory(
            portalBaseUrl: "",
            emailResult: (true, true, null));

        var (_, userId) = await SeedUserAsync(factory);
        var client      = factory.CreateClient();

        var response = await client.PostAsync(
            $"/api/admin/users/{userId}/resend-invite",
            null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("PortalBaseUrl", body);
    }

    [Fact]
    public async Task ResendInvite_Returns503_WhenNotificationsServiceBaseUrlNotConfigured()
    {
        using var factory = BuildFactory(
            portalBaseUrl: "https://portal.example.com",
            emailResult: (Configured: false, Success: false, Error: "NotificationsService:BaseUrl is not configured."));

        var (_, userId) = await SeedUserAsync(factory);
        var client      = factory.CreateClient();

        var response = await client.PostAsync(
            $"/api/admin/users/{userId}/resend-invite",
            null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Notifications service is not configured", body);
    }

    // ── Test doubles ──────────────────────────────────────────────────────────

    private sealed class StubNotificationsEmailClient : INotificationsEmailClient
    {
        private readonly (bool Configured, bool Success, string? Error) _result;

        public StubNotificationsEmailClient((bool Configured, bool Success, string? Error) result)
            => _result = result;

        public Task<(bool EmailConfigured, bool Success, string? Error)> SendPasswordResetEmailAsync(
            string toEmail, string displayName, string resetLink, Guid tenantId,
            CancellationToken ct = default)
            => Task.FromResult(_result);

        public Task<(bool EmailConfigured, bool Success, string? Error)> SendInviteEmailAsync(
            string toEmail, string displayName, string activationLink, Guid tenantId,
            CancellationToken ct = default)
            => Task.FromResult(_result);

        public Task<(bool EmailConfigured, bool Success, string? Error)> SendCareConnectLawFirmInviteEmailAsync(
            string toEmail, string displayName, string activationLink, Guid tenantId,
            CancellationToken ct = default)
            => Task.FromResult(_result);

        public Task<(bool EmailConfigured, bool Success, string? Error)> SendTenantRegistrationApprovedEmailAsync(
            string toEmail, string displayName, string tenantName, string activationLink, int expiryHours, Guid tenantId,
            CancellationToken ct = default)
            => Task.FromResult(_result);

        public Task<(bool EmailConfigured, bool Success, string? Error)> SendTenantAccessGrantedEmailAsync(
            string toEmail, string displayName, string tenantName, string portalUrl, Guid tenantId,
            CancellationToken ct = default)
            => Task.FromResult(_result);
    }

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public TestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "test-platform-admin"),
                new Claim("sub",         "test-platform-admin"),
                new Claim(ClaimTypes.Role, "PlatformAdmin"),
                new Claim("tenant_id",   Guid.Empty.ToString()),
            };
            var identity  = new ClaimsIdentity(claims, "Test");
            var principal = new ClaimsPrincipal(identity);
            var ticket    = new AuthenticationTicket(principal, "Test");
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
