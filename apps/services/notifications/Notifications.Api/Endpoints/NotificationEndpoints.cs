using BuildingBlocks.Authorization;
using Notifications.Api.Authorization;
using Notifications.Api.Middleware;
using Notifications.Application.DTOs;
using Notifications.Application.Interfaces;

namespace Notifications.Api.Endpoints;

public static class NotificationEndpoints
{
    public static void MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/notifications").WithTags("Notifications");

        // ── POST /v1/notifications ────────────────────────────────────────────
        // LS-NOTIF-CORE-021: Secured via ServiceSubmission policy.
        // Requires the caller to present a service JWT (svc claim present).
        // Ordinary user JWTs and unauthenticated callers are rejected.
        group.MapPost("/", async (HttpContext context, INotificationService service, ILoggerFactory loggerFactory, SubmitNotificationDto request) =>
        {
            var tenantId = context.GetTenantId();
            NotificationResultDto result;
            try
            {
                result = await service.SubmitAsync(tenantId, request);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            if (result.Status != "sent" && result.Status != "blocked")
            {
                var logger = loggerFactory.CreateLogger("Notifications.Api.Endpoints");
                logger.LogWarning(
                    "[NOTIF-WARN] Notification {NotificationId} submitted but not delivered. " +
                    "Status={Status} FailureCategory={FailureCategory} LastError={LastError} " +
                    "TenantId={TenantId}",
                    result.Id, result.Status,
                    result.FailureCategory ?? "(none)",
                    result.LastErrorMessage ?? "(none)",
                    tenantId);
            }

            return result.Status == "blocked"
                ? Results.Json(result, statusCode: 422)
                : Results.Created($"/v1/notifications/{result.Id}", result);
        })
        .RequireAuthorization(Policies.ServiceSubmission);

        // ── GET /v1/notifications/stats ───────────────────────────────────────
        // Must be registered BEFORE /{id:guid} to avoid routing ambiguity
        group.MapGet("/stats", async (
            HttpContext context,
            INotificationService service,
            DateTime? from,
            DateTime? to,
            string? channel,
            string? status,
            string? provider,
            string? productKey) =>
        {
            var tenantId = context.GetTenantIdFromClaims();
            var query = new NotificationStatsQuery
            {
                From       = from,
                To         = to,
                Channel    = channel,
                Status     = status,
                Provider   = provider,
                ProductKey = productKey,
            };
            var result = await service.GetStatsAsync(tenantId, query);
            return Results.Ok(result);
        })
        .RequireAuthorization(Policies.AuthenticatedUser);

        // ── GET /v1/notifications ─────────────────────────────────────────────
        group.MapGet("/", async (
            HttpContext context,
            INotificationService service,
            int? page,
            int? pageSize,
            string? status,
            string? channel,
            string? provider,
            string? recipient,
            string? productKey,
            DateTime? from,
            DateTime? to,
            string? sortBy,
            string? sortDirection,
            // Backward-compat params still supported
            int? limit,
            int? offset) =>
        {
            var tenantId = context.GetTenantIdFromClaims();

            var usesPaged = page.HasValue || pageSize.HasValue || status != null || channel != null
                         || provider != null || recipient != null || productKey != null
                         || from.HasValue || to.HasValue || sortBy != null || sortDirection != null;

            if (usesPaged)
            {
                var query = new NotificationListQuery
                {
                    Page          = page ?? 1,
                    PageSize      = pageSize ?? limit ?? 50,
                    Status        = status,
                    Channel       = channel,
                    Provider      = provider,
                    Recipient     = recipient,
                    ProductKey    = productKey,
                    From          = from,
                    To            = to,
                    SortBy        = sortBy,
                    SortDirection = sortDirection,
                };
                var result = await service.ListPagedAsync(tenantId, query);
                return Results.Ok(result);
            }

            // Legacy path: backward-compatible raw list
            var items = await service.ListAsync(tenantId, limit ?? 50, offset ?? 0);
            return Results.Ok(items);
        })
        .RequireAuthorization(Policies.AuthenticatedUser);

        // ── GET /v1/notifications/{id} ────────────────────────────────────────
        group.MapGet("/{id:guid}", async (HttpContext context, INotificationService service, Guid id) =>
        {
            var tenantId = context.GetTenantIdFromClaims();
            var result = await service.GetByIdAsync(tenantId, id);
            return result != null ? Results.Ok(result) : Results.NotFound();
        })
        .RequireAuthorization(Policies.AuthenticatedUser);

        // ── GET /v1/notifications/{id}/events ─────────────────────────────────
        group.MapGet("/{id:guid}/events", async (HttpContext context, INotificationService service, Guid id) =>
        {
            var tenantId = context.GetTenantIdFromClaims();
            var result = await service.GetEventsAsync(tenantId, id);
            if (result.Count == 0)
            {
                var exists = await service.GetByIdAsync(tenantId, id);
                if (exists == null) return Results.NotFound();
            }
            return Results.Ok(result);
        })
        .RequireAuthorization(Policies.AuthenticatedUser);

        // ── GET /v1/notifications/{id}/issues ─────────────────────────────────
        group.MapGet("/{id:guid}/issues", async (HttpContext context, INotificationService service, Guid id) =>
        {
            var tenantId = context.GetTenantIdFromClaims();
            var result = await service.GetIssuesAsync(tenantId, id);
            var exists = await service.GetByIdAsync(tenantId, id);
            if (exists == null) return Results.NotFound();
            return Results.Ok(result);
        })
        .RequireAuthorization(Policies.AuthenticatedUser);

        // ── POST /v1/notifications/{id}/retry ────────────────────────────────
        group.MapPost("/{id:guid}/retry", async (HttpContext context, INotificationService service, Guid id) =>
        {
            var tenantId    = context.GetTenantIdFromClaims();
            var actorUserId = context.GetUserContext().UserId;
            var result      = await service.RetryAsync(tenantId, id, actorUserId);
            if (result == null) return Results.NotFound();

            if (result.FailureCategory == "not_retryable")
                return Results.Json(result, statusCode: 422);

            return Results.Ok(result);
        })
        .RequireAuthorization(Policies.AuthenticatedUser);

        // ── POST /v1/notifications/{id}/resend ───────────────────────────────
        group.MapPost("/{id:guid}/resend", async (HttpContext context, INotificationService service, Guid id) =>
        {
            var tenantId    = context.GetTenantIdFromClaims();
            var actorUserId = context.GetUserContext().UserId;
            var result      = await service.ResendAsync(tenantId, id, actorUserId);
            if (result == null) return Results.NotFound();
            return Results.Created($"/v1/notifications/{result.NewNotificationId}", result);
        })
        .RequireAuthorization(Policies.AuthenticatedUser);
    }
}
