using BuildingBlocks.Authorization;
using BuildingBlocks.Authorization.Filters;
using BuildingBlocks.Context;
using Liens.Application.DTOs;
using Liens.Application.Interfaces;
using Liens.Domain;
using Liens.Domain.Entities;
using Liens.Domain.Enums;
using Liens.Api.Serialization;
using Liens.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace Liens.Api.Endpoints;

public static class LienEndpoints
{
    private sealed class LegacyLiensMedicalListing
    {
        public List<LegacyLiensMedicalListItem> medicalList { get; init; } = [];
        public List<LegacyLiensMedicalFacilityListItem> facilityList { get; init; } = [];
        public List<LegacyLiensMedicalCodeListItem> codeList { get; init; } = [];
        public List<LegacyLiensMedicalDocumentListItem> documentList { get; init; } = [];
    }

    private sealed class LegacyLiensMedicalListItem
    {
        public string id { get; init; } = string.Empty;
        public string caseId { get; init; } = string.Empty;
        public string status { get; init; } = string.Empty;
        public string purchaseDate { get; init; } = string.Empty;
        public string initialServiceDate { get; init; } = string.Empty;
        public string endServiceDate { get; init; } = string.Empty;
        public string note { get; init; } = string.Empty;
        public string created { get; init; } = string.Empty;
        public string createdBy { get; init; } = string.Empty;
        public string updated { get; init; } = string.Empty;
        public string updatedBy { get; init; } = string.Empty;
        public string fundingCompanyId { get; init; } = string.Empty;
        public string fundingCompany { get; init; } = string.Empty;
        public string isBulk { get; init; } = string.Empty;
        public string isServicing { get; init; } = string.Empty;
    }

    private sealed class LegacyLiensMedicalFacilityListItem
    {
        public string id { get; init; } = string.Empty;
        public string liensId { get; init; } = string.Empty;
        public string facilityId { get; init; } = string.Empty;
        public string facilityContactId { get; init; } = string.Empty;
        public string email { get; init; } = string.Empty;
        public string phone { get; init; } = string.Empty;
        public string medicalProviderId { get; init; } = string.Empty;
        public string created { get; init; } = string.Empty;
        public string createdBy { get; init; } = string.Empty;
        public string updated { get; init; } = string.Empty;
        public string updatedBy { get; init; } = string.Empty;
    }

    private sealed class LegacyLiensMedicalCodeListItem
    {
        public string id { get; init; } = string.Empty;
        public string liensId { get; init; } = string.Empty;
        public string code { get; init; } = string.Empty;
        public string medicareCost { get; init; } = string.Empty;
        public string billingAmount { get; init; } = string.Empty;
        public string purchaseAmount { get; init; } = string.Empty;
        public string created { get; init; } = string.Empty;
        public string createdBy { get; init; } = string.Empty;
        public string updated { get; init; } = string.Empty;
        public string updatedBy { get; init; } = string.Empty;
    }

    private sealed class LegacyLiensMedicalDocumentListItem
    {
        public string id { get; init; } = string.Empty;
        public string liensId { get; init; } = string.Empty;
        public string filename { get; init; } = string.Empty;
        public string typeId { get; init; } = string.Empty;
        public string url { get; init; } = string.Empty;
        public string status { get; init; } = string.Empty;
    }

    private sealed class LegacyReassignMedicalProviderRequest
    {
        public string? liensId { get; init; }
        public string? medicalProvider { get; init; }
    }

    private sealed class LegacyReassignFacilityRequest
    {
        public string? liensId { get; init; }
        public string? facility { get; init; }
    }

    private sealed class LegacyReassignFacilityContactPersonRequest
    {
        public string? liensId { get; init; }
        public string? facilityContactPerson { get; init; }
    }

    private sealed class LegacyReassignFundingCompanyRequest
    {
        public string? liensId { get; init; }
        public string? fundingCompany { get; init; }
    }

    private sealed class SearchLiensRequest
    {
        public string? Search { get; init; }
        public string? Status { get; init; }
        public string? LienType { get; init; }
        public Guid? CaseId { get; init; }
        public Guid? FacilityId { get; init; }
        public int Page { get; init; } = 1;
        public int PageSize { get; init; } = 20;
        public string[]? LawFirmIds { get; init; }
        public string[]? MedicalFacilityIds { get; init; }
        public string[]? CaseManagerIds { get; init; }
        public string[]? LienStatusIds { get; init; }
        public string? PurchaseDateFrom { get; init; }
        public string? PurchaseDateTo { get; init; }
        public string? ClosedDateFrom { get; init; }
        public string? ClosedDateTo { get; init; }
        public string? SortBy { get; init; }
        public string? SortDirection { get; init; }
    }

    internal sealed record AdvancedLienFilterRow(
        Lien Lien,
        string LawFirmId,
        string CaseManagerId,
        string FacilityFilterId);

    public static void MapLienEndpoints(this WebApplication app)
    {
        // Compatibility for the tenant portal's legacy BFF path. The gateway removes
        // its /liens prefix before forwarding, leaving this service path.
        app.MapDelete("/liens/delete-medicaldocument/{id:guid}", DeleteMedicalDocumentLegacy)
            .RequireAuthorization(Policies.AuthenticatedUser)
            .RequireProductAccess(LiensPermissions.ProductCode)
            .RequirePermission(LiensPermissions.LienUpdate);

        var group = app.MapGroup("/api/liens/liens")
            .RequireAuthorization(Policies.AuthenticatedUser)
            .RequireProductAccess(LiensPermissions.ProductCode);

        group.MapGet("/", ListLiens)
            .RequirePermission(LiensPermissions.LienRead);

        group.MapPost("/search", SearchLiens)
            .RequirePermission(LiensPermissions.LienRead);

        group.MapGet("/{id:guid}", GetLienById)
            .RequirePermission(LiensPermissions.LienRead);

        group.MapGet("/by-number/{lienNumber}", GetLienByNumber)
            .RequirePermission(LiensPermissions.LienRead);

        // Legacy transfer from CaseEndpoints: full listing behavior from GetLeinsMedicalFullListing.
        group.MapPost("/full-listing", GetLeinsMedicalFullListingLegacy)
            .RequirePermission(LiensPermissions.LienRead);

        // Legacy transfer from CaseEndpoints: case-specific full listing behavior.
        group.MapPost("/full-listing/{caseId:guid}", GetLeinsMedicalFullListingByCaseLegacy)
            .RequirePermission(LiensPermissions.LienRead);

        // Legacy compatibility route from previous service: POST /case/liens/details/{caseId}
        // under the liens base path becomes POST /api/liens/liens/details/{lienId}.
        group.MapPost("/details/{lienId:guid}", GetLeinsMedicalListingLegacy)
            .RequirePermission(LiensPermissions.LienRead);

        // Legacy compatibility route from previous service: DELETE /case/liens/delete-medicalcode/{id}
        // under the liens base path becomes DELETE /api/liens/liens/delete-medicalcode/{id}.
        group.MapDelete("/delete-medicalcode/{id:guid}", DeleteMedicalCodeLegacy)
            .RequirePermission(LiensPermissions.LienUpdate);

        // Legacy compatibility route from previous service: DELETE /case/liens/delete-medicaldocument/{id}
        // under the liens base path becomes DELETE /api/liens/liens/delete-medicaldocument/{id}.
        group.MapDelete("/delete-medicaldocument/{id:guid}", DeleteMedicalDocumentLegacy)
            .RequirePermission(LiensPermissions.LienUpdate);

        // Legacy compatibility route from previous service: DELETE /case/delete-casedocument/{id}
        // under the liens base path becomes DELETE /api/liens/liens/delete-casedocument/{id}.
        group.MapDelete("/delete-casedocument/{id:guid}", DeleteCaseDocumentLegacy)
            .RequirePermission(LiensPermissions.LienUpdate);

        // Legacy transfer from CaseEndpoints: POST /case/liens/reassign/medical-provider.
        group.MapPost("/reassign/medical-provider", ReassignMedicalProviderLegacy)
            .RequirePermission(LiensPermissions.LienUpdate);

        // Legacy transfer from CaseEndpoints: POST /case/liens/reassign/facility.
        group.MapPost("/reassign/facility", ReassignFacilityLegacy)
            .RequirePermission(LiensPermissions.LienUpdate);

        // Legacy migration from CaseController: POST /case/liens/reassign/contact-person.
        group.MapPost("/reassign/contact-person", ReassignFacilityContactPersonLegacy)
            .RequirePermission(LiensPermissions.LienUpdate);

        // Legacy migration from CaseController: POST /case/liens/reassign/funding-company.
        group.MapPost("/reassign/funding-company", ReassignFundingCompanyLegacy)
            .RequirePermission(LiensPermissions.LienUpdate);

        // Legacy compatibility route from previous service: DELETE /case/liens/delete/{liensId}
        group.MapDelete("/delete/{liensId:guid}", DeleteLiensLegacy)
            .RequirePermission(LiensPermissions.LienUpdate);

        group.MapPost("/", CreateLien)
            .RequirePermission(LiensPermissions.LienCreate);

        group.MapPut("/{id:guid}", UpdateLien)
            .RequirePermission(LiensPermissions.LienUpdate);
    }

    private static Guid RequireTenantId(ICurrentRequestContext ctx)
    {
        return ctx.TenantId
            ?? throw new UnauthorizedAccessException("Tenant context is required.");
    }

    private static Guid RequireUserId(ICurrentRequestContext ctx)
    {
        return ctx.UserId
            ?? throw new UnauthorizedAccessException("User context is required.");
    }

    private static Guid RequireOrgId(ICurrentRequestContext ctx)
    {
        return ctx.OrgId
            ?? throw new UnauthorizedAccessException("Organization context is required.");
    }

    private static async Task<IResult> ListLiens(
        ILienService lienService,
        IServicingItemService servicingItemService,
        LiensDbContext db,
        ICurrentRequestContext ctx,
        string? search = null,
        string? status = null,
        string? lienType = null,
        Guid? caseId = null,
        Guid? facilityId = null,
        string? lawFirmIds = null,
        string? medicalFacilityIds = null,
        string? caseManagerIds = null,
        string? lienStatusIds = null,
        string? purchaseDateFrom = null,
        string? purchaseDateTo = null,
        string? closedDateFrom = null,
        string? closedDateTo = null,
        string? sortBy = null,
        string? sortDirection = null,
        int page = 1,
        int pageSize = 20,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);

        if (HasAdvancedLienFilters(
            lawFirmIds,
            medicalFacilityIds,
            caseManagerIds,
            lienStatusIds,
            purchaseDateFrom,
            purchaseDateTo,
            closedDateFrom,
            closedDateTo) ||
            !string.IsNullOrWhiteSpace(sortBy))
        {
            return await SearchLiensCore(
                db,
                lienService,
                servicingItemService,
                tenantId,
                search,
                status,
                lienType,
                caseId,
                facilityId,
                page,
                pageSize,
                SplitCsvValues(lawFirmIds),
                SplitCsvValues(medicalFacilityIds),
                SplitCsvValues(caseManagerIds),
                SplitCsvValues(lienStatusIds),
                purchaseDateFrom,
                purchaseDateTo,
                closedDateFrom,
                closedDateTo,
                sortBy,
                sortDirection,
                ct);
        }

        var selectedStatusCodes = LienStatus.ExpandFilterValues(SplitCsvValues(status));
        var result = await lienService.SearchAsync(
            tenantId,
            search,
            status,
            lienType,
            caseId,
            facilityId,
            page,
            pageSize,
            ct,
            excludeRejectedAndCancelled: !selectedStatusCodes.Contains(LienStatus.Cancelled));
        var enriched = await EnrichLienResponsesAsync(result.Items, tenantId, servicingItemService, ct);
        var mappedItems = MapBuyingLienStatuses(enriched);
        return Results.Ok(new PaginatedResult<LienResponse>
        {
            Items = mappedItems,
            Page = result.Page,
            PageSize = result.PageSize,
            TotalCount = result.TotalCount,
        });
    }

    private static async Task<IResult> SearchLiens(
        SearchLiensRequest request,
        ILienService lienService,
        IServicingItemService servicingItemService,
        LiensDbContext db,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);

        return await SearchLiensCore(
            db,
            lienService,
            servicingItemService,
            tenantId,
            request.Search,
            request.Status,
            request.LienType,
            request.CaseId,
            request.FacilityId,
            request.Page,
            request.PageSize,
            request.LawFirmIds ?? [],
            request.MedicalFacilityIds ?? [],
            request.CaseManagerIds ?? [],
            request.LienStatusIds ?? [],
            request.PurchaseDateFrom,
            request.PurchaseDateTo,
            request.ClosedDateFrom,
            request.ClosedDateTo,
            request.SortBy,
            request.SortDirection,
            ct);
    }

    private static async Task<IResult> GetLienById(
        Guid id,
        ILienService lienService,
        IServicingItemService servicingItemService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var result = await lienService.GetByIdAsync(tenantId, id, ct);
        return result is null
            ? Results.NotFound(new { error = new { code = "not_found", message = $"Lien '{id}' not found." } })
            : Results.Ok((await EnrichLienResponsesAsync([result], tenantId, servicingItemService, ct)).Single());
    }

    private static async Task<IResult> GetLienByNumber(
        string lienNumber,
        ILienService lienService,
        IServicingItemService servicingItemService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var result = await lienService.GetByLienNumberAsync(tenantId, lienNumber, ct);
        return result is null
            ? Results.NotFound(new { error = new { code = "not_found", message = $"Lien with number '{lienNumber}' not found." } })
            : Results.Ok((await EnrichLienResponsesAsync([result], tenantId, servicingItemService, ct)).Single());
    }

    private static async Task<List<LienResponse>> EnrichLienResponsesAsync(
        List<LienResponse> liens,
        Guid tenantId,
        IServicingItemService servicingItemService,
        CancellationToken ct)
    {
        var enriched = new List<LienResponse>(liens.Count);

        foreach (var lien in liens)
        {
            var codeResults = await servicingItemService.SearchAsync(
                tenantId,
                search: null,
                status: null,
                priority: null,
                assignedTo: null,
                caseId: null,
                lienId: lien.Id,
                page: 1,
                pageSize: 500,
                ct);

            var totalPurchase = 0m;
            var totalBilling = 0m;

            foreach (var item in codeResults.Items.Where(i =>
                         string.Equals(i.TaskType, "LegacyMedicalCode", StringComparison.Ordinal)))
            {
                var codeFields = ParseLegacyNoteFields(item.Notes);
                if (decimal.TryParse(codeFields.GetValueOrDefault("purchaseAmount", string.Empty), NumberStyles.Any, CultureInfo.InvariantCulture, out var purchase))
                    totalPurchase += purchase;
                if (decimal.TryParse(codeFields.GetValueOrDefault("billingAmount", string.Empty), NumberStyles.Any, CultureInfo.InvariantCulture, out var billing))
                    totalBilling += billing;
            }

            var facilityInfoFields = codeResults.Items
                .Where(i => string.Equals(i.TaskType, "LegacyMedicalFacilityInfo", StringComparison.Ordinal))
                .OrderByDescending(i => i.CreatedAtUtc)
                .Select(i => ParseLegacyNoteFields(i.Notes))
                .FirstOrDefault() ?? new Dictionary<string, string>(StringComparer.Ordinal);

            enriched.Add(new LienResponse
            {
                Id = lien.Id,
                LienNumber = lien.LienNumber,
                ExternalReference = lien.ExternalReference,
                LienType = lien.LienType,
                Status = lien.Status,
                StatusLabel = lien.StatusLabel,
                CaseId = lien.CaseId,
                SellingCaseId = lien.SellingCaseId,
                FacilityId = lien.FacilityId,
                OriginalAmount = lien.OriginalAmount,
                CurrentBalance = lien.CurrentBalance,
                OfferPrice = lien.OfferPrice,
                PurchasePrice = lien.PurchasePrice,
                PayoffAmount = lien.PayoffAmount,
                Jurisdiction = lien.Jurisdiction,
                IsConfidential = lien.IsConfidential,
                SubjectFirstName = lien.SubjectFirstName,
                SubjectLastName = lien.SubjectLastName,
                SubjectDisplayName = lien.SubjectDisplayName,
                Plaintiff = lien.Plaintiff,
                LawFirm = lien.LawFirm,
                MedicalFacility = FirstNonEmpty(
                    lien.MedicalFacility,
                    facilityInfoFields.GetValueOrDefault("facilityName", string.Empty)),
                CaseManager = lien.CaseManager,
                OrgId = lien.OrgId,
                SellingOrgId = lien.SellingOrgId,
                BuyingOrgId = lien.BuyingOrgId,
                HoldingOrgId = lien.HoldingOrgId,
                SellerStatus = lien.SellerStatus,
                IncidentDate = lien.IncidentDate,
                PurchaseDate = lien.PurchaseDate,
                InitialServiceDate = lien.InitialServiceDate,
                EndServiceDate = lien.EndServiceDate,
                TotalPurchase = totalPurchase,
                TotalBilling = totalBilling,
                IsBulk = lien.IsBulk,
                IsServicing = lien.IsServicing,
                ImportedCreatedByName = lien.ImportedCreatedByName,
                Description = lien.Description,
                Notes = lien.Notes,
                OpenedAtUtc = lien.OpenedAtUtc,
                ClosedAtUtc = lien.ClosedAtUtc,
                CreatedAtUtc = lien.CreatedAtUtc,
                UpdatedAtUtc = lien.UpdatedAtUtc,
            });
        }

        return enriched;
    }

    private static async Task<IResult> SearchLiensCore(
        LiensDbContext db,
        ILienService lienService,
        IServicingItemService servicingItemService,
        Guid tenantId,
        string? search,
        string? status,
        string? lienType,
        Guid? caseId,
        Guid? facilityId,
        int page,
        int pageSize,
        IReadOnlyCollection<string> lawFirmIds,
        IReadOnlyCollection<string> medicalFacilityIds,
        IReadOnlyCollection<string> caseManagerIds,
        IReadOnlyCollection<string> lienStatusIds,
        string? purchaseDateFrom,
        string? purchaseDateTo,
        string? closedDateFrom,
        string? closedDateTo,
        string? sortBy,
        string? sortDirection,
        CancellationToken ct)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 100) pageSize = 100;

        var resolvedStatusCodes = await ResolveLienStatusCodesAsync(db, tenantId, lienStatusIds, ct);
        var purchaseFrom = ParseDateOnlyFilter(purchaseDateFrom);
        var purchaseTo = ParseDateOnlyFilter(purchaseDateTo);
        var closedFrom = ParseDateTimeFilter(closedDateFrom, endOfDay: false);
        var closedTo = ParseDateTimeFilter(closedDateTo, endOfDay: true);
        var directStatusCodes = LienStatus.ExpandFilterValues(
            string.IsNullOrWhiteSpace(status)
                ? []
                : SplitCsvValues(status));

        var shouldExcludeRejectedAndCancelled =
            !directStatusCodes.Contains(LienStatus.Cancelled) &&
            !resolvedStatusCodes.Contains(LienStatus.Cancelled);

        var query = db.Liens
            .AsNoTracking()
            .Where(l => l.TenantId == tenantId);

        if (shouldExcludeRejectedAndCancelled)
        {
            query = query.Where(l =>
                l.Status != LienStatus.Cancelled &&
                l.Status != "Rejected");
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(l =>
                l.LienNumber.Contains(term) ||
                (l.SubjectFirstName != null && l.SubjectFirstName.Contains(term)) ||
                (l.SubjectLastName != null && l.SubjectLastName.Contains(term)) ||
                (l.Description != null && l.Description.Contains(term)));
        }

        if (directStatusCodes.Count == 1)
            query = query.Where(l => l.Status == directStatusCodes.Single());
        else if (directStatusCodes.Count > 1)
            query = query.Where(l => directStatusCodes.Contains(l.Status));

        if (resolvedStatusCodes.Count > 0)
            query = query.Where(l => resolvedStatusCodes.Contains(l.Status));

        if (!string.IsNullOrWhiteSpace(lienType))
            query = query.Where(l => l.LienType == lienType);

        if (caseId.HasValue)
            query = query.Where(l => l.CaseId == caseId.Value);

        if (facilityId.HasValue)
            query = query.Where(l => l.FacilityId == facilityId.Value);

        if (purchaseFrom.HasValue)
            query = query.Where(l => l.PurchaseDate.HasValue && l.PurchaseDate.Value >= purchaseFrom.Value);

        if (purchaseTo.HasValue)
            query = query.Where(l => l.PurchaseDate.HasValue && l.PurchaseDate.Value <= purchaseTo.Value);

        if (closedFrom.HasValue)
            query = query.Where(l => l.ClosedAtUtc.HasValue && l.ClosedAtUtc.Value >= closedFrom.Value);

        if (closedTo.HasValue)
            query = query.Where(l => l.ClosedAtUtc.HasValue && l.ClosedAtUtc.Value <= closedTo.Value);

        var normalizedLawFirmIds = NormalizeFilterValues(lawFirmIds);
        var normalizedMedicalFacilityIds = NormalizeFilterValues(medicalFacilityIds);
        var normalizedCaseManagerIds = NormalizeFilterValues(caseManagerIds);
        var requiresRelationshipFiltering =
            normalizedLawFirmIds.Count > 0 ||
            normalizedMedicalFacilityIds.Count > 0 ||
            normalizedCaseManagerIds.Count > 0;

        // Status/date filters are already fully represented by the database query.
        // Page those results before the per-lien detail and servicing enrichment;
        // otherwise a broad status such as Active performs several queries for every
        // matching lien and can leave the UI waiting until the request times out.
        if (!requiresRelationshipFiltering && string.IsNullOrWhiteSpace(sortBy))
        {
            var totalCount = await query.CountAsync(ct);
            var pageLiens = await query
                .OrderByDescending(l => l.CreatedAtUtc)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(ct);

            var pageResponses = await GetDetailedLienResponsesAsync(
                pageLiens,
                lienService,
                tenantId,
                servicingItemService,
                ct);

            return Results.Ok(new PaginatedResult<LienResponse>
            {
                Items = MapBuyingLienStatuses(pageResponses),
                Page = page,
                PageSize = pageSize,
                TotalCount = totalCount,
            });
        }

        var liens = await query
            .OrderByDescending(l => l.CreatedAtUtc)
            .ToListAsync(ct);

        var advancedRows = await BuildAdvancedLienFilterRowsAsync(db, tenantId, liens, ct);

        var filteredLiens = advancedRows
            .Where(row => MatchesAdvancedFilter(normalizedLawFirmIds, row.LawFirmId))
            .Where(row => MatchesAdvancedFilter(normalizedCaseManagerIds, row.CaseManagerId))
            .Where(row => MatchesAdvancedFilter(normalizedMedicalFacilityIds, row.FacilityFilterId))
            .Select(row => row.Lien)
            .ToList();

        var enriched = await GetDetailedLienResponsesAsync(
            filteredLiens,
            lienService,
            tenantId,
            servicingItemService,
            ct);
        var amountReceivedByLienId = await GetAmountReceivedByLienIdAsync(
            db,
            tenantId,
            filteredLiens,
            sortBy,
            ct);
        var sorted = ApplyLienSorting(enriched, sortBy, sortDirection, amountReceivedByLienId);
        var pagedLiens = sorted
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();
        var mappedItems = MapBuyingLienStatuses(pagedLiens);

        return Results.Ok(new PaginatedResult<LienResponse>
        {
            Items = mappedItems,
            Page = page,
            PageSize = pageSize,
            TotalCount = filteredLiens.Count,
        });
    }

    private static List<LienResponse> MapBuyingLienStatuses(List<LienResponse> liens)
        => liens.Select(MapBuyingLienStatus).ToList();

    private static LienResponse MapBuyingLienStatus(LienResponse lien)
    {
        var buyingStatus = string.IsNullOrWhiteSpace(lien.StatusLabel) ? lien.Status : lien.StatusLabel;

        return new LienResponse
        {
            Id = lien.Id,
            LienNumber = lien.LienNumber,
            ExternalReference = lien.ExternalReference,
            LienType = lien.LienType,
            Status = buyingStatus,
            StatusLabel = buyingStatus,
            CaseId = lien.CaseId,
            SellingCaseId = lien.SellingCaseId,
            FacilityId = lien.FacilityId,
            OriginalAmount = lien.OriginalAmount,
            CurrentBalance = lien.CurrentBalance,
            OfferPrice = lien.OfferPrice,
            PurchasePrice = lien.PurchasePrice,
            PayoffAmount = lien.PayoffAmount,
            Jurisdiction = lien.Jurisdiction,
            IsConfidential = lien.IsConfidential,
            SubjectFirstName = lien.SubjectFirstName,
            SubjectLastName = lien.SubjectLastName,
            SubjectDisplayName = lien.SubjectDisplayName,
            Plaintiff = lien.Plaintiff,
            LawFirm = lien.LawFirm,
            MedicalFacility = lien.MedicalFacility,
            CaseManager = lien.CaseManager,
            OrgId = lien.OrgId,
            SellingOrgId = lien.SellingOrgId,
            BuyingOrgId = lien.BuyingOrgId,
            HoldingOrgId = lien.HoldingOrgId,
            SellerStatus = lien.SellerStatus,
            IncidentDate = lien.IncidentDate,
            PurchaseDate = lien.PurchaseDate,
            InitialServiceDate = lien.InitialServiceDate,
            EndServiceDate = lien.EndServiceDate,
            TotalPurchase = lien.TotalPurchase,
            TotalBilling = lien.TotalBilling,
            IsBulk = lien.IsBulk,
            IsServicing = lien.IsServicing,
            ImportedCreatedByName = lien.ImportedCreatedByName,
            Description = lien.Description,
            Notes = lien.Notes,
            OpenedAtUtc = lien.OpenedAtUtc,
            ClosedAtUtc = lien.ClosedAtUtc,
            CreatedAtUtc = lien.CreatedAtUtc,
            UpdatedAtUtc = lien.UpdatedAtUtc,
        };
    }

    private static async Task<List<LienResponse>> GetDetailedLienResponsesAsync(
        IReadOnlyCollection<Lien> liens,
        ILienService lienService,
        Guid tenantId,
        IServicingItemService servicingItemService,
        CancellationToken ct)
    {
        var responses = new List<LienResponse>(liens.Count);
        foreach (var lien in liens)
        {
            var response = await lienService.GetByIdAsync(tenantId, lien.Id, ct);
            if (response is not null)
                responses.Add(response);
        }

        return await EnrichLienResponsesAsync(responses, tenantId, servicingItemService, ct);
    }

    private static List<LienResponse> ApplyLienSorting(
        List<LienResponse> liens,
        string? sortBy,
        string? sortDirection,
        IReadOnlyDictionary<Guid, decimal> amountReceivedByLienId)
    {
        if (liens.Count <= 1 || string.IsNullOrWhiteSpace(sortBy))
            return liens;

        var normalizedSortBy = sortBy.Trim()
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        var descending = string.Equals(sortDirection?.Trim(), "desc", StringComparison.OrdinalIgnoreCase);

        IOrderedEnumerable<LienResponse> ordered = normalizedSortBy switch
        {
            "lienid" or "liennumber" => descending
                ? liens.OrderByDescending(l => l.LienNumber, StringComparer.OrdinalIgnoreCase)
                : liens.OrderBy(l => l.LienNumber, StringComparer.OrdinalIgnoreCase),
            "plaintiff" or "plaintiffname" or "clientname" => descending
                ? liens.OrderByDescending(l => l.Plaintiff ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                : liens.OrderBy(l => l.Plaintiff ?? string.Empty, StringComparer.OrdinalIgnoreCase),
            "lawfirm" => descending
                ? liens.OrderByDescending(l => l.LawFirm ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                : liens.OrderBy(l => l.LawFirm ?? string.Empty, StringComparer.OrdinalIgnoreCase),
            "medicalfacility" or "facility" or "facilityname" => descending
                ? liens.OrderByDescending(l => l.MedicalFacility ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                : liens.OrderBy(l => l.MedicalFacility ?? string.Empty, StringComparer.OrdinalIgnoreCase),
            "purchasedate" => descending
                ? liens.OrderByDescending(l => ParseLienPurchaseDate(l.PurchaseDate) ?? DateOnly.MinValue)
                : liens.OrderBy(l => ParseLienPurchaseDate(l.PurchaseDate) ?? DateOnly.MinValue),
            "isservicing" or "servicing" => descending
                ? liens.OrderByDescending(l => IsServicingLien(l.IsServicing))
                : liens.OrderBy(l => IsServicingLien(l.IsServicing)),
            "amountreceived" or "payment" => descending
                ? liens.OrderByDescending(l => GetAmountReceived(l.Id, amountReceivedByLienId))
                : liens.OrderBy(l => GetAmountReceived(l.Id, amountReceivedByLienId)),
            "purchaseamount" or "totalpurchase" => descending
                ? liens.OrderByDescending(l => l.TotalPurchase ?? decimal.MinValue)
                : liens.OrderBy(l => l.TotalPurchase ?? decimal.MinValue),
            "billingamount" or "totalbilling" => descending
                ? liens.OrderByDescending(l => l.TotalBilling ?? decimal.MinValue)
                : liens.OrderBy(l => l.TotalBilling ?? decimal.MinValue),
            "lienstatus" or "status" => descending
                ? liens.OrderByDescending(l => l.Status, StringComparer.OrdinalIgnoreCase)
                : liens.OrderBy(l => l.Status, StringComparer.OrdinalIgnoreCase),
            "initialservicedate" => descending
                ? liens.OrderByDescending(l => l.InitialServiceDate ?? DateOnly.MinValue)
                : liens.OrderBy(l => l.InitialServiceDate ?? DateOnly.MinValue),
            "casemanager" => descending
                ? liens.OrderByDescending(l => l.CaseManager ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                : liens.OrderBy(l => l.CaseManager ?? string.Empty, StringComparer.OrdinalIgnoreCase),
            _ => descending
                ? liens.OrderByDescending(l => l.CreatedAtUtc)
                : liens.OrderBy(l => l.CreatedAtUtc),
        };

        return ordered
            .ThenBy(l => l.LienNumber, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<IReadOnlyDictionary<Guid, decimal>> GetAmountReceivedByLienIdAsync(
        LiensDbContext db,
        Guid tenantId,
        IReadOnlyCollection<Lien> liens,
        string? sortBy,
        CancellationToken ct)
    {
        var normalizedSortBy = sortBy?.Trim()
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        if (normalizedSortBy is not ("amountreceived" or "payment") || liens.Count == 0)
            return new Dictionary<Guid, decimal>();

        var lienIds = liens.Select(lien => lien.Id).ToList();
        return await db.SettlementPaymentDetails
            .AsNoTracking()
            .Where(payment =>
                payment.TenantId == tenantId &&
                lienIds.Contains(payment.LienId) &&
                !payment.IsDeleted &&
                payment.PostingStatus != SettlementPaymentDetail.VoidedStatus)
            .GroupBy(payment => payment.LienId)
            .ToDictionaryAsync(
                group => group.Key,
                group => group.Sum(payment => payment.Amount),
                ct);
    }

    private static decimal GetAmountReceived(
        Guid lienId,
        IReadOnlyDictionary<Guid, decimal> amountReceivedByLienId)
        => amountReceivedByLienId.GetValueOrDefault(lienId);

    private static DateOnly? ParseLienPurchaseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return DateOnly.TryParseExact(
            value,
            "MM/dd/yyyy",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? parsed
            : null;
    }

    private static bool IsServicingLien(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
           (value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("y", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("1", StringComparison.OrdinalIgnoreCase));

    internal static async Task<List<AdvancedLienFilterRow>> BuildAdvancedLienFilterRowsAsync(
        LiensDbContext db,
        Guid tenantId,
        IReadOnlyCollection<Lien> liens,
        CancellationToken ct)
    {
        if (liens.Count == 0)
            return [];

        var caseIds = liens
            .Where(l => l.CaseId.HasValue)
            .Select(l => l.CaseId!.Value)
            .Distinct()
            .ToList();
        var lienIds = liens.Select(l => l.Id).ToList();

        var casesById = await db.Cases
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId && caseIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, ct);

        var lawFirmContacts = await db.Contacts
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.ContactType == ContactType.LawFirm)
            .OrderBy(c => c.DisplayName)
            .ToListAsync(ct);

        var contactsById = await db.Contacts
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId)
            .ToDictionaryAsync(c => c.Id, ct);

        var facilityContacts = contactsById.Values
            .Where(contact => IsStandaloneFacilityContact(contact))
            .ToList();
        var facilityContactsById = facilityContacts.ToDictionary(c => c.Id, EqualityComparer<Guid>.Default);
        var facilityContactsByLinkedFacilityId = facilityContacts
            .Where(c => c.FacilityId.HasValue)
            .GroupBy(c => c.FacilityId!.Value)
            .ToDictionary(g => g.Key, g => g.First(), EqualityComparer<Guid>.Default);
        var facilityContactsByName = facilityContacts
            .SelectMany(c => GetFacilityContactLookupNames(c).Select(name => new { Name = name, Contact = c }))
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Contact, StringComparer.OrdinalIgnoreCase);

        var servicingItems = await db.ServicingItems
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId &&
                        s.LienId.HasValue &&
                        lienIds.Contains(s.LienId.Value) &&
                        s.TaskType == "LegacyMedicalFacilityInfo")
            .ToListAsync(ct);

        var lawFirmByOrgId = lawFirmContacts
            .GroupBy(c => c.OrgId)
            .ToDictionary(g => g.Key, g => g.First(), EqualityComparer<Guid>.Default);

        var facilityInfoByLienId = servicingItems
            .Where(s => s.LienId.HasValue)
            .GroupBy(s => s.LienId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.CreatedAtUtc).First());

        return liens.Select(l =>
        {
            casesById.TryGetValue(l.CaseId ?? Guid.Empty, out var caseInfo);
            var caseFields = caseInfo is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : ParseLegacyNoteFields(caseInfo.Notes);

            var lawFirmContact = caseInfo is not null
                ? lawFirmByOrgId.GetValueOrDefault(caseInfo.OrgId)
                : null;

            var lawFirmId = caseFields.GetValueOrDefault("lawFirmId", string.Empty);
            if (string.IsNullOrWhiteSpace(lawFirmId) && caseInfo is not null)
                lawFirmId = lawFirmContact?.Id.ToString() ?? caseInfo.OrgId.ToString();

            var caseManagerId = caseFields.GetValueOrDefault("caseManagerId", string.Empty);
            var facilityFilterId = ResolveFacilityFilterId(
                l,
                facilityInfoByLienId.GetValueOrDefault(l.Id),
                facilityContactsById,
                facilityContactsByLinkedFacilityId,
                facilityContactsByName);

            return new AdvancedLienFilterRow(l, lawFirmId, caseManagerId, facilityFilterId);
        }).ToList();
    }

    private static string ResolveFacilityFilterId(
        Lien lien,
        ServicingItem? facilityInfo,
        IReadOnlyDictionary<Guid, Contact> facilityContactsById,
        IReadOnlyDictionary<Guid, Contact> facilityContactsByLinkedFacilityId,
        IReadOnlyDictionary<string, Contact> facilityContactsByName)
    {
        var facilityId = lien.FacilityId?.ToString() ?? string.Empty;
        var facilityName = string.Empty;

        if (facilityInfo is not null)
        {
            var fields = ParseLegacyNoteFields(facilityInfo.Notes);
            facilityId = fields.GetValueOrDefault("facilityId", facilityId);
            facilityName = fields.GetValueOrDefault("facilityName", string.Empty);
        }

        if (Guid.TryParse(facilityId, out var parsedFacilityId))
        {
            if (facilityContactsById.TryGetValue(parsedFacilityId, out var facilityContact) ||
                facilityContactsByLinkedFacilityId.TryGetValue(parsedFacilityId, out facilityContact))
            {
                return facilityContact.Id.ToString();
            }

            return parsedFacilityId.ToString();
        }

        if (!string.IsNullOrWhiteSpace(facilityName) &&
            facilityContactsByName.TryGetValue(facilityName.Trim(), out var facilityContactByName))
        {
            return facilityContactByName.Id.ToString();
        }

        return string.Empty;
    }

    internal static async Task<HashSet<string>> ResolveLienStatusCodesAsync(
        LiensDbContext db,
        Guid tenantId,
        IReadOnlyCollection<string> filterValues,
        CancellationToken ct)
    {
        var normalized = NormalizeFilterValues(filterValues);
        if (normalized.Count == 0)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var guidIds = normalized
            .Where(value => Guid.TryParse(value, out _))
            .Select(Guid.Parse)
            .ToList();

        var lookupStatusValues = guidIds.Count == 0
            ? []
            : await db.LookupValues
                .AsNoTracking()
                .Where(l => (l.TenantId == tenantId || l.TenantId == null) &&
                            l.Category == LookupCategory.LienStatus &&
                            guidIds.Contains(l.Id))
                .Select(l => new { l.Code, l.Name, l.Description })
                .ToListAsync(ct);

        // Older lien-list clients send the lookup row ID for the three
        // legacy business groups (Open, Closed, Rejected).  Those rows are
        // backed by a canonical lifecycle status such as Draft, so treating
        // the ID as only that canonical status incorrectly omits Active and
        // the other Open states.  Preserve direct canonical status filters,
        // while expanding lookup-ID filters back to their business group.
        var lookupFilterValues = lookupStatusValues.SelectMany(value =>
        {
            var values = new List<string> { value.Code, value.Name };
            if (!string.IsNullOrWhiteSpace(value.Description))
                values.Add(value.Description);

            if (LienStatus.Open.Contains(value.Code))
                values.Add("Open");
            else if (string.Equals(value.Code, LienStatus.Settled, StringComparison.OrdinalIgnoreCase))
                values.Add("Closed");
            else if (string.Equals(value.Code, LienStatus.Declined, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(value.Code, LienStatus.Withdrawn, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(value.Code, LienStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
                values.Add("Rejected");

            return values;
        });

        return LienStatus.ExpandFilterValues(normalized.Concat(lookupFilterValues))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool HasAdvancedLienFilters(params string?[] values)
        => values.Any(value => !string.IsNullOrWhiteSpace(value));

    private static IReadOnlyCollection<string> SplitCsvValues(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static HashSet<string> NormalizeFilterValues(IReadOnlyCollection<string> values)
        => values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool MatchesAdvancedFilter(HashSet<string> selectedValues, string candidate)
        => selectedValues.Count == 0 ||
           (!string.IsNullOrWhiteSpace(candidate) && selectedValues.Contains(candidate.Trim()));

    private static DateOnly? ParseDateOnlyFilter(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return DateOnly.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value
            : null;
    }

    private static DateTime? ParseDateTimeFilter(string? raw, bool endOfDay)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            return null;

        return endOfDay ? value.Date.AddDays(1).AddTicks(-1) : value.Date;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static async Task<IResult> CreateLien(
        CreateLienRequest request,
        ILienService lienService,
        ICurrentRequestContext ctx,
        HttpRequest httpRequest,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var orgId = RequireOrgId(ctx);
        var userId = RequireUserId(ctx);
        var idempotencyKey = httpRequest.Headers["Idempotency-Key"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(request.ExternalReference) &&
            !string.IsNullOrWhiteSpace(idempotencyKey))
            request = request with { ExternalReference = idempotencyKey };
        var result = await lienService.CreateAsync(tenantId, orgId, userId, request, ct);
        return Results.Created($"/api/liens/liens/{result.Id}", result);
    }

    private static async Task<IResult> GetLeinsMedicalFullListingLegacy(
        ILienService lienService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);

        try
        {
            var data = await GetLegacyLiensListingAsync(lienService, tenantId, null, ct);
            if (data.Count == 0)
            {
                return Results.NotFound(new
                {
                    isSuccess = false,
                    message = "No Liens Found.",
                });
            }

            return Results.Ok(new
            {
                isSuccess = true,
                message = "Liens List.",
                data,
            });
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new
            {
                isSuccess = false,
                message = $"Error: retrieving data. {ex.Message}",
            });
        }
    }

    private static async Task<IResult> GetLeinsMedicalFullListingByCaseLegacy(
        Guid caseId,
        ILienService lienService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);

        try
        {
            var data = await GetLegacyLiensListingAsync(lienService, tenantId, caseId, ct);
            if (data.Count == 0)
            {
                return Results.NotFound(new
                {
                    isSuccess = false,
                    message = "No Liens Found.",
                });
            }

            return Results.Ok(new
            {
                isSuccess = true,
                message = "Liens List.",
                data,
            });
        }
        catch (Exception)
        {
            return Results.BadRequest(new
            {
                isSuccess = false,
                message = "Error: retrieving data.",
            });
        }
    }

    private static async Task<IResult> GetLeinsMedicalListingLegacy(
        Guid lienId,
        ILienService lienService,
        IServicingItemService servicingItemService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);

        try
        {
            var data = new LegacyLiensMedicalListing();
            var lien = await lienService.GetByIdAsync(tenantId, lienId, ct);

            if (lien is not null)
            {
                data.medicalList.Add(new LegacyLiensMedicalListItem
                {
                    id = lien.Id.ToString(),
                    caseId = lien.CaseId?.ToString() ?? string.Empty,
                    status = lien.Status,
                    purchaseDate = lien.PurchaseDate ?? string.Empty,
                    initialServiceDate = FormatLegacyDate(lien.InitialServiceDate),
                    endServiceDate = FormatLegacyDate(lien.EndServiceDate),
                    note = lien.Description ?? string.Empty,
                    created = FormatLegacyTimestamp(lien.CreatedAtUtc),
                    createdBy = lien.ImportedCreatedByName ?? string.Empty,
                    updated = FormatLegacyTimestamp(lien.UpdatedAtUtc),
                    updatedBy = string.Empty,
                    fundingCompanyId = lien.ExternalReference ?? string.Empty,
                    fundingCompany = string.Empty,
                    isBulk = lien.IsBulk ?? string.Empty,
                    isServicing = lien.IsServicing ?? string.Empty,
                });

                if (lien.FacilityId.HasValue)
                {
                    data.facilityList.Add(new LegacyLiensMedicalFacilityListItem
                    {
                        id = string.Empty,
                        liensId = lien.Id.ToString(),
                        facilityId = lien.FacilityId.Value.ToString(),
                        facilityContactId = string.Empty,
                        email = string.Empty,
                        phone = string.Empty,
                        medicalProviderId = string.Empty,
                        created = FormatLegacyTimestamp(lien.CreatedAtUtc),
                        createdBy = string.Empty,
                        updated = FormatLegacyTimestamp(lien.UpdatedAtUtc),
                        updatedBy = string.Empty,
                    });
                }
            }

            var codeResults = await servicingItemService.SearchAsync(
                tenantId,
                search: "LegacyMedicalCode",
                status: null,
                priority: null,
                assignedTo: null,
                caseId: null,
                lienId: lienId,
                page: 1,
                pageSize: 500,
                ct);

            foreach (var item in codeResults.Items.Where(i =>
                string.Equals(i.TaskType, "LegacyMedicalCode", StringComparison.Ordinal) &&
                i.LienId == lienId))
            {
                var fields = ParseLegacyNoteFields(item.Notes);
                data.codeList.Add(new LegacyLiensMedicalCodeListItem
                {
                    id = item.Id.ToString(),
                    liensId = item.LienId?.ToString() ?? string.Empty,
                    code = fields.GetValueOrDefault("code", string.Empty),
                    medicareCost = fields.GetValueOrDefault("medicareCost", string.Empty),
                    billingAmount = fields.GetValueOrDefault("billingAmount", string.Empty),
                    purchaseAmount = fields.GetValueOrDefault("purchaseAmount", string.Empty),
                    created = FormatLegacyTimestamp(item.CreatedAtUtc),
                    createdBy = string.Empty,
                    updated = FormatLegacyTimestamp(item.UpdatedAtUtc),
                    updatedBy = string.Empty,
                });
            }

            return Results.Ok(new
            {
                isSuccess = true,
                message = "Liens details.",
                data,
            });
        }
        catch (Exception)
        {
            return Results.BadRequest(new
            {
                isSuccess = false,
                message = "Error: retrieving data.",
            });
        }
    }

    private static async Task<IResult> DeleteMedicalCodeLegacy(
        Guid id,
        IServicingItemService servicingItemService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var userId = RequireUserId(ctx);

        var existing = await servicingItemService.GetByIdAsync(tenantId, id, ct);
        if (existing is null || !string.Equals(existing.TaskType, "LegacyMedicalCode", StringComparison.Ordinal))
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "No record found to delete.",
            });
        }

        try
        {
            await servicingItemService.DeleteAsync(tenantId, id, userId, ct);
            return Results.Ok(new
            {
                isSuccess = true,
                message = "Successfully delete medical code record.",
            });
        }
        catch (Exception ex)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = ex.Message,
            });
        }
    }

    private static async Task<IResult> DeleteMedicalDocumentLegacy(
        Guid id,
        IServicingItemService servicingItemService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var userId = RequireUserId(ctx);

        var existing = await servicingItemService.GetByIdAsync(tenantId, id, ct);
        if (existing is null ||
            (!string.Equals(existing.TaskType, "LegacyMedicalDocument", StringComparison.Ordinal) &&
             !string.Equals(existing.TaskType, "LegacyLienDocument", StringComparison.Ordinal)))
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "Error: Unable to delete Medical Document.",
            });
        }

        try
        {
            await servicingItemService.DeleteAsync(tenantId, id, userId, ct);
            return Results.Ok(new
            {
                isSuccess = true,
                message = "Successfully deleted Medical Document.",
            });
        }
        catch (Exception ex)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = ex.Message,
            });
        }
    }

    private static async Task<IResult> DeleteCaseDocumentLegacy(
        Guid id,
        IServicingItemService servicingItemService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var userId = RequireUserId(ctx);

        var existing = await servicingItemService.GetByIdAsync(tenantId, id, ct);
        if (existing is null || !string.Equals(existing.TaskType, "LegacyCaseDocument", StringComparison.Ordinal))
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "Error: Unable to delete Case Document.",
            });
        }

        try
        {
            await servicingItemService.DeleteAsync(tenantId, id, userId, ct);
            return Results.Ok(new
            {
                isSuccess = true,
                message = "Successfully deleted case document.",
            });
        }
        catch (Exception ex)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = ex.Message,
            });
        }
    }

    private static async Task<IResult> ReassignMedicalProviderLegacy(
        LegacyReassignMedicalProviderRequest request,
        ILienService lienService,
        IServicingItemService servicingItemService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var orgId = RequireOrgId(ctx);
        var userId = RequireUserId(ctx);

        if (!Guid.TryParse(request.liensId, out var lienId) || string.IsNullOrWhiteSpace(request.medicalProvider))
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "unable to re-assigned liens.",
            });
        }

        var lien = await lienService.GetByIdAsync(tenantId, lienId, ct);
        if (lien is null)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "unable to re-assigned liens.",
            });
        }

        try
        {
            var infoResult = await servicingItemService.SearchAsync(
                tenantId,
                search: "LegacyMedicalFacilityInfo",
                status: null,
                priority: null,
                assignedTo: null,
                caseId: null,
                lienId: lienId,
                page: 1,
                pageSize: 50,
                ct);

            var existing = infoResult.Items.FirstOrDefault(i =>
                string.Equals(i.TaskType, "LegacyMedicalFacilityInfo", StringComparison.Ordinal) &&
                i.LienId == lienId);

            if (existing is null)
            {
                var create = new CreateServicingItemRequest
                {
                    TaskNumber = $"LMFI-{DateTime.UtcNow:yyyyMMddHHmmssfff}",
                    TaskType = "LegacyMedicalFacilityInfo",
                    Description = "Legacy medical facility information",
                    AssignedTo = "system",
                    CaseId = lien.CaseId,
                    LienId = lienId,
                    Notes = $"medicalProviderId={request.medicalProvider.Trim()}",
                };

                await servicingItemService.CreateAsync(tenantId, orgId, userId, create, ct);
            }
            else
            {
                var fields = ParseLegacyNoteFields(existing.Notes);
                fields["medicalProviderId"] = request.medicalProvider.Trim();

                var update = new UpdateServicingItemRequest
                {
                    TaskType = existing.TaskType,
                    Description = existing.Description,
                    AssignedTo = string.IsNullOrWhiteSpace(existing.AssignedTo) ? "system" : existing.AssignedTo,
                    AssignedToUserId = existing.AssignedToUserId,
                    Priority = existing.Priority,
                    Status = existing.Status,
                    CaseId = existing.CaseId,
                    LienId = existing.LienId,
                    DueDate = existing.DueDate,
                    Notes = SerializeLegacyNoteFields(fields),
                    Resolution = existing.Resolution,
                };

                await servicingItemService.UpdateAsync(tenantId, existing.Id, userId, update, ct);
            }

            return Results.Ok(new
            {
                isSuccess = true,
                message = "Successfully re-assigned liens to new medical provider.",
            });
        }
        catch
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "unable to re-assigned liens.",
            });
        }
    }

    private static async Task<IResult> ReassignFacilityLegacy(
        LegacyReassignFacilityRequest request,
        ILienService lienService,
        IContactService contactService,
        IFacilityService facilityService,
        IServicingItemService servicingItemService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var orgId = RequireOrgId(ctx);
        var userId = RequireUserId(ctx);

        if (!Guid.TryParse(request.liensId, out var lienId) ||
            !Guid.TryParse(request.facility, out var facilityId))
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "unable to re-assigned liens.",
            });
        }

        var lien = await lienService.GetByIdAsync(tenantId, lienId, ct);
        if (lien is null)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "unable to re-assigned liens.",
            });
        }

        try
        {
            var mapped = new UpdateLienRequest
            {
                ExternalReference = lien.ExternalReference,
                LienType = lien.LienType,
                CaseId = lien.CaseId,
                FacilityId = facilityId,
                OriginalAmount = lien.OriginalAmount,
                Jurisdiction = lien.Jurisdiction,
                IsConfidential = lien.IsConfidential,
                SubjectFirstName = lien.SubjectFirstName,
                SubjectLastName = lien.SubjectLastName,
                IncidentDate = lien.IncidentDate,
                Description = lien.Description,
            };

            await lienService.UpdateAsync(tenantId, lienId, userId, mapped, ct);
            var facilityName = await ResolveLegacyFacilityNameAsync(
                tenantId,
                facilityId,
                contactService,
                facilityService,
                ct);

            var infoResult = await servicingItemService.SearchAsync(
                tenantId,
                search: "LegacyMedicalFacilityInfo",
                status: null,
                priority: null,
                assignedTo: null,
                caseId: null,
                lienId: lienId,
                page: 1,
                pageSize: 50,
                ct);

            var existing = infoResult.Items.FirstOrDefault(i =>
                string.Equals(i.TaskType, "LegacyMedicalFacilityInfo", StringComparison.Ordinal) &&
                i.LienId == lienId);

            if (existing is null)
            {
                var fields = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["facilityId"] = facilityId.ToString(),
                };

                if (!string.IsNullOrWhiteSpace(facilityName))
                    fields["facilityName"] = facilityName;

                var create = new CreateServicingItemRequest
                {
                    TaskNumber = $"LMFI-{DateTime.UtcNow:yyyyMMddHHmmssfff}",
                    TaskType = "LegacyMedicalFacilityInfo",
                    Description = "Legacy medical facility information",
                    AssignedTo = "system",
                    CaseId = lien.CaseId,
                    LienId = lienId,
                    Notes = SerializeLegacyNoteFields(fields),
                };

                await servicingItemService.CreateAsync(tenantId, orgId, userId, create, ct);
            }
            else
            {
                var fields = ParseLegacyNoteFields(existing.Notes);
                fields["facilityId"] = facilityId.ToString();
                if (!string.IsNullOrWhiteSpace(facilityName))
                    fields["facilityName"] = facilityName;

                var update = new UpdateServicingItemRequest
                {
                    TaskType = existing.TaskType,
                    Description = existing.Description,
                    AssignedTo = string.IsNullOrWhiteSpace(existing.AssignedTo) ? "system" : existing.AssignedTo,
                    AssignedToUserId = existing.AssignedToUserId,
                    Priority = existing.Priority,
                    Status = existing.Status,
                    CaseId = existing.CaseId,
                    LienId = existing.LienId,
                    DueDate = existing.DueDate,
                    Notes = SerializeLegacyNoteFields(fields),
                    Resolution = existing.Resolution,
                };

                await servicingItemService.UpdateAsync(tenantId, existing.Id, userId, update, ct);
            }

            return Results.Ok(new
            {
                isSuccess = true,
                message = "Successfully re-assigned liens to new facility.",
            });
        }
        catch
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "unable to re-assigned liens.",
            });
        }
    }

    private static async Task<IResult> ReassignFacilityContactPersonLegacy(
        LegacyReassignFacilityContactPersonRequest request,
        ILienService lienService,
        IServicingItemService servicingItemService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var orgId = RequireOrgId(ctx);
        var userId = RequireUserId(ctx);

        if (!Guid.TryParse(request.liensId, out var lienId) ||
            !Guid.TryParse(request.facilityContactPerson, out var facilityContactId))
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "unable to re-assigned liens.",
            });
        }

        var lien = await lienService.GetByIdAsync(tenantId, lienId, ct);
        if (lien is null)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "unable to re-assigned liens.",
            });
        }

        try
        {
            var infoResult = await servicingItemService.SearchAsync(
                tenantId,
                search: "LegacyMedicalFacilityInfo",
                status: null,
                priority: null,
                assignedTo: null,
                caseId: null,
                lienId: lienId,
                page: 1,
                pageSize: 50,
                ct);

            var existing = infoResult.Items.FirstOrDefault(i =>
                string.Equals(i.TaskType, "LegacyMedicalFacilityInfo", StringComparison.Ordinal) &&
                i.LienId == lienId);

            if (existing is null)
            {
                var create = new CreateServicingItemRequest
                {
                    TaskNumber = $"LMFI-{DateTime.UtcNow:yyyyMMddHHmmssfff}",
                    TaskType = "LegacyMedicalFacilityInfo",
                    Description = "Legacy medical facility information",
                    AssignedTo = "system",
                    CaseId = lien.CaseId,
                    LienId = lienId,
                    Notes = $"facilityContactId={facilityContactId}",
                };

                await servicingItemService.CreateAsync(tenantId, orgId, userId, create, ct);
            }
            else
            {
                var fields = ParseLegacyNoteFields(existing.Notes);
                fields["facilityContactId"] = facilityContactId.ToString();

                var update = new UpdateServicingItemRequest
                {
                    TaskType = existing.TaskType,
                    Description = existing.Description,
                    AssignedTo = string.IsNullOrWhiteSpace(existing.AssignedTo) ? "system" : existing.AssignedTo,
                    AssignedToUserId = existing.AssignedToUserId,
                    Priority = existing.Priority,
                    Status = existing.Status,
                    CaseId = existing.CaseId,
                    LienId = existing.LienId,
                    DueDate = existing.DueDate,
                    Notes = SerializeLegacyNoteFields(fields),
                    Resolution = existing.Resolution,
                };

                await servicingItemService.UpdateAsync(tenantId, existing.Id, userId, update, ct);
            }

            return Results.Ok(new
            {
                isSuccess = true,
                message = "Successfully re-assigned liens to new facility contact person.",
            });
        }
        catch
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "unable to re-assigned liens.",
            });
        }
    }

    private static async Task<IResult> ReassignFundingCompanyLegacy(
        LegacyReassignFundingCompanyRequest request,
        ILienService lienService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var userId = RequireUserId(ctx);

        if (!Guid.TryParse(request.liensId, out var lienId) || string.IsNullOrWhiteSpace(request.fundingCompany))
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "unable to re-assigned liens.",
            });
        }

        var lien = await lienService.GetByIdAsync(tenantId, lienId, ct);
        if (lien is null)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "unable to re-assigned liens.",
            });
        }

        try
        {
            var mapped = new UpdateLienRequest
            {
                ExternalReference = request.fundingCompany.Trim(),
                LienType = lien.LienType,
                CaseId = lien.CaseId,
                FacilityId = lien.FacilityId,
                OriginalAmount = lien.OriginalAmount,
                Jurisdiction = lien.Jurisdiction,
                IsConfidential = lien.IsConfidential,
                SubjectFirstName = lien.SubjectFirstName,
                SubjectLastName = lien.SubjectLastName,
                IncidentDate = lien.IncidentDate,
                Description = lien.Description,
            };

            await lienService.UpdateAsync(tenantId, lienId, userId, mapped, ct);

            return Results.Ok(new
            {
                isSuccess = true,
                message = "Successfully re-assigned liens to new funding company.",
            });
        }
        catch
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "unable to re-assigned liens.",
            });
        }
    }

    private static async Task<IResult> DeleteLiensLegacy(
        Guid liensId,
        ILienService lienService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var userId   = RequireUserId(ctx);

        var existing = await lienService.GetByIdAsync(tenantId, liensId, ct);
        if (existing is null)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "No record found to delete.",
            });
        }

        try
        {
            await lienService.DeleteAsync(tenantId, liensId, userId, ct);
            return Results.Ok(new
            {
                isSuccess = true,
                message = "Successfully Deleted Lien.",
            });
        }
        catch (Exception ex)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = ex.Message,
            });
        }
    }

    private static async Task<List<LienResponse>> GetLegacyLiensListingAsync(
        ILienService lienService,
        Guid tenantId,
        Guid? caseId,
        CancellationToken ct)
    {
        const int pageSize = 200;
        var page = 1;
        var data = new List<LienResponse>();

        while (true)
        {
            var result = await lienService.SearchAsync(
                tenantId,
                search: null,
                status: null,
                lienType: null,
                caseId: caseId,
                facilityId: null,
                page: page,
                pageSize: pageSize,
                ct);

            if (result.Items.Count == 0)
                break;

            data.AddRange(result.Items);

            if (data.Count >= result.TotalCount)
                break;

            page++;
        }

        return data;
    }

    private static async Task<string?> ResolveLegacyFacilityNameAsync(
        Guid tenantId,
        Guid requestedFacilityId,
        IContactService contactService,
        IFacilityService facilityService,
        CancellationToken ct)
    {
        var facilityContact = await contactService.GetByIdAsync(tenantId, requestedFacilityId, ct);
        if (facilityContact is not null && IsStandaloneFacilityContact(facilityContact))
            return ResolveFacilityDisplayName(facilityContact);

        var facility = await facilityService.GetByIdAsync(tenantId, requestedFacilityId, ct);
        if (facility is not null && !string.IsNullOrWhiteSpace(facility.Name))
            return facility.Name.Trim();

        if (facilityContact?.FacilityId is Guid linkedFacilityId)
        {
            var linkedFacility = await facilityService.GetByIdAsync(tenantId, linkedFacilityId, ct);
            if (linkedFacility is not null && !string.IsNullOrWhiteSpace(linkedFacility.Name))
                return linkedFacility.Name.Trim();
        }

        return null;
    }

    private static bool IsStandaloneFacilityContact(ContactResponse contact) =>
        (string.Equals(contact.ContactType, ContactType.Facility, StringComparison.Ordinal) ||
         string.Equals(contact.ContactType, ContactType.MedicalFacility, StringComparison.Ordinal)) &&
        string.IsNullOrWhiteSpace(contact.ContactSubtype);

    private static bool IsStandaloneFacilityContact(Contact contact) =>
        (string.Equals(contact.ContactType, ContactType.Facility, StringComparison.Ordinal) ||
         string.Equals(contact.ContactType, ContactType.MedicalFacility, StringComparison.Ordinal)) &&
        string.IsNullOrWhiteSpace(contact.ContactSubtype);

    private static string ResolveFacilityDisplayName(ContactResponse contact)
        => string.IsNullOrWhiteSpace(contact.Organization)
            ? contact.DisplayName
            : contact.Organization.Trim();

    private static IEnumerable<string> GetFacilityContactLookupNames(Contact contact)
    {
        if (!string.IsNullOrWhiteSpace(contact.Organization))
            yield return contact.Organization.Trim();

        if (!string.IsNullOrWhiteSpace(contact.DisplayName))
            yield return contact.DisplayName.Trim();
    }

    private static Dictionary<string, string> ParseLegacyNoteFields(string? notes)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(notes))
            return result;

        const string legacyMetadataMarker = "[legacy-meta]";
        var rawMetadata = notes;
        var markerIndex = notes.IndexOf(legacyMetadataMarker, StringComparison.Ordinal);
        if (markerIndex >= 0)
            rawMetadata = notes[(markerIndex + legacyMetadataMarker.Length)..].Trim();

        foreach (var segment in rawMetadata.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = segment.IndexOf('=');
            if (eq > 0)
            {
                var key = segment[..eq].Trim();
                var value = segment[(eq + 1)..].Trim();
                result[key] = value;
            }
        }

        return result;
    }

    private static string SerializeLegacyNoteFields(Dictionary<string, string> fields)
    {
        if (fields.Count == 0)
            return string.Empty;

        return string.Join("; ", fields.Select(pair => $"{pair.Key}={pair.Value}"));
    }

    private static string FormatLegacyDate(DateOnly? value)
        => value?.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string FormatLegacyTimestamp(DateTime value)
        => PacificTimeHelper.FormatTimestamp(value);

    private static async Task<IResult> UpdateLien(
        Guid id,
        UpdateLienRequest request,
        ILienService lienService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var userId = RequireUserId(ctx);
        var result = await lienService.UpdateAsync(tenantId, id, userId, request, ct);
        return Results.Ok(result);
    }
}
