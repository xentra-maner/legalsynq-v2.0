using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Identity.Application.DTOs;
using Identity.Application.Interfaces;
using Identity.Domain;
using Identity.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Identity.Tests;

public class AuthMeSessionRenewalTests
{
    private static WebApplicationFactory<Program> BuildFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");

            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:IdentityDb"] = "Server=localhost;Database=identity_test_placeholder;",
                    ["Jwt:SigningKey"] = "test-only-signing-key-32-chars-padded-ok",
                    ["Jwt:Issuer"] = "test-issuer",
                    ["Jwt:Audience"] = "test-audience",
                    ["TenantService:ProvisioningSecret"] = "",
                });
            });

            builder.ConfigureTestServices(services =>
            {
                var hostedSvcs = services
                    .Where(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService))
                    .ToList();
                foreach (var s in hostedSvcs) services.Remove(s);

                var dbDescriptors = services
                    .Where(d =>
                        d.ServiceType == typeof(IdentityDbContext) ||
                        d.ServiceType == typeof(DbContextOptions<IdentityDbContext>) ||
                        d.ServiceType == typeof(DbContextOptions))
                    .ToList();
                foreach (var d in dbDescriptors) services.Remove(d);

                var dbName = "auth-me-renewal-test-" + Guid.CreateVersion7();
                services.AddDbContext<IdentityDbContext>(opts => opts.UseInMemoryDatabase(dbName));
            });
        });

    [Fact]
    public async Task AuthMe_returns_refreshed_access_token_for_valid_session()
    {
        using var factory = BuildFactory();
        var seeded = await SeedActiveUserAsync(factory);

        using var scope = factory.Services.CreateScope();
        var authService = scope.ServiceProvider.GetRequiredService<IAuthService>();
        var login = await authService.LoginAsync(new LoginRequest(
            Email: seeded.Email,
            Password: seeded.Password,
            TenantCode: seeded.TenantCode));

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(login.AccessToken);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(jwt.Claims, "test"));
        var body = await authService.GetCurrentUserAsync(principal);
        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body!.RefreshedAccessToken));
        Assert.NotEqual(login.AccessToken, body.RefreshedAccessToken);
        Assert.True(body.ExpiresAtUtc > DateTime.UtcNow);
    }

    private static async Task<(Guid TenantId, string TenantCode, string Email, string Password)> SeedActiveUserAsync(
        WebApplicationFactory<Program> factory)
    {
        const string password = "Password123!";
        const string email = "user@example.com";

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        var tenant = Tenant.Rehydrate(
            Guid.CreateVersion7(),
            $"active-{Guid.CreateVersion7():N}",
            displayName: "Active Tenant",
            status: ProvisioningStatus.Active.ToString());
        db.Tenants.Add(tenant);

        var role = Role.Create(
            tenant.Id,
            "TenantAdmin",
            description: "Test tenant admin role",
            isSystemRole: true,
            scope: RoleScopes.Tenant);
        db.Roles.Add(role);

        var user = User.Create(
            tenant.Id,
            email,
            passwordHasher.Hash(password),
            "Test",
            "User");
        db.Users.Add(user);
        db.UserTenants.Add(UserTenant.Create(user.Id, tenant.Id));
        db.ScopedRoleAssignments.Add(ScopedRoleAssignment.Create(
            user.Id,
            role.Id,
            ScopedRoleAssignment.ScopeTypes.Global,
            tenantId: tenant.Id));

        await db.SaveChangesAsync();
        return (tenant.Id, tenant.Code, email, password);
    }

}
