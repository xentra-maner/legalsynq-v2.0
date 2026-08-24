using BuildingBlocks.Authorization;
using BuildingBlocks.Authorization.Filters;
using BuildingBlocks.Context;
using BuildingBlocks.Exceptions;
using CareConnect.Application.DTOs;
using CareConnect.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace CareConnect.Api.Endpoints;

public static class PendingReferralRequestEndpoints
{
    private static readonly string[] LawFirmReviewerRoles =
    [
        ProductRoleCodes.CareConnectReferrer,
        ProductRoleCodes.CareConnectReferrerAdmin,
    ];

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
        .RequireProductRole(ProductCodes.SynqCareConnect, LawFirmReviewerRoles);

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
        .RequireProductRole(ProductCodes.SynqCareConnect, LawFirmReviewerRoles);

        group.MapPut("/{id:guid}", async (
            Guid id,
            UpdatePendingReferralRequest request,
            ICurrentRequestContext ctx,
            IPendingReferralRequestService service,
            CancellationToken ct) =>
        {
            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");
            var orgId = ctx.OrgId ?? throw new InvalidOperationException("org_id claim is missing.");
            var result = await service.UpdateForLawFirmAsync(tenantId, orgId, id, ctx.UserId, request, ct);
            return Results.Ok(result);
        })
        .RequireProductRole(ProductCodes.SynqCareConnect, LawFirmReviewerRoles);

        group.MapPost("/{id:guid}/decline", async (
            Guid id,
            ICurrentRequestContext ctx,
            IPendingReferralRequestService service,
            CancellationToken ct) =>
        {
            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");
            var orgId = ctx.OrgId ?? throw new InvalidOperationException("org_id claim is missing.");
            var result = await service.CancelForLawFirmAsync(tenantId, orgId, id, ctx.UserId, ct);
            return Results.Ok(result);
        })
        .RequireProductRole(ProductCodes.SynqCareConnect, LawFirmReviewerRoles);

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
        .RequireProductRole(ProductCodes.SynqCareConnect, LawFirmReviewerRoles);

        group.MapPost("/{id:guid}/attachments/upload", async (
            Guid id,
            HttpRequest request,
            ICurrentRequestContext ctx,
            IPendingReferralRequestService service,
            Microsoft.Extensions.Options.IOptions<CareConnect.Api.Options.AttachmentUploadOptions> uploadOptions,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("CareConnect.PendingReferralRequests");
            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");
            var orgId = ctx.OrgId ?? throw new InvalidOperationException("org_id claim is missing.");

            if (!request.HasFormContentType)
                return Results.BadRequest(new { error = "Request must be multipart/form-data." });

            var form = await request.ReadFormAsync(ct);
            if (form.Files.Count == 0)
                return Results.BadRequest(new { error = "No file was provided." });

            var file = form.Files[0];
            var options = uploadOptions.Value;
            if (file.Length > options.MaxFileSizeBytes)
                return Results.BadRequest(new { error = $"File size exceeds the maximum allowed size of {options.MaxFileSizeBytes / (1024 * 1024)} MB." });

            var normalizedType = file.ContentType?.Split(';')[0].Trim().ToLowerInvariant() ?? string.Empty;
            if (!options.AllowedContentTypes.Contains(normalizedType, StringComparer.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = $"File type '{file.ContentType}' is not permitted.", allowed = options.AllowedContentTypes });

            try
            {
                await using var stream = file.OpenReadStream();
                var result = await service.UploadAttachmentForLawFirmAsync(
                    tenantId,
                    orgId,
                    id,
                    ctx.UserId,
                    stream,
                    file.FileName,
                    file.ContentType ?? "application/octet-stream",
                    file.Length,
                    ct);

                return Results.Created($"/api/pending-referral-requests/{id}/attachments/{result.Id}", result);
            }
            catch (NotFoundException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Pending referral request document upload failed for request {PendingRequestId}.", id);
                return Results.Problem("An unexpected error occurred while uploading the document.");
            }
        })
        .DisableAntiforgery()
        .RequireProductRole(ProductCodes.SynqCareConnect, LawFirmReviewerRoles);

        group.MapGet("/{id:guid}/attachments/{attachmentId:guid}/url", async (
            Guid id,
            Guid attachmentId,
            [FromQuery] bool? download,
            ICurrentRequestContext ctx,
            IPendingReferralRequestService service,
            CancellationToken ct) =>
        {
            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");
            var orgId = ctx.OrgId ?? throw new InvalidOperationException("org_id claim is missing.");

            SignedUrlResponse? result;
            try
            {
                result = await service.GetAttachmentSignedUrlForLawFirmAsync(
                    tenantId,
                    orgId,
                    id,
                    attachmentId,
                    download ?? false,
                    ct);
            }
            catch (NotFoundException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }

            return result is null
                ? Results.Problem("The document is not currently accessible.", statusCode: StatusCodes.Status503ServiceUnavailable)
                : Results.Ok(result);
        })
        .RequireProductRole(ProductCodes.SynqCareConnect, LawFirmReviewerRoles);
    }
}
