using BuildingBlocks.Authorization;
using BuildingBlocks.Authorization.Filters;
using BuildingBlocks.Exceptions;
using CareConnect.Application.Cache;
using CareConnect.Application.DTOs;
using CareConnect.Application.Interfaces;
using CareConnect.Application.Repositories;
using CareConnect.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Net.Mail;

namespace CareConnect.Api.Endpoints;

// CC2-INT-B07 — Public Network Surface.
// CC2-INT-B08 — Public Referral Initiation (POST /api/public/referrals).
// These endpoints are intentionally anonymous — no JWT or platform_session required.
// Tenant isolation is enforced via the X-Tenant-Id header, which is resolved
// server-side by the Next.js BFF from the request subdomain → Tenant service lookup.
// The caller (Next.js Server Component / BFF proxy) NEVER reads this header from user input;
// it resolves the tenant from the subdomain and forwards only the GUID.
//
// BLK-SEC-02-02: Trust boundary enforced via two-layer validation:
//   Layer 1 — X-Internal-Gateway-Secret: proves request passed through the trusted YARP gateway.
//   Layer 2 — X-Tenant-Id-Sig: HMAC-SHA256 of X-Tenant-Id signed by the BFF using
//             PublicTrustBoundary:InternalRequestSecret; proves X-Tenant-Id was not spoofed.
//
// Spoofed X-Tenant-Id from direct gateway callers → rejected at Layer 2 (no valid HMAC).
// Direct-to-service requests bypassing the gateway → rejected at Layer 1 (no gateway secret).
public static class PublicNetworkEndpoints
{
    private const string DefaultPublicReferralService = "General Referral";

    public static void MapPublicNetworkEndpoints(this WebApplication app)
    {
        // All public routes share the /api/public/network prefix.
        // The Gateway is configured to route /careconnect/api/public/** anonymously.
        var group = app.MapGroup("/api/public/network");

        // ── GET /api/public/network ─────────────────────────────────────────
        // Lists networks for the resolved tenant.
        // Header: X-Tenant-Id (GUID, resolved from subdomain by Next.js BFF)
        // Query:  organizationId (optional GUID) — when the referral portal has a
        //         law firm selected, scopes the list to tenant-owned networks plus
        //         that law firm's own network, so one law firm never sees another
        //         law firm's private network. Omitted → tenant-owned networks only
        //         (law-firm-owned networks are excluded, not merely unscoped).
        group.MapGet("/", async (
            string?             organizationId,
            HttpContext        http,
            IConfiguration     config,
            INetworkRepository repo,
            IMemoryCache       cache,
            CancellationToken  ct) =>
        {
            var tenantId = CareConnect.Api.Helpers.PublicTrustBoundary.ValidateAndResolveTenantId(http, config, "public-network");
            if (tenantId == null)
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    detail: "Request origin could not be verified.");

            Guid? orgId = Guid.TryParse(organizationId, out var parsedOrgId) ? parsedOrgId : null;

            // BLK-PERF-02: Cache the network list per tenant (and per organization,
            // when scoped). Trust boundary validation (above) has already verified
            // tenantId is trustworthy. Cache key is tenant-scoped — different tenants
            // never share an entry.
            var cacheKey = orgId.HasValue
                ? CareConnectCacheKeys.PublicNetworkList(tenantId.Value, orgId.Value)
                : CareConnectCacheKeys.PublicNetworkList(tenantId.Value);

            var summaries = await cache.GetOrCreateAsync(
                cacheKey,
                async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = CareConnectCacheTtl.PublicNetwork;
                    entry.Size = 1;
                    // BLK-PERF-01: Single query — replaces the N+1 loop of
                    // GetAllByTenantAsync + N×GetWithProvidersAsync.
                    var rows = await repo.GetAllWithProviderCountAsync(tenantId.Value, orgId, ct);
                    return rows
                        .Select(r => new PublicNetworkSummary(r.Id, r.Name, r.Description ?? string.Empty, r.ProviderCount, r.OwningOrganizationId))
                        .ToList();
                });

            return Results.Ok(summaries);
        }).AllowAnonymous().RequireRateLimiting("public-read-limit");

        // ── GET /api/public/network/{id}/providers ─────────────────────────
        // Query: organizationId (optional GUID) — the referral portal's selected law
        // firm. A Private provider only appears when it belongs to that organizationId
        // (or is unowned/legacy); Public providers always appear. See ProviderVisibility.IsVisibleTo.
        group.MapGet("/{id:guid}/providers", async (
            Guid               id,
            string?            organizationId,
            HttpContext         http,
            IConfiguration      config,
            INetworkRepository  repo,
            IMemoryCache        cache,
            CancellationToken   ct) =>
        {
            var tenantId = CareConnect.Api.Helpers.PublicTrustBoundary.ValidateAndResolveTenantId(http, config, "public-network");
            if (tenantId == null)
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    detail: "Request origin could not be verified.");
            Guid? orgId = Guid.TryParse(organizationId, out var parsedOrgId) ? parsedOrgId : null;

            // BLK-PERF-02: Cache the org-agnostic active membership list per tenant+network
            // for 60 s (unchanged cache key — no per-org cache entries, no invalidation-key
            // fan-out needed). Visibility filtering by organizationId happens per-request,
            // AFTER the cache read, so the cached entity list can be reused across every
            // organizationId that requests this network.
            var memberships = await cache.GetOrCreateAsync(
                CareConnectCacheKeys.PublicNetworkProviders(tenantId.Value, id),
                async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = CareConnectCacheTtl.PublicNetwork;
                    entry.Size = 1;

                    var network = await repo.GetByIdAsync(tenantId.Value, id, ct);
                    if (network == null) return null;

                    var all = await repo.GetNetworkProviderMembershipsAsync(tenantId.Value, id, ct);
                    return all.Where(IsPublicProviderLocationActive).ToList();
                });

            if (memberships == null) return Results.NotFound();

            var items = memberships
                .Where(np => ProviderVisibility.IsVisibleTo(np, orgId, viewerSeesAll: false))
                .Select(ToPublicProviderItem)
                .ToList();
            return Results.Ok(items);
        }).AllowAnonymous().RequireRateLimiting("public-read-limit");

        // ── GET /api/public/network/{id}/providers/markers ──────────────────
        group.MapGet("/{id:guid}/providers/markers", async (
            Guid               id,
            string?            organizationId,
            HttpContext         http,
            IConfiguration      config,
            INetworkRepository  repo,
            IMemoryCache        cache,
            CancellationToken   ct) =>
        {
            var tenantId = CareConnect.Api.Helpers.PublicTrustBoundary.ValidateAndResolveTenantId(http, config, "public-network");
            if (tenantId == null)
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    detail: "Request origin could not be verified.");
            Guid? orgId = Guid.TryParse(organizationId, out var parsedOrgId) ? parsedOrgId : null;

            // BLK-PERF-02: same org-agnostic caching approach as /providers above —
            // cache the filtered-by-active entity list, apply Visibility filtering per request.
            var memberships = await cache.GetOrCreateAsync(
                CareConnectCacheKeys.PublicNetworkMarkers(tenantId.Value, id),
                async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = CareConnectCacheTtl.PublicNetwork;
                    entry.Size = 1;

                    var network = await repo.GetByIdAsync(tenantId.Value, id, ct);
                    if (network == null) return null;

                    var all = await repo.GetNetworkProviderMembershipsAsync(tenantId.Value, id, ct);
                    return all.Where(IsPublicProviderLocationActive).ToList();
                });

            if (memberships == null) return Results.NotFound();

            // Include every visible provider so the client can geocode those whose
            // coordinates have not yet been stored (0.0 signals "needs geocoding").
            var markers = memberships
                .Where(np => ProviderVisibility.IsVisibleTo(np, orgId, viewerSeesAll: false))
                .Select(ToPublicProviderMarker)
                .ToList();
            return Results.Ok(markers);
        }).AllowAnonymous().RequireRateLimiting("public-read-limit");

        // ── GET /api/public/network/{id}/detail ────────────────────────────
        group.MapGet("/{id:guid}/detail", async (
            Guid               id,
            string?            organizationId,
            HttpContext         http,
            IConfiguration      config,
            INetworkRepository  repo,
            ISpecialtyService   specialtyService,
            IMemoryCache        cache,
            CancellationToken   ct) =>
        {
            var tenantId = CareConnect.Api.Helpers.PublicTrustBoundary.ValidateAndResolveTenantId(http, config, "public-network");
            if (tenantId == null)
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    detail: "Request origin could not be verified.");
            Guid? orgId = Guid.TryParse(organizationId, out var parsedOrgId) ? parsedOrgId : null;

            // BLK-PERF-02: Cache the org-agnostic payload (network + active memberships +
            // specialty options) per tenant+network for 60 s. Single factory still covers
            // both data sets to avoid a split-brain cache state between /providers and
            // /detail. Visibility filtering by organizationId happens per-request, after
            // the cache read.
            var cached = await cache.GetOrCreateAsync<(Guid Id, string Name, string Description, List<NetworkProvider> Memberships, List<SpecialtyResponse> SpecialtyOptions)?>(
                CareConnectCacheKeys.PublicNetworkDetail(tenantId.Value, id),
                async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = CareConnectCacheTtl.PublicNetwork;
                    entry.Size = 1;

                    var network = await repo.GetWithProvidersAsync(tenantId.Value, id, ct);
                    if (network == null) return null;
                    var specialtyOptions = await specialtyService.GetAllAsync(includeInactive: false, ct);

                    var memberships = network.NetworkProviders
                        .Where(np => np.Provider != null && np.Facility != null)
                        .Where(IsPublicProviderLocationActive)
                        .OrderBy(np => np.Provider.OrganizationName ?? np.Provider.Name)
                        .ThenBy(np => np.Facility.Name)
                        .ToList();

                    return (network.Id, network.Name, network.Description, Memberships: memberships, SpecialtyOptions: specialtyOptions);
                });

            if (cached == null) return Results.NotFound();

            var visibleMemberships = cached.Value.Memberships
                .Where(np => ProviderVisibility.IsVisibleTo(np, orgId, viewerSeesAll: false))
                .ToList();

            var items = visibleMemberships.Select(ToPublicProviderItem).ToList();
            // Include every visible provider (0.0 lat/lng = needs client-side geocoding).
            var markers = visibleMemberships.Select(ToPublicProviderMarker).ToList();

            var detail = new PublicNetworkDetail(
                cached.Value.Id, cached.Value.Name, cached.Value.Description,
                items, markers, cached.Value.SpecialtyOptions);
            return Results.Ok(detail);
        }).AllowAnonymous().RequireRateLimiting("public-read-limit");

        // ── GET /api/public/treatment-types ────────────────────────────────
        // Returns the platform-wide treatment type lookup list.
        // Trust-boundary validated (same as other public endpoints).
        app.MapGet("/api/public/treatment-types", async (
            HttpContext       http,
            IConfiguration    config,
            CareConnect.Infrastructure.Data.CareConnectDbContext db,
            CancellationToken ct) =>
        {
            var tenantId = CareConnect.Api.Helpers.PublicTrustBoundary.ValidateAndResolveTenantId(http, config, "public-network");
            if (tenantId == null)
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    detail: "Request origin could not be verified.");

            try
            {
                var conn = db.Database.GetDbConnection();
                if (conn.State != System.Data.ConnectionState.Open)
                    await conn.OpenAsync(ct);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT `Id`, `Name`, `Category`, `DisplayOrder`
                    FROM `cc_TreatmentTypes`
                    WHERE `IsActive` = 1
                    ORDER BY `DisplayOrder` ASC, `Name` ASC
                    """;
                var result = new List<object>();
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var treatmentTypeId = reader.GetValue(0) switch
                    {
                        Guid guidId => guidId.ToString(),
                        _ => reader.GetString(0),
                    };

                    result.Add(new
                    {
                        id           = treatmentTypeId,
                        name         = reader.GetString(1),
                        category     = reader.IsDBNull(2) ? null : reader.GetString(2),
                        displayOrder = reader.GetInt32(3),
                    });
                }
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                var log = http.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("CareConnect.PublicTreatmentTypes");
                log.LogError(ex, "Failed to query cc_TreatmentTypes.");
                return Results.Problem("Unable to load treatment types.");
            }
        }).AllowAnonymous().RequireRateLimiting("public-read-limit");

        // ── GET /api/public/referral-attributions ───────────────────────────
        // Anonymous mirror of GET /api/referral-attributions/options — the authenticated
        // in-app referral form and this anonymous public one must offer the same active
        // Referral Attribution options for a given tenant. Trust-boundary validated (same
        // as other public endpoints), never exposes inactive attributions.
        app.MapGet("/api/public/referral-attributions", async (
            HttpContext                    http,
            IConfiguration                 config,
            IReferralAttributionService    service,
            CancellationToken              ct) =>
        {
            var tenantId = CareConnect.Api.Helpers.PublicTrustBoundary.ValidateAndResolveTenantId(http, config, "public-network");
            if (tenantId == null)
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    detail: "Request origin could not be verified.");

            var result = await service.ListAsync(tenantId.Value, activeOnly: true, ct);
            var options = result.Select(a => new ReferralAttributionSummary
            {
                Id = a.Id,
                FirstName = a.FirstName,
                LastName = a.LastName,
                IsActive = a.IsActive,
            });
            return Results.Ok(options);
        }).AllowAnonymous().RequireRateLimiting("public-read-limit");

        // ── POST /api/public/referrals ──────────────────────────────────────
        // CC2-INT-B08 — Public referral initiation.
        // Accepts an unauthenticated referral submission from the public network directory.
        // Rate-limited (10 req/min per IP, policy registered in Program.cs) to prevent abuse.
        // Tenant isolation: X-Tenant-Id set server-side by Next.js BFF from subdomain — never
        // read from user input.
        // Token/notification flow: delegated to IReferralService.CreateAsync, which fires:
        //   - SendNewReferralNotificationAsync  (email + signed token for URL-stage providers)
        //   - SendProviderAssignedNotificationAsync (platform Notifications → portal visibility)
        app.MapPost("/api/public/referrals", async (
            PublicReferralRequest         req,
            HttpContext                   http,
            IConfiguration               config,
            IProviderRepository          providerRepo,
            INetworkRepository           networkRepo,
            IReferralService             referralSvc,
            IIdentityOrganizationService identityOrgs,
            ILoggerFactory               loggerFactory,
            CancellationToken            ct) =>
        {
            var logger = loggerFactory.CreateLogger("CareConnect.PublicReferrals");
            return await HandlePublicReferral(req, http, config, providerRepo, networkRepo, referralSvc, identityOrgs, logger, ct);
        })
        .AllowAnonymous()
        .RequireRateLimiting("public-referral-limit");

        // ── POST /api/public/referrals/{referralId}/attachments/upload ──────────
        // CC2-INT-B08 — Public document upload for referrals submitted from the public network form.
        // Secured by the same two-layer trust boundary as the referral creation endpoint.
        // Accepts a single file (multipart/form-data) and proxies it to the Documents service.
        app.MapPost("/api/public/referrals/{referralId:guid}/attachments/upload", async (
            Guid                                   referralId,
            HttpRequest                            httpRequest,
            HttpContext                            http,
            IConfiguration                         config,
            IReferralAttachmentService             attachmentSvc,
            Microsoft.Extensions.Options.IOptions<CareConnect.Api.Options.AttachmentUploadOptions> uploadOptions,
            ILoggerFactory                         loggerFactory,
            CancellationToken                      ct) =>
        {
            var logger = loggerFactory.CreateLogger("CareConnect.PublicReferrals");

            var tenantId = CareConnect.Api.Helpers.PublicTrustBoundary.ValidateAndResolveTenantId(http, config, "public-network");
            if (tenantId == null)
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    detail: "Request origin could not be verified.");

            if (!httpRequest.HasFormContentType)
                return Results.BadRequest(new { error = "Request must be multipart/form-data." });

            var form = await httpRequest.ReadFormAsync(ct);
            if (form.Files.Count == 0)
                return Results.BadRequest(new { error = "No file was provided." });

            var file    = form.Files[0];
            var options = uploadOptions.Value;

            if (file.Length > options.MaxFileSizeBytes)
                return Results.BadRequest(new { error = $"File size exceeds the maximum allowed size of {options.MaxFileSizeBytes / (1024 * 1024)} MB." });

            var normalizedType = file.ContentType?.Split(';')[0].Trim().ToLowerInvariant() ?? string.Empty;
            if (!options.AllowedContentTypes.Contains(normalizedType, StringComparer.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = $"File type '{file.ContentType}' is not permitted.", allowed = options.AllowedContentTypes });

            try
            {
                await using var stream       = file.OpenReadStream();
                var             uploadReq    = new UploadAttachmentRequest { Scope = AttachmentScope.Shared };
                var             result       = await attachmentSvc.UploadAsync(
                    tenantId.Value,
                    referralId,
                    userId: null,
                    callerOrgId: null,
                    callerEmail: null,
                    isAdmin: false,
                    stream,
                    file.FileName,
                    file.ContentType ?? "application/octet-stream",
                    file.Length,
                    uploadReq,
                    ct,
                    bypassAccessCheck: true);

                logger.LogInformation(
                    "Public referral document uploaded: ReferralId={ReferralId} AttachmentId={AttachmentId} File={FileName}",
                    referralId, result.Id, file.FileName);

                return Results.Created($"/api/public/referrals/{referralId}/attachments/{result.Id}", result);
            }
            catch (NotFoundException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Public referral document upload failed for referral {ReferralId}.", referralId);
                return Results.Problem("An unexpected error occurred while uploading the document.");
            }
        })
        .AllowAnonymous()
        .RequireRateLimiting("public-referral-limit")
        .DisableAntiforgery();

        // ── GET /api/public/referrer-status?email=xxx ────────────────────────
        // CC-PORTAL-CHECK — After a public referral is submitted, the success screen
        // checks whether the law firm's email already has active portal access in
        // this tenant.
        //
        // Response: { hasPortalAccess: bool, status: string }
        //   active_in_tenant           → login CTA
        //   existing_user_other_tenant → link-account CTA
        //   no_account                 → default enrollment CTA
        //
        // Delegates the tenant-aware user lookup to the Identity service. Any
        // infrastructure failure → no_account / hasPortalAccess = false (safe default).
        app.MapGet("/api/public/referrer-status", async (
            string?                    email,
            HttpContext                 http,
            IConfiguration             config,
            IIdentityOrganizationService identityOrgs,
            CancellationToken          ct) =>
        {
            var tenantId = CareConnect.Api.Helpers.PublicTrustBoundary.ValidateAndResolveTenantId(http, config, "public-network");
            if (tenantId is null)
                return Results.StatusCode(403);

            if (string.IsNullOrWhiteSpace(email))
                return Results.Ok(new
                {
                    hasPortalAccess = false,
                    status = ReferrerPortalAccessStatuses.NoAccount,
                });

            var status = await identityOrgs.GetReferrerPortalAccessStatusAsync(tenantId.Value, email.Trim(), ct);
            return Results.Ok(new
            {
                hasPortalAccess = status == ReferrerPortalAccessStatuses.ActiveInTenant,
                status,
            });
        })
        .AllowAnonymous()
        .RequireRateLimiting("referrer-status-limit");

        // ── GET /api/treatment-types (authenticated) ────────────────────────
        // Authenticated mirror of /api/public/treatment-types — used when a portal-
        // authenticated law firm user loads the referral form from the browse-networks view.
        // Treatment types are platform-wide (no tenant column) so no tenant resolution needed;
        // JWT auth is sufficient for isolation.
        app.MapGet("/api/treatment-types", async (
            HttpContext       http,
            CareConnect.Infrastructure.Data.CareConnectDbContext db,
            CancellationToken ct) =>
        {
            try
            {
                var conn = db.Database.GetDbConnection();
                if (conn.State != System.Data.ConnectionState.Open)
                    await conn.OpenAsync(ct);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT `Id`, `Name`, `Category`, `DisplayOrder`
                    FROM `cc_TreatmentTypes`
                    WHERE `IsActive` = 1
                    ORDER BY `DisplayOrder` ASC, `Name` ASC
                    """;
                var result = new List<object>();
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var treatmentTypeId = reader.GetValue(0) switch
                    {
                        Guid guidId => guidId.ToString(),
                        _ => reader.GetString(0),
                    };
                    result.Add(new
                    {
                        id           = treatmentTypeId,
                        name         = reader.GetString(1),
                        category     = reader.IsDBNull(2) ? null : reader.GetString(2),
                        displayOrder = reader.GetInt32(3),
                    });
                }
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                var log = http.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("CareConnect.TreatmentTypes");
                log.LogError(ex, "Failed to query cc_TreatmentTypes.");
                return Results.Problem("Unable to load treatment types.");
            }
        })
        .RequireAuthorization(Policies.AuthenticatedUser)
        .RequireProductAccess(ProductCodes.SynqCareConnect);
    }

    private static async Task<IResult> HandlePublicReferral(
        PublicReferralRequest         req,
        HttpContext                   http,
        IConfiguration               config,
        IProviderRepository          providerRepo,
        INetworkRepository           networkRepo,
        IReferralService             referralSvc,
        IIdentityOrganizationService identityOrgs,
        ILogger                      logger,
        CancellationToken            ct)
    {
            var tenantId = CareConnect.Api.Helpers.PublicTrustBoundary.ValidateAndResolveTenantId(http, config, "public-network");
            if (tenantId == null)
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    detail: "Request origin could not be verified.");

            // CC-OWNER-CHECK: Block tenant owners from submitting referrals.
            // The network operator (tenant owner) is not a valid referrer.
            if (!string.IsNullOrWhiteSpace(req.SenderEmail))
            {
                var isOwner = await identityOrgs.CheckAnyTenantOwnerEmailAsync(
                    req.SenderEmail.Trim(), ct);
                if (isOwner)
                {
                    logger.LogWarning(
                        "Public referral rejected: sender email belongs to a tenant owner (tenantContext={TenantId}).",
                        tenantId.Value);
                    return Results.Conflict(new
                    {
                        message = "The email address used is associated with the account that owns this network and cannot be used to submit referrals.",
                        code    = "OWNER_REFERRAL_BLOCKED",
                    });
                }
            }

            // CC-PORTAL-ACCOUNT-CHECK: Block public referrals from senders who already
            // have an active CareConnect portal account on this tenant.
            // They should log in and use the authenticated referral flow instead.
            if (!string.IsNullOrWhiteSpace(req.SenderEmail))
            {
                var portalStatus = await identityOrgs.GetReferrerPortalAccessStatusAsync(
                    tenantId.Value, req.SenderEmail.Trim(), ct);
                if (portalStatus == ReferrerPortalAccessStatuses.ActiveInTenant)
                {
                    logger.LogWarning(
                        "Public referral rejected: sender email has active portal access " +
                        "(tenantContext={TenantId}).",
                        tenantId.Value);
                    return Results.Conflict(new
                    {
                        message = "This email address is already associated with an active CareConnect account. Please log in to submit referrals.",
                        code    = "ACTIVE_ACCOUNT_EXISTS",
                    });
                }
            }

            // Input validation
            var errors = ValidatePublicReferralRequest(req);
            if (errors.Count > 0)
                return Results.UnprocessableEntity(new { message = "Validation failed.", errors });

            NetworkProvider? membership = null;
            if (req.NetworkProviderId.HasValue)
            {
                try { membership = await networkRepo.GetTenantNetworkMembershipAsync(tenantId.Value, req.NetworkProviderId.Value, ct); }
                catch { membership = null; }
            }

            Provider? provider = membership?.Provider;
            Facility? facility = membership?.Facility;

            if (provider is null)
            {
                // Legacy fallback for clients still submitting only ProviderId.
                try { provider = await providerRepo.GetByIdCrossAsync(req.ProviderId, ct); }
                catch { provider = null; }

                if (provider is null)
                    return Results.NotFound(new { message = "Provider not found." });

                bool providerInTenantNetwork;
                try { providerInTenantNetwork = await networkRepo.IsProviderInTenantNetworkAsync(tenantId.Value, req.ProviderId, ct); }
                catch { providerInTenantNetwork = false; }

                if (!providerInTenantNetwork)
                {
                    logger.LogWarning(
                        "Public referral rejected: provider {ProviderId} is not in any network for tenant {TenantId}. " +
                        "Possible cross-tenant provider injection attempt.",
                        req.ProviderId, tenantId.Value);
                    return Results.NotFound(new { message = "Provider not found." });
                }
            }
            else if (req.ProviderId != Guid.Empty && provider.Id != req.ProviderId)
            {
                logger.LogWarning(
                    "Public referral rejected: networkProviderId {NetworkProviderId} does not match provider {ProviderId}.",
                    req.NetworkProviderId, req.ProviderId);
                return Results.NotFound(new { message = "Provider not found." });
            }

            if (membership is not null && !membership.AcceptingReferrals)
                return Results.UnprocessableEntity(new { message = "This provider location is not currently accepting referrals." });

            if (membership is null && !provider.AcceptingReferrals)
                return Results.UnprocessableEntity(new { message = "This provider is not currently accepting referrals." });

            // Map to the internal CreateReferralRequest.
            // ReferrerName/ReferrerEmail drive the signed-token email notification flow.
            var createReq = new CreateReferralRequest
            {
                ProviderId              = provider.Id,
                FacilityId              = facility?.Id,
                NetworkProviderId       = membership?.Id,
                ClientFirstName         = req.PatientFirstName.Trim(),
                ClientLastName          = req.PatientLastName.Trim(),
                ClientPhone             = req.PatientPhone.Trim(),
                ClientEmail             = req.PatientEmail?.Trim() ?? string.Empty,
                ClientDob               = req.PatientDateOfBirth.HasValue
                                            ? req.PatientDateOfBirth.Value.ToDateTime(TimeOnly.MinValue)
                                            : null,
                RequestedService        = string.IsNullOrWhiteSpace(req.ServiceType)
                                            ? DefaultPublicReferralService
                                            : req.ServiceType.Trim(),
                Urgency                 = !string.IsNullOrWhiteSpace(req.Urgency) && Referral.ValidUrgencies.All.Contains(req.Urgency)
                                            ? req.Urgency
                                            : Referral.ValidUrgencies.Normal,
                TreatmentTypeId         = req.TreatmentTypeId,
                ReferralAttributionId   = req.ReferralAttributionId,
                DateOfAccident          = req.PatientDateOfAccident,
                Notes                   = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim(),
                LienCompanyName         = req.LienCompanyName?.Trim(),
                LienCompanyEmail        = req.LienCompanyEmail?.Trim(),
                ReferrerFirstName       = req.SenderFirstName.Trim(),
                ReferrerLastName        = req.SenderLastName?.Trim(),
                ReferrerFirmName        = req.SenderFirmName?.Trim(),
                ReferrerPhone           = req.SenderPhone?.Trim(),
                ReferrerEmail           = req.SenderEmail.Trim(),
                ReferringOrganizationId = null,   // public — no org context
                ReceivingOrganizationId = null,
            };

            // Create referral via the existing pipeline.
            // CreateAsync persists the referral and fires fire-and-observe notifications.
            // userId = null (anonymous submission).
            try
            {
                var referral = await referralSvc.CreateAsync(tenantId.Value, userId: null, createReq, ct);

                logger.LogInformation(
                    "Public referral submitted: ReferralId={ReferralId} ProviderId={ProviderId} " +
                    "Stage={Stage} Tenant={TenantId}",
                    referral.Id, req.ProviderId, provider.AccessStage, tenantId.Value);

                var response = new PublicReferralResponse(
                    referral.Id,
                    provider.Id,
                    facility?.Id,
                    membership?.Id,
                    provider.Name,
                    provider.AccessStage,
                    "Referral submitted successfully. The provider will be in touch shortly.");

                return Results.Created($"/api/public/referrals/{referral.Id}", response);
            }
            catch (NotFoundException ex)
            {
                logger.LogWarning(ex, "Public referral: provider not found mid-creation.");
                return Results.NotFound(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Public referral creation failed for provider {ProviderId}.", req.ProviderId);
                return Results.Problem("An unexpected error occurred while submitting your referral.");
            }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// CC2-INT-B08: Validates a public referral submission.
    /// Returns a dictionary of field → error message for any validation failures.
    /// </summary>
    private static Dictionary<string, string> ValidatePublicReferralRequest(PublicReferralRequest req)
    {
        var errors = new Dictionary<string, string>();

        if (req.ProviderId == Guid.Empty && !req.NetworkProviderId.HasValue)
            errors["providerId"] = "A valid provider or provider-location selection is required.";

        if (string.IsNullOrWhiteSpace(req.SenderFirstName))
            errors["senderFirstName"] = "Your first name is required.";
        else if (req.SenderFirstName.Length > 100)
            errors["senderFirstName"] = "First name must not exceed 100 characters.";

        if (!string.IsNullOrWhiteSpace(req.SenderLastName) && req.SenderLastName.Length > 100)
            errors["senderLastName"] = "Last name must not exceed 100 characters.";

        if (string.IsNullOrWhiteSpace(req.SenderEmail))
            errors["senderEmail"] = "Your email address is required.";
        else if (!IsValidEmail(req.SenderEmail))
            errors["senderEmail"] = "A valid email address is required.";

        if (string.IsNullOrWhiteSpace(req.PatientFirstName) || req.PatientFirstName.Trim().Length < 1)
            errors["patientFirstName"] = "Patient first name is required.";
        else if (req.PatientFirstName.Length > 100)
            errors["patientFirstName"] = "First name must not exceed 100 characters.";

        if (string.IsNullOrWhiteSpace(req.PatientLastName) || req.PatientLastName.Trim().Length < 1)
            errors["patientLastName"] = "Patient last name is required.";
        else if (req.PatientLastName.Length > 100)
            errors["patientLastName"] = "Last name must not exceed 100 characters.";

        if (string.IsNullOrWhiteSpace(req.PatientPhone))
            errors["patientPhone"] = "Patient phone number is required.";
        else if (req.PatientPhone.Trim().Length < 7 || req.PatientPhone.Length > 30)
            errors["patientPhone"] = "Please enter a valid phone number.";

        if (!string.IsNullOrWhiteSpace(req.PatientEmail) && !IsValidEmail(req.PatientEmail))
            errors["patientEmail"] = "Please enter a valid patient email address.";

        if (!req.PatientDateOfBirth.HasValue)
            errors["patientDateOfBirth"] = "Patient date of birth is required.";
        else if (req.PatientDateOfBirth.Value.Year < 1900)
            errors["patientDateOfBirth"] = "Please enter a valid year (1900 or later).";
        else if (req.PatientDateOfBirth.Value > DateOnly.FromDateTime(DateTime.UtcNow))
            errors["patientDateOfBirth"] = "Date of birth cannot be in the future.";

        if (!req.PatientDateOfAccident.HasValue)
            errors["patientDateOfAccident"] = "Date of accident is required.";
        else if (req.PatientDateOfAccident.Value.Year < 1900)
            errors["patientDateOfAccident"] = "Please enter a valid year (1900 or later).";
        else if (req.PatientDateOfAccident.Value > DateOnly.FromDateTime(DateTime.UtcNow))
            errors["patientDateOfAccident"] = "Date of accident cannot be in the future.";

        if (req.PatientAddress is not null && req.PatientAddress.Length > 500)
            errors["patientAddress"] = "Address must not exceed 500 characters.";

        if (req.ServiceType is not null && req.ServiceType.Length > 200)
            errors["serviceType"] = "Service type must not exceed 200 characters.";

        if (req.Notes is not null && req.Notes.Length > 2000)
            errors["notes"] = "Notes must not exceed 2000 characters.";

        return errors;
    }

    private static bool IsValidEmail(string email)
    {
        try { _ = new MailAddress(email.Trim()); return true; }
        catch { return false; }
    }

    private static List<SpecialtyResponse> MapSpecialties(Provider provider)
    {
        return provider.ProviderSpecialties
            .Where(ps => ps.Specialty != null)
            .OrderByDescending(ps => ps.IsPrimary)
            .ThenBy(ps => ps.Specialty!.Name)
            .Select(ps => new SpecialtyResponse
            {
                Id = ps.Specialty!.Id,
                Name = ps.Specialty.Name,
                Code = ps.Specialty.Code,
                Description = ps.Specialty.Description,
                IsActive = ps.Specialty.IsActive
            })
            .ToList();
    }

    private static PublicProviderItem ToPublicProviderItem(NetworkProvider np)
    {
        var p = np.Provider;
        var f = np.Facility;
        return new PublicProviderItem(
            p.Id,
            np.Id,
            p.Id,
            f.Id,
            p.Name,
            p.Title,
            p.OrganizationName,
            f.Name,
            f.AddressLine1,
            f.Phone ?? p.Phone,
            f.Email ?? p.Email,
            f.City,
            f.State,
            f.PostalCode,
            np.IsActive,
            np.AcceptingReferrals,
            p.AccessStage,
            null,
            MapSpecialties(p),
            PrimarySpecialtyId(p),
            PrimarySpecialtyName(p),
            IsMobile: f.IsMobile,
            ServiceRadiusMiles: f.ServiceRadiusMiles,
            ServiceAreaLabel: f.IsMobile ? f.AddressLine1 : null);
    }

    private static bool IsPublicProviderLocationActive(NetworkProvider np) =>
        np.IsActive &&
        np.Provider.IsActive &&
        np.Facility.IsActive;

    private static PublicProviderMarker ToPublicProviderMarker(NetworkProvider np)
    {
        var p = np.Provider;
        var f = np.Facility;
        return new PublicProviderMarker(
            p.Id,
            np.Id,
            p.Id,
            f.Id,
            p.Name,
            p.Title,
            p.OrganizationName,
            f.Name,
            f.City,
            f.State,
            np.AcceptingReferrals,
            f.Latitude ?? p.Latitude ?? 0.0,
            f.Longitude ?? p.Longitude ?? 0.0,
            MapSpecialties(p),
            PrimarySpecialtyId(p),
            PrimarySpecialtyName(p),
            IsMobile: f.IsMobile,
            ServiceRadiusMiles: f.ServiceRadiusMiles,
            ServiceAreaLabel: f.IsMobile ? f.AddressLine1 : null);
    }

    private static Guid? PrimarySpecialtyId(Provider provider) =>
        MapSpecialties(provider).FirstOrDefault()?.Id;

    private static string? PrimarySpecialtyName(Provider provider) =>
        MapSpecialties(provider).FirstOrDefault()?.Name;
}
