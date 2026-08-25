using BuildingBlocks.Authorization;
using BuildingBlocks.Authorization.Filters;
using BuildingBlocks.Context;
using BuildingBlocks.Exceptions;
using CareConnect.Application.DTOs;
using CareConnect.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace CareConnect.Api.Endpoints;

/// <summary>
/// Tenant administration of Referral Representative access codes. Admin-only end to end —
/// a representative can never reach these routes (they carry none of the CareConnect
/// permission codes this gate requires). Admins generate a code scoped to one attribution
/// and share it out of band; they never see or choose which user account redeems it — see
/// RepresentativeReferralEndpoints' redeem route for the self-service side.
/// Suggested UI location: CareConnect → Referral Originations → (view an origination).
/// </summary>
public static class ReferralAttributionAccessCodeEndpoints
{
    public static void MapReferralAttributionAccessCodeEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/referral-representative-access-codes")
            .RequireAuthorization(Policies.PlatformOrTenantAdmin)
            .RequireProductAccess(ProductCodes.SynqCareConnect);

        // Scoped to one attribution — the admin UI shows this attribution's own detail
        // view, never a cross-attribution list, since a code is 1:1 with its attribution.
        group.MapGet("/by-attribution/{referralAttributionId:guid}", async (
            Guid referralAttributionId,
            IReferralAttributionAccessCodeService service,
            ICurrentRequestContext ctx,
            CancellationToken ct) =>
        {
            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");
            var result = await service.GetActiveByAttributionAsync(tenantId, referralAttributionId, ct);
            return result is null ? Results.NoContent() : Results.Ok(result);
        });

        group.MapPost("/", async (
            [FromBody] CreateReferralAttributionAccessCodeRequest request,
            IReferralAttributionAccessCodeService service,
            ICurrentRequestContext ctx,
            CancellationToken ct) =>
        {
            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");
            try
            {
                var result = await service.GenerateAsync(tenantId, ctx.UserId, ctx.Name, request, ct);
                return Results.Created($"/api/referral-representative-access-codes/{result.Id}", result);
            }
            catch (ValidationException ex)
            {
                return Results.BadRequest(new { error = ex.Message, fields = ex.Errors });
            }
            catch (ConflictException ex)
            {
                return Results.Conflict(new { error = ex.Message, code = ex.ErrorCode });
            }
        });

        // Deactivate/reactivate — the product's "revoke" action. No hard delete: code
        // history must remain reviewable through audit records.
        group.MapPatch("/{id:guid}/active", async (
            Guid id,
            [FromBody] SetReferralAttributionAccessCodeActiveRequest request,
            IReferralAttributionAccessCodeService service,
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

        group.MapDelete("/{id:guid}", async (
            Guid id,
            IReferralAttributionAccessCodeService service,
            ICurrentRequestContext ctx,
            CancellationToken ct) =>
        {
            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");
            try
            {
                // DELETE is exposed for API-contract completeness but behaves as a
                // deactivation, not a physical delete — see the non-destructive rule above.
                await service.SetActiveAsync(tenantId, id, ctx.UserId, ctx.Name, isActive: false, ct);
                return Results.NoContent();
            }
            catch (NotFoundException)
            {
                return Results.NotFound();
            }
        });
    }
}
