using System.Net;
using BuildingBlocks.Authorization;
using BuildingBlocks.Authorization.Filters;
using BuildingBlocks.Context;
using BuildingBlocks.Exceptions;
using CareConnect.Application.Cache;
using CareConnect.Application.DTOs;
using CareConnect.Application.Interfaces;
using CareConnect.Application.Services;
using CareConnect.Infrastructure.Data;
using LegalSynq.AuditClient;
using LegalSynq.AuditClient.DTOs;
using LegalSynq.AuditClient.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Text.Json;
using AuditVisibility = LegalSynq.AuditClient.Enums.VisibilityScope;

namespace CareConnect.Api.Endpoints;

// CC2-INT-B06 / CC2-INT-B06-01 — provider network management + shared provider registry.
// Access: CARECONNECT_NETWORK_MANAGER or CARECONNECT_REFERRER_ADMIN (LSV3-1084, law-firm-scoped
// network self-management) product role, or PlatformAdmin / TenantAdmin bypass.
public static class NetworkEndpoints
{
    // BLK-SEC-06: HttpContext.Items key Program.cs stashes the raw physical TCP peer address
    // under, captured before UseForwardedHeaders can rewrite Connection.RemoteIpAddress from a
    // client-supplied X-Forwarded-For header. See the provider-import endpoint below.
    internal const string RawRemoteIpAddressKey = "RawRemoteIpAddress";

    public static void MapNetworkEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/networks")
            .RequireAuthorization(Policies.AuthenticatedUser);

        // ── List networks ──────────────────────────────────────────────────────
        group.MapGet("/", async (
            INetworkService service,
            ICurrentRequestContext ctx,
            CancellationToken ct) =>
        {
            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");
            var networks = await service.GetAllAsync(tenantId, ct);
            return Results.Ok(networks);
        })
        .RequireProductRole(ProductCodes.SynqCareConnect, ProductRoleCodes.CareConnectNetworkManager, ProductRoleCodes.CareConnectReferrerAdmin);

        // ── Create network ─────────────────────────────────────────────────────
        group.MapPost("/", async (
            [FromBody] CreateNetworkRequest request,
            INetworkService service,
            ICurrentRequestContext ctx,
            IAuditEventClient auditClient,
            HttpContext http,
            CancellationToken ct) =>
        {
            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");
            var network = await service.CreateAsync(tenantId, ctx.UserId, request, ct, owningOrganizationId: ctx.OrgId);
            // BLK-COMP-01: Audit network creation — every network lifecycle event is traceable.
            var correlationId = http.Items["CorrelationId"]?.ToString() ?? http.TraceIdentifier;
            _ = EmitNetworkAuditAsync(auditClient,
                eventType:     "careconnect.network.created",
                action:        "NetworkCreated",
                description:   $"Network '{network.Name}' created by user.",
                tenantId:      tenantId,
                actorUserId:   ctx.UserId,
                networkId:     network.Id,
                correlationId: correlationId);
            return Results.Created($"/api/networks/{network.Id}", network);
        })
        .RequireProductRole(ProductCodes.SynqCareConnect, ProductRoleCodes.CareConnectNetworkManager, ProductRoleCodes.CareConnectReferrerAdmin);

        // ── Get network detail ─────────────────────────────────────────────────
        group.MapGet("/{id:guid}", async (
            Guid id,
            INetworkService service,
            ICurrentRequestContext ctx,
            CancellationToken ct) =>
        {
            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");
            var network = await service.GetByIdAsync(tenantId, id, ct);
            return Results.Ok(network);
        })
        .RequireProductRole(ProductCodes.SynqCareConnect, ProductRoleCodes.CareConnectNetworkManager, ProductRoleCodes.CareConnectReferrerAdmin);

        // ── Update network ─────────────────────────────────────────────────────
        group.MapPut("/{id:guid}", async (
            Guid id,
            [FromBody] UpdateNetworkRequest request,
            INetworkService service,
            ICurrentRequestContext ctx,
            IAuditEventClient auditClient,
            IMemoryCache cache,
            HttpContext http,
            CancellationToken ct) =>
        {
            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");
            var network = await service.UpdateAsync(
                tenantId, id, ctx.UserId, request, ct,
                isTenantAdmin: IsSystemAdmin(ctx),
                callerOrgId: ctx.OrgId,
                isNetworkManager: IsNetworkManager(ctx));
            // BLK-PERF-02: Network metadata changed — evict all public surface cache entries
            // for this tenant+network so the next public read reflects the update.
            foreach (var key in CareConnectCacheKeys.PublicNetworkInvalidationKeys(tenantId, id))
                cache.Remove(key);
            // BLK-COMP-01: Audit network update.
            var correlationId = http.Items["CorrelationId"]?.ToString() ?? http.TraceIdentifier;
            _ = EmitNetworkAuditAsync(auditClient,
                eventType:     "careconnect.network.updated",
                action:        "NetworkUpdated",
                description:   $"Network '{id}' updated by user.",
                tenantId:      tenantId,
                actorUserId:   ctx.UserId,
                networkId:     id,
                correlationId: correlationId);
            return Results.Ok(network);
        })
        .RequireProductRole(ProductCodes.SynqCareConnect, ProductRoleCodes.CareConnectNetworkManager, ProductRoleCodes.CareConnectReferrerAdmin);

        // ── Delete network ─────────────────────────────────────────────────────
        group.MapDelete("/{id:guid}", async (
            Guid id,
            INetworkService service,
            ICurrentRequestContext ctx,
            IAuditEventClient auditClient,
            IMemoryCache cache,
            HttpContext http,
            CancellationToken ct) =>
        {
            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");
            await service.DeleteAsync(
                tenantId, id, ct,
                isTenantAdmin: IsSystemAdmin(ctx),
                callerOrgId: ctx.OrgId,
                isNetworkManager: IsNetworkManager(ctx));
            // BLK-PERF-02: Network deleted — evict all public surface cache entries
            // for this tenant+network (list + detail + providers + markers).
            foreach (var key in CareConnectCacheKeys.PublicNetworkInvalidationKeys(tenantId, id))
                cache.Remove(key);
            // BLK-COMP-01: Audit network deletion.
            var correlationId = http.Items["CorrelationId"]?.ToString() ?? http.TraceIdentifier;
            _ = EmitNetworkAuditAsync(auditClient,
                eventType:     "careconnect.network.deleted",
                action:        "NetworkDeleted",
                description:   $"Network '{id}' deleted by user.",
                tenantId:      tenantId,
                actorUserId:   ctx.UserId,
                networkId:     id,
                correlationId: correlationId);
            return Results.NoContent();
        })
        .RequireProductRole(ProductCodes.SynqCareConnect, ProductRoleCodes.CareConnectNetworkManager, ProductRoleCodes.CareConnectReferrerAdmin);

        // ── Search shared provider registry ────────────────────────────────────
        // CC2-INT-B06-01: Global cross-tenant search. Returns up to 20 matching providers.
        // Used by the tenant portal "Add Provider" search box.
        group.MapGet("/{id:guid}/providers/search", async (
            Guid id,
            [FromQuery] string? name,
            [FromQuery] string? phone,
            [FromQuery] string? npi,
            [FromQuery] string? city,
            INetworkService service,
            ICurrentRequestContext ctx,
            CancellationToken ct) =>
        {
            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");
            // Access control — verify the network belongs to this tenant
            _ = await service.GetByIdAsync(tenantId, id, ct);
            var results = await service.SearchProvidersAsync(name, phone, npi, city, ct);
            return Results.Ok(results);
        })
        .RequireProductRole(ProductCodes.SynqCareConnect, ProductRoleCodes.CareConnectNetworkManager, ProductRoleCodes.CareConnectReferrerAdmin);

        // ── Add provider to network (match-or-create) ──────────────────────────
        // CC2-INT-B06-01: Body: { existingProviderId } | { newProvider: {...} }
        // Match → associate. No match + new data → create in registry → associate.
        group.MapPost("/{id:guid}/providers", async (
            Guid id,
            [FromBody] AddProviderToNetworkRequest request,
            INetworkService service,
            ICurrentRequestContext ctx,
            IAuditEventClient auditClient,
            IMemoryCache cache,
            HttpContext http,
            CancellationToken ct) =>
        {
            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");
            var provider = await service.AddProviderAsync(
                tenantId,
                id,
                request,
                ctx.UserId,
                ct,
                owningOrganizationId: ctx.OrgId,
                isTenantAdmin: IsSystemAdmin(ctx));
            // BLK-PERF-02: Provider membership changed — evict public provider/marker/detail/list
            // cache entries for this tenant+network so the directory reflects the addition.
            foreach (var key in CareConnectCacheKeys.PublicNetworkInvalidationKeys(tenantId, id))
                cache.Remove(key);
            // BLK-COMP-01: Audit provider association — every network membership change is traceable.
            var correlationId = http.Items["CorrelationId"]?.ToString() ?? http.TraceIdentifier;
            _ = EmitNetworkAuditAsync(auditClient,
                eventType:     "careconnect.network.provider_added",
                action:        "ProviderAdded",
                description:   $"Provider '{provider.ProviderId}' added to network '{id}' by user.",
                tenantId:      tenantId,
                actorUserId:   ctx.UserId,
                networkId:     id,
                correlationId: correlationId,
                metadata:      JsonSerializer.Serialize(new { networkProviderId = provider.NetworkProviderId, providerId = provider.ProviderId, facilityId = provider.FacilityId }));
            return Results.Ok(provider);
        })
        .RequireProductRole(ProductCodes.SynqCareConnect, ProductRoleCodes.CareConnectNetworkManager, ProductRoleCodes.CareConnectReferrerAdmin);

        // ── Edit a provider through a tenant-owned network ────────────────────
        // The shared provider record may only be changed here after verifying the
        // provider is a member of the caller's network.
        group.MapPut("/{id:guid}/providers/{providerId:guid}", async (
            Guid id,
            Guid providerId,
            [FromBody] UpdateNetworkProviderRequest request,
            INetworkService service,
            ICurrentRequestContext ctx,
            IMemoryCache cache,
            CancellationToken ct) =>
        {
            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");
            var provider = await service.UpdateProviderAsync(
                tenantId,
                id,
                providerId,
                request,
                ctx.UserId,
                ct,
                isTenantAdmin: IsSystemAdmin(ctx),
                callerOrgId: ctx.OrgId,
                isNetworkManager: IsNetworkManager(ctx));

            foreach (var key in CareConnectCacheKeys.PublicNetworkInvalidationKeys(tenantId, id))
                cache.Remove(key);

            return Results.Ok(provider);
        })
        .RequireProductRole(ProductCodes.SynqCareConnect, ProductRoleCodes.CareConnectNetworkManager, ProductRoleCodes.CareConnectReferrerAdmin);

        // ── Remove provider from network ───────────────────────────────────────
        group.MapDelete("/{id:guid}/providers/{providerId:guid}", async (
            Guid id,
            Guid providerId,
            INetworkService service,
            ICurrentRequestContext ctx,
            IAuditEventClient auditClient,
            IMemoryCache cache,
            HttpContext http,
            CancellationToken ct,
            [FromQuery] bool cascadeFacility = false) =>
        {
            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");
            // cascadeFacility: only "Delete location" (the Facilities panel button) should tag
            // cc_Facilities.IsActive false. The tenant-portal "Remove from network" icon keeps
            // its original behavior — membership-only soft delete, Facility untouched — since
            // it's a distinct action from Delete location, not merely a different UI trigger
            // for the same one.
            await service.RemoveProviderAsync(
                tenantId, id, providerId, cascadeFacility, ctx.UserId, ct,
                isTenantAdmin: IsSystemAdmin(ctx),
                callerOrgId: ctx.OrgId,
                isNetworkManager: IsNetworkManager(ctx));
            // BLK-PERF-02: Provider removed from network — evict public provider/marker/detail/list
            // cache entries for this tenant+network so the directory reflects the removal.
            foreach (var key in CareConnectCacheKeys.PublicNetworkInvalidationKeys(tenantId, id))
                cache.Remove(key);
            // BLK-COMP-01: Audit provider disassociation.
            var correlationId = http.Items["CorrelationId"]?.ToString() ?? http.TraceIdentifier;
            _ = EmitNetworkAuditAsync(auditClient,
                eventType:     "careconnect.network.provider_removed",
                action:        "ProviderRemoved",
                description:   $"Provider '{providerId}' removed from network '{id}' by user.",
                tenantId:      tenantId,
                actorUserId:   ctx.UserId,
                networkId:     id,
                correlationId: correlationId,
                metadata:      JsonSerializer.Serialize(new { providerId = providerId }));
            return Results.NoContent();
        })
        .RequireProductRole(ProductCodes.SynqCareConnect, ProductRoleCodes.CareConnectNetworkManager, ProductRoleCodes.CareConnectReferrerAdmin);

        // ── Map markers for providers in network ───────────────────────────────
        group.MapGet("/{id:guid}/providers/markers", async (
            Guid id,
            INetworkService service,
            ICurrentRequestContext ctx,
            CancellationToken ct) =>
        {
            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");
            var markers = await service.GetMarkersAsync(tenantId, id, ct);
            return Results.Ok(markers);
        })
        .RequireProductRole(ProductCodes.SynqCareConnect, ProductRoleCodes.CareConnectNetworkManager, ProductRoleCodes.CareConnectReferrerAdmin);

        // ── List providers in network ──────────────────────────────────────────
        group.MapGet("/{id:guid}/providers", async (
            Guid id,
            INetworkService service,
            ICurrentRequestContext ctx,
            CancellationToken ct) =>
        {
            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");
            var detail = await service.GetByIdAsync(tenantId, id, ct);
            return Results.Ok(detail.Providers);
        })
        .RequireProductRole(ProductCodes.SynqCareConnect, ProductRoleCodes.CareConnectNetworkManager, ProductRoleCodes.CareConnectReferrerAdmin);

        // ── Network Referral Monitor ────────────────────────────────────────────
        // GET /api/network/referrals — all referrals for this tenant's network,
        // accessible to the lien company's network manager to see law-firm → provider flows.
        app.MapGet("/api/network/referrals", GetNetworkReferralsAsync)
            .RequireAuthorization(Policies.AuthenticatedUser)
            .RequireProductRole(ProductCodes.SynqCareConnect, ProductRoleCodes.CareConnectNetworkManager);

        // ── Referrer read-only network directory ────────────────────────────────
        // CC-REFERRER-BROWSE: Law firm portal users (CareConnectReferrer) can
        // browse all active networks within the tenant and view provider maps
        // so they know which providers to target when submitting referrals.
        var dirGroup = app.MapGroup("/api/networks/directory")
            .RequireAuthorization(Policies.AuthenticatedUser);

        // List all networks (summary) — referrers see name, description, provider count
        dirGroup.MapGet("/", async (
            INetworkService      service,
            ICurrentRequestContext ctx,
            CancellationToken    ct) =>
        {
            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");
            var networks = await service.GetAllAsync(tenantId, ct);
            return Results.Ok(networks);
        })
        .RequireProductRole(ProductCodes.SynqCareConnect, ProductRoleCodes.CareConnectReferrer);

        // Get network with full provider list
        dirGroup.MapGet("/{id:guid}", async (
            Guid                  id,
            INetworkService       service,
            ICurrentRequestContext ctx,
            CancellationToken     ct) =>
        {
            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");
            var detail = await service.GetByIdAsync(tenantId, id, ct);
            return Results.Ok(detail);
        })
        .RequireProductRole(ProductCodes.SynqCareConnect, ProductRoleCodes.CareConnectReferrer);

        // Get provider map markers for a specific network
        dirGroup.MapGet("/{id:guid}/markers", async (
            Guid                  id,
            INetworkService       service,
            ICurrentRequestContext ctx,
            CancellationToken     ct) =>
        {
            var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");
            var markers = await service.GetMarkersAsync(tenantId, id, ct);
            return Results.Ok(markers);
        })
        .RequireProductRole(ProductCodes.SynqCareConnect, ProductRoleCodes.CareConnectReferrer);

        app.MapPost("/api/networks/{id:guid}/providers/import", async (
            Guid id,
            HttpRequest httpRequest,
            INetworkService service,
            IAuditEventClient auditClient,
            IMemoryCache cache,
            HttpContext http,
            CancellationToken ct) =>
        {
            // BLK-SEC-06: this endpoint is intentionally unauthenticated (see .AllowAnonymous()
            // below) — it's a bulk provider-write operation meant to be run only via `curl`
            // against the loopback interface (127.0.0.1/::1) on the box the service runs on,
            // never reachable from outside. Gated on the raw physical TCP peer address captured
            // by Program.cs *before* UseForwardedHeaders runs, so a caller can't spoof loopback
            // by sending X-Forwarded-For: 127.0.0.1 through the trusted reverse proxy.
            if (!IsLoopbackCaller(http))
                return Results.Forbid();

            var file = await ReadImportFileAsync(httpRequest, ct);

            await using var stream = file.OpenReadStream();
            var result = await service.ImportProvidersAsync(
                id,
                stream,
                file.FileName,
                dryRun: ParseDryRunFlag(httpRequest),
                userId: null,
                ct);

            if (!result.DryRun)
            {
                foreach (var key in CareConnectCacheKeys.PublicNetworkInvalidationKeys(result.TenantId, id))
                    cache.Remove(key);

                var correlationId = http.Items["CorrelationId"]?.ToString() ?? http.TraceIdentifier;
                _ = EmitNetworkAuditAsync(auditClient,
                    eventType: "careconnect.network.providers_imported",
                    action: "ProvidersImported",
                    description: $"Imported providers into network '{id}' via local import endpoint.",
                    tenantId: result.TenantId,
                    actorUserId: null,
                    networkId: id,
                    correlationId: correlationId,
                    metadata: JsonSerializer.Serialize(new
                    {
                        result.TotalRows,
                        result.ProcessedRows,
                        result.CreatedProviders,
                        result.CreatedFacilities,
                        result.LinkedLocations,
                        result.ReusedByNpi,
                        result.ReusedByEmail,
                        result.AlreadyInNetwork,
                        result.FailedRows
                    }));
            }

            return Results.Ok(result);
        })
        .AllowAnonymous()
        .DisableAntiforgery();
    }

    private static bool IsSystemAdmin(ICurrentRequestContext ctx) =>
        ctx.IsPlatformAdmin || ctx.Roles.Contains(Roles.TenantAdmin, StringComparer.OrdinalIgnoreCase);

    private static bool IsNetworkManager(ICurrentRequestContext ctx) =>
        ctx.ProductRoles.Contains($"{ProductCodes.SynqCareConnect}:{ProductRoleCodes.CareConnectNetworkManager}", StringComparer.OrdinalIgnoreCase);

    private static bool IsLoopbackCaller(HttpContext http)
    {
        var ip = http.Items.TryGetValue(RawRemoteIpAddressKey, out var raw) && raw is IPAddress captured
            ? captured
            : http.Connection.RemoteIpAddress;

        if (ip is null)
            return false;

        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        return IPAddress.IsLoopback(ip);
    }

    private static async Task<IFormFile> ReadImportFileAsync(HttpRequest httpRequest, CancellationToken ct)
    {
        if (!httpRequest.HasFormContentType)
            throw new ValidationException("Validation failed.",
                new() { ["file"] = ["Request must be multipart/form-data."] });

        var form = await httpRequest.ReadFormAsync(ct);
        if (form.Files.Count == 0)
            throw new ValidationException("Validation failed.",
                new() { ["file"] = ["No file was provided."] });

        var file = form.Files[0];
        if (file.Length == 0)
            throw new ValidationException("Validation failed.",
                new() { ["file"] = ["The import file is empty."] });

        const long maxBytes = 5L * 1024 * 1024;
        if (file.Length > maxBytes)
            throw new ValidationException("Validation failed.",
                new() { ["file"] = ["The import file exceeds the 5 MB limit."] });

        var extension = Path.GetExtension(file.FileName);
        if (!string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new ValidationException("Validation failed.",
                new() { ["file"] = ["Only CSV or XLSX files are supported for provider imports."] });

        return file;
    }

    private static bool ParseDryRunFlag(HttpRequest httpRequest)
    {
        var raw = httpRequest.Form["dryRun"].FirstOrDefault();
        return bool.TryParse(raw, out var parsed) && parsed;
    }

    // ── BLK-COMP-01: Shared audit helper ─────────────────────────────────────────
    // Mirrors the EmitAuditAsync pattern in ProviderAdminEndpoints.
    // Fire-and-observe: caller uses `_ = EmitNetworkAuditAsync(...)` — never awaited,
    // never gates the primary business operation on audit delivery success.
    private static Task EmitNetworkAuditAsync(
        IAuditEventClient auditClient,
        string            eventType,
        string            action,
        string            description,
        Guid              tenantId,
        Guid?             actorUserId,
        Guid              networkId,
        string            correlationId,
        string?           metadata = null)
    {
        try
        {
            return auditClient.IngestAsync(new IngestAuditEventRequest
            {
                EventType     = eventType,
                EventCategory = EventCategory.Business,
                SourceSystem  = "care-connect",
                SourceService = "network-management",
                Visibility    = AuditVisibility.Tenant,
                Severity      = SeverityLevel.Info,
                OccurredAtUtc = DateTimeOffset.UtcNow,
                Scope = new AuditEventScopeDto
                {
                    ScopeType = ScopeType.Tenant,
                    TenantId  = tenantId.ToString(),
                    UserId    = actorUserId?.ToString(),
                },
                Actor = new AuditEventActorDto
                {
                    Type = actorUserId.HasValue ? ActorType.User : ActorType.System,
                    Id   = actorUserId?.ToString() ?? "system",
                },
                Entity = new AuditEventEntityDto
                {
                    Type = "Network",
                    Id   = networkId.ToString(),
                },
                Action        = action,
                Description   = description,
                Outcome       = "success",
                CorrelationId = correlationId,
                Metadata      = metadata,
                IdempotencyKey = IdempotencyKey.ForWithTimestamp(
                    DateTimeOffset.UtcNow, "care-connect", eventType, networkId.ToString()),
                Tags = ["network", "careconnect"],
            });
        }
        catch
        {
            return Task.CompletedTask;
        }
    }

    // ── GET /api/network/referrals ─────────────────────────────────────────────
    // Network Manager Referral Monitor — scoped to the caller's tenant.
    // Returns all referrals flowing through the network (sent by law firms to providers).
    // Supports ?status=, ?search= (client name, case #, referrer, provider), ?page=, ?pageSize=.
    private static async Task<IResult> GetNetworkReferralsAsync(
        CareConnectDbContext    db,
        ITenantServiceClient    tenantClient,
        ICurrentRequestContext  ctx,
        [FromQuery] int     page     = 1,
        [FromQuery] int     pageSize = 50,
        [FromQuery] string? status   = null,
        [FromQuery] string? search   = null,
        CancellationToken   ct       = default)
    {
        var tenantId = ctx.TenantId ?? throw new InvalidOperationException("tenant_id claim is missing.");
        var tenantDisplayNames = await ReferralTenantNameResolver.ResolveAsync([tenantId], tenantClient, ct);
        var networkName = tenantDisplayNames.GetValueOrDefault(tenantId, ReferralTenantNameResolver.Fallback);

        page     = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = db.Referrals
            .Include(r => r.Provider)
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId);

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(r => r.Status == status);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            var matchesNetworkName = networkName.Contains(s, StringComparison.OrdinalIgnoreCase);

            if (!matchesNetworkName)
            {
                query = query.Where(r =>
                    EF.Functions.Like((r.ClientFirstName + " " + r.ClientLastName).ToLower(), "%" + s + "%") ||
                    (r.CaseNumber    != null && EF.Functions.Like(r.CaseNumber.ToLower(),    "%" + s + "%")) ||
                    (r.ReferrerName  != null && EF.Functions.Like(r.ReferrerName.ToLower(),  "%" + s + "%")) ||
                    (r.Provider      != null && EF.Functions.Like(r.Provider.Name.ToLower(), "%" + s + "%")));
            }
        }

        query = query.OrderByDescending(r => r.CreatedAtUtc);

        var total = await query.CountAsync(ct);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new
            {
                id                      = r.Id,
                status                  = r.Status,
                urgency                 = r.Urgency,
                clientFirstName         = r.ClientFirstName,
                clientLastName          = r.ClientLastName,
                caseNumber              = r.CaseNumber,
                requestedService        = r.RequestedService,
                providerName            = r.Provider != null ? r.Provider.Name : (string?)null,
                providerOrganizationName = r.Provider != null ? r.Provider.OrganizationName : (string?)null,
                referringOrganizationId = r.ReferringOrganizationId,
                referrerName            = r.ReferrerName,
                referrerEmail           = r.ReferrerEmail,
                networkName,
                createdAtUtc            = r.CreatedAtUtc,
                updatedAtUtc            = r.UpdatedAtUtc,
            })
            .ToListAsync(ct);

        return Results.Ok(new { items, total, page, pageSize });
    }
}
