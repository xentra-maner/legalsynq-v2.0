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
/// Guards the shape of the GET /api/admin/users list projection — specifically
/// that <c>updatedAtUtc</c> is exposed so the tenant portal can render a
/// "Last Date Edited" column.
/// </summary>
public class ListUsersProjectionTests
{
    private static readonly MethodInfo Handler =
        typeof(AdminEndpoints).GetMethod("ListUsers", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ListUsers not found on AdminEndpoints.");

    [Fact]
    public async Task ListItems_Include_CreatedAndUpdatedTimestamps()
    {
        var db = new IdentityDbContext(new DbContextOptionsBuilder<IdentityDbContext>()
            .UseInMemoryDatabase(Guid.CreateVersion7().ToString()).Options);
        var tenant = Tenant.Create("Acme", $"acme-{Guid.CreateVersion7():N}");
        var user = User.Create(tenant.Id, "alice@example.com", "hash", "Alice", "Adams");
        db.Tenants.Add(tenant);
        db.Users.Add(user);
        db.UserTenants.Add(UserTenant.Create(user.Id, tenant.Id));
        await db.SaveChangesAsync();

        var caller = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, Guid.CreateVersion7().ToString()),
            new Claim("tenant_id", tenant.Id.ToString()),
            new Claim(ClaimTypes.Role, "TenantAdmin"),
        ], "test"));

        var result = await (Task<IResult>)Handler.Invoke(null,
            [db, caller, 1, 20, "", "", "", "", ""])!;

        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value!;
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value));
        var item = doc.RootElement.GetProperty("items")[0];

        Assert.True(item.TryGetProperty("updatedAtUtc", out _), "list item must expose updatedAtUtc");
        Assert.True(item.TryGetProperty("createdAtUtc", out _), "list item must expose createdAtUtc");
    }
}
