using System.Reflection;
using System.Security.Claims;
using Identity.Api.Endpoints;
using Identity.Domain;
using Identity.Infrastructure.Data;
using Identity.Infrastructure.Services;
using LegalSynq.AuditClient;
using LegalSynq.AuditClient.DTOs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Identity.Api.Tests;

/// <summary>
/// Covers the POST /api/admin/users create-with-password handler
/// (<c>AdminEndpoints.CreateUserWithPassword</c>), invoked via reflection against
/// an EF Core InMemory database.
/// </summary>
public class AdminCreateUserWithPasswordTests
{
    private static readonly MethodInfo Handler =
        typeof(AdminEndpoints).GetMethod("CreateUserWithPassword", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("CreateUserWithPassword not found on AdminEndpoints.");

    private static readonly Type RequestType =
        typeof(AdminEndpoints).GetNestedType("AdminCreateUserRequest", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("AdminCreateUserRequest not found on AdminEndpoints.");

    private static object MakeRequest(
        Guid tenantId, string email, string first, string last, string password,
        Guid? roleId = null, Guid? organizationId = null) =>
        Activator.CreateInstance(RequestType, tenantId, email, first, last, password, roleId, organizationId)!;

    private static Task<IResult> Invoke(object body, ClaimsPrincipal caller, IdentityDbContext db) =>
        (Task<IResult>)Handler.Invoke(null,
            [body, caller, db, new BcryptPasswordHasher(), new NoOpAudit(), CancellationToken.None])!;

    private static IdentityDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<IdentityDbContext>()
            .UseInMemoryDatabase(Guid.CreateVersion7().ToString()).Options);

    private static ClaimsPrincipal TenantAdmin(Guid tenantId) =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, Guid.CreateVersion7().ToString()),
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.Role, "TenantAdmin"),
        ], "test"));

    private static (IdentityDbContext Db, Tenant Tenant) SeedTenant()
    {
        var db = CreateDb();
        var tenant = Tenant.Create("Acme", $"acme-{Guid.CreateVersion7():N}");
        db.Tenants.Add(tenant);
        db.SaveChanges();
        return (db, tenant);
    }

    [Fact]
    public async Task HappyPath_CreatesActiveUser_WithTenantRoleAndMembership()
    {
        var (db, tenant) = SeedTenant();
        var role = Role.Create(tenant.Id, "SYNQLIEN_SELLER", scope: "Product");
        var org = Organization.Create(tenant.Id, "Acme Law", OrgType.LawFirm, displayName: "Acme Law");
        db.Roles.Add(role);
        db.Organizations.Add(org);
        await db.SaveChangesAsync();

        var result = await Invoke(
            MakeRequest(tenant.Id, "New.User@Example.com", "New", "User", "Sup3rSecret!", role.Id, org.Id),
            TenantAdmin(tenant.Id), db);

        Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(201, ((IStatusCodeHttpResult)result).StatusCode);

        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Email == "new.user@example.com");
        Assert.True(user.IsActive);
        Assert.True(new BcryptPasswordHasher().Verify("Sup3rSecret!", user.PasswordHash));
        Assert.True(await db.UserTenants.AnyAsync(ut => ut.UserId == user.Id && ut.TenantId == tenant.Id && ut.IsActive));
        Assert.True(await db.ScopedRoleAssignments.AnyAsync(s => s.UserId == user.Id && s.RoleId == role.Id));
        Assert.True(await db.UserOrganizationMemberships.AnyAsync(m => m.UserId == user.Id && m.OrganizationId == org.Id));
    }

    [Fact]
    public async Task ShortPassword_Returns400()
    {
        var (db, tenant) = SeedTenant();
        var result = await Invoke(MakeRequest(tenant.Id, "a@b.com", "A", "B", "short"), TenantAdmin(tenant.Id), db);
        Assert.Equal(400, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task DuplicateEmail_Returns409()
    {
        var (db, tenant) = SeedTenant();
        var existing = User.Create(tenant.Id, "taken@example.com", "hash", "T", "K");
        db.Users.Add(existing);
        db.UserTenants.Add(UserTenant.Create(existing.Id, tenant.Id));
        await db.SaveChangesAsync();

        var result = await Invoke(
            MakeRequest(tenant.Id, "taken@example.com", "New", "User", "Sup3rSecret!"),
            TenantAdmin(tenant.Id), db);

        Assert.Equal(409, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task OrganizationFromAnotherTenant_Returns400()
    {
        var (db, tenant) = SeedTenant();
        var otherTenant = Tenant.Create("Other", $"other-{Guid.CreateVersion7():N}");
        var foreignOrg = Organization.Create(otherTenant.Id, "Foreign", OrgType.LawFirm);
        db.Tenants.Add(otherTenant);
        db.Organizations.Add(foreignOrg);
        await db.SaveChangesAsync();

        var result = await Invoke(
            MakeRequest(tenant.Id, "x@example.com", "X", "Y", "Sup3rSecret!", organizationId: foreignOrg.Id),
            TenantAdmin(tenant.Id), db);

        Assert.Equal(400, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task UnknownRole_Returns400()
    {
        var (db, tenant) = SeedTenant();
        var result = await Invoke(
            MakeRequest(tenant.Id, "x@example.com", "X", "Y", "Sup3rSecret!", roleId: Guid.CreateVersion7()),
            TenantAdmin(tenant.Id), db);

        Assert.Equal(400, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task CrossTenantCaller_Returns403()
    {
        var (db, tenant) = SeedTenant();
        var result = await Invoke(
            MakeRequest(tenant.Id, "x@example.com", "X", "Y", "Sup3rSecret!"),
            TenantAdmin(Guid.CreateVersion7()), db);

        Assert.IsType<ForbidHttpResult>(result);
    }
}

file sealed class NoOpAudit : IAuditEventClient
{
    public Task<IngestResult> IngestAsync(IngestAuditEventRequest request, CancellationToken ct = default) =>
        Task.FromResult(new IngestResult(true, Guid.CreateVersion7().ToString(), null, 202));

    public Task<BatchIngestResult> IngestBatchAsync(BatchIngestRequest request, CancellationToken ct = default) =>
        Task.FromResult(new BatchIngestResult(0, 0, 0, []));
}
