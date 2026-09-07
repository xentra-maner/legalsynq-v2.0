using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Identity.Api.Endpoints;
using Identity.Domain;
using Identity.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Identity.Api.Tests;

/// <summary>
/// Guards the GET /api/admin/roles list: tenant scoping for non-PlatformAdmin
/// callers, and the projection fields the tenant-portal Roles table needs
/// (<c>permissions</c> codes, <c>createdAtUtc</c>, <c>updatedAtUtc</c>).
/// </summary>
public class ListRolesProjectionTests
{
    private static readonly MethodInfo Handler =
        typeof(AdminEndpoints).GetMethod("ListRoles", BindingFlags.NonPublic | BindingFlags.Static)!;

    [Fact]
    public async Task NonPlatformAdmin_SeesOnlyOwnTenantRoles_WithProjectionFields()
    {
        var db = new IdentityDbContext(new DbContextOptionsBuilder<IdentityDbContext>()
            .UseInMemoryDatabase(Guid.CreateVersion7().ToString()).Options);

        var tenantA = Tenant.Create("A", $"a-{Guid.CreateVersion7():N}");
        var tenantB = Tenant.Create("B", $"b-{Guid.CreateVersion7():N}");
        db.Tenants.AddRange(tenantA, tenantB);

        var productId = Guid.CreateVersion7();
        var perm = Permission.Create(productId, "SYNQ_LIENS.case:read", "Read Case", category: "Case");
        db.Permissions.Add(perm);

        var roleA = Role.Create(tenantA.Id, "Reviewer A", "desc", isSystemRole: false, scope: "Tenant");
        var roleB = Role.Create(tenantB.Id, "Reviewer B", "desc", isSystemRole: false, scope: "Tenant");
        db.Roles.AddRange(roleA, roleB);
        db.RolePermissionAssignments.Add(RolePermissionAssignment.Create(roleA.Id, perm.Id));
        await db.SaveChangesAsync();

        var caller = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, Guid.CreateVersion7().ToString()),
            new Claim("tenant_id", tenantA.Id.ToString()),
            new Claim(ClaimTypes.Role, "TenantAdmin"),
        ], "test"));

        var result = await (Task<IResult>)Handler.Invoke(null, [db, caller, 1, 20, "", ""])!;

        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value!;
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value));
        var items = doc.RootElement.GetProperty("items");

        Assert.Equal(1, items.GetArrayLength());
        var item = items[0];
        Assert.Equal("Reviewer A", item.GetProperty("name").GetString());
        Assert.Equal("SYNQ_LIENS.case:read", item.GetProperty("permissions")[0].GetString());
        Assert.True(item.TryGetProperty("createdAtUtc", out _));
        Assert.True(item.TryGetProperty("updatedAtUtc", out _));
    }
}
