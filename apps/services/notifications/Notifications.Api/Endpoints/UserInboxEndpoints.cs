using BuildingBlocks.Authorization;
using Notifications.Api.Authorization;
using Notifications.Application.DTOs;
using Notifications.Application.Interfaces;

namespace Notifications.Api.Endpoints;

public static class UserInboxEndpoints
{
    private static readonly HashSet<int> AllowedPageSizes = [10, 25, 50];
    private static readonly HashSet<string> AllowedCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "all", "lien", "message",
    };
    private static readonly HashSet<string> AllowedReadStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "all", "unread",
    };

    public static void MapUserInboxEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/inbox")
            .WithTags("User Inbox")
            .RequireAuthorization(Policies.NotificationInboxUser);

        group.MapGet("/", async (
            HttpContext context,
            IUserInboxService service,
            string? category,
            string? readState,
            int? page,
            int? pageSize,
            DateTime? asOfUtc,
            CancellationToken ct) =>
        {
            var normalizedCategory = string.IsNullOrWhiteSpace(category) ? "all" : category.Trim().ToLowerInvariant();
            var normalizedReadState = string.IsNullOrWhiteSpace(readState) ? "all" : readState.Trim().ToLowerInvariant();
            var resolvedPage = page ?? 1;
            var resolvedPageSize = pageSize ?? 10;

            if (!AllowedCategories.Contains(normalizedCategory))
                return Results.BadRequest(new { error = "category must be all, lien, or message." });
            if (!AllowedReadStates.Contains(normalizedReadState))
                return Results.BadRequest(new { error = "readState must be all or unread." });
            if (resolvedPage < 1)
                return Results.BadRequest(new { error = "page must be greater than zero." });
            if (!AllowedPageSizes.Contains(resolvedPageSize))
                return Results.BadRequest(new { error = "pageSize must be 10, 25, or 50." });
            if (resolvedPage > int.MaxValue / resolvedPageSize)
                return Results.BadRequest(new { error = "page is too large." });

            var (tenantId, userId) = GetScope(context);
            var result = await service.ListAsync(
                tenantId, userId, normalizedCategory, normalizedReadState,
                resolvedPage, resolvedPageSize, asOfUtc, ct);
            return Results.Ok(result);
        });

        group.MapGet("/summary", async (
            HttpContext context,
            IUserInboxService service,
            int? limit,
            CancellationToken ct) =>
        {
            var resolvedLimit = limit ?? 3;
            if (resolvedLimit is < 1 or > 10)
                return Results.BadRequest(new { error = "limit must be between 1 and 10." });

            var (tenantId, userId) = GetScope(context);
            return Results.Ok(await service.GetSummaryAsync(tenantId, userId, resolvedLimit, ct));
        });

        group.MapPut("/{id:guid}/read", async (
            HttpContext context,
            IUserInboxService service,
            Guid id,
            CancellationToken ct) =>
        {
            var (tenantId, userId) = GetScope(context);
            var result = await service.MarkReadAsync(tenantId, userId, id, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        group.MapPost("/mark-all-read", async (
            HttpContext context,
            IUserInboxService service,
            MarkAllInboxReadRequest request,
            CancellationToken ct) =>
        {
            if (request.ThroughUtc == default)
                return Results.BadRequest(new { error = "throughUtc is required." });

            var (tenantId, userId) = GetScope(context);
            return Results.Ok(await service.MarkAllReadAsync(tenantId, userId, request.ThroughUtc, ct));
        });

        group.MapDelete("/{id:guid}", async (
            HttpContext context,
            IUserInboxService service,
            Guid id,
            CancellationToken ct) =>
        {
            var (tenantId, userId) = GetScope(context);
            return await service.DismissAsync(tenantId, userId, id, ct)
                ? Results.NoContent()
                : Results.NotFound();
        });
    }

    private static (Guid TenantId, Guid UserId) GetScope(HttpContext context)
    {
        var userContext = context.GetUserContext();
        return (userContext.TenantId, Guid.Parse(userContext.UserId));
    }
}
