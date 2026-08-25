using BuildingBlocks.Authorization;
using BuildingBlocks.Authorization.Filters;
using BuildingBlocks.Context;
using BuildingBlocks.Exceptions;
using CareConnect.Application.DTOs;
using CareConnect.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace CareConnect.Api.Endpoints;

/// <summary>
/// Tenant administration of Referral Origination options.
/// Suggested UI location: Tenant Portal → Settings → Referral Configuration → Referral Originations,
/// but the CareConnect-domain config lives in the CareConnect admin surface (like providers,
/// categories, specialties) — reachable from the Tenant Portal but not a Tenant-service concept.
/// </summary>
public static class ReferralAttributionEndpoints
{
    public static void MapReferralAttributionEndpoints(this WebApplication app)
    {
        var adminGroup = app.MapGroup("/api/referral-attributions")
            .RequireAuthorization(Policies.PlatformOrTenantAdmin)
            .RequireProductAccess(ProductCodes.SynqCareConnect);

        adminGroup.MapGet("/", async (
            bool? activeOnly,
            IReferralAttributionService service,
            ICurrentRequestContext ctx,
            CancellationToken ct) =>
        {
            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");
            var result = await service.ListAsync(tenantId, activeOnly, ct);
            return Results.Ok(result);
        });

        adminGroup.MapGet("/{id:guid}", async (
            Guid id,
            IReferralAttributionService service,
            ICurrentRequestContext ctx,
            CancellationToken ct) =>
        {
            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");
            try
            {
                return Results.Ok(await service.GetByIdAsync(tenantId, id, ct));
            }
            catch (NotFoundException)
            {
                return Results.NotFound();
            }
        });

        adminGroup.MapPost("/", async (
            [FromBody] CreateReferralAttributionRequest request,
            IReferralAttributionService service,
            ICurrentRequestContext ctx,
            CancellationToken ct) =>
        {
            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");
            try
            {
                var result = await service.CreateAsync(tenantId, ctx.UserId, ctx.Name, request, ct);
                return Results.Created($"/api/referral-attributions/{result.Id}", result);
            }
            catch (ValidationException ex)
            {
                return Results.BadRequest(new { error = ex.Message, fields = ex.Errors });
            }
        });

        adminGroup.MapPatch("/{id:guid}", async (
            Guid id,
            [FromBody] UpdateReferralAttributionRequest request,
            IReferralAttributionService service,
            ICurrentRequestContext ctx,
            CancellationToken ct) =>
        {
            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");
            try
            {
                return Results.Ok(await service.UpdateAsync(tenantId, id, ctx.UserId, ctx.Name, request, ct));
            }
            catch (NotFoundException)
            {
                return Results.NotFound();
            }
            catch (ValidationException ex)
            {
                return Results.BadRequest(new { error = ex.Message, fields = ex.Errors });
            }
        });

        adminGroup.MapPatch("/{id:guid}/active", async (
            Guid id,
            [FromBody] SetReferralAttributionActiveRequest request,
            IReferralAttributionService service,
            ICurrentRequestContext ctx,
            CancellationToken ct) =>
        {
            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");
            try
            {
                return Results.Ok(await service.SetActiveAsync(tenantId, id, ctx.UserId, ctx.Name, request.IsActive, ct));
            }
            catch (NotFoundException)
            {
                return Results.NotFound();
            }
        });

        // ── Law Firm Portal: active options for the current tenant only ─────────
        // Any authenticated CareConnect user may read this (it's a dropdown source,
        // not a configuration surface) — narrower auth than the admin group above.
        app.MapGet("/api/referral-attributions/options", async (
            IReferralAttributionService service,
            ICurrentRequestContext ctx,
            CancellationToken ct) =>
        {
            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");
            var result = await service.ListAsync(tenantId, activeOnly: true, ct);
            var options = result.Select(a => new ReferralAttributionSummary
            {
                Id = a.Id,
                FirstName = a.FirstName,
                LastName = a.LastName,
                IsActive = a.IsActive,
            });
            return Results.Ok(options);
        })
        .RequireAuthorization(Policies.AuthenticatedUser)
        .RequireProductAccess(ProductCodes.SynqCareConnect);
    }
}
