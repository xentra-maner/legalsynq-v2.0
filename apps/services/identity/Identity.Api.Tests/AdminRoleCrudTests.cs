using System.Reflection;
using System.Security.Claims;
using Identity.Api.Endpoints;
using Identity.Domain;
using Identity.Infrastructure.Data;
using LegalSynq.AuditClient;
using LegalSynq.AuditClient.DTOs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Identity.Api.Tests;

/// <summary>
/// Covers the tenant-portal Role Management handlers on <c>AdminEndpoints</c>
/// (<c>CreateRole</c> / <c>UpdateRole</c> / <c>DeleteRole</c>), invoked via
/// reflection against an EF Core InMemory database.
/// </summary>
public class AdminRoleCrudTests
{
    private static readonly Type CreateReq =
        typeof(AdminEndpoints).GetNestedType("CreateRoleRequest", BindingFlags.NonPublic)!;
    private static readonly Type UpdateReq =
        typeof(AdminEndpoints).GetNestedType("UpdateRoleRequest", BindingFlags.NonPublic)!;
    private static readonly MethodInfo CreateRole =
        typeof(AdminEndpoints).GetMethod("CreateRole", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo UpdateRole =
        typeof(AdminEndpoints).GetMethod("UpdateRole", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo DeleteRole =
        typeof(AdminEndpoints).GetMethod("DeleteRole", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static object CreateBody(string name, string? desc, params string[] codes) =>
        Activator.CreateInstance(CreateReq, name, desc, codes)!;
    private static object UpdateBody(string name, string? desc, params string[] codes) =>
        Activator.CreateInstance(UpdateReq, name, desc, (IReadOnlyList<string>)codes)!;

    private static Task<IResult> InvokeCreate(object body, ClaimsPrincipal caller, IdentityDbContext db, IAuditEventClient a) =>
        (Task<IResult>)CreateRole.Invoke(null, [body, caller, db, a, "", CancellationToken.None])!;
    private static Task<IResult> InvokeUpdate(Guid id, object body, ClaimsPrincipal caller, IdentityDbContext db, IAuditEventClient a) =>
        (Task<IResult>)UpdateRole.Invoke(null, [id, body, caller, db, a, CancellationToken.None])!;
    private static Task<IResult> InvokeDelete(Guid id, ClaimsPrincipal caller, IdentityDbContext db, IAuditEventClient a) =>
        (Task<IResult>)DeleteRole.Invoke(null, [id, caller, db, a, CancellationToken.None])!;

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

    private static ClaimsPrincipal PlainUser(Guid tenantId, params string[] permissions)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.CreateVersion7().ToString()),
            new("tenant_id", tenantId.ToString()),
        };
        claims.AddRange(permissions.Select(p => new Claim("permissions", p)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private const string CaseRead = "SYNQ_LIENS.case:read";
    private const string CaseCreate = "SYNQ_LIENS.case:create";
    private const string LienRead = "SYNQ_LIENS.lien:read";

    private static (IdentityDbContext Db, Tenant Tenant) Seed()
    {
        var db = CreateDb();
        var tenant = Tenant.Create("Acme", $"acme-{Guid.CreateVersion7():N}");
        db.Tenants.Add(tenant);
        var productId = Guid.CreateVersion7();
        foreach (var code in new[] { CaseRead, CaseCreate, LienRead })
            db.Permissions.Add(Permission.Create(productId, code, code, category: "Case"));
        db.SaveChanges();
        return (db, tenant);
    }

    private static Guid PermId(IdentityDbContext db, string code) =>
        db.Permissions.AsNoTracking().Single(p => p.Code == code).Id;

    [Fact]
    public async Task Create_CustomRole_Returns201_WithPermissionsAndAudit()
    {
        var (db, tenant) = Seed();
        var audit = new RecordingAudit();

        var result = await InvokeCreate(CreateBody("Reviewer", "Reviews cases", CaseRead, LienRead),
            TenantAdmin(tenant.Id), db, audit);

        Assert.Equal(201, ((IStatusCodeHttpResult)result).StatusCode);
        var role = db.Roles.AsNoTracking().Single(r => r.Name == "Reviewer");
        Assert.False(role.IsSystemRole);
        Assert.Equal(tenant.Id, role.TenantId);
        Assert.Equal(2, db.RolePermissionAssignments.Count(a => a.RoleId == role.Id));
        Assert.Contains(audit.Events, e => e.EventType == "identity.role.created");
    }

    [Fact]
    public async Task Create_DuplicateName_SameTenant_Returns409()
    {
        var (db, tenant) = Seed();
        await InvokeCreate(CreateBody("Reviewer", null, CaseRead), TenantAdmin(tenant.Id), db, new RecordingAudit());
        var result = await InvokeCreate(CreateBody("reviewer", null, CaseRead), TenantAdmin(tenant.Id), db, new RecordingAudit());
        Assert.Equal(409, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task Create_UnknownPermissionCode_Returns400()
    {
        var (db, tenant) = Seed();
        var result = await InvokeCreate(CreateBody("Reviewer", null, "SYNQ_LIENS.case:read", "SYNQ_LIENS.nope:nope"),
            TenantAdmin(tenant.Id), db, new RecordingAudit());
        Assert.Equal(400, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task Create_NoPermissions_Returns400()
    {
        var (db, tenant) = Seed();
        var result = await InvokeCreate(CreateBody("Reviewer", null), TenantAdmin(tenant.Id), db, new RecordingAudit());
        Assert.Equal(400, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task Create_NonAdmin_RequestingUnheldPermission_Returns403()
    {
        var (db, tenant) = Seed();
        var result = await InvokeCreate(CreateBody("Reviewer", null, CaseRead, CaseCreate),
            PlainUser(tenant.Id, CaseRead), db, new RecordingAudit());
        Assert.Equal(403, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task Update_ReplacesPermissionSet_RenamesAndBumpsHolderAccessVersion()
    {
        var (db, tenant) = Seed();
        await InvokeCreate(CreateBody("Reviewer", "old", CaseRead), TenantAdmin(tenant.Id), db, new RecordingAudit());
        var role = db.Roles.AsNoTracking().Single(r => r.Name == "Reviewer");

        var holder = User.Create(tenant.Id, "holder@example.com", "hash", "H", "older");
        db.Users.Add(holder);
        db.UserTenants.Add(UserTenant.Create(holder.Id, tenant.Id));
        db.ScopedRoleAssignments.Add(ScopedRoleAssignment.Create(
            holder.Id, role.Id, ScopedRoleAssignment.ScopeTypes.Global, tenantId: tenant.Id));
        await db.SaveChangesAsync();
        var versionBefore = holder.AccessVersion;

        var result = await InvokeUpdate(role.Id, UpdateBody("Reviewer II", "new", LienRead, CaseCreate),
            TenantAdmin(tenant.Id), db, new RecordingAudit());

        Assert.Equal(200, ((IStatusCodeHttpResult)result).StatusCode);
        var reloaded = db.Roles.AsNoTracking().Single(r => r.Id == role.Id);
        Assert.Equal("Reviewer II", reloaded.Name);
        var codes = db.RolePermissionAssignments.AsNoTracking()
            .Where(a => a.RoleId == role.Id)
            .Join(db.Permissions.AsNoTracking(), a => a.PermissionId, p => p.Id, (_, p) => p.Code)
            .OrderBy(c => c).ToArray();
        Assert.Equal(new[] { CaseCreate, LienRead }.OrderBy(c => c).ToArray(), codes);
        Assert.Equal(versionBefore + 1, db.Users.AsNoTracking().Single(u => u.Id == holder.Id).AccessVersion);
    }

    [Fact]
    public async Task Update_SystemRole_Returns409()
    {
        var (db, tenant) = Seed();
        var sysRole = Role.Create(tenant.Id, "TenantAdmin", "system", isSystemRole: true, scope: "Tenant");
        db.Roles.Add(sysRole);
        await db.SaveChangesAsync();

        var result = await InvokeUpdate(sysRole.Id, UpdateBody("TenantAdmin", "x", CaseRead),
            TenantAdmin(tenant.Id), db, new RecordingAudit());
        Assert.Equal(409, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task Update_CrossTenant_Returns403()
    {
        var (db, tenant) = Seed();
        await InvokeCreate(CreateBody("Reviewer", null, CaseRead), TenantAdmin(tenant.Id), db, new RecordingAudit());
        var role = db.Roles.AsNoTracking().Single(r => r.Name == "Reviewer");

        var result = await InvokeUpdate(role.Id, UpdateBody("Reviewer", null, CaseRead),
            TenantAdmin(Guid.CreateVersion7()), db, new RecordingAudit());
        Assert.IsType<ForbidHttpResult>(result);
    }

    [Fact]
    public async Task Delete_UnusedCustomRole_Returns204_AndRemovesAssignments()
    {
        var (db, tenant) = Seed();
        await InvokeCreate(CreateBody("Reviewer", null, CaseRead), TenantAdmin(tenant.Id), db, new RecordingAudit());
        var role = db.Roles.AsNoTracking().Single(r => r.Name == "Reviewer");

        var result = await InvokeDelete(role.Id, TenantAdmin(tenant.Id), db, new RecordingAudit());

        Assert.IsType<NoContent>(result);
        Assert.False(db.Roles.AsNoTracking().Any(r => r.Id == role.Id));
        Assert.False(db.RolePermissionAssignments.AsNoTracking().Any(a => a.RoleId == role.Id));
    }

    [Fact]
    public async Task Delete_RoleInUse_Returns409()
    {
        var (db, tenant) = Seed();
        await InvokeCreate(CreateBody("Reviewer", null, CaseRead), TenantAdmin(tenant.Id), db, new RecordingAudit());
        var role = db.Roles.AsNoTracking().Single(r => r.Name == "Reviewer");

        var holder = User.Create(tenant.Id, "h@example.com", "hash", "H", "H");
        db.Users.Add(holder);
        db.ScopedRoleAssignments.Add(ScopedRoleAssignment.Create(
            holder.Id, role.Id, ScopedRoleAssignment.ScopeTypes.Global, tenantId: tenant.Id));
        await db.SaveChangesAsync();

        var result = await InvokeDelete(role.Id, TenantAdmin(tenant.Id), db, new RecordingAudit());
        Assert.Equal(409, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task Delete_SystemRole_Returns409()
    {
        var (db, tenant) = Seed();
        var sysRole = Role.Create(tenant.Id, "TenantAdmin", "system", isSystemRole: true, scope: "Tenant");
        db.Roles.Add(sysRole);
        await db.SaveChangesAsync();

        var result = await InvokeDelete(sysRole.Id, TenantAdmin(tenant.Id), db, new RecordingAudit());
        Assert.Equal(409, ((IStatusCodeHttpResult)result).StatusCode);
    }
}

file sealed class RecordingAudit : IAuditEventClient
{
    public List<IngestAuditEventRequest> Events { get; } = [];

    public Task<IngestResult> IngestAsync(IngestAuditEventRequest request, CancellationToken ct = default)
    {
        Events.Add(request);
        return Task.FromResult(new IngestResult(true, Guid.CreateVersion7().ToString(), null, 202));
    }

    public Task<BatchIngestResult> IngestBatchAsync(BatchIngestRequest request, CancellationToken ct = default) =>
        Task.FromResult(new BatchIngestResult(0, 0, 0, []));
}
