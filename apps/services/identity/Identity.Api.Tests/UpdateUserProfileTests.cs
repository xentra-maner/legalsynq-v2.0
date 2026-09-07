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
/// Covers the PATCH /api/admin/users/{id} profile-edit handler
/// (<c>AdminEndpoints.UpdateUserProfile</c>), invoked via reflection against an
/// EF Core InMemory database — mirrors <see cref="AdminResetPasswordTests"/>.
/// </summary>
public class UpdateUserProfileTests
{
    private static readonly MethodInfo Handler =
        typeof(AdminEndpoints).GetMethod("UpdateUserProfile", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("UpdateUserProfile not found on AdminEndpoints.");

    private static readonly Type RequestType =
        typeof(AdminEndpoints).GetNestedType("UpdateUserProfileRequest", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("UpdateUserProfileRequest not found on AdminEndpoints.");

    private static object MakeRequest(
        string? firstName = null, string? lastName = null, string? email = null, string? title = null) =>
        Activator.CreateInstance(RequestType, firstName, lastName, email, title)!;

    private static Task<IResult> Invoke(
        Guid id, object body, ClaimsPrincipal caller, IdentityDbContext db, IAuditEventClient audit) =>
        (Task<IResult>)Handler.Invoke(null, [id, body, caller, db, audit, CancellationToken.None])!;

    private static IdentityDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<IdentityDbContext>()
            .UseInMemoryDatabase(Guid.CreateVersion7().ToString()).Options);

    private static ClaimsPrincipal TenantAdmin(Guid tenantId, Guid? adminId = null) =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, (adminId ?? Guid.CreateVersion7()).ToString()),
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.Role, "TenantAdmin"),
        ], "test"));

    private static (IdentityDbContext Db, Tenant Tenant, User User) Seed(string email = "alice@example.com")
    {
        var db = CreateDb();
        var tenant = Tenant.Create("Acme", $"acme-{Guid.CreateVersion7():N}");
        var user = User.Create(tenant.Id, email, "hash", "Alice", "Adams");
        db.Tenants.Add(tenant);
        db.Users.Add(user);
        db.UserTenants.Add(UserTenant.Create(user.Id, tenant.Id));
        db.SaveChanges();
        return (db, tenant, user);
    }

    [Fact]
    public async Task UpdatesName_Returns200_TouchesUpdatedAt_AndAudits()
    {
        var (db, tenant, user) = Seed();
        var originalUpdatedAt = user.UpdatedAtUtc;
        var audit = new RecordingAuditClient();
        await Task.Delay(5);

        var result = await Invoke(user.Id, MakeRequest(firstName: "Alicia", lastName: "Adams"),
            TenantAdmin(tenant.Id), db, audit);

        Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(200, ((IStatusCodeHttpResult)result).StatusCode);

        var reloaded = await db.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id);
        Assert.Equal("Alicia", reloaded.FirstName);
        Assert.True(reloaded.UpdatedAtUtc > originalUpdatedAt);
        Assert.Contains(audit.Events, e => e.EventType == "identity.user.profile_updated");
    }

    [Fact]
    public async Task ChangingEmail_BumpsSessionVersion()
    {
        var (db, tenant, user) = Seed("old@example.com");
        var result = await Invoke(user.Id, MakeRequest(email: "New@Example.com"),
            TenantAdmin(tenant.Id), db, new RecordingAuditClient());

        Assert.Equal(200, ((IStatusCodeHttpResult)result).StatusCode);
        var reloaded = await db.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id);
        Assert.Equal("new@example.com", reloaded.Email);
        Assert.Equal(1, reloaded.SessionVersion);
    }

    [Fact]
    public async Task DuplicateEmail_Returns409()
    {
        var (db, tenant, user) = Seed("alice@example.com");
        var other = User.Create(tenant.Id, "bob@example.com", "hash", "Bob", "Brown");
        db.Users.Add(other);
        db.UserTenants.Add(UserTenant.Create(other.Id, tenant.Id));
        await db.SaveChangesAsync();

        var result = await Invoke(user.Id, MakeRequest(email: "bob@example.com"),
            TenantAdmin(tenant.Id), db, new RecordingAuditClient());

        Assert.Equal(409, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task OnlyOneNamePart_Returns400()
    {
        var (db, tenant, user) = Seed();
        var result = await Invoke(user.Id, MakeRequest(firstName: "Solo"),
            TenantAdmin(tenant.Id), db, new RecordingAuditClient());

        Assert.Equal(400, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task CrossTenantCaller_Returns403()
    {
        var (db, _, user) = Seed();
        var result = await Invoke(user.Id, MakeRequest(firstName: "X", lastName: "Y"),
            TenantAdmin(Guid.CreateVersion7()), db, new RecordingAuditClient());

        Assert.IsType<ForbidHttpResult>(result);
    }

    [Fact]
    public async Task UnknownUser_Returns404()
    {
        var (db, tenant, _) = Seed();
        var result = await Invoke(Guid.CreateVersion7(), MakeRequest(firstName: "X", lastName: "Y"),
            TenantAdmin(tenant.Id), db, new RecordingAuditClient());

        Assert.IsType<NotFound>(result);
    }

    [Fact]
    public async Task NoOpBody_Returns200_WithoutAudit()
    {
        var (db, tenant, user) = Seed("alice@example.com");
        var audit = new RecordingAuditClient();

        var result = await Invoke(user.Id, MakeRequest(firstName: "Alice", lastName: "Adams", email: "alice@example.com"),
            TenantAdmin(tenant.Id), db, audit);

        Assert.Equal(200, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.Empty(audit.Events);
    }
}

file sealed class RecordingAuditClient : IAuditEventClient
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
