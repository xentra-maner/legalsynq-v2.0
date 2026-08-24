using Identity.Infrastructure.Data;
using Identity.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Identity.Tests;

public class EffectiveAccessServiceTests
{
    [Fact]
    public async Task GetEffectiveAccessAsync_DoesNotGrantTenantProductsWithoutExplicitAssignments()
    {
        var dbName = "effective-access-no-legacy-default-" + Guid.CreateVersion7();
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        await using var db = new IdentityDbContext(options);

        var tenant = Identity.Domain.Tenant.Create("Access Tenant", $"access-{Guid.CreateVersion7():N}");
        var product = Identity.Domain.Product.Create("SynqCareConnect", "SYNQ_CARECONNECT");
        var user = Identity.Domain.User.Create(tenant.Id, "user@example.com", "password-hash", "Test", "User");

        db.Tenants.Add(tenant);
        db.Products.Add(product);
        db.Users.Add(user);
        db.UserTenants.Add(Identity.Domain.UserTenant.Create(user.Id, tenant.Id));
        db.Set<Identity.Domain.TenantProduct>().Add(Identity.Domain.TenantProduct.Create(tenant.Id, product.Id));

        await db.SaveChangesAsync();

        var service = new EffectiveAccessService(
            db,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<EffectiveAccessService>.Instance);

        var result = await service.GetEffectiveAccessAsync(tenant.Id, user.Id);

        Assert.Empty(result.Products);
        Assert.Empty(result.ProductSources);
    }

    [Fact]
    public async Task GetEffectiveAccessAsync_ReturnsExplicitlyGrantedCareConnectProduct()
    {
        var dbName = "effective-access-explicit-careconnect-" + Guid.CreateVersion7();
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        await using var db = new IdentityDbContext(options);

        var tenant = Identity.Domain.Tenant.Create("Access Tenant", $"access-{Guid.CreateVersion7():N}");
        var product = Identity.Domain.Product.Create("SynqCareConnect", "SYNQ_CARECONNECT");
        var user = Identity.Domain.User.Create(tenant.Id, "user@example.com", "password-hash", "Test", "User");

        db.Tenants.Add(tenant);
        db.Products.Add(product);
        db.Users.Add(user);
        db.UserTenants.Add(Identity.Domain.UserTenant.Create(user.Id, tenant.Id));
        db.Set<Identity.Domain.TenantProduct>().Add(Identity.Domain.TenantProduct.Create(tenant.Id, product.Id));
        db.UserProductAccessRecords.Add(
            Identity.Domain.UserProductAccess.Create(tenant.Id, user.Id, "SYNQ_CARECONNECT"));

        await db.SaveChangesAsync();

        var service = new EffectiveAccessService(
            db,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<EffectiveAccessService>.Instance);

        var result = await service.GetEffectiveAccessAsync(tenant.Id, user.Id);

        Assert.Equal(["SYNQ_CARECONNECT"], result.Products);
        Assert.Contains(result.ProductSources, p => p.ProductCode == "SYNQ_CARECONNECT" && p.Source == "Direct");
    }

    [Fact]
    public async Task GetEffectiveAccessAsync_CareConnectReferrerAdmin_AlsoGetsReferrerRole()
    {
        // LSV3-1083: CARECONNECT_REFERRER_ADMIN (law-firm Super Admin/Manager) should be
        // able to do everything a plain CARECONNECT_REFERRER can, without needing a second
        // explicit role assignment.
        var dbName = "effective-access-referrer-admin-implies-referrer-" + Guid.CreateVersion7();
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        await using var db = new IdentityDbContext(options);

        var tenant = Identity.Domain.Tenant.Create("Access Tenant", $"access-{Guid.CreateVersion7():N}");
        var product = Identity.Domain.Product.Create("SynqCareConnect", "SYNQ_CARECONNECT");
        var user = Identity.Domain.User.Create(tenant.Id, "admin@example.com", "password-hash", "Test", "Admin");

        db.Tenants.Add(tenant);
        db.Products.Add(product);
        db.Users.Add(user);
        db.UserTenants.Add(Identity.Domain.UserTenant.Create(user.Id, tenant.Id));
        db.Set<Identity.Domain.TenantProduct>().Add(Identity.Domain.TenantProduct.Create(tenant.Id, product.Id));
        db.UserProductAccessRecords.Add(
            Identity.Domain.UserProductAccess.Create(tenant.Id, user.Id, "SYNQ_CARECONNECT"));
        db.UserRoleAssignments.Add(
            Identity.Domain.UserRoleAssignment.Create(tenant.Id, user.Id, "CARECONNECT_REFERRER_ADMIN", "SYNQ_CARECONNECT"));

        await db.SaveChangesAsync();

        var service = new EffectiveAccessService(
            db,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<EffectiveAccessService>.Instance);

        var result = await service.GetEffectiveAccessAsync(tenant.Id, user.Id);

        Assert.Contains("SYNQ_CARECONNECT:CARECONNECT_REFERRER_ADMIN", result.ProductRolesFlat);
        Assert.Contains("SYNQ_CARECONNECT:CARECONNECT_REFERRER", result.ProductRolesFlat);
        Assert.Contains(result.RoleSources, r => r.RoleCode == "CARECONNECT_REFERRER" && r.Source == "Implied");
    }

    [Fact]
    public async Task GetEffectiveAccessAsync_CareConnectReferrer_DoesNotGainReferrerAdmin()
    {
        // The implication only runs one direction — a plain Referrer must not silently
        // become a Super Admin/Manager.
        var dbName = "effective-access-referrer-does-not-imply-admin-" + Guid.CreateVersion7();
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        await using var db = new IdentityDbContext(options);

        var tenant = Identity.Domain.Tenant.Create("Access Tenant", $"access-{Guid.CreateVersion7():N}");
        var product = Identity.Domain.Product.Create("SynqCareConnect", "SYNQ_CARECONNECT");
        var user = Identity.Domain.User.Create(tenant.Id, "referrer@example.com", "password-hash", "Test", "Referrer");

        db.Tenants.Add(tenant);
        db.Products.Add(product);
        db.Users.Add(user);
        db.UserTenants.Add(Identity.Domain.UserTenant.Create(user.Id, tenant.Id));
        db.Set<Identity.Domain.TenantProduct>().Add(Identity.Domain.TenantProduct.Create(tenant.Id, product.Id));
        db.UserProductAccessRecords.Add(
            Identity.Domain.UserProductAccess.Create(tenant.Id, user.Id, "SYNQ_CARECONNECT"));
        db.UserRoleAssignments.Add(
            Identity.Domain.UserRoleAssignment.Create(tenant.Id, user.Id, "CARECONNECT_REFERRER", "SYNQ_CARECONNECT"));

        await db.SaveChangesAsync();

        var service = new EffectiveAccessService(
            db,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<EffectiveAccessService>.Instance);

        var result = await service.GetEffectiveAccessAsync(tenant.Id, user.Id);

        Assert.Contains("SYNQ_CARECONNECT:CARECONNECT_REFERRER", result.ProductRolesFlat);
        Assert.DoesNotContain("SYNQ_CARECONNECT:CARECONNECT_REFERRER_ADMIN", result.ProductRolesFlat);
    }
}
