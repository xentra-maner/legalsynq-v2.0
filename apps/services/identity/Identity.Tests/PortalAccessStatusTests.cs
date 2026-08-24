using System.Net;
using System.Net.Http.Json;
using Identity.Application.Interfaces;
using Identity.Api.Endpoints;
using Identity.Domain;
using Identity.Infrastructure.Data;
using LegalSynq.AuditClient;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Identity.Tests;

public class PortalAccessStatusTests
{
    private const string CareConnectProductCode = "SYNQ_CARECONNECT";

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

                var dbName = "portal-access-status-test-" + Guid.CreateVersion7();
                services.AddDbContext<IdentityDbContext>(opts => opts.UseInMemoryDatabase(dbName));
            });
        });

    [Fact]
    public async Task PortalAccess_ReturnsExistingUserOtherTenant_WhenEmailExistsOutsideTargetTenant()
    {
        using var factory = BuildFactory();
        var targetTenantId = await SeedExistingCrossTenantUserAsync(factory, "lawyer@example.com");
        var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"/api/internal/users/portal-access?tenantId={targetTenantId}&email=lawyer@example.com");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PortalAccessStatusResponse>();
        Assert.NotNull(body);
        Assert.Equal("existing_user_other_tenant", body.Status);
    }

    [Fact]
    public async Task PortalAccess_ReturnsActiveInTenant_WhenEmailAlreadyHasTenantCareConnectReferrerAccess()
    {
        using var factory = BuildFactory();
        var targetTenantId = await SeedActiveTenantReferrerAsync(factory, "active@example.com");
        var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"/api/internal/users/portal-access?tenantId={targetTenantId}&email=active@example.com");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PortalAccessStatusResponse>();
        Assert.NotNull(body);
        Assert.Equal("active_in_tenant", body.Status);
    }

    [Fact]
    public async Task PortalAccess_ReturnsNoAccount_WhenUserHasTenantMembershipButNoPortalReadyCareConnectAccess()
    {
        using var factory = BuildFactory();
        var tenantId = await SeedTenantMemberWithoutCareConnectReferrerAccessAsync(factory, "member@example.com");
        var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"/api/internal/users/portal-access?tenantId={tenantId}&email=member@example.com");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PortalAccessStatusResponse>();
        Assert.NotNull(body);
        Assert.Equal("no_account", body.Status);
    }

    [Fact]
    public async Task PortalAccess_ReturnsNoAccount_WhenEmailDoesNotExist()
    {
        using var factory = BuildFactory();
        var tenantId = await SeedTenantOnlyAsync(factory);
        var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"/api/internal/users/portal-access?tenantId={tenantId}&email=missing@example.com");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PortalAccessStatusResponse>();
        Assert.NotNull(body);
        Assert.Equal("no_account", body.Status);
    }

    [Fact]
    public async Task AccountExists_ReturnsTrue_WhenEmailExists()
    {
        using var factory = BuildFactory();
        var tenantId = await SeedExistingCrossTenantUserAsync(factory, "existing.account@example.com");
        var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"/api/internal/users/account-exists?tenantId={tenantId}&email=existing.account@example.com");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AccountExistsResponse>();
        Assert.NotNull(body);
        Assert.True(body.Exists);
        Assert.Equal(tenantId, body.TenantId);
    }

    [Fact]
    public async Task AccountExists_ReturnsFalse_WhenEmailDoesNotExist()
    {
        using var factory = BuildFactory();
        var tenantId = await SeedTenantOnlyAsync(factory);
        var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"/api/internal/users/account-exists?tenantId={tenantId}&email=missing@example.com");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AccountExistsResponse>();
        Assert.NotNull(body);
        Assert.False(body.Exists);
        Assert.Equal(tenantId, body.TenantId);
    }

    [Fact]
    public async Task UserDisplay_ReturnsTenantScopedFirstAndLastName()
    {
        using var factory = BuildFactory();
        Guid tenantId;
        Guid otherTenantId;
        Guid userId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();

            var tenant = Tenant.Create("Seller Tenant", $"seller-{Guid.CreateVersion7():N}");
            var otherTenant = Tenant.Create("Other Tenant", $"other-{Guid.CreateVersion7():N}");
            db.Tenants.AddRange(tenant, otherTenant);

            var user = User.Create(
                tenant.Id,
                "processor@example.test",
                "password-hash",
                "Seller",
                "Processor");
            db.Users.Add(user);
            db.UserTenants.Add(UserTenant.Create(user.Id, tenant.Id));

            await db.SaveChangesAsync();

            tenantId = tenant.Id;
            otherTenantId = otherTenant.Id;
            userId = user.Id;
        }

        var client = factory.CreateClient();
        var response = await client.GetAsync(
            $"/api/internal/users/{userId:D}/display?tenantId={tenantId:D}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<UserDisplayResponse>();
        Assert.NotNull(body);
        Assert.True(body.Found);
        Assert.Equal(userId, body.UserId);
        Assert.Equal(tenantId, body.TenantId);
        Assert.Equal("Seller", body.FirstName);
        Assert.Equal("Processor", body.LastName);
        Assert.Equal("Seller Processor", body.DisplayName);

        var otherTenantResponse = await client.GetAsync(
            $"/api/internal/users/{userId:D}/display?tenantId={otherTenantId:D}");
        Assert.Equal(HttpStatusCode.OK, otherTenantResponse.StatusCode);
        var otherTenantBody = await otherTenantResponse.Content.ReadFromJsonAsync<UserDisplayResponse>();
        Assert.NotNull(otherTenantBody);
        Assert.False(otherTenantBody.Found);
        Assert.Equal(userId, otherTenantBody.UserId);
        Assert.Equal(otherTenantId, otherTenantBody.TenantId);
    }

    [Fact]
    public async Task UserDisplay_ReturnsFirstAndLastName_WhenUserHasActiveOrganizationMembership()
    {
        using var factory = BuildFactory();
        Guid tenantId;
        Guid organizationId;
        Guid userId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();

            var tenant = Tenant.Create("Seller Tenant", $"seller-{Guid.CreateVersion7():N}");
            db.Tenants.Add(tenant);

            var organization = Organization.Create(
                tenant.Id,
                "RL Liens1",
                OrgType.Provider,
                displayName: "RL Liens1");
            db.Organizations.Add(organization);

            var user = User.Create(
                tenant.Id,
                "org.processor@example.test",
                "password-hash",
                "Organization",
                "Processor");
            db.Users.Add(user);

            var membership = UserOrganizationMembership.Create(user.Id, organization.Id, MemberRole.Member);
            membership.SetPrimary();
            db.UserOrganizationMemberships.Add(membership);

            await db.SaveChangesAsync();

            tenantId = tenant.Id;
            organizationId = organization.Id;
            userId = user.Id;
        }

        var client = factory.CreateClient();
        var response = await client.GetAsync(
            $"/api/internal/users/{userId:D}/display?tenantId={tenantId:D}&organizationId={organizationId:D}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<UserDisplayResponse>();
        Assert.NotNull(body);
        Assert.True(body.Found);
        Assert.Equal(userId, body.UserId);
        Assert.Equal(tenantId, body.TenantId);
        Assert.Equal("Organization", body.FirstName);
        Assert.Equal("Processor", body.LastName);
        Assert.Equal("Organization Processor", body.DisplayName);

        var withoutOrganizationResponse = await client.GetAsync(
            $"/api/internal/users/{userId:D}/display?tenantId={tenantId:D}");
        Assert.Equal(HttpStatusCode.OK, withoutOrganizationResponse.StatusCode);
        var withoutOrganizationBody = await withoutOrganizationResponse.Content.ReadFromJsonAsync<UserDisplayResponse>();
        Assert.NotNull(withoutOrganizationBody);
        Assert.False(withoutOrganizationBody.Found);
    }

    [Fact]
    public async Task TenantOwnerDisplay_ReturnsTenantOwnerNameAndOrganizationDisplay()
    {
        using var factory = BuildFactory();
        Guid tenantId;
        Guid ownerUserId;
        Guid organizationId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();

            var tenant = Tenant.Create("Seller Tenant", $"seller-{Guid.CreateVersion7():N}");
            db.Tenants.Add(tenant);

            var owner = User.Create(
                tenant.Id,
                "owner@example.test",
                "password-hash",
                "Tenant",
                "Owner");
            db.Users.Add(owner);
            db.UserTenants.Add(UserTenant.Create(owner.Id, tenant.Id));
            tenant.SetOwner(owner.Id);

            var organization = Organization.Create(
                tenant.Id,
                "RL Liens1",
                OrgType.Provider,
                displayName: "RL Liens1");
            db.Organizations.Add(organization);

            await db.SaveChangesAsync();

            tenantId = tenant.Id;
            ownerUserId = owner.Id;
            organizationId = organization.Id;
        }

        var client = factory.CreateClient();
        var response = await client.GetAsync(
            $"/api/internal/users/tenant-owner/display?organizationId={organizationId:D}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TenantOwnerDisplayResponse>();
        Assert.NotNull(body);
        Assert.True(body.Found);
        Assert.Equal(tenantId, body.TenantId);
        Assert.Equal(organizationId, body.OrganizationId);
        Assert.Equal(ownerUserId, body.UserId);
        Assert.Equal("Tenant", body.FirstName);
        Assert.Equal("Owner", body.LastName);
        Assert.Equal("Tenant Owner", body.DisplayName);
        Assert.Equal("RL Liens1", body.OrganizationName);
        Assert.Equal("RL Liens1", body.OrganizationDisplayName);
    }

    [Fact]
    public async Task SelfRegister_LinksExistingUserByNormalizedEmail_WithoutCreatingDuplicateUser()
    {
        using var factory = BuildFactory();
        var password = "ExistingPassword123!";
        Guid targetOrgId;
        Guid targetTenantId;
        Guid existingUserId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            await EnsureCareConnectProductSeededAsync(db);

            var homeTenant = Tenant.Create("Home Tenant", $"home-{Guid.CreateVersion7():N}");
            var targetTenant = Tenant.Create("Target Tenant", $"target-{Guid.CreateVersion7():N}");
            db.Tenants.AddRange(homeTenant, targetTenant);

            var homeOrg = Organization.Create(homeTenant.Id, "Home Firm", OrgType.LawFirm, displayName: "Home Firm");
            var targetOrg = Organization.Create(targetTenant.Id, "Target Firm", OrgType.LawFirm, displayName: "Target Firm");
            db.Organizations.AddRange(homeOrg, targetOrg);

            var existingUser = User.Create(
                homeTenant.Id,
                "legacy.referrer@example.com",
                passwordHasher.Hash(password),
                "Legacy",
                "Referrer");
            SetEmailForTest(existingUser, "Legacy.Referrer@Example.com");

            db.Users.Add(existingUser);
            db.UserTenants.Add(UserTenant.Create(existingUser.Id, homeTenant.Id));

            var homeMembership = UserOrganizationMembership.Create(existingUser.Id, homeOrg.Id, MemberRole.Member);
            homeMembership.SetPrimary();
            db.UserOrganizationMemberships.Add(homeMembership);

            await db.SaveChangesAsync();

            targetOrgId = targetOrg.Id;
            targetTenantId = targetTenant.Id;
            existingUserId = existingUser.Id;
        }

        using var invokeScope = factory.Services.CreateScope();
        var invokeDb = invokeScope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var invokePasswordHasher = invokeScope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var provisioningEngine = invokeScope.ServiceProvider.GetRequiredService<IProductProvisioningService>();
        var userProductAccessService = invokeScope.ServiceProvider.GetRequiredService<IUserProductAccessService>();
        var auditClient = invokeScope.ServiceProvider.GetRequiredService<IAuditEventClient>();
        var loggerFactory = invokeScope.ServiceProvider.GetRequiredService<ILoggerFactory>();

        var result = await AdminEndpointsLscc010.SelfRegisterUser(
            targetOrgId,
            new AdminEndpointsLscc010.SelfRegisterUserRequest(
                TenantId: null,
                Email: "legacy.referrer@example.com",
                Password: password,
                FirstName: "Legacy",
                LastName: "Referrer",
                Phone: "+15551234567"),
            invokeDb,
            invokePasswordHasher,
            provisioningEngine,
            userProductAccessService,
            auditClient,
            loggerFactory,
            CancellationToken.None);

        var statusResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status200OK, statusResult.StatusCode);

        var valueResult = Assert.IsAssignableFrom<IValueHttpResult>(result);
        var body = Assert.IsType<AdminEndpointsLscc010.SelfRegisterUserResponse>(valueResult.Value);
        Assert.Equal(existingUserId, body.UserId);
        Assert.False(body.IsNew);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<IdentityDbContext>();

        var normalizedUserCount = await verifyDb.Users
            .CountAsync(u => u.Email.Trim().ToLower() == "legacy.referrer@example.com");
        Assert.Equal(1, normalizedUserCount);

        Assert.True(await verifyDb.UserTenants.AnyAsync(ut =>
            ut.UserId == existingUserId && ut.TenantId == targetTenantId && ut.IsActive));
        Assert.True(await verifyDb.UserOrganizationMemberships.AnyAsync(m =>
            m.UserId == existingUserId && m.OrganizationId == targetOrgId && m.IsActive));
        Assert.True(await verifyDb.UserProductAccessRecords.AnyAsync(a =>
            a.UserId == existingUserId
            && a.TenantId == targetTenantId
            && a.ProductCode == CareConnectProductCode
            && a.AccessStatus == AccessStatus.Granted));
        Assert.True(await verifyDb.UserRoleAssignments.AnyAsync(a =>
            a.UserId == existingUserId
            && a.TenantId == targetTenantId
            && a.ProductCode == CareConnectProductCode
            && a.RoleCode == "CARECONNECT_REFERRER_ADMIN"
            && a.AssignmentStatus == AssignmentStatus.Active));
        Assert.Equal("+15551234567", await verifyDb.Users
            .Where(u => u.Id == existingUserId)
            .Select(u => u.Phone)
            .FirstOrDefaultAsync());
    }

    [Fact]
    public async Task SelfRegister_CreatesExplicitCareConnectProductAccess_ForNewUser()
    {
        using var factory = BuildFactory();
        Guid targetOrgId;
        Guid targetTenantId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            await EnsureCareConnectProductSeededAsync(db);

            var targetTenant = Tenant.Create("Target Tenant", $"target-{Guid.CreateVersion7():N}");
            db.Tenants.Add(targetTenant);

            var targetOrg = Organization.Create(targetTenant.Id, "Target Firm", OrgType.LawFirm, displayName: "Target Firm");
            db.Organizations.Add(targetOrg);

            await db.SaveChangesAsync();

            targetOrgId = targetOrg.Id;
            targetTenantId = targetTenant.Id;
        }

        using var invokeScope = factory.Services.CreateScope();
        var invokeDb = invokeScope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var invokePasswordHasher = invokeScope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var provisioningEngine = invokeScope.ServiceProvider.GetRequiredService<IProductProvisioningService>();
        var userProductAccessService = invokeScope.ServiceProvider.GetRequiredService<IUserProductAccessService>();
        var auditClient = invokeScope.ServiceProvider.GetRequiredService<IAuditEventClient>();
        var loggerFactory = invokeScope.ServiceProvider.GetRequiredService<ILoggerFactory>();

        var result = await AdminEndpointsLscc010.SelfRegisterUser(
            targetOrgId,
            new AdminEndpointsLscc010.SelfRegisterUserRequest(
                TenantId: null,
                Email: "new.referrer@example.com",
                Password: "NewPassword123!",
                FirstName: "New",
                LastName: "Referrer",
                Phone: "+15559876543"),
            invokeDb,
            invokePasswordHasher,
            provisioningEngine,
            userProductAccessService,
            auditClient,
            loggerFactory,
            CancellationToken.None);

        var statusResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status201Created, statusResult.StatusCode);

        var valueResult = Assert.IsAssignableFrom<IValueHttpResult>(result);
        var body = Assert.IsType<AdminEndpointsLscc010.SelfRegisterUserResponse>(valueResult.Value);
        Assert.True(body.IsNew);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<IdentityDbContext>();

        Assert.True(await verifyDb.UserProductAccessRecords.AnyAsync(a =>
            a.UserId == body.UserId
            && a.TenantId == targetTenantId
            && a.ProductCode == CareConnectProductCode
            && a.AccessStatus == AccessStatus.Granted));
        Assert.True(await verifyDb.UserRoleAssignments.AnyAsync(a =>
            a.UserId == body.UserId
            && a.TenantId == targetTenantId
            && a.ProductCode == CareConnectProductCode
            && a.RoleCode == "CARECONNECT_REFERRER_ADMIN"
            && a.AssignmentStatus == AssignmentStatus.Active));
        Assert.Equal("+15559876543", await verifyDb.Users
            .Where(u => u.Id == body.UserId)
            .Select(u => u.Phone)
            .FirstOrDefaultAsync());
    }

    [Fact]
    public async Task SelfRegister_AllowsMissingLastName_ForNewUser()
    {
        using var factory = BuildFactory();
        Guid targetOrgId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            await EnsureCareConnectProductSeededAsync(db);

            var targetTenant = Tenant.Create("Target Tenant", $"target-{Guid.CreateVersion7():N}");
            db.Tenants.Add(targetTenant);

            var targetOrg = Organization.Create(targetTenant.Id, "Target Firm", OrgType.LawFirm, displayName: "Target Firm");
            db.Organizations.Add(targetOrg);

            await db.SaveChangesAsync();

            targetOrgId = targetOrg.Id;
        }

        using var invokeScope = factory.Services.CreateScope();
        var invokeDb = invokeScope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var invokePasswordHasher = invokeScope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var provisioningEngine = invokeScope.ServiceProvider.GetRequiredService<IProductProvisioningService>();
        var userProductAccessService = invokeScope.ServiceProvider.GetRequiredService<IUserProductAccessService>();
        var auditClient = invokeScope.ServiceProvider.GetRequiredService<IAuditEventClient>();
        var loggerFactory = invokeScope.ServiceProvider.GetRequiredService<ILoggerFactory>();

        var result = await AdminEndpointsLscc010.SelfRegisterUser(
            targetOrgId,
            new AdminEndpointsLscc010.SelfRegisterUserRequest(
                TenantId: null,
                Email: "single.name@example.com",
                Password: "SingleName123!",
                FirstName: "Prince",
                LastName: null,
                Phone: "+15550123456",
                Title: "Dr."),
            invokeDb,
            invokePasswordHasher,
            provisioningEngine,
            userProductAccessService,
            auditClient,
            loggerFactory,
            CancellationToken.None);

        var statusResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status201Created, statusResult.StatusCode);

        var valueResult = Assert.IsAssignableFrom<IValueHttpResult>(result);
        var body = Assert.IsType<AdminEndpointsLscc010.SelfRegisterUserResponse>(valueResult.Value);
        Assert.True(body.IsNew);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<IdentityDbContext>();

        var savedUser = await verifyDb.Users.SingleAsync(u => u.Id == body.UserId);
        Assert.Equal("Dr.", savedUser.Title);
        Assert.Equal("Prince", savedUser.FirstName);
        Assert.Equal(string.Empty, savedUser.LastName);
        Assert.Equal("+15550123456", savedUser.Phone);
    }

    [Fact]
    public async Task SelfRegister_UpdatesPhone_ForExistingUserAlreadyInTenant()
    {
        using var factory = BuildFactory();
        var password = "TenantPassword123!";
        Guid targetOrgId;
        Guid existingUserId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            await EnsureCareConnectProductSeededAsync(db);

            var tenant = Tenant.Create("Target Tenant", $"target-{Guid.CreateVersion7():N}");
            db.Tenants.Add(tenant);

            var org = Organization.Create(tenant.Id, "Target Firm", OrgType.LawFirm, displayName: "Target Firm");
            db.Organizations.Add(org);

            var existingUser = User.Create(
                tenant.Id,
                "tenant.member@example.com",
                passwordHasher.Hash(password),
                "Tenant",
                "Member");
            db.Users.Add(existingUser);
            db.UserTenants.Add(UserTenant.Create(existingUser.Id, tenant.Id));

            var membership = UserOrganizationMembership.Create(existingUser.Id, org.Id, MemberRole.Member);
            membership.SetPrimary();
            db.UserOrganizationMemberships.Add(membership);

            await db.SaveChangesAsync();

            targetOrgId = org.Id;
            existingUserId = existingUser.Id;
        }

        using var invokeScope = factory.Services.CreateScope();
        var invokeDb = invokeScope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var invokePasswordHasher = invokeScope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var provisioningEngine = invokeScope.ServiceProvider.GetRequiredService<IProductProvisioningService>();
        var userProductAccessService = invokeScope.ServiceProvider.GetRequiredService<IUserProductAccessService>();
        var auditClient = invokeScope.ServiceProvider.GetRequiredService<IAuditEventClient>();
        var loggerFactory = invokeScope.ServiceProvider.GetRequiredService<ILoggerFactory>();

        var result = await AdminEndpointsLscc010.SelfRegisterUser(
            targetOrgId,
            new AdminEndpointsLscc010.SelfRegisterUserRequest(
                TenantId: null,
                Email: "tenant.member@example.com",
                Password: password,
                FirstName: "Tenant",
                LastName: "Member",
                Phone: "+15552223333"),
            invokeDb,
            invokePasswordHasher,
            provisioningEngine,
            userProductAccessService,
            auditClient,
            loggerFactory,
            CancellationToken.None);

        var statusResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status200OK, statusResult.StatusCode);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        Assert.Equal("+15552223333", await verifyDb.Users
            .Where(u => u.Id == existingUserId)
            .Select(u => u.Phone)
            .FirstOrDefaultAsync());
    }

    private static async Task EnsureCareConnectProductSeededAsync(IdentityDbContext db)
    {
        if (await db.Products.AnyAsync(p => p.Code == CareConnectProductCode))
            return;

        db.Products.Add(Product.Create("SynqCareConnect", CareConnectProductCode));
    }

    private static async Task<Guid> SeedExistingCrossTenantUserAsync(WebApplicationFactory<Program> factory, string email)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        await EnsureCareConnectProductSeededAsync(db);

        var homeTenant = Tenant.Create("Home Tenant", $"home-{Guid.CreateVersion7():N}");
        var targetTenant = Tenant.Create("Target Tenant", $"target-{Guid.CreateVersion7():N}");
        db.Tenants.AddRange(homeTenant, targetTenant);

        var homeOrg = Organization.Create(homeTenant.Id, "Home Firm", OrgType.LawFirm, displayName: "Home Firm");
        var targetOrg = Organization.Create(targetTenant.Id, "Target Firm", OrgType.LawFirm, displayName: "Target Firm");
        db.Organizations.AddRange(homeOrg, targetOrg);

        var user = User.Create(homeTenant.Id, email, "password-hash", "Lawyer", "User");
        db.Users.Add(user);
        db.UserTenants.Add(UserTenant.Create(user.Id, homeTenant.Id));

        var homeMembership = UserOrganizationMembership.Create(user.Id, homeOrg.Id, MemberRole.Member);
        homeMembership.SetPrimary();
        db.UserOrganizationMemberships.Add(homeMembership);
        GrantCareConnectReferrerAccess(db, homeTenant.Id, user.Id);

        await db.SaveChangesAsync();
        return targetTenant.Id;
    }

    private static async Task<Guid> SeedActiveTenantReferrerAsync(WebApplicationFactory<Program> factory, string email)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        await EnsureCareConnectProductSeededAsync(db);

        var tenant = Tenant.Create("Active Tenant", $"active-{Guid.CreateVersion7():N}");
        db.Tenants.Add(tenant);

        var org = Organization.Create(tenant.Id, "Active Firm", OrgType.LawFirm, displayName: "Active Firm");
        db.Organizations.Add(org);

        var user = User.Create(tenant.Id, email, "password-hash", "Active", "User");
        db.Users.Add(user);
        db.UserTenants.Add(UserTenant.Create(user.Id, tenant.Id));

        var membership = UserOrganizationMembership.Create(user.Id, org.Id, MemberRole.Member);
        membership.SetPrimary();
        db.UserOrganizationMemberships.Add(membership);
        GrantCareConnectReferrerAccess(db, tenant.Id, user.Id);

        await db.SaveChangesAsync();
        return tenant.Id;
    }

    private static async Task<Guid> SeedTenantMemberWithoutCareConnectReferrerAccessAsync(WebApplicationFactory<Program> factory, string email)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        await EnsureCareConnectProductSeededAsync(db);

        var tenant = Tenant.Create("Member Tenant", $"member-{Guid.CreateVersion7():N}");
        db.Tenants.Add(tenant);

        var org = Organization.Create(tenant.Id, "Member Firm", OrgType.LawFirm, displayName: "Member Firm");
        db.Organizations.Add(org);

        var user = User.Create(tenant.Id, email, "password-hash", "Member", "User");
        db.Users.Add(user);
        db.UserTenants.Add(UserTenant.Create(user.Id, tenant.Id));

        var membership = UserOrganizationMembership.Create(user.Id, org.Id, MemberRole.Member);
        membership.SetPrimary();
        db.UserOrganizationMemberships.Add(membership);

        await db.SaveChangesAsync();
        return tenant.Id;
    }

    private static async Task<Guid> SeedTenantOnlyAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();

        var tenant = Tenant.Create("Empty Tenant", $"empty-{Guid.CreateVersion7():N}");
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    private sealed record PortalAccessStatusResponse(string? Status);

    private sealed record AccountExistsResponse(bool Exists, Guid? TenantId);

    private sealed record UserDisplayResponse(
        bool Found,
        Guid UserId,
        Guid TenantId,
        string? Email,
        string? FirstName,
        string? LastName,
        string? DisplayName);

    private sealed record TenantOwnerDisplayResponse(
        bool Found,
        Guid? TenantId,
        Guid? OrganizationId,
        Guid? UserId,
        string? Email,
        string? FirstName,
        string? LastName,
        string? DisplayName,
        string? OrganizationName,
        string? OrganizationDisplayName);

    private sealed record SelfRegisterResponse(Guid UserId, bool IsNew);

    private static void SetEmailForTest(User user, string email)
    {
        typeof(User)
            .GetProperty(nameof(User.Email))!
            .SetValue(user, email);
    }

    private static void GrantCareConnectReferrerAccess(IdentityDbContext db, Guid tenantId, Guid userId)
    {
        var product = db.Products.Local.Single(p => p.Code == CareConnectProductCode);
        db.Set<TenantProduct>().Add(TenantProduct.Create(tenantId, product.Id));
        db.UserProductAccessRecords.Add(UserProductAccess.Create(tenantId, userId, CareConnectProductCode));
        db.UserRoleAssignments.Add(UserRoleAssignment.Create(
            tenantId,
            userId,
            "CARECONNECT_REFERRER",
            CareConnectProductCode));
    }
}
