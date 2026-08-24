using BuildingBlocks.Authorization;
using BuildingBlocks.Authorization.Filters;
using BuildingBlocks.Context;
using CareConnect.Application.Authorization;
using CareConnect.Application.DTOs;
using CareConnect.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace CareConnect.Api.Endpoints;

public static class ProviderEndpoints
{
    public static void MapProviderEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/providers")
            .RequireProductAccess(ProductCodes.SynqCareConnect);

        group.MapGet("/", async (
            [AsParameters] ProviderSearchParams p,
            IProviderService service,
            ICurrentRequestContext ctx,
            AuthorizationService authSvc,
            CancellationToken ct) =>
        {
            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");
            await CareConnectAuthHelper.RequireAsync(ctx, authSvc, PermissionCodes.ProviderSearch, ct);

            var query = new GetProvidersQuery
            {
                Name               = p.Name,
                CategoryCode       = p.CategoryCode,
                SpecialtyCode      = p.SpecialtyCode,
                City               = p.City,
                State              = p.State,
                AcceptingReferrals = p.AcceptingReferrals,
                IsActive           = p.IsActive,
                Page               = p.Page     ?? 1,
                PageSize           = p.PageSize ?? 20,
                Latitude           = p.Latitude,
                Longitude          = p.Longitude,
                RadiusMiles        = p.RadiusMiles,
                NorthLat           = p.NorthLat,
                SouthLat           = p.SouthLat,
                EastLng            = p.EastLng,
                WestLng            = p.WestLng,
                OrganizationId     = p.OrganizationId,
            };

            var result = await service.SearchAsync(tenantId, query, ct);
            return Results.Ok(result);
        })
        .RequireAuthorization(Policies.AuthenticatedUser);

        group.MapGet("/map", async (
            [AsParameters] ProviderSearchParams p,
            IProviderService service,
            ICurrentRequestContext ctx,
            AuthorizationService authSvc,
            CancellationToken ct) =>
        {
            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");
            await CareConnectAuthHelper.RequireAsync(ctx, authSvc, PermissionCodes.ProviderMap, ct);

            var query = new GetProvidersQuery
            {
                Name               = p.Name,
                CategoryCode       = p.CategoryCode,
                SpecialtyCode      = p.SpecialtyCode,
                City               = p.City,
                State              = p.State,
                AcceptingReferrals = p.AcceptingReferrals,
                IsActive           = p.IsActive,
                Page               = 1,
                PageSize           = 500,
                Latitude           = p.Latitude,
                Longitude          = p.Longitude,
                RadiusMiles        = p.RadiusMiles,
                NorthLat           = p.NorthLat,
                SouthLat           = p.SouthLat,
                EastLng            = p.EastLng,
                WestLng            = p.WestLng
            };

            var markers = await service.GetMarkersAsync(tenantId, query, ct);
            return Results.Ok(markers);
        })
        .RequireAuthorization(Policies.AuthenticatedUser);

        group.MapGet("/{id:guid}", async (
            Guid id,
            IProviderService service,
            ICurrentRequestContext ctx,
            AuthorizationService authSvc,
            CancellationToken ct) =>
        {
            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");
            await CareConnectAuthHelper.RequireAsync(ctx, authSvc, PermissionCodes.ProviderSearch, ct);
            var provider = await service.GetByIdAsync(tenantId, id, ct);
            return Results.Ok(provider);
        })
        .RequireAuthorization(Policies.AuthenticatedUser);

        group.MapPost("/", async (
            [FromBody] CreateProviderRequest request,
            IProviderService service,
            ICurrentRequestContext ctx,
            AuthorizationService authSvc,
            CancellationToken ct) =>
        {
            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");
            await CareConnectAuthHelper.RequireAsync(ctx, authSvc, PermissionCodes.ProviderManage, ct);
            var provider = await service.CreateAsync(tenantId, ctx.UserId, request, ct);
            return Results.Created($"/api/providers/{provider.Id}", provider);
        })
        .RequireAuthorization(Policies.AuthenticatedUser)
        .RequireProductRole(ProductCodes.SynqCareConnect,
            ProductRoleCodes.CareConnectReceiver, ProductRoleCodes.CareConnectReferrerAdmin, "CARECONNECT_ADMIN");

        group.MapPut("/{id:guid}", async (
            Guid id,
            [FromBody] UpdateProviderRequest request,
            IProviderService service,
            ICurrentRequestContext ctx,
            AuthorizationService authSvc,
            CancellationToken ct) =>
        {
            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");
            await CareConnectAuthHelper.RequireAsync(ctx, authSvc, PermissionCodes.ProviderManage, ct);
            var provider = await service.UpdateAsync(tenantId, id, ctx.UserId, request, ct);
            return Results.Ok(provider);
        })
        .RequireAuthorization(Policies.AuthenticatedUser)
        .RequireProductRole(ProductCodes.SynqCareConnect,
            ProductRoleCodes.CareConnectReceiver, ProductRoleCodes.CareConnectReferrerAdmin, "CARECONNECT_ADMIN");

        // ── Availability endpoint ──────────────────────────────────────────
        group.MapGet("/{providerId:guid}/availability", async (
            Guid providerId,
            [AsParameters] ProviderAvailabilityParams p,
            IProviderService service,
            ICurrentRequestContext ctx,
            AuthorizationService authSvc,
            CancellationToken ct) =>
        {
            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");
            await CareConnectAuthHelper.RequireAsync(ctx, authSvc, PermissionCodes.ProviderSearch, ct);
            var result = await service.GetAvailabilityAsync(tenantId, providerId, p.From, p.To, p.ServiceOfferingId, p.FacilityId, ct);
            return Results.Ok(result);
        })
        .RequireAuthorization(Policies.AuthenticatedUser);
    }
}

internal sealed class ProviderSearchParams
{
    public string? Name               { get; init; }
    public string? CategoryCode       { get; init; }
    public string? SpecialtyCode      { get; init; }
    public string? City               { get; init; }
    public string? State              { get; init; }
    public bool?   AcceptingReferrals { get; init; }
    public bool?   IsActive           { get; init; }
    public int?    Page               { get; init; }
    public int?    PageSize           { get; init; }
    public double? Latitude           { get; init; }
    public double? Longitude          { get; init; }
    public double? RadiusMiles        { get; init; }
    public double? NorthLat           { get; init; }
    public double? SouthLat           { get; init; }
    public double? EastLng            { get; init; }
    public double? WestLng            { get; init; }
    // LSCC-01-003: Admin provisioning — filter by Identity OrganizationId
    public Guid?   OrganizationId     { get; init; }
}

internal sealed class ProviderAvailabilityParams
{
    public DateTime  From              { get; init; }
    public DateTime  To                { get; init; }
    public Guid?     ServiceOfferingId { get; init; }
    public Guid?     FacilityId        { get; init; }
}
