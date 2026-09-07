using System.Security.Claims;

namespace Notifications.Api.Authorization;

public static class NotificationInboxAuthorization
{
    public static bool IsUserPrincipal(ClaimsPrincipal principal)
        => principal.FindFirst("svc") is null &&
           Guid.TryParse(principal.FindFirst("sub")?.Value, out var userId) &&
           userId != Guid.Empty &&
           Guid.TryParse(principal.FindFirst("tenant_id")?.Value, out var tenantId) &&
           tenantId != Guid.Empty;
}
