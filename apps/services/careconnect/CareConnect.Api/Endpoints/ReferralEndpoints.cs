using BuildingBlocks.Authorization;
using BuildingBlocks.Authorization.Filters;
using BuildingBlocks.Context;
using BuildingBlocks.Exceptions;
using CareConnect.Api.Helpers;
using CareConnect.Api.Options;
using CareConnect.Application.Authorization;
using CareConnect.Application.DTOs;
using CareConnect.Application.Interfaces;
using CareConnect.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;

namespace CareConnect.Api.Endpoints;

public static class ReferralEndpoints
{
    public static void MapReferralEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/referrals");

        group.MapGet("/", async (
            [AsParameters] ReferralSearchParams p,
            IReferralService service,
            ICurrentRequestContext ctx,
            AuthorizationService authSvc,
            CancellationToken ct) =>
        {
            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");

            var isProviderOrg = CareConnectParticipantHelper.IsReceiverContext(ctx);
            var isAdmin = ctx.IsPlatformAdmin || ctx.Roles.Contains(Roles.TenantAdmin);

            // BLK-PERF-01: Clamp page size to protect against unbounded result-set queries.
            // Default 20; maximum 100 — callers requesting more receive exactly 100.
            var query = new GetReferralsQuery
            {
                Status             = p.Status,
                ProviderId         = p.ProviderId,
                SearchText         = p.Search,
                ClientName         = p.ClientName,
                CaseNumber         = p.CaseNumber,
                ProviderName       = p.ProviderName,
                ReferrerName       = p.ReferrerName,
                Urgency            = p.Urgency,
                CreatedFrom        = p.CreatedFrom,
                CreatedTo          = p.CreatedTo,
                Page               = Math.Max(1, p.Page ?? 1),
                PageSize           = Math.Clamp(p.PageSize ?? 20, 1, 100),
            };

            if (ctx.IsPlatformAdmin)
            {
                // PlatformAdmin: all referrals in the tenant, no org scoping
            }
            else if (isProviderOrg)
            {
                query.CrossTenantReceiver = true;
                query.ReceivingOrgId      = ctx.OrgId;
            }
            else if (isAdmin)
            {
                // TenantAdmin on a referrer org: all referrals in the tenant
            }
            else
            {
                // Cross-tenant referrer match — mirrors the provider's CrossTenantReceiver
                // branch above. Matches purely on ReferringOrgId/ReferrerEmail instead of
                // gating on TenantId first, so the firm's list stays correct even if a
                // referral's TenantId ever drifts from the firm's session tenant (e.g. a
                // stale/conflicting Tenant-service record used to resolve the public
                // submission endpoint's tenant).
                query.CrossTenantReferrer = true;
                query.ReferringOrgId      = ctx.OrgId;
                // CC-REFERRER-EMAIL: also surface public referrals submitted before the
                // law firm activated their portal (those have ReferrerEmail set but no
                // ReferringOrganizationId).
                query.ReferrerEmail = ctx.Email;
            }

            var result = await service.SearchAsync(tenantId, query, ct);
            return Results.Ok(result);
        })
        .RequireAuthorization(Policies.AuthenticatedUser)
        .RequireProductAccess(ProductCodes.SynqCareConnect);

        // LSCC-01-002-02: Returns the explicit readiness result for the current caller
        // as a provider in the CareConnect receiver path.
        // Checks whether the caller's JWT product roles satisfy the full receiver-ready bundle:
        //   CareConnectReceiver role + ReferralReadAddressed + ReferralAccept capabilities.
        // Read-only — no side effects, no provisioning, no role assignment.
        // LSCC-01-004: On IsProvisioned=false, fires a best-effort blocked-access log entry
        //   for admin operational visibility. Log failure never blocks the response.
        // NOTE: must be registered before /{id:guid} to avoid the GUID route capturing "access-readiness".
        group.MapGet("/access-readiness", async (
            IProviderAccessReadinessService readinessSvc,
            IBlockedAccessLogService        blockedLogSvc,
            ICurrentRequestContext          ctx,
            CancellationToken               ct) =>
        {
            // PlatformAdmin and TenantAdmin are always considered access-ready —
            // they bypass capability checks throughout the platform.
            if (ctx.IsPlatformAdmin || ctx.Roles.Contains(Roles.TenantAdmin, StringComparer.OrdinalIgnoreCase))
            {
                return Results.Ok(new ProviderAccessReadinessResult
                {
                    IsProvisioned     = true,
                    HasReceiverRole   = true,
                    HasReferralAccess = true,
                    HasReferralAccept = true,
                });
            }

            var result = await readinessSvc.GetReadinessAsync(ctx.ProductRoles, ct);

            // LSCC-01-004: Best-effort log — fire and observe. Must never block the response.
            if (!result.IsProvisioned)
            {
                _ = blockedLogSvc.LogAsync(
                    tenantId:       ctx.TenantId,
                    userId:         ctx.UserId,
                    userEmail:      ctx.Email,
                    organizationId: ctx.OrgId,
                    providerId:     null,
                    referralId:     null,
                    failureReason:  result.Reason ?? "unknown",
                    ct:             CancellationToken.None);
            }

            return Results.Ok(result);
        })
        .RequireAuthorization(Policies.AuthenticatedUser)
        .RequireProductAccess(ProductCodes.SynqCareConnect);

        // LSCC-002: Row-level access control — caller must be an admin or a participant
        // (ReferringOrganizationId or ReceivingOrganizationId matches their org).
        // CC-REFERRER-EMAIL: also grants access when the referral was submitted publicly
        // (no ReferringOrganizationId) but the caller's email matches ReferrerEmail.
        // Returns 404 (not 403) for non-participants to avoid confirming record existence.
        group.MapGet("/{id:guid}", async (
            Guid id,
            IReferralService service,
            ICurrentRequestContext ctx,
            CancellationToken ct) =>
        {
            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");
            var isProviderOrg = CareConnectParticipantHelper.IsReceiverContext(ctx);
            var globalLookup = ShouldUseGlobalReferralLookup(ctx, isProviderOrg);
            var referral = await service.GetByIdAsync(tenantId, id, ct, isPlatformAdmin: globalLookup);

            if (!CareConnectParticipantHelper.IsAdmin(ctx))
            {
                var isParticipant =
                    (ctx.OrgId.HasValue && referral.ReferringOrganizationId == ctx.OrgId) ||
                    (ctx.OrgId.HasValue && referral.ReceivingOrganizationId  == ctx.OrgId) ||
                    // CC-REFERRER-EMAIL: public referral matched by email
                    (!string.IsNullOrWhiteSpace(ctx.Email) &&
                     referral.ReferringOrganizationId == null &&
                     string.Equals(referral.ReferrerEmail, ctx.Email, StringComparison.OrdinalIgnoreCase));

                if (!isParticipant)
                    return Results.NotFound();
            }

            if (isProviderOrg && ctx.OrgId.HasValue && referral.ReceivingOrganizationId == ctx.OrgId
                && referral.Status == "New")
            {
                try { await service.MarkAsOpenedAsync(id, ct); }
                catch { }
            }

            return Results.Ok(referral);
        })
        .RequireAuthorization(Policies.AuthenticatedUser)
        .RequireProductAccess(ProductCodes.SynqCareConnect);

        group.MapGet("/{id:guid}/history", async (
            Guid id,
            IReferralService service,
            ICurrentRequestContext ctx,
            CancellationToken ct) =>
        {
            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");
            var isProviderOrg = CareConnectParticipantHelper.IsReceiverContext(ctx);
            var globalLookup = ShouldUseGlobalReferralLookup(ctx, isProviderOrg);

            // Participant check — mirrors GET /{id:guid} to prevent cross-tenant data access.
            var referral = await service.GetByIdAsync(tenantId, id, ct, isPlatformAdmin: globalLookup);
            if (!CareConnectParticipantHelper.IsAdmin(ctx))
            {
                var isParticipant =
                    (ctx.OrgId.HasValue && referral.ReferringOrganizationId == ctx.OrgId) ||
                    (ctx.OrgId.HasValue && referral.ReceivingOrganizationId  == ctx.OrgId) ||
                    // CC-REFERRER-EMAIL: public referral matched by email
                    (!string.IsNullOrWhiteSpace(ctx.Email) &&
                     referral.ReferringOrganizationId == null &&
                     string.Equals(referral.ReferrerEmail, ctx.Email, StringComparison.OrdinalIgnoreCase));
                if (!isParticipant)
                    return Results.NotFound();
            }

            var history = await service.GetHistoryAsync(tenantId, id, ct, isPlatformAdmin: globalLookup);
            return Results.Ok(history);
        })
        .RequireAuthorization(Policies.AuthenticatedUser)
        .RequireProductAccess(ProductCodes.SynqCareConnect);

        group.MapGet("/{id:guid}/comments", async (
            Guid id,
            IReferralThreadService threadService,
            ICurrentRequestContext ctx,
            CancellationToken ct) =>
        {
            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");

            var comments = await threadService.GetAuthenticatedCommentsAsync(
                tenantId,
                id,
                ctx.OrgId,
                ctx.Email,
                useGlobalLookup: ShouldUseGlobalReferralLookup(ctx, CareConnectParticipantHelper.IsReceiverContext(ctx)),
                bypassParticipantCheck: CareConnectParticipantHelper.IsAdmin(ctx),
                ct);

            return comments is null ? Results.NotFound() : Results.Ok(comments);
        })
        .RequireAuthorization(Policies.AuthenticatedUser)
        .RequireProductAccess(ProductCodes.SynqCareConnect);

        group.MapPost("/{id:guid}/comments", async (
            Guid id,
            HttpRequest httpRequest,
            IReferralThreadService threadService,
            ICurrentRequestContext ctx,
            AuthorizationService authSvc,
            IOptions<AttachmentUploadOptions> uploadOptions,
            CancellationToken ct) =>
        {
            var request = await ReadAuthenticatedCommentRequestAsync(httpRequest, ct);
            if (request is null)
                return Results.BadRequest(new { error = "Request body is required." });

            var files = request.Files ?? new FormFileCollection();
            var message = MessageAttachmentUploadHelper.NormalizeMessage(request.Message);
            var messageValidationError = MessageAttachmentUploadHelper.ValidateMessage(message, files);
            if (messageValidationError is not null)
                return messageValidationError;

            var validationError = MessageAttachmentUploadHelper.ValidateFiles(files, uploadOptions.Value);
            if (validationError is not null)
                return validationError;

            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");
            var canPostThread = await CareConnectAuthHelper.HasAnyAsync(
                ctx,
                authSvc,
                [PermissionCodes.ReferralReadOwn, PermissionCodes.ReferralReadAddressed],
                ct);
            if (!canPostThread)
                return Results.Forbid();

            var senderName = string.IsNullOrWhiteSpace(ctx.Name)
                ? (string.IsNullOrWhiteSpace(ctx.Email) ? "Provider" : ctx.Email!)
                : ctx.Name!;

            List<ReferralMessageAttachmentUpload> uploads = [];
            try
            {
                uploads = MessageAttachmentUploadHelper.OpenUploads(files);
                var comment = uploads.Count == 0
                    ? await threadService.PostAuthenticatedCommentAsync(
                        tenantId,
                        id,
                        ctx.OrgId,
                        ctx.Email,
                        senderName,
                        message,
                        useGlobalLookup: ShouldUseGlobalReferralLookup(ctx, CareConnectParticipantHelper.IsReceiverContext(ctx)),
                        ct: ct)
                    : await threadService.PostAuthenticatedCommentWithAttachmentsAsync(
                        tenantId,
                        id,
                        ctx.OrgId,
                        ctx.Email,
                        senderName,
                        message,
                        useGlobalLookup: ShouldUseGlobalReferralLookup(ctx, CareConnectParticipantHelper.IsReceiverContext(ctx)),
                        uploads,
                        createdByUserId: ctx.UserId,
                        ct: ct);

                return comment is null
                    ? Results.NotFound()
                    : Results.Created($"/api/referrals/{id}/comments/{comment.Id}", comment);
            }
            finally
            {
                MessageAttachmentUploadHelper.DisposeUploads(uploads);
            }
        })
        .RequireAuthorization(Policies.AuthenticatedUser)
        .RequireProductAccess(ProductCodes.SynqCareConnect)
        .DisableAntiforgery();

        // LS-ID-TNT-012: filter-level JWT permission check; handler also validates via IEffectivePermissionService.
        group.MapPost("/", async (
            [FromBody] CreateReferralRequest request,
            IReferralService service,
            ICurrentRequestContext ctx,
            AuthorizationService authSvc,
            IConfiguration config,
            CancellationToken ct) =>
        {
            await CareConnectAuthHelper.RequireAsync(ctx, authSvc, PermissionCodes.ReferralCreate, ct);
            var effectiveTenantId = AuthenticatedReferralScopeResolver.ResolveTenantId(
                ctx,
                request,
                HasVerifiedScopeSelection(ctx, request, config));
            // JWT claims are authoritative for authenticated referral creation —
            // override any client-submitted referrer identity with the verified token values.
            // Referring organization always comes from the authenticated account context.
            request.ReferringOrganizationId = ctx.OrgId;
            if (!string.IsNullOrWhiteSpace(ctx.Name))  request.ReferrerName  = ctx.Name;
            if (!string.IsNullOrWhiteSpace(ctx.Email)) request.ReferrerEmail = ctx.Email;
            var referral = await service.CreateAsync(effectiveTenantId, ctx.UserId, request, ct, actorName: ctx.Name ?? ctx.Email);
            return Results.Created($"/api/referrals/{referral.Id}", referral);
        })
        .RequireAuthorization(Policies.AuthenticatedUser)
        .RequireProductAccess(ProductCodes.SynqCareConnect)
        .RequireOrgProductAccess(ProductCodes.SynqCareConnect);

        group.MapPut("/{id:guid}", async (
            Guid id,
            [FromBody] UpdateReferralRequest request,
            IReferralService service,
            ICurrentRequestContext ctx,
            AuthorizationService authSvc,
            CancellationToken ct) =>
        {
            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");

            // Null status = treatment-type-only update; apply the fallback ReferralUpdateStatus permission.
            var requiredPermission = ReferralWorkflowRules.RequiredPermissionFor(request.Status ?? string.Empty);
            await CareConnectAuthHelper.RequireAsync(ctx, authSvc, requiredPermission, ct);

            var isProviderOrg = CareConnectParticipantHelper.IsReceiverContext(ctx);
            var bypassTenant = ShouldUseGlobalReferralLookup(ctx, isProviderOrg);

            // Participant check — verify caller is a participant before mutating the referral.
            // Returns 404 (not 403) to avoid confirming record existence across tenants.
            var existing = await service.GetByIdAsync(tenantId, id, ct, isPlatformAdmin: bypassTenant);
            if (!ctx.IsPlatformAdmin)
            {
                var isParticipant =
                    (ctx.OrgId.HasValue && existing.ReferringOrganizationId == ctx.OrgId) ||
                    (ctx.OrgId.HasValue && existing.ReceivingOrganizationId  == ctx.OrgId) ||
                    // CC-REFERRER-EMAIL: public referral matched by email
                    (!string.IsNullOrWhiteSpace(ctx.Email) &&
                     existing.ReferringOrganizationId == null &&
                     string.Equals(existing.ReferrerEmail, ctx.Email, StringComparison.OrdinalIgnoreCase));
                if (!isParticipant)
                    return Results.NotFound();
            }

            // TreatmentTypeId may only be set by the Receiver (provider org) or a PlatformAdmin.
            if (request.TreatmentTypeId.HasValue && !ctx.IsPlatformAdmin)
            {
                var isReceiverOrg = ctx.OrgId.HasValue && existing.ReceivingOrganizationId == ctx.OrgId;
                if (!isReceiverOrg)
                    return Results.Forbid();
            }

            var referral = await service.UpdateAsync(tenantId, id, ctx.UserId, request, ct, bypassTenantScope: bypassTenant, actorName: ctx.Name ?? ctx.Email);
            return Results.Ok(referral);
        })
        .RequireAuthorization(Policies.AuthenticatedUser)
        .RequireProductAccess(ProductCodes.SynqCareConnect)
        .RequireOrgProductAccess(ProductCodes.SynqCareConnect);

        // ── LSCC-005-01: Hardening endpoints (authenticated) ────────────────────

        // GET /api/referrals/{id}/notifications — email delivery history for a referral
        group.MapGet("/{id:guid}/notifications", async (
            Guid id,
            IReferralService service,
            ICurrentRequestContext ctx,
            CancellationToken ct) =>
        {
            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");
            var isProviderOrg = CareConnectParticipantHelper.IsReceiverContext(ctx);
            var globalLookup = ShouldUseGlobalReferralLookup(ctx, isProviderOrg);

            // Participant check — mirrors GET /{id:guid} to prevent cross-tenant data access.
            var referral = await service.GetByIdAsync(tenantId, id, ct, isPlatformAdmin: globalLookup);
            if (!ctx.IsPlatformAdmin)
            {
                var isParticipant =
                    (ctx.OrgId.HasValue && referral.ReferringOrganizationId == ctx.OrgId) ||
                    (ctx.OrgId.HasValue && referral.ReceivingOrganizationId  == ctx.OrgId) ||
                    // CC-REFERRER-EMAIL: public referral matched by email
                    (!string.IsNullOrWhiteSpace(ctx.Email) &&
                     referral.ReferringOrganizationId == null &&
                     string.Equals(referral.ReferrerEmail, ctx.Email, StringComparison.OrdinalIgnoreCase));
                if (!isParticipant)
                    return Results.NotFound();
            }

            var notifs = await service.GetNotificationsAsync(tenantId, id, ct, isPlatformAdmin: globalLookup);
            return Results.Ok(notifs);
        })
        .RequireAuthorization(Policies.AuthenticatedUser)
        .RequireProductAccess(ProductCodes.SynqCareConnect);

        // POST /api/referrals/{id}/resend-email — resend provider notification email
        // Only available while referral is in New status.
        group.MapPost("/{id:guid}/resend-email", async (
            Guid id,
            IReferralService service,
            ICurrentRequestContext ctx,
            AuthorizationService authSvc,
            CancellationToken ct) =>
        {
            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");
            await CareConnectAuthHelper.RequireAsync(ctx, authSvc, PermissionCodes.ReferralCreate, ct);

            try
            {
                // LSCC-01-005-01 (DEF-002)
                var referral = await service.ResendEmailAsync(
                    tenantId,
                    id,
                    ct,
                    isPlatformAdmin: ShouldUseGlobalReferralLookup(ctx, CareConnectParticipantHelper.IsReceiverContext(ctx)));
                return Results.Ok(referral);
            }
            catch (NotFoundException)
            {
                return Results.NotFound();
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        })
        .RequireAuthorization(Policies.AuthenticatedUser)
        .RequireProductAccess(ProductCodes.SynqCareConnect)
        .RequireOrgProductAccess(ProductCodes.SynqCareConnect)
        .RequirePermission(PermissionCodes.ReferralCreate);

        // POST /api/referrals/{id}/reassign-provider — manually reassign the referral to a new provider.
        // Restricted to platform admins and tenant admins (enforced by PlatformOrTenantAdmin policy).
        // Fires a PROVIDER_ASSIGNED notification to the incoming provider and revokes old view tokens.
        group.MapPost("/{id:guid}/reassign-provider", async (
            Guid id,
            [FromBody] ReassignProviderRequest request,
            IReferralService service,
            ICurrentRequestContext ctx,
            CancellationToken ct) =>
        {
            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");

            if (request.NewProviderId == Guid.Empty)
                return Results.BadRequest(new { error = "newProviderId is required." });

            try
            {
                var referral = await service.ReassignProviderAsync(tenantId, id, request.NewProviderId, ctx.UserId, ctx.IsPlatformAdmin, ct);
                return Results.Ok(referral);
            }
            catch (NotFoundException)
            {
                return Results.NotFound();
            }
        })
        .RequireAuthorization(Policies.PlatformOrTenantAdmin)
        .RequireProductAccess(ProductCodes.SynqCareConnect);

        // Referral Attribution is set only once, at law firm submission time (see
        // ReferralService.CreateAsync / ResolveAttributionForSubmissionAsync), and is
        // immutable afterward — there is deliberately no admin edit endpoint for it.

        // POST /api/referrals/{id}/revoke-token — invalidate all previously issued view tokens
        group.MapPost("/{id:guid}/revoke-token", async (
            Guid id,
            IReferralService service,
            ICurrentRequestContext ctx,
            AuthorizationService authSvc,
            CancellationToken ct) =>
        {
            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");
            await CareConnectAuthHelper.RequireAsync(ctx, authSvc, PermissionCodes.ReferralCreate, ct);

            try
            {
                var referral = await service.RevokeTokenAsync(
                    tenantId,
                    id,
                    ct,
                    isPlatformAdmin: ShouldUseGlobalReferralLookup(ctx, CareConnectParticipantHelper.IsReceiverContext(ctx)));
                return Results.Ok(referral);
            }
            catch (NotFoundException)
            {
                return Results.NotFound();
            }
        })
        .RequireAuthorization(Policies.AuthenticatedUser)
        .RequireProductAccess(ProductCodes.SynqCareConnect)
        .RequireOrgProductAccess(ProductCodes.SynqCareConnect)
        .RequirePermission(PermissionCodes.ReferralCreate);

        // GET /api/referrals/{id}/audit — operational audit timeline (LSCC-005-02)
        // Returns status-history + notification events merged and sorted chronologically.
        group.MapGet("/{id:guid}/audit", async (
            Guid id,
            IReferralService service,
            ICurrentRequestContext ctx,
            CancellationToken ct) =>
        {
            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");
            try
            {
                // LSCC-01-005-01 (DEF-002)
                var timeline = await service.GetAuditTimelineAsync(
                    tenantId,
                    id,
                    ct,
                    isPlatformAdmin: ShouldUseGlobalReferralLookup(ctx, CareConnectParticipantHelper.IsReceiverContext(ctx)));
                return Results.Ok(timeline);
            }
            catch (NotFoundException)
            {
                return Results.NotFound();
            }
        })
        .RequireAuthorization(Policies.AuthenticatedUser)
        .RequireProductAccess(ProductCodes.SynqCareConnect);

        // ── LSCC-005: Public token-based endpoints (no auth required) ──────────

        // Resolves a view token to determine how to route the provider:
        //   "pending"  → LSCC-01-002-01: route to login + returnTo=/careconnect/referrals/{id}
        //   "active"   → route to login + returnTo=/careconnect/referrals/{id}
        //   "invalid"  → token is bad or expired
        //   "notfound" → referral was deleted
        group.MapGet("/resolve-view-token", async (
            [FromQuery] string token,
            IReferralService service,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(token))
                return Results.BadRequest(new { error = "token is required." });

            var result = await service.ResolveViewTokenAsync(token, ct);
            return Results.Ok(result);
        });
        // Note: no .RequireAuthorization — intentionally public

        // ── LSCC-008: Provider activation funnel (public, token-gated) ──────────

        // GET /api/referrals/{id}/public-summary?token=...
        // Returns limited referral context for the activation landing page.
        // Token is HMAC-validated + version-checked before any data is returned.
        group.MapGet("/{id:guid}/public-summary", async (
            Guid id,
            [FromQuery] string token,
            IReferralService service,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(token))
                return Results.BadRequest(new { error = "token is required." });

            var result = await service.GetPublicSummaryAccessAsync(id, token, ct);
            if (result.Data is not null)
                return Results.Ok(result.Data);

            if (result.FailureReason == CareConnect.Application.DTOs.ReferralTokenFailureReasons.ReferralNotFound)
                return Results.Json(new { reason = result.FailureReason }, statusCode: StatusCodes.Status404NotFound);

            return Results.Json(new { reason = result.FailureReason }, statusCode: StatusCodes.Status401Unauthorized);
        });
        // Note: no .RequireAuthorization — intentionally public, token-gated

        // POST /api/referrals/{id}/track-funnel
        // Body: { token, eventType }
        // Records a provider funnel event (ReferralViewed | ActivationStarted).
        group.MapPost("/{id:guid}/track-funnel", async (
            Guid id,
            [FromBody] TrackFunnelEventRequest request,
            IReferralService service,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Token))
                return Results.BadRequest(new { error = "token is required." });

            if (string.IsNullOrWhiteSpace(request.EventType))
                return Results.BadRequest(new { error = "eventType is required." });

            var ok = await service.TrackFunnelEventAsync(
                id, request.Token, request.EventType,
                request.RequesterName, request.RequesterEmail, ct);
            return ok ? Results.Ok() : Results.BadRequest(new { error = "Invalid token or unrecognised event type." });
        });
        // Note: no .RequireAuthorization — intentionally public, token-gated

        // POST /api/referrals/{id}/auto-provision
        // LSCC-010: Attempts instant provider activation.
        // On success:  provider is linked to an Identity org, returns loginUrl.
        // On fallback: upserts LSCC-009 activation request for admin review.
        // Public, token-gated (same HMAC-validated approach as track-funnel).
        group.MapPost("/{id:guid}/auto-provision", async (
            Guid id,
            [FromBody] AutoProvisionRequest request,
            IAutoProvisionService provisioner,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Token))
                return Results.BadRequest(new { error = "token is required." });

            var result = await provisioner.ProvisionAsync(
                id, request.Token, request.RequesterName, request.RequesterEmail, ct,
                requesterFirstName: request.RequesterFirstName,
                requesterLastName:  request.RequesterLastName);

            return Results.Ok(result);
        });
        // Note: no .RequireAuthorization — intentionally public, token-gated

        // GET /api/referrals/{id}/public-attachments/{attachmentId}/url?token=...&download=false
        // Returns a short-lived signed URL for a shared-scope attachment.
        // Token-gated: the same HMAC view token used by the activation landing page.
        // Allows unauthenticated providers to download documents submitted with their referral.
        group.MapGet("/{id:guid}/public-attachments/{attachmentId:guid}/url", async (
            Guid                      id,
            Guid                      attachmentId,
            [FromQuery] string        token,
            [FromQuery] bool?         download,
            IReferralService          service,
            IReferralAttachmentService attachmentSvc,
            CancellationToken         ct) =>
        {
            if (string.IsNullOrWhiteSpace(token))
                return Results.BadRequest(new { error = "token is required." });

            // Reuse GetPublicSummaryAsync to validate the token — null means invalid/expired.
            var summaryResult = await service.GetPublicSummaryAccessAsync(id, token, ct);
            if (summaryResult.Data is null)
            {
                if (summaryResult.FailureReason == CareConnect.Application.DTOs.ReferralTokenFailureReasons.ReferralNotFound)
                    return Results.Json(new { reason = summaryResult.FailureReason }, statusCode: StatusCodes.Status404NotFound);

                return Results.Json(new { reason = summaryResult.FailureReason }, statusCode: StatusCodes.Status401Unauthorized);
            }

            try
            {
                // isAdmin=true bypasses scope enforcement — safe because token is already HMAC-validated.
                var result = await attachmentSvc.GetSignedUrlAsync(
                    summaryResult.Data.TenantId,
                    id,
                    attachmentId,
                    callerOrgId:   null,
                    callerOrgType: null,
                    isAdmin:       true,
                    isDownload:    download ?? false,
                    ct:            ct);

                if (result is null)
                    return Results.Problem("The document is not currently accessible.", statusCode: 503);

                return Results.Ok(result);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        });
        // Note: no .RequireAuthorization — token-gated, intentionally public

        // POST /api/referrals/{id}/accept-by-token
        // Public, HMAC view-token gated. Accepts the referral directly from the provider thread page.
        group.MapPost("/{id:guid}/accept-by-token", async (
            Guid id,
            [FromBody] AcceptByTokenRequest request,
            IReferralService referralService,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Token))
                return Results.BadRequest(new { error = "token is required." });
            try
            {
                var result = await referralService.AcceptByTokenAsync(id, request.Token, ct);
                return Results.Ok(result);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized");
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict, title: "Conflict");
            }
        });
        // Note: no .RequireAuthorization — intentionally public, token-gated

        // POST /api/referrals/{id}/decline-by-token
        // Public, HMAC view-token gated. Declines the referral directly from the provider thread page.
        group.MapPost("/{id:guid}/decline-by-token", async (
            Guid id,
            [FromBody] DeclineByTokenRequest request,
            IReferralService referralService,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Token))
                return Results.BadRequest(new { error = "token is required." });
            try
            {
                var result = await referralService.DeclineByTokenAsync(id, request.Token, ct, declineNotes: request.DeclineNotes);
                return Results.Ok(result);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized");
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict, title: "Conflict");
            }
        });
        // Note: no .RequireAuthorization — intentionally public, token-gated

        // POST /api/referrals/{id}/complete-by-token
        // Public, HMAC view-token gated. Completes the referral from the provider thread page.
        group.MapPost("/{id:guid}/complete-by-token", async (
            Guid id,
            [FromBody] AcceptByTokenRequest request,
            IReferralService referralService,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Token))
                return Results.BadRequest(new { error = "token is required." });
            try
            {
                var result = await referralService.CompleteByTokenAsync(id, request.Token, ct);
                return Results.Ok(result);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized");
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict, title: "Conflict");
            }
        });
        // Note: no .RequireAuthorization — intentionally public, token-gated

        // POST /api/referrals/{id}/cancel-by-token
        // Public, HMAC view-token gated. Cancels the referral from the provider thread page.
        group.MapPost("/{id:guid}/cancel-by-token", async (
            Guid id,
            [FromBody] AcceptByTokenRequest request,
            IReferralService referralService,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Token))
                return Results.BadRequest(new { error = "token is required." });
            try
            {
                var result = await referralService.CancelByTokenAsync(id, request.Token, ct);
                return Results.Ok(result);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized");
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict, title: "Conflict");
            }
        });
        // Note: no .RequireAuthorization — intentionally public, token-gated

        // PATCH /api/referrals/{id}/treatment-type-by-token
        // Public, view-token gated. Sets/clears the treatment type from the provider thread page.
        group.MapPatch("/{id:guid}/treatment-type-by-token", async (
            Guid                                   id,
            [FromBody] UpdateTreatmentTypeByTokenRequest request,
            IReferralService                       referralService,
            CancellationToken                      ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Token))
                return Results.BadRequest(new { error = "token is required." });
            try
            {
                var result = await referralService.UpdateTreatmentTypeByTokenAsync(
                    id, request.Token, request.TreatmentTypeId, ct);
                return Results.Ok(result);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized");
            }
            catch (NotFoundException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict, title: "Conflict");
            }
            catch (ValidationException ex)
            {
                return Results.UnprocessableEntity(new { errors = ex.Errors });
            }
        });
        // Note: no .RequireAuthorization — intentionally public, token-gated
    }

    private static async Task<AuthenticatedCommentRequest?> ReadAuthenticatedCommentRequestAsync(
        HttpRequest httpRequest,
        CancellationToken ct)
    {
        if (httpRequest.HasFormContentType)
        {
            var form = await httpRequest.ReadFormAsync(ct);
            return new AuthenticatedCommentRequest(
                form["message"].FirstOrDefault() ?? string.Empty,
                form.Files);
        }

        var request = await httpRequest.ReadFromJsonAsync<CreateReferralCommentRequest>(cancellationToken: ct);
        return request is null
            ? null
            : new AuthenticatedCommentRequest(request.Message, null);
    }

    private static bool HasVerifiedScopeSelection(
        ICurrentRequestContext ctx,
        CreateReferralRequest request,
        IConfiguration config)
    {
        if (!ctx.UserId.HasValue ||
            !request.TenantId.HasValue ||
            string.IsNullOrWhiteSpace(request.ReferrerScopeSignature))
            return false;

        var secret = config["PublicTrustBoundary:InternalRequestSecret"];
        if (string.IsNullOrWhiteSpace(secret))
            return false;

        var data = BuildReferrerScopeSignaturePayload(
            ctx.UserId.Value,
            request.TenantId.Value);

        return TryValidateHmac(data, request.ReferrerScopeSignature, secret);
    }

    private static string BuildReferrerScopeSignaturePayload(
        Guid userId,
        Guid tenantId) =>
        $"{userId:D}:{tenantId:D}";

    private static bool TryValidateHmac(string data, string sig, string secret)
    {
        try
        {
            byte[] sigBytes;
            try { sigBytes = Convert.FromBase64String(sig); }
            catch { return false; }

            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var expected = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));

            return sigBytes.Length == expected.Length &&
                   CryptographicOperations.FixedTimeEquals(expected, sigBytes);
        }
        catch
        {
            return false;
        }
    }

    private static bool ShouldUseGlobalReferralLookup(ICurrentRequestContext ctx, bool isProviderOrg)
        => CareConnectParticipantHelper.ShouldUseGlobalReferralLookup(ctx, isProviderOrg);
}

internal sealed class ReferralSearchParams
{
    public string?   Status      { get; init; }
    public Guid?     ProviderId  { get; init; }
    public string?   Search      { get; init; }
    public string?   ClientName  { get; init; }
    public string?   CaseNumber  { get; init; }
    public string?   ProviderName { get; init; }
    public string?   ReferrerName { get; init; }
    public string?   Urgency     { get; init; }
    public DateTime? CreatedFrom { get; init; }
    public DateTime? CreatedTo   { get; init; }
    public int?      Page        { get; init; }
    public int?      PageSize    { get; init; }
}

internal sealed class ReassignProviderRequest
{
    public Guid NewProviderId { get; init; }
}

internal sealed record AuthenticatedCommentRequest(string Message, IFormFileCollection? Files);
