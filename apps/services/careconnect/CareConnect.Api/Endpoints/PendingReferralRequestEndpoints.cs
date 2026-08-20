using BuildingBlocks.Authorization;
using BuildingBlocks.Authorization.Filters;
using BuildingBlocks.Context;
using CareConnect.Application.DTOs;
using CareConnect.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace CareConnect.Api.Endpoints;

public static class PendingReferralRequestEndpoints
{
    public static void MapPendingReferralRequestEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/pending-referral-requests")
            .RequireAuthorization(Policies.AuthenticatedUser)
            .RequireProductAccess(ProductCodes.SynqCareConnect);

        group.MapGet("/", async (
            [FromQuery] string? status,
            [FromQuery] int? page,
            [FromQuery] int? pageSize,
            ICurrentRequestContext ctx,
            IPendingReferralRequestService service,
            CancellationToken ct) =>
        {
            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");
            var orgId = ctx.OrgId ?? throw new InvalidOperationException("org_id claim is missing.");
            var result = await service.SearchForLawFirmAsync(
                tenantId,
                orgId,
                status,
                Math.Max(1, page ?? 1),
                Math.Clamp(pageSize ?? 20, 1, 100),
                ct);
            return Results.Ok(result);
        })
        .RequireProductRole(ProductCodes.SynqCareConnect, ProductRoleCodes.CareConnectReferrer);

        group.MapGet("/{id:guid}", async (
            Guid id,
            ICurrentRequestContext ctx,
            IPendingReferralRequestService service,
            CancellationToken ct) =>
        {
            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");
            var orgId = ctx.OrgId ?? throw new InvalidOperationException("org_id claim is missing.");
            var result = await service.GetForLawFirmAsync(tenantId, orgId, id, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        })
        .RequireProductRole(ProductCodes.SynqCareConnect, ProductRoleCodes.CareConnectReferrer);

        group.MapPost("/{id:guid}/convert", async (
            Guid id,
            ConvertPendingReferralRequest request,
            ICurrentRequestContext ctx,
            IPendingReferralRequestService service,
            CancellationToken ct) =>
        {
            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");
            var orgId = ctx.OrgId ?? throw new InvalidOperationException("org_id claim is missing.");
            var result = await service.ConvertAsync(tenantId, orgId, id, ctx.UserId, request, ct);
            return Results.Ok(result);
        })
        .RequireProductRole(ProductCodes.SynqCareConnect, ProductRoleCodes.CareConnectReferrer);
    }
}
