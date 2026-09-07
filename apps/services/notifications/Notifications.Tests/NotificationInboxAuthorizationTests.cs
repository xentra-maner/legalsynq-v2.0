using System.Security.Claims;
using Notifications.Api.Authorization;
using Xunit;

namespace Notifications.Tests;

public sealed class NotificationInboxAuthorizationTests
{
    [Fact]
    public void IsUserPrincipal_AcceptsNonEmptyUserAndTenantClaims()
    {
        var principal = Principal(
            new("sub", Guid.NewGuid().ToString()),
            new("tenant_id", Guid.NewGuid().ToString()));

        Assert.True(NotificationInboxAuthorization.IsUserPrincipal(principal));
    }

    [Theory]
    [InlineData("missing-sub")]
    [InlineData("missing-tenant")]
    [InlineData("empty-sub")]
    [InlineData("empty-tenant")]
    [InlineData("service")]
    public void IsUserPrincipal_RejectsInvalidOrServiceIdentities(string scenario)
    {
        var claims = new List<Claim>();
        if (scenario != "missing-sub")
            claims.Add(new Claim("sub", scenario == "empty-sub" ? Guid.Empty.ToString() : Guid.NewGuid().ToString()));
        if (scenario != "missing-tenant")
            claims.Add(new Claim("tenant_id", scenario == "empty-tenant" ? Guid.Empty.ToString() : Guid.NewGuid().ToString()));
        if (scenario == "service")
            claims.Add(new Claim("svc", "liens-service"));

        Assert.False(NotificationInboxAuthorization.IsUserPrincipal(Principal(claims.ToArray())));
    }

    private static ClaimsPrincipal Principal(params Claim[] claims)
        => new(new ClaimsIdentity(claims, "test"));
}
