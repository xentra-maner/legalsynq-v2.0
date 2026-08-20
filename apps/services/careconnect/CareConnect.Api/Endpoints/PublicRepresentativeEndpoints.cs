using CareConnect.Api.Helpers;
using CareConnect.Application.DTOs;
using CareConnect.Application.Interfaces;

namespace CareConnect.Api.Endpoints;

/// <summary>
/// Anonymous Referral Representative Portal — no login, no JWT, no product role. Modeled
/// on PublicNetworkEndpoints' anonymous pattern (same two-layer trust boundary, same
/// AllowAnonymous + rate-limit shape) rather than the platform's authenticated-session
/// pattern, because the portal itself has none: the access code is the only credential,
/// end to end.
///
/// Unlike the public network directory (whose AccessCodeGate is a one-time, client-side-
/// only UX unlock — the data endpoints themselves stay open regardless), referral data is
/// PII (client name, DOB, phone, email) and every read here re-verifies the caller's code
/// server-side on every single request via IReferralAttributionAccessCodeService.VerifyAsync.
/// Nothing is cached and nothing is trusted from a prior request: revoking a code or
/// deactivating its attribution takes effect on the very next call, not on next login
/// (there is no login) and not on next page reload of a previously-unlocked client.
/// </summary>
public static class PublicRepresentativeEndpoints
{
    private const string SourceService = "public-representative";

    public static void MapPublicRepresentativeEndpoints(this WebApplication app)
    {
        MapPublicPortalGroup(app.MapGroup("/api/public/referral-portal"));
        MapPublicPortalGroup(app.MapGroup("/api/public/representative"));
    }

    private static void MapPublicPortalGroup(RouteGroupBuilder group)
    {

        // ── POST /api/public/representative/verify ──────────────────────────
        // Stateless code check — tells the caller whether a code is currently valid and,
        // if so, which attribution it names (so the client can show "Cam Perry's referrals"
        // before the first data call). Never mutates anything.
        group.MapPost("/verify", async (
            VerifyReferralAttributionAccessCodeRequest request,
            HttpContext http,
            IConfiguration config,
            IReferralAttributionAccessCodeService codeService,
            CancellationToken ct) =>
        {
            var tenantId = PublicTrustBoundary.ValidateAndResolveTenantId(http, config, SourceService);
            if (tenantId is null)
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    detail: "Request origin could not be verified.");

            var result = await codeService.VerifyAsync(tenantId.Value, request.Code, ct);
            return Results.Ok(result);
        }).AllowAnonymous().RequireRateLimiting("public-read-limit");

        // ── GET /api/public/representative/referrals ─────────────────────────
        group.MapGet("/referrals", async (
            [AsParameters] PublicRepresentativeReferralSearchParams p,
            HttpContext http,
            IConfiguration config,
            IReferralAttributionAccessCodeService codeService,
            IRepresentativeReferralService representativeService,
            CancellationToken ct) =>
        {
            var tenantId = PublicTrustBoundary.ValidateAndResolveTenantId(http, config, SourceService);
            if (tenantId is null)
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    detail: "Request origin could not be verified.");

            var attributionId = await VerifyAndResolveAttributionAsync(codeService, tenantId.Value, p.Code, ct);
            if (attributionId is null)
                return Results.Json(new { error = "This code is invalid, has expired, or no longer grants access." }, statusCode: StatusCodes.Status401Unauthorized);

            var query = new GetRepresentativeReferralsQuery
            {
                SubmittedFrom = p.SubmittedFrom,
                SubmittedTo = p.SubmittedTo,
                Status = p.Status,
                ProviderId = p.ProviderId,
                LawFirmOrganizationId = p.LawFirmOrganizationId,
                Page = Math.Max(1, p.Page ?? 1),
                PageSize = Math.Clamp(p.PageSize ?? 20, 1, 100),
            };

            var result = await representativeService.SearchAsync(tenantId.Value, attributionId.Value, query, ct);
            return Results.Ok(result);
        }).AllowAnonymous().RequireRateLimiting("public-read-limit");

        // ── GET /api/public/representative/referrals/{referralId} ────────────
        group.MapGet("/referrals/{referralId:guid}", async (
            Guid referralId,
            string? code,
            HttpContext http,
            IConfiguration config,
            IReferralAttributionAccessCodeService codeService,
            IRepresentativeReferralService representativeService,
            CancellationToken ct) =>
        {
            var tenantId = PublicTrustBoundary.ValidateAndResolveTenantId(http, config, SourceService);
            if (tenantId is null)
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    detail: "Request origin could not be verified.");

            var attributionId = await VerifyAndResolveAttributionAsync(codeService, tenantId.Value, code, ct);
            if (attributionId is null)
                return Results.Json(new { error = "This code is invalid, has expired, or no longer grants access." }, statusCode: StatusCodes.Status401Unauthorized);

            var detail = await representativeService.GetByIdAsync(tenantId.Value, attributionId.Value, referralId, ct);
            // Generic 404 for "doesn't exist" and "not attributed to this code" alike.
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        }).AllowAnonymous().RequireRateLimiting("public-read-limit");

        // ── GET /api/public/representative/referral-metrics ─────────────────
        group.MapGet("/referral-metrics", async (
            string? code,
            DateTime? from,
            DateTime? to,
            HttpContext http,
            IConfiguration config,
            IReferralAttributionAccessCodeService codeService,
            IRepresentativeReferralService representativeService,
            CancellationToken ct) =>
        {
            var tenantId = PublicTrustBoundary.ValidateAndResolveTenantId(http, config, SourceService);
            if (tenantId is null)
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    detail: "Request origin could not be verified.");

            var attributionId = await VerifyAndResolveAttributionAsync(codeService, tenantId.Value, code, ct);
            if (attributionId is null)
                return Results.Json(new { error = "This code is invalid, has expired, or no longer grants access." }, statusCode: StatusCodes.Status401Unauthorized);

            var metrics = await representativeService.GetMetricsAsync(tenantId.Value, attributionId.Value, from, to, ct);
            return Results.Ok(metrics);
        }).AllowAnonymous().RequireRateLimiting("public-read-limit");

        // ── GET /api/public/referral-portal/law-firms ───────────────────────
        group.MapGet("/law-firms", async (
            string? code,
            HttpContext http,
            IConfiguration config,
            IReferralAttributionAccessCodeService codeService,
            IPendingReferralRequestService pendingService,
            CancellationToken ct) =>
        {
            var tenantId = PublicTrustBoundary.ValidateAndResolveTenantId(http, config, SourceService);
            if (tenantId is null)
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    detail: "Request origin could not be verified.");

            var attributionId = await VerifyAndResolveAttributionAsync(codeService, tenantId.Value, code, ct);
            if (attributionId is null)
                return Results.Json(new { error = "This code is invalid, has expired, or no longer grants access." }, statusCode: StatusCodes.Status401Unauthorized);

            var lawFirms = await pendingService.ListLawFirmOptionsAsync(tenantId.Value, ct);
            return Results.Ok(lawFirms);
        }).AllowAnonymous().RequireRateLimiting("public-read-limit");

        // ── GET /api/public/referral-portal/providers ──────────────────────
        // Master provider list for referral-portal recommendations. Selecting
        // a provider here records a preference only; it does not create a referral
        // or notify the provider.
        group.MapGet("/providers", async (
            [AsParameters] ProviderSearchParams p,
            string? code,
            HttpContext http,
            IConfiguration config,
            IReferralAttributionAccessCodeService codeService,
            IProviderService providerService,
            CancellationToken ct) =>
        {
            var tenantId = PublicTrustBoundary.ValidateAndResolveTenantId(http, config, SourceService);
            if (tenantId is null)
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    detail: "Request origin could not be verified.");

            var attributionId = await VerifyAndResolveAttributionAsync(codeService, tenantId.Value, code, ct);
            if (attributionId is null)
                return Results.Json(new { error = "This code is invalid, has expired, or no longer grants access." }, statusCode: StatusCodes.Status401Unauthorized);

            var query = BuildProviderQuery(p, pageSizeDefault: 100);
            var result = await providerService.SearchAsync(tenantId.Value, query, ct);
            return Results.Ok(result);
        }).AllowAnonymous().RequireRateLimiting("public-read-limit");

        // ── GET /api/public/referral-portal/providers/map ──────────────────
        group.MapGet("/providers/map", async (
            [AsParameters] ProviderSearchParams p,
            string? code,
            HttpContext http,
            IConfiguration config,
            IReferralAttributionAccessCodeService codeService,
            IProviderService providerService,
            CancellationToken ct) =>
        {
            var tenantId = PublicTrustBoundary.ValidateAndResolveTenantId(http, config, SourceService);
            if (tenantId is null)
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    detail: "Request origin could not be verified.");

            var attributionId = await VerifyAndResolveAttributionAsync(codeService, tenantId.Value, code, ct);
            if (attributionId is null)
                return Results.Json(new { error = "This code is invalid, has expired, or no longer grants access." }, statusCode: StatusCodes.Status401Unauthorized);

            var query = BuildProviderQuery(p, pageSizeDefault: 500, fixedPage: 1, fixedPageSize: 500);
            var markers = await providerService.GetMarkersAsync(tenantId.Value, query, ct);
            return Results.Ok(markers);
        }).AllowAnonymous().RequireRateLimiting("public-read-limit");

        // ── POST /api/public/referral-portal/pending-referrals ───────────────
        group.MapPost("/pending-referrals", async (
            string? code,
            CreatePendingReferralRequest request,
            HttpContext http,
            IConfiguration config,
            IReferralAttributionAccessCodeService codeService,
            IPendingReferralRequestService pendingService,
            CancellationToken ct) =>
        {
            var tenantId = PublicTrustBoundary.ValidateAndResolveTenantId(http, config, SourceService);
            if (tenantId is null)
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    detail: "Request origin could not be verified.");

            var attributionId = await VerifyAndResolveAttributionAsync(codeService, tenantId.Value, code, ct);
            if (attributionId is null)
                return Results.Json(new { error = "This code is invalid, has expired, or no longer grants access." }, statusCode: StatusCodes.Status401Unauthorized);

            var created = await pendingService.CreateAsync(tenantId.Value, attributionId.Value, request, ct);
            return Results.Created($"/api/public/referral-portal/pending-referrals/{created.Id}", created);
        }).AllowAnonymous().RequireRateLimiting("public-referral-limit");
    }

    private static GetProvidersQuery BuildProviderQuery(
        ProviderSearchParams p,
        int pageSizeDefault,
        int? fixedPage = null,
        int? fixedPageSize = null) => new()
    {
        Name = p.Name,
        CategoryCode = p.CategoryCode,
        SpecialtyCode = p.SpecialtyCode,
        City = p.City,
        State = p.State,
        AcceptingReferrals = p.AcceptingReferrals,
        IsActive = p.IsActive ?? true,
        Page = fixedPage ?? Math.Max(1, p.Page ?? 1),
        PageSize = fixedPageSize ?? Math.Clamp(p.PageSize ?? pageSizeDefault, 1, pageSizeDefault),
        Latitude = p.Latitude,
        Longitude = p.Longitude,
        RadiusMiles = p.RadiusMiles,
        NorthLat = p.NorthLat,
        SouthLat = p.SouthLat,
        EastLng = p.EastLng,
        WestLng = p.WestLng,
        OrganizationId = p.OrganizationId,
    };

    /// <summary>
    /// The single choke point every data endpoint above calls through: re-verifies the raw
    /// code from scratch. Returns null for every failure mode (missing/malformed code,
    /// invalid/expired/revoked code, deactivated attribution) — callers surface one generic
    /// 401, never distinguishing which case occurred.
    /// </summary>
    private static async Task<Guid?> VerifyAndResolveAttributionAsync(
        IReferralAttributionAccessCodeService codeService,
        Guid tenantId,
        string? code,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code))
            return null;

        var result = await codeService.VerifyAsync(tenantId, code, ct);
        return result.Ok ? result.ReferralAttributionId : null;
    }
}

public class PublicRepresentativeReferralSearchParams
{
    public string? Code { get; set; }
    public DateTime? SubmittedFrom { get; set; }
    public DateTime? SubmittedTo { get; set; }
    public string? Status { get; set; }
    public Guid? ProviderId { get; set; }
    public Guid? LawFirmOrganizationId { get; set; }
    public int? Page { get; set; }
    public int? PageSize { get; set; }
}
