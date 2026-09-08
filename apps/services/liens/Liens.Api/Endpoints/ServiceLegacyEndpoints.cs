using BuildingBlocks.Authorization;
using BuildingBlocks.Authorization.Filters;
using BuildingBlocks.Context;
using Liens.Api.Serialization;
using Liens.Application.DTOs;
using Liens.Application.Interfaces;
using Liens.Domain;
using Liens.Domain.Entities;
using Liens.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Liens.Infrastructure.Persistence;
using Microsoft.Extensions.Primitives;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Globalization;
using System.Text;

namespace Liens.Api.Endpoints;

public static class ServiceLegacyEndpoints
{
    private sealed class LegacyCaseV3FilterRequest
    {
        public int page { get; init; } = 1;
        public int limit { get; init; } = 20;
        public string? lawFirmId { get; init; }
        public string? accidentTypeId { get; init; }
        public string? statusId { get; init; }
        public string? caseManagerId { get; init; }
        public string? keyword { get; init; }
        public string? search { get; init; }
        public string? sortBy { get; init; }
        public string? sortDirection { get; init; }
    }

    private sealed class LegacyServiceLiensV3Request
    {
        public int page { get; init; } = 1;
        public int limit { get; init; } = 20;
        public string? keyword { get; init; }
        public string? caseId { get; init; }
    }

    private sealed class LegacySettlementHistoryRequest
    {
        public string CaseId { get; init; } = string.Empty;
        public int Page { get; init; } = 1;
        public int Limit { get; init; } = 10;
    }

    private sealed class IdentityUserResponse
    {
        public Guid Id { get; init; }
        public string? FirstName { get; init; }
        public string? LastName { get; init; }
        public string? Email { get; init; }
    }

    private sealed class LegacyServicingDetailsRequest
    {
        public string? caseId { get; init; }
        public string? caseStatusId { get; init; }
        public string? isUCCFiled { get; init; }
        public string? switchedDate { get; init; }
        public string? lawFirmId { get; init; }
        public string? caseManager { get; init; }
        public string? attorney { get; init; }
    }

    private sealed class LegacyUpdateLienStatusRequest
    {
        public string? caseId { get; init; }
        public string? liensId { get; init; }
        public string? statusId { get; init; }
    }

    private sealed class LegacyUpdateMultipleLienStatusRequest
    {
        public string? caseId { get; init; }
        public string? lienIds { get; init; }
        public string? lienStatus { get; init; }
        public string? closedDate { get; init; }
        public string? note { get; init; }
    }

    private sealed class LegacyDeleteSettlementPaymentRequest
    {
        public string? caseId { get; init; }
        public string? paymentId { get; init; }
    }

    private sealed class LegacyGenerateCaseCsvRequest
    {
        public string? caseId { get; init; }
        public string? lawFirmId { get; init; }
        public string? accidentTypeId { get; init; }
        public string? statusId { get; init; }
        public string? caseManagerId { get; init; }
        public string? keyword { get; init; }
        public string? search { get; init; }
        public string? sortBy { get; init; }
        public string? sortDirection { get; init; }
        public bool legacyFormat { get; init; }
    }

    private readonly record struct LegacyServiceCaseMetrics(
        string? SettlementStatus = null,
        string? SettlementDate = null,
        decimal SettlementAmount = 0m,
        decimal BillingAmount = 0m,
        decimal PurchaseAmount = 0m);

    private readonly record struct LegacyServiceCsvRow(
        CaseResponse Case,
        LegacyServiceCaseMetrics Metrics);

    private readonly record struct LegacyMedicalAmounts(
        decimal PurchaseAmount = 0m,
        bool HasPurchaseAmount = false,
        decimal BillingAmount = 0m,
        bool HasBillingAmount = false)
    {
        public LegacyMedicalAmounts Add(LegacyMedicalAmounts other) => new(
            PurchaseAmount + other.PurchaseAmount,
            HasPurchaseAmount || other.HasPurchaseAmount,
            BillingAmount + other.BillingAmount,
            HasBillingAmount || other.HasBillingAmount);
    }

    public static void MapServiceLegacyEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/service")
            .RequireAuthorization(Policies.AuthenticatedUser)
            .RequireProductAccess(LiensPermissions.ProductCode);

        group.MapGet("/case1", GetServiceCaseLegacy)
            .RequirePermission(LiensPermissions.CaseRead);
        group.MapGet("/case", GetServiceCaseLegacy)
            .RequirePermission(LiensPermissions.CaseRead);
        group.MapPost("/case/v3", GetServiceCaseV3Legacy)
            .RequirePermission(LiensPermissions.CaseRead);

        group.MapGet("/liens/{caseId}", GetServiceLiensLegacy)
            .RequirePermission(LiensPermissions.LienRead);
        group.MapGet("/closed-liens/{caseId}", GetClosedServiceLiensLegacy)
            .RequirePermission(LiensPermissions.LienRead);
        group.MapGet("/all-liens/{caseId}", GetAllServiceLiensLegacy)
            .RequirePermission(LiensPermissions.LienRead);
        group.MapPost("/liens/v3", GetServiceLiensV3Legacy)
            .RequirePermission(LiensPermissions.LienRead);

        group.MapPost("/delete-payment", DeleteSettlementPaymentLegacy)
            .RequirePermission(LiensPermissions.LienUpdate);
        group.MapPost("/settlement/history/v3", GetSettlementHistoryV3Legacy)
            .RequirePermission(LiensPermissions.LienRead);
        group.MapPatch("/update-details", UpdateServicingDetailsLegacy)
            .RequirePermission(LiensPermissions.CaseUpdate);
        group.MapGet("/liens/settlement/payment-details/{caseId}", GetSettlementPaymentDetailsLegacy)
            .RequirePermission(LiensPermissions.LienRead);
        group.MapPatch("/liens/update/status", UpdateLienStatusLegacy)
            .RequirePermission(LiensPermissions.LienUpdate);
        group.MapGet("/liens/settlement-details/{caseId}", GetSettlementDetailsLegacy)
            .RequirePermission(LiensPermissions.LienRead);
        group.MapPost("/generate-csv", GenerateServiceCsvLegacy)
            .RequirePermission(LiensPermissions.CaseRead);
        group.MapPost("/update-liens-status", UpdateLienStatusBulkLegacy)
            .RequirePermission(LiensPermissions.LienUpdate);
    }

    private static async Task<IResult> GetServiceCaseLegacy(
        ICaseService caseService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var orgId = ctx.OrgId;
        var result = await caseService.SearchAsync(tenantId, null, null, 1, 200, orgId, ct);

        if (result.Items.Count == 0)
        {
            return Results.BadRequest(new { message = "No record", isSuccess = false });
        }

        return Results.Ok(new
        {
            message = "List of Case",
            isSuccess = true,
            data = result.Items.Select(MapLegacyServiceCase).ToList(),
        });
    }

    private static async Task<IResult> GetServiceCaseV3Legacy(
        LegacyCaseV3FilterRequest filter,
        ICaseService caseService,
        LiensDbContext db,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var keyword = !string.IsNullOrWhiteSpace(filter.keyword)
            ? filter.keyword
            : filter.search;
        var result = await caseService.SearchV3Async(
            tenantId: tenantId,
            keyword: keyword,
            statusId: filter.statusId,
            page: Math.Max(filter.page, 1),
            limit: Math.Max(filter.limit, 1),
            sortBy: filter.sortBy,
            sortDirection: filter.sortDirection,
            // The repository treats GUID values as legacy organization IDs and
            // metadata contact IDs, preserving both contracts with OR semantics.
            lawFirmOrgId: null,
            accidentTypeId: filter.accidentTypeId,
            caseManagerId: filter.caseManagerId,
            lawFirmIds: filter.lawFirmId,
            ct: ct);

        var metricsByCaseId = await GetLegacyServiceCaseMetricsAsync(
            db,
            tenantId,
            result.Items.Select(item => item.Id).ToArray(),
            ct);

        return Results.Ok(new
        {
            isSuccess = true,
            message = "List of Case",
            data = result.Items.Select(item =>
            {
                metricsByCaseId.TryGetValue(item.Id, out var metrics);
                return MapLegacyServiceCaseV3(item, metrics);
            }).ToList(),
            page = result.Page,
            limit = result.PageSize,
            totalCount = result.TotalCount,
        });
    }

    private static async Task<IReadOnlyDictionary<Guid, LegacyServiceCaseMetrics>> GetLegacyServiceCaseMetricsAsync(
        LiensDbContext db,
        Guid tenantId,
        IReadOnlyCollection<Guid> caseIds,
        CancellationToken ct)
    {
        var distinctCaseIds = caseIds.ToHashSet(EqualityComparer<Guid>.Default);
        if (distinctCaseIds.Count == 0)
            return new Dictionary<Guid, LegacyServiceCaseMetrics>();

        var liens = await db.Liens.AsNoTracking()
            .Where(lien =>
                lien.TenantId == tenantId &&
                lien.CaseId.HasValue &&
                distinctCaseIds.Contains(lien.CaseId.Value))
            .Select(lien => new
            {
                lien.Id,
                CaseId = lien.CaseId!.Value,
                lien.OriginalAmount,
                lien.PurchasePrice,
            })
            .ToListAsync(ct);

        var lienIds = liens
            .Select(lien => lien.Id)
            .ToHashSet(EqualityComparer<Guid>.Default);
        var medicalCodeItems = await db.ServicingItems.AsNoTracking()
            .Where(item =>
                item.TenantId == tenantId &&
                item.LienId.HasValue &&
                lienIds.Contains(item.LienId.Value) &&
                item.TaskType == "LegacyMedicalCode")
            .Select(item => new { LienId = item.LienId!.Value, item.Notes })
            .ToListAsync(ct);
        var medicalAmountsByLienId = medicalCodeItems
            .GroupBy(item => item.LienId)
            .ToDictionary(
                group => group.Key,
                group => group.Aggregate(
                    new LegacyMedicalAmounts(),
                    (amounts, item) => amounts.Add(ParseLegacyMedicalAmounts(item.Notes))));

        var settlements = await db.LienSettlements.AsNoTracking()
            .Where(settlement =>
                settlement.TenantId == tenantId &&
                !settlement.IsDeleted &&
                distinctCaseIds.Contains(settlement.CaseId))
            .Select(settlement => new
            {
                settlement.Id,
                settlement.CaseId,
                settlement.LienId,
                settlement.PaymentNumber,
                settlement.Amount,
                settlement.SettlementDate,
                settlement.Status,
                settlement.Note,
                settlement.CreatedAtUtc,
            })
            .ToListAsync(ct);
        var settlementsByCaseId = settlements
            .GroupBy(settlement => settlement.CaseId)
            .ToDictionary(group => group.Key, group => group.ToList());
        var settlementsByPayment = settlements
            .GroupBy(settlement => (settlement.LienId, settlement.PaymentNumber))
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(settlement => settlement.CreatedAtUtc)
                    .ThenByDescending(settlement => settlement.Id)
                    .First());

        var payments = await db.SettlementPaymentDetails.AsNoTracking()
            .Where(payment =>
                payment.TenantId == tenantId &&
                !payment.IsDeleted &&
                payment.PostingStatus != SettlementPaymentDetail.VoidedStatus &&
                distinctCaseIds.Contains(payment.CaseId))
            .Select(payment => new
            {
                payment.Id,
                payment.CaseId,
                payment.LienId,
                payment.PaymentNumber,
                payment.Amount,
                payment.Note,
                payment.CreatedAtUtc,
            })
            .ToListAsync(ct);
        var latestPaymentsByCaseId = payments
            .GroupBy(payment => payment.CaseId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(payment => payment.CreatedAtUtc)
                    .ThenByDescending(payment => payment.Id)
                    .First());

        var paymentLookups = await db.LookupValues.AsNoTracking()
            .Where(lookup =>
                lookup.IsActive &&
                (lookup.TenantId == null || lookup.TenantId == tenantId) &&
                (lookup.Category == LookupCategory.SettlementType ||
                 lookup.Category == LookupCategory.SettlementStatus))
            .ToListAsync(ct);

        var result = new Dictionary<Guid, LegacyServiceCaseMetrics>(distinctCaseIds.Count);
        foreach (var caseId in distinctCaseIds)
        {
            var caseLiens = liens.Where(lien => lien.CaseId == caseId).ToList();
            var billingAmount = caseLiens.Sum(lien =>
                medicalAmountsByLienId.TryGetValue(lien.Id, out var medicalAmounts) &&
                medicalAmounts.HasBillingAmount
                    ? medicalAmounts.BillingAmount
                    : lien.OriginalAmount);
            var purchaseAmount = caseLiens.Sum(lien =>
                medicalAmountsByLienId.TryGetValue(lien.Id, out var medicalAmounts) &&
                medicalAmounts.HasPurchaseAmount
                    ? medicalAmounts.PurchaseAmount
                    : lien.PurchasePrice ?? 0m);

            settlementsByCaseId.TryGetValue(caseId, out var caseSettlements);
            var settlementAmount = caseSettlements?.Sum(settlement =>
                ResolveLegacySettlementAmount(settlement.Amount, settlement.Note)) ?? 0m;
            if (settlementAmount == 0m)
            {
                settlementAmount = payments
                    .Where(payment => payment.CaseId == caseId)
                    .Sum(payment => payment.Amount);
            }
            var settlementDate = caseSettlements?
                .Where(settlement => settlement.SettlementDate.HasValue)
                .Max(settlement => settlement.SettlementDate);

            string? settlementStatusId = null;
            if (latestPaymentsByCaseId.TryGetValue(caseId, out var latestPayment))
            {
                settlementsByPayment.TryGetValue(
                    (latestPayment.LienId, latestPayment.PaymentNumber),
                    out var matchingSettlement);
                var metadata = ParseSettlementPaymentMetadata(latestPayment.Note);
                var storedTypeId = metadata.GetValueOrDefault("type") ?? string.Empty;
                var storedStatusId = metadata.GetValueOrDefault("status") ??
                                     matchingSettlement?.Status ??
                                     caseSettlements?
                                         .OrderByDescending(settlement => settlement.CreatedAtUtc)
                                         .ThenByDescending(settlement => settlement.Id)
                                         .FirstOrDefault()?.Status ??
                                     string.Empty;
                settlementStatusId = IsLegacyLienStatus(storedStatusId) &&
                                     IsSettlementPaymentStatus(paymentLookups, storedTypeId)
                    ? storedTypeId
                    : storedStatusId;
                if (settlementAmount > 0m &&
                    IsNoRecoveryPaymentStatus(paymentLookups, settlementStatusId))
                {
                    settlementStatusId = "Closed";
                }

                if (!settlementDate.HasValue)
                {
                    result[caseId] = new LegacyServiceCaseMetrics(
                        ResolvePaymentLookupName(
                            paymentLookups,
                            LookupCategory.SettlementType,
                            settlementStatusId),
                        PacificTimeHelper.FormatDate(latestPayment.CreatedAtUtc),
                        settlementAmount,
                        billingAmount,
                        purchaseAmount);
                    continue;
                }
            }
            else
            {
                settlementStatusId = caseSettlements?
                    .OrderByDescending(settlement => settlement.CreatedAtUtc)
                    .ThenByDescending(settlement => settlement.Id)
                    .FirstOrDefault()?.Status;
            }

            result[caseId] = new LegacyServiceCaseMetrics(
                ResolvePaymentLookupName(
                    paymentLookups,
                    LookupCategory.SettlementType,
                    settlementStatusId ?? string.Empty),
                settlementDate?.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture) ?? string.Empty,
                settlementAmount,
                billingAmount,
                purchaseAmount);
        }

        return result;
    }

    private static async Task<IResult> GetServiceLiensLegacy(
        string caseId,
        ILienService lienService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        return await GetServiceLiensByStatusAsync(caseId, lienService, ctx, includeOpen: true, includeClosed: false, ct);
    }

    private static async Task<IResult> GetClosedServiceLiensLegacy(
        string caseId,
        ILienService lienService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        return await GetServiceLiensByStatusAsync(caseId, lienService, ctx, includeOpen: false, includeClosed: true, ct);
    }

    private static async Task<IResult> GetAllServiceLiensLegacy(
        string caseId,
        ILienService lienService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        return await GetServiceLiensByStatusAsync(caseId, lienService, ctx, includeOpen: true, includeClosed: true, ct);
    }

    private static async Task<IResult> GetServiceLiensByStatusAsync(
        string caseId,
        ILienService lienService,
        ICurrentRequestContext ctx,
        bool includeOpen,
        bool includeClosed,
        CancellationToken ct)
    {
        var tenantId = RequireTenantId(ctx);
        if (!Guid.TryParse(caseId, out var parsedCaseId))
        {
            return Results.BadRequest(new { isSuccess = false, message = "Invalid caseId" });
        }

        var items = await SearchLiensByCaseAsync(lienService, tenantId, parsedCaseId, ct);
        items = items.Where(item =>
                (includeOpen && LienStatus.Open.Contains(item.Status)) ||
                (includeClosed && LienStatus.Terminal.Contains(item.Status)))
            .ToList();

        if (items.Count == 0)
        {
            return Results.BadRequest(new { isSuccess = false, message = "No record" });
        }

        return Results.Ok(new
        {
            isSuccess = true,
            message = "Liens List.",
            data = items.Select(MapLegacyServiceLien).ToList(),
        });
    }

    private static async Task<IResult> GetServiceLiensV3Legacy(
        LegacyServiceLiensV3Request filter,
        ILienService lienService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = CaseEndpoints.RequireTenantId(ctx);
        Guid? caseId = Guid.TryParse(filter.caseId, out var parsedCaseId) ? parsedCaseId : null;
        var result = await lienService.SearchAsync(
            tenantId,
            filter.keyword,
            null,
            null,
            caseId,
            null,
            Math.Max(filter.page, 1),
            Math.Max(filter.limit, 1),
            ct);

        if (result.Items.Count == 0)
        {
            return Results.NotFound(new { isSuccess = false, message = "No record found." });
        }

        return Results.Ok(new
        {
            isSuccess = true,
            message = "Liens List.",
            data = result.Items.Select(MapLegacyServiceLien).ToList(),
            page = result.Page,
            limit = result.PageSize,
            totalCount = result.TotalCount,
        });
    }

    private static async Task<IResult> DeleteSettlementPaymentLegacy(
        LegacyDeleteSettlementPaymentRequest request,
        ISettlementService settlementService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var userId = RequireUserId(ctx);
        if (!Guid.TryParse(request.paymentId, out var paymentId))
        {
            return Results.BadRequest(new { isSuccess = false, message = "Invalid paymentId" });
        }

        await settlementService.DeletePaymentAsync(tenantId, paymentId, userId, ct);
        return Results.Ok(new { isSuccess = true, message = "Successfully Deleted." });
    }

    private static async Task<IResult> GetSettlementHistoryV3Legacy(
        LegacySettlementHistoryRequest request,
        ISettlementService settlementService,
        ILienService lienService,
        LiensDbContext db,
        IHttpClientFactory httpClientFactory,
        HttpContext httpContext,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        if (!Guid.TryParse(request.CaseId, out var caseId))
        {
            return Results.BadRequest(new { isSuccess = false, message = "Invalid CaseId" });
        }

        var history = await BuildSettlementHistoryAsync(
            tenantId,
            caseId,
            settlementService,
            lienService,
            db,
            httpClientFactory,
            httpContext.Request.Headers.Authorization,
            ct);
        var page = Math.Max(request.Page, 1);
        var limit = Math.Max(request.Limit, 1);
        var data = history.Skip((page - 1) * limit).Take(limit).ToList();

        return Results.Ok(new
        {
            isSuccess = true,
            message = "Settlement history retrieved successfully.",
            data,
            totalCount = history.Count,
            page,
            limit,
        });
    }

    private static async Task<IResult> UpdateServicingDetailsLegacy(
        LegacyServicingDetailsRequest request,
        ICaseService caseService,
        LiensDbContext db,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var userId = RequireUserId(ctx);
        if (!Guid.TryParse(request.caseId, out var caseId))
        {
            return Results.BadRequest(new { isSuccess = false, message = "Invalid caseId" });
        }

        var existing = await caseService.GetByIdAsync(tenantId, caseId, ct);
        if (existing is null)
        {
            return Results.BadRequest(new { isSuccess = false, message = "Case not found" });
        }

        var schedulesLawFirmSwitch =
            request.lawFirmId is not null && LawFirmChangeHistory.IsFutureSwitch(request.switchedDate);
        var repeatsScheduledSwitch = schedulesLawFirmSwitch &&
            await LawFirmChangeHistory.IsSamePendingSwitchAsync(
                db,
                tenantId,
                existing.PendingLawFirmId,
                existing.SwitchedDate,
                request.lawFirmId,
                request.switchedDate,
                ct);
        var update = new UpdateCaseRequest
        {
            ClientFirstName = existing.ClientFirstName,
            ClientLastName = existing.ClientLastName,
            ExternalReference = existing.ExternalReference,
            Title = existing.Title,
            ClientDob = existing.ClientDob,
            ClientPhone = existing.ClientPhone,
            ClientEmail = existing.ClientEmail,
            ClientAddress = existing.ClientAddress,
            DateOfIncident = existing.DateOfIncident,
            InsuranceCarrier = existing.InsuranceCarrier,
            PolicyNumber = existing.PolicyNumber,
            ClaimNumber = existing.ClaimNumber,
            Description = existing.Description,
            Notes = existing.Notes,
            Status = string.IsNullOrWhiteSpace(request.caseStatusId)
                ? existing.Status
                : CaseEndpoints.NormalizeLegacyCaseStatus(request.caseStatusId),
            StatusLabel = CaseEndpoints.ResolveLegacyCaseStatusLabel(request.caseStatusId),
            DemandAmount = existing.DemandAmount,
            SettlementAmount = existing.SettlementAmount,
            IsUccFiled = request.isUCCFiled,
            LawFirmId = schedulesLawFirmSwitch ? existing.LawFirmId : request.lawFirmId,
            PendingLawFirmId = schedulesLawFirmSwitch
                ? request.lawFirmId
                : request.lawFirmId is not null ? string.Empty : null,
            CaseManagerId = request.caseManager,
            AttorneyId = request.attorney,
            SwitchedDate = schedulesLawFirmSwitch
                ? repeatsScheduledSwitch ? existing.SwitchedDate : request.switchedDate
                : request.lawFirmId is not null ? string.Empty : request.switchedDate,
        };

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await caseService.UpdateAsync(tenantId, caseId, userId, update, ct);

        if (request.lawFirmId is not null)
        {
            await LawFirmChangeHistory.RecordAsync(
                db,
                tenantId,
                caseId,
                existing.LawFirmId,
                request.lawFirmId,
                request.switchedDate,
                userId,
                ctx.Name ?? ctx.Email ?? userId.ToString(),
                ct,
                existing.PendingLawFirmId,
                existing.SwitchedDate);
        }

        await transaction.CommitAsync(ct);

        return Results.Ok(new { isSuccess = true, message = "Successfully updated servicing details." });
    }

    private static async Task<IResult> GetSettlementPaymentDetailsLegacy(
        string caseId,
        ISettlementService settlementService,
        ILienService lienService,
        LiensDbContext db,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        if (!Guid.TryParse(caseId, out var parsedCaseId))
        {
            return Results.BadRequest(new { isSuccess = false, message = "Invalid caseId" });
        }

        var payments = await settlementService.GetPaymentsByCaseAsync(tenantId, parsedCaseId, ct);
        var settlements = await settlementService.GetSettlementsByCaseAsync(tenantId, parsedCaseId, ct);
        var liens = await SearchLiensByCaseAsync(lienService, tenantId, parsedCaseId, ct);
        var liensById = liens.ToDictionary(l => l.Id);
        var settlementsByPayment = settlements
            .GroupBy(settlement => (settlement.LienId, settlement.PaymentNumber))
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(settlement => settlement.CreatedAtUtc).First());
        var displayPaymentNumbers = ResolveDisplayPaymentNumbers(payments);
        var paymentLookups = await db.LookupValues.AsNoTracking()
            .Where(lookup =>
                lookup.IsActive &&
                (lookup.TenantId == null || lookup.TenantId == tenantId) &&
                (lookup.Category == LookupCategory.SettlementType ||
                 lookup.Category == LookupCategory.SettlementStatus))
            .ToListAsync(ct);

        var data = payments.Select(payment =>
        {
            liensById.TryGetValue(payment.LienId, out var lien);
            settlementsByPayment.TryGetValue(
                (payment.LienId, payment.PaymentNumber),
                out var settlement);
            var storedTypeId = payment.SettlementTypeId ?? string.Empty;
            var storedStatusId = payment.SettlementStatusId ?? settlement?.Status ?? string.Empty;
            var usesLegacyPaymentFields = IsLegacyLienStatus(storedStatusId) &&
                                          IsSettlementPaymentStatus(paymentLookups, storedTypeId);
            var typeId = usesLegacyPaymentFields ? "other" : storedTypeId;
            if (string.IsNullOrWhiteSpace(typeId))
                typeId = "other";
            var statusId = usesLegacyPaymentFields ? storedTypeId : storedStatusId;
            var isNoRecovery = payment.Amount <= 0m &&
                               IsNoRecoveryPaymentStatus(paymentLookups, statusId);
            if (payment.Amount > 0m &&
                IsNoRecoveryPaymentStatus(paymentLookups, statusId))
            {
                statusId = "Closed";
            }
            var amountToSettle = payment.Amount != 0m
                ? payment.Amount
                : settlement is { Amount: not 0m }
                    ? settlement.Amount
                    : lien?.CurrentBalance ?? settlement?.Amount ?? 0m;
            var checkAmount = payment.Amount == 0m ? amountToSettle : payment.Amount;
            var legacyLienStatus = ResolveLegacyLienStatus(lien?.Status);
            return new
            {
                id = payment.Id.ToString(),
                caseId = payment.CaseId.ToString(),
                lienId = payment.LienId.ToString(),
                lienCode = lien?.LienNumber ?? string.Empty,
                lienStatus = legacyLienStatus,
                lienStatusId = legacyLienStatus,
                amount = payment.Amount.ToString("0.00", CultureInfo.InvariantCulture),
                checkAmount = isNoRecovery
                    ? string.Empty
                    : checkAmount.ToString("0.00", CultureInfo.InvariantCulture),
                checkDate = isNoRecovery
                    ? string.Empty
                    : payment.PaymentDate?.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture) ?? string.Empty,
                checkNumber = payment.CheckNumber ?? string.Empty,
                typeId,
                type = ResolvePaymentLookupName(paymentLookups, LookupCategory.SettlementStatus, typeId),
                statusId,
                status = isNoRecovery
                    ? "No Recovery"
                    : ResolvePaymentLookupName(paymentLookups, LookupCategory.SettlementType, statusId),
                payor = payment.Payee ?? payment.PaymentMethod ?? string.Empty,
                netProfit = (payment.NetProfit ?? 0m).ToString("0.00", CultureInfo.InvariantCulture),
                note = payment.Note ?? settlement?.Note ?? string.Empty,
                paymentNumber = displayPaymentNumbers[payment.Id].ToString(CultureInfo.InvariantCulture),
                date = PacificTimeHelper.FormatDate(payment.CreatedAtUtc),
                amountToSettle = amountToSettle.ToString("0.00", CultureInfo.InvariantCulture),
            };
        }).ToList();

        return Results.Ok(new { isSuccess = true, message = "Settlement payment details retrieved successfully.", data });
    }

    private static IReadOnlyDictionary<Guid, int> ResolveDisplayPaymentNumbers(
        IReadOnlyCollection<SettlementPaymentDetailResponse> payments)
    {
        var usedNumbers = payments
            .Where(payment => payment.PaymentNumber > 0)
            .Select(payment => payment.PaymentNumber)
            .ToHashSet();
        var resolved = payments
            .Where(payment => payment.PaymentNumber > 0)
            .ToDictionary(payment => payment.Id, payment => payment.PaymentNumber);
        var nextFallback = 1;

        foreach (var payment in payments
                     .Where(payment => payment.PaymentNumber <= 0)
                     .OrderBy(payment => payment.CreatedAtUtc)
                     .ThenBy(payment => payment.Id))
        {
            while (usedNumbers.Contains(nextFallback))
                nextFallback++;

            resolved[payment.Id] = nextFallback;
            usedNumbers.Add(nextFallback++);
        }

        return resolved;
    }

    private static string ResolvePaymentLookupName(
        IReadOnlyCollection<LookupValue> lookups,
        string category,
        string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var match = lookups.FirstOrDefault(lookup =>
            string.Equals(lookup.Category, category, StringComparison.Ordinal) &&
            (string.Equals(lookup.Id.ToString(), value, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(lookup.Code, value, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(lookup.Name, value, StringComparison.OrdinalIgnoreCase)));

        if (match is not null)
            return match.Name;

        return HumanizeLegacyCode(value);
    }

    private static string ResolveLegacyLienStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return string.Empty;

        if (LienStatus.Open.Contains(status))
            return "Open";

        return string.Equals(status, LienStatus.Settled, StringComparison.OrdinalIgnoreCase)
            ? "Closed"
            : status;
    }

    private static bool IsLegacyLienStatus(string value) =>
        string.Equals(value, "Open", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "Closed", StringComparison.OrdinalIgnoreCase);

    private static bool IsSettlementPaymentStatus(
        IReadOnlyCollection<LookupValue> lookups,
        string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (value.Equals("full_payment", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("reduced_payment", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("partial_loss", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("no_recovery", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return lookups.Any(lookup =>
            string.Equals(lookup.Category, LookupCategory.SettlementType, StringComparison.Ordinal) &&
            (string.Equals(lookup.Id.ToString(), value, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(lookup.Code, value, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(lookup.Name, value, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsNoRecoveryPaymentStatus(
        IReadOnlyCollection<LookupValue> lookups,
        string value)
    {
        if (IsNoRecoveryValue(value))
            return true;

        var lookup = lookups.FirstOrDefault(item =>
            string.Equals(item.Id.ToString(), value, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.Code, value, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.Name, value, StringComparison.OrdinalIgnoreCase));

        return lookup is not null &&
               (IsNoRecoveryValue(lookup.Code) || IsNoRecoveryValue(lookup.Name));
    }

    private static bool IsNoRecoveryValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = value.Trim()
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);
        return normalized == "4" ||
               string.Equals(normalized, "NoRecovery", StringComparison.OrdinalIgnoreCase);
    }

    private static string HumanizeLegacyCode(string value)
    {
        if (string.Equals(value, "other", StringComparison.OrdinalIgnoreCase))
            return "Other";

        if (!value.Contains('_'))
            return value;

        return string.Join(' ', value
            .Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));
    }

    private static async Task<IResult> UpdateLienStatusLegacy(
        LegacyUpdateLienStatusRequest request,
        ILienService lienService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var userId = RequireUserId(ctx);
        if (!Guid.TryParse(request.liensId, out var lienId) || string.IsNullOrWhiteSpace(request.statusId))
        {
            return Results.BadRequest(new { isSuccess = false, message = "Invalid request" });
        }

        await lienService.SetLegacyMedicalStatusAsync(tenantId, lienId, userId, request.statusId.Trim(), ct);
        return Results.Ok(new { isSuccess = true, message = "Successfully updated lien status." });
    }

    private static async Task<IResult> GetSettlementDetailsLegacy(
        string caseId,
        ISettlementService settlementService,
        ILienService lienService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        if (!Guid.TryParse(caseId, out var parsedCaseId))
        {
            return Results.BadRequest(new { isSuccess = false, message = "Invalid caseId" });
        }

        var settlements = await settlementService.GetSettlementsByCaseAsync(tenantId, parsedCaseId, ct);
        var liens = await SearchLiensByCaseAsync(lienService, tenantId, parsedCaseId, ct);
        var liensById = liens.ToDictionary(l => l.Id);

        var data = settlements.Select(settlement =>
        {
            liensById.TryGetValue(settlement.LienId, out var lien);
            return new
            {
                caseId = settlement.CaseId.ToString(),
                lienId = settlement.LienId.ToString(),
                lienCode = lien?.LienNumber ?? string.Empty,
                lienStatus = lien?.Status ?? string.Empty,
                paymentNumber = settlement.PaymentNumber.ToString(CultureInfo.InvariantCulture),
                amount = settlement.Amount.ToString("0.00", CultureInfo.InvariantCulture),
                status = settlement.Status,
                note = settlement.Note ?? string.Empty,
            };
        }).ToList();

        return Results.Ok(new { isSuccess = true, message = "Settlement details retrieved successfully.", data });
    }

    private static async Task<IResult> GenerateServiceCsvLegacy(
        LegacyGenerateCaseCsvRequest request,
        ICaseService caseService,
        LiensDbContext db,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        byte[] bytes;

        if (request.legacyFormat)
        {
            Guid? orgId = Guid.TryParse(request.lawFirmId, out var lawFirmId) ? lawFirmId : null;
            var legacyResult = await caseService.SearchAsync(
                tenantId,
                null,
                request.statusId,
                1,
                1000,
                orgId,
                ct);

            if (!string.IsNullOrWhiteSpace(request.caseId) &&
                Guid.TryParse(request.caseId, out var legacyCaseId))
            {
                legacyResult = new PaginatedResult<CaseResponse>
                {
                    Items = legacyResult.Items.Where(c => c.Id == legacyCaseId).ToList(),
                    Page = 1,
                    PageSize = legacyResult.Items.Count,
                    TotalCount = legacyResult.Items.Count,
                };
            }

            if (legacyResult.Items.Count == 0)
                return NoServiceCsvData();

            bytes = BuildLegacyServiceCsv(legacyResult.Items);
        }
        else
        {
            const int pageSize = 100;
            var page = 1;
            var cases = new List<CaseResponse>();
            var keyword = !string.IsNullOrWhiteSpace(request.keyword)
                ? request.keyword
                : request.search;

            while (true)
            {
                var result = await caseService.SearchV3Async(
                    tenantId: tenantId,
                    keyword: keyword,
                    statusId: request.statusId,
                    page: page,
                    limit: pageSize,
                    sortBy: request.sortBy,
                    sortDirection: request.sortDirection,
                    accidentTypeId: request.accidentTypeId,
                    caseManagerId: request.caseManagerId,
                    lawFirmIds: request.lawFirmId,
                    ct: ct);

                if (result.Items.Count == 0)
                    break;

                cases.AddRange(result.Items);
                if (cases.Count >= result.TotalCount)
                    break;

                page++;
            }

            var filtered = cases
                .Where(item => string.IsNullOrWhiteSpace(request.caseId) ||
                               string.Equals(item.Id.ToString(), request.caseId, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(item.CaseNumber, request.caseId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (filtered.Count == 0)
                return NoServiceCsvData();

            var rows = new List<LegacyServiceCsvRow>(filtered.Count);
            foreach (var batch in filtered.Chunk(pageSize))
            {
                var metricsByCaseId = await GetLegacyServiceCaseMetricsAsync(
                    db,
                    tenantId,
                    batch.Select(item => item.Id).ToArray(),
                    ct);
                rows.AddRange(batch.Select(item => new LegacyServiceCsvRow(
                    item,
                    metricsByCaseId.GetValueOrDefault(item.Id))));
            }

            bytes = BuildServiceTableCsv(rows);
        }
        var exportItem = new
        {
            base64 = Convert.ToBase64String(bytes),
            filename = $"servicing_{DateTime.UtcNow:yyyyMMddHHmmss}.csv",
            export_format = "csv",
        };

        return Results.Ok(new
        {
            isSuccess = true,
            message = "CSV generated successfully.",
            data = new object[] { exportItem },
        });
    }

    private static IResult NoServiceCsvData() => Results.NotFound(new
    {
        isSuccess = false,
        message = "No data generated.",
        data = (object?)null,
    });

    private static byte[] BuildServiceTableCsv(IReadOnlyList<LegacyServiceCsvRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Case number,Plaintiff Name,Current Law Firm,Current Status,Settlement Status,Billing Amount,Purchase Amount,Amount Settled,Settled Date");

        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(",", new[]
            {
                CsvEscape(row.Case.CaseNumber),
                CsvEscape(row.Case.ClientDisplayName),
                CsvEscape(row.Case.LawFirm ?? string.Empty),
                CsvEscape(row.Case.Status),
                CsvEscape(row.Metrics.SettlementStatus ?? string.Empty),
                CsvEscape(row.Metrics.BillingAmount.ToString("#,##0.00", CultureInfo.InvariantCulture)),
                CsvEscape(row.Metrics.PurchaseAmount.ToString("#,##0.00", CultureInfo.InvariantCulture)),
                CsvEscape(row.Metrics.SettlementAmount.ToString("#,##0.00", CultureInfo.InvariantCulture)),
                CsvEscape(row.Metrics.SettlementDate ?? string.Empty),
            }));
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static byte[] BuildLegacyServiceCsv(IReadOnlyList<CaseResponse> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine("CaseId,CaseNumber,ClientFirstName,ClientLastName,Status,DateOfLoss");
        foreach (var item in items)
        {
            sb.AppendLine($"{item.Id},{CsvEscape(item.CaseNumber)},{CsvEscape(item.ClientFirstName)},{CsvEscape(item.ClientLastName)},{CsvEscape(item.Status)},{item.DateOfIncident?.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture)}");
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static async Task<IResult> UpdateLienStatusBulkLegacy(
        LegacyUpdateMultipleLienStatusRequest request,
        ILienService lienService,
        ISettlementService settlementService,
        LiensDbContext db,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var userId = RequireUserId(ctx);
        if (!Guid.TryParse(request.caseId, out var caseId) ||
            string.IsNullOrWhiteSpace(request.lienIds) ||
            string.IsNullOrWhiteSpace(request.lienStatus) ||
            !TryParseLegacyClosedDate(request.closedDate, out var closedDate))
        {
            return Results.BadRequest(new { isSuccess = false, message = "Invalid request" });
        }

        var rawLienIds = request.lienIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (rawLienIds.Count == 0 || rawLienIds.Any(value => !Guid.TryParse(value, out _)))
        {
            return Results.BadRequest(new { isSuccess = false, message = "Invalid lienIds" });
        }

        var lienIds = rawLienIds.Select(Guid.Parse).Distinct().ToList();
        var caseExists = await db.Cases.AsNoTracking()
            .AnyAsync(item => item.TenantId == tenantId && item.Id == caseId, ct);
        var selectedLiens = await db.Liens.AsNoTracking()
            .Where(item => item.TenantId == tenantId && lienIds.Contains(item.Id))
            .Select(item => new { item.Id, item.CaseId })
            .ToListAsync(ct);
        if (!caseExists ||
            selectedLiens.Count != lienIds.Count ||
            selectedLiens.Any(item => item.CaseId != caseId))
        {
            return Results.BadRequest(new
            {
                isSuccess = false,
                message = "Case and liens must belong to the authenticated tenant and match each other",
            });
        }

        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(ct)
            : null;

        try
        {
            var payments = await settlementService.GetPaymentsByCaseAsync(tenantId, caseId, ct);
            var settlements = await settlementService.GetSettlementsByCaseAsync(tenantId, caseId, ct);
            var liensWithReceivedAmount = payments
                .Where(payment => payment.Amount > 0m)
                .Select(payment => payment.LienId)
                .ToHashSet();
            liensWithReceivedAmount.UnionWith(settlements
                .Where(settlement => settlement.Amount > 0m)
                .Select(settlement => settlement.LienId));

            foreach (var lienId in lienIds)
            {
                await lienService.SetLegacyMedicalStatusAsync(
                    tenantId,
                    lienId,
                    userId,
                    request.lienStatus.Trim(),
                    ct);
                await settlementService.CreatePaymentAsync(
                    tenantId,
                    userId,
                    new CreateSettlementPaymentDetailRequest
                    {
                        CaseId = caseId,
                        LienId = lienId,
                        Amount = 0m,
                        PaymentDate = closedDate,
                        Notes = request.note,
                        SettlementType = "other",
                        SettlementStatus = liensWithReceivedAmount.Contains(lienId)
                            ? "Closed"
                            : "4",
                    },
                    ct);
            }

            if (transaction is not null)
                await transaction.CommitAsync(ct);
        }
        catch
        {
            if (transaction is not null)
                await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }

        return Results.Ok(new { isSuccess = true, message = "Lien(s) successfully updated." });
    }

    private static bool TryParseLegacyClosedDate(string? value, out DateOnly closedDate)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            closedDate = default;
            return false;
        }

        return DateOnly.TryParseExact(
                   value.Trim(),
                   ["yyyy-MM-dd", "MM/dd/yyyy", "M/d/yyyy"],
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.None,
                   out closedDate) ||
               DateOnly.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out closedDate);
    }

    private static Guid RequireTenantId(ICurrentRequestContext ctx) =>
        ctx.TenantId ?? throw new UnauthorizedAccessException("Tenant context is required.");

    private static Guid RequireUserId(ICurrentRequestContext ctx) =>
        ctx.UserId ?? throw new UnauthorizedAccessException("User context is required.");

    private static async Task<List<LienResponse>> SearchLiensByCaseAsync(
        ILienService lienService,
        Guid tenantId,
        Guid caseId,
        CancellationToken ct)
    {
        const int pageSize = 200;
        var page = 1;
        var items = new List<LienResponse>();

        while (true)
        {
            var result = await lienService.SearchAsync(tenantId, null, null, null, caseId, null, page, pageSize, ct);
            if (result.Items.Count == 0)
                break;

            items.AddRange(result.Items);
            if (items.Count >= result.TotalCount)
                break;

            page++;
        }

        return items;
    }

    private static async Task<List<object>> BuildSettlementHistoryAsync(
        Guid tenantId,
        Guid caseId,
        ISettlementService settlementService,
        ILienService lienService,
        LiensDbContext db,
        IHttpClientFactory httpClientFactory,
        StringValues authorizationHeader,
        CancellationToken ct)
    {
        var results = new List<object>();
        var reductions = await settlementService.GetReductionsByCaseAsync(tenantId, caseId, ct);
        var settlements = await settlementService.GetSettlementsByCaseAsync(tenantId, caseId, ct);
        var payments = await settlementService.GetPaymentsByCaseAsync(tenantId, caseId, ct);
        var lawFirmChanges = await db.LienCaseNotes.AsNoTracking()
            .Where(item =>
                item.TenantId == tenantId &&
                item.CaseId == caseId &&
                !item.IsDeleted &&
                item.Category == CaseNoteCategory.SettlementHistory)
            .Select(item => new
            {
                item.Id,
                item.Content,
                item.CreatedByUserId,
                item.CreatedByName,
                item.CreatedAtUtc,
            })
            .ToListAsync(ct);
        var liens = await SearchLiensByCaseAsync(lienService, tenantId, caseId, ct);
        var lienNumbersById = liens.ToDictionary(lien => lien.Id, lien => lien.LienNumber);
        var updatedByNames = await ResolveUserNamesAsync(
            reductions.Select(item => item.UpdatedByUserId ?? item.CreatedByUserId)
                .Concat(settlements.Select(item => item.UpdatedByUserId ?? item.CreatedByUserId))
                .Concat(payments.Select(item => item.UpdatedByUserId ?? item.CreatedByUserId))
                .Concat(lawFirmChanges.Select(item => (Guid?)item.CreatedByUserId)),
            httpClientFactory,
            authorizationHeader,
            ct);

        results.AddRange(reductions.Select(item => (object)new
        {
            id = item.Id.ToString(),
            type = "reduction",
            lienId = lienNumbersById.GetValueOrDefault(item.LienId, string.Empty),
            lienCode = lienNumbersById.GetValueOrDefault(item.LienId, string.Empty),
            amount = item.Amount,
            date = item.ReductionDate.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture),
            note = item.Note ?? string.Empty,
            createdAt = item.CreatedAtUtc,
            updatedBy = ResolveUpdatedByName(item.UpdatedByUserId ?? item.CreatedByUserId, updatedByNames),
        }));
        results.AddRange(settlements.Select(item => (object)new
        {
            id = item.Id.ToString(),
            type = "settlement",
            lienId = lienNumbersById.GetValueOrDefault(item.LienId, string.Empty),
            lienCode = lienNumbersById.GetValueOrDefault(item.LienId, string.Empty),
            amount = item.Amount,
            paymentNumber = item.PaymentNumber,
            status = item.Status,
            note = item.Note ?? string.Empty,
            createdAt = item.CreatedAtUtc,
            updatedBy = ResolveUpdatedByName(item.UpdatedByUserId ?? item.CreatedByUserId, updatedByNames),
        }));
        results.AddRange(payments.Select(item => (object)new
        {
            id = item.Id.ToString(),
            type = "payment",
            lienId = lienNumbersById.GetValueOrDefault(item.LienId, string.Empty),
            lienCode = lienNumbersById.GetValueOrDefault(item.LienId, string.Empty),
            amount = item.Amount,
            paymentNumber = item.PaymentNumber,
            payee = item.Payee ?? string.Empty,
            checkNumber = item.CheckNumber ?? string.Empty,
            note = item.Note ?? string.Empty,
            createdAt = item.CreatedAtUtc,
            updatedBy = ResolveUpdatedByName(item.UpdatedByUserId ?? item.CreatedByUserId, updatedByNames),
        }));
        results.AddRange(lawFirmChanges.Select(item =>
        {
            var updatedBy = ResolveUpdatedByName(item.CreatedByUserId, updatedByNames);
            if (string.IsNullOrWhiteSpace(updatedBy))
                updatedBy = item.CreatedByName;

            return (object)new
            {
                id = item.Id.ToString(),
                type = "law-firm-change",
                lienId = string.Empty,
                lienCode = string.Empty,
                amount = 0m,
                description = item.Content,
                note = item.Content,
                date = PacificTimeHelper.FormatTimestamp(item.CreatedAtUtc),
                user = updatedBy,
                createdAt = item.CreatedAtUtc,
                updatedBy,
            };
        }));

        return results.OrderByDescending(item => ((dynamic)item).createdAt).ToList();
    }

    private static async Task<IReadOnlyDictionary<Guid, string>> ResolveUserNamesAsync(
        IEnumerable<Guid?> userIds,
        IHttpClientFactory httpClientFactory,
        StringValues authorizationHeader,
        CancellationToken ct)
    {
        var ids = userIds
            .Where(userId => userId.HasValue)
            .Select(userId => userId!.Value)
            .Distinct()
            .ToHashSet();
        if (ids.Count == 0)
            return new Dictionary<Guid, string>();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/users");
            if (AuthenticationHeaderValue.TryParse(authorizationHeader, out var authorization))
                request.Headers.Authorization = authorization;

            using var response = await httpClientFactory.CreateClient("Identity").SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return new Dictionary<Guid, string>();

            var users = await response.Content.ReadFromJsonAsync<List<IdentityUserResponse>>(cancellationToken: ct)
                ?? [];
            return users
                .Where(user => ids.Contains(user.Id))
                .ToDictionary(
                    user => user.Id,
                    user => string.Join(" ", new[] { user.FirstName, user.LastName }
                        .Where(value => !string.IsNullOrWhiteSpace(value))).Trim());
        }
        catch (HttpRequestException)
        {
            return new Dictionary<Guid, string>();
        }
    }

    private static string ResolveUpdatedByName(
        Guid? userId,
        IReadOnlyDictionary<Guid, string> userNames)
        => !userId.HasValue
            ? string.Empty
            : userNames.TryGetValue(userId.Value, out var userName) && !string.IsNullOrWhiteSpace(userName)
                ? userName
                : userId.Value.ToString();

    private static object MapLegacyServiceCase(CaseResponse item) => new
    {
        caseId = item.Id.ToString(),
        caseCode = item.CaseNumber,
        plaintiffName = item.ClientDisplayName,
        firstName = item.ClientFirstName,
        lastName = item.ClientLastName,
        lawfirm = item.LawFirm ?? string.Empty,
        lawFirmId = item.LawFirmId ?? string.Empty,
        caseManager = item.CaseManager ?? string.Empty,
        caseManagerId = item.CaseManagerId ?? string.Empty,
        status = item.Status,
        dateOfLoss = item.DateOfIncident?.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture) ?? string.Empty,
    };

    private static object MapLegacyServiceCaseV3(
        CaseResponse item,
        LegacyServiceCaseMetrics metrics) => new
    {
        caseId = item.Id.ToString(),
        caseCode = item.CaseNumber,
        plaintiffName = item.ClientDisplayName,
        firstName = item.ClientFirstName,
        lastName = item.ClientLastName,
        lawfirm = item.LawFirm ?? string.Empty,
        lawFirmId = item.LawFirmId ?? string.Empty,
        caseManager = item.CaseManager ?? string.Empty,
        caseManagerId = item.CaseManagerId ?? string.Empty,
        status = item.Status,
        dateOfLoss = item.DateOfIncident?.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture) ?? string.Empty,
        settlementStatus = metrics.SettlementStatus ?? string.Empty,
        settlementDate = metrics.SettlementDate ?? string.Empty,
        settlementAmount = metrics.SettlementAmount,
        billingAmount = metrics.BillingAmount,
        purchaseAmount = metrics.PurchaseAmount,
    };

    private static object MapLegacyServiceLien(LienResponse item) => new
    {
        liensId = item.Id.ToString(),
        lienCode = item.LienNumber,
        caseId = item.CaseId?.ToString() ?? string.Empty,
        status = item.Status,
        amount = item.OriginalAmount.ToString("0.00", CultureInfo.InvariantCulture),
        purchaseAmount = item.PurchasePrice?.ToString("0.00", CultureInfo.InvariantCulture) ?? "0.00",
        currentBalance = item.CurrentBalance?.ToString("0.00", CultureInfo.InvariantCulture) ?? "0.00",
    };

    private static LegacyMedicalAmounts ParseLegacyMedicalAmounts(string? notes)
    {
        var amounts = new LegacyMedicalAmounts();
        if (string.IsNullOrWhiteSpace(notes))
            return amounts;

        foreach (var segment in notes.Split(
                     ';',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = segment.IndexOf('=');
            if (separator <= 0 ||
                !decimal.TryParse(
                    segment[(separator + 1)..].Trim(),
                    NumberStyles.Any,
                    CultureInfo.InvariantCulture,
                    out var amount))
            {
                continue;
            }

            var key = segment[..separator].Trim();
            if (string.Equals(key, "purchaseAmount", StringComparison.Ordinal))
                amounts = amounts with { PurchaseAmount = amount, HasPurchaseAmount = true };
            else if (string.Equals(key, "billingAmount", StringComparison.Ordinal))
                amounts = amounts with { BillingAmount = amount, HasBillingAmount = true };
        }

        return amounts;
    }

    private static decimal ResolveLegacySettlementAmount(decimal amount, string? notes)
    {
        var fields = ParseLegacyNoteFields(notes);
        return fields.TryGetValue("totalSettledAmount", out var legacyAmount) &&
               decimal.TryParse(
                   legacyAmount,
                   NumberStyles.Any,
                   CultureInfo.InvariantCulture,
                   out var parsedLegacyAmount)
            ? parsedLegacyAmount
            : amount;
    }

    private static Dictionary<string, string> ParseSettlementPaymentMetadata(string? notes)
    {
        const string legacyMetadataMarker = "[legacy-meta]";
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(notes))
            return metadata;

        var rawMetadata = notes;
        var markerIndex = notes.IndexOf(legacyMetadataMarker, StringComparison.Ordinal);
        if (markerIndex >= 0)
            rawMetadata = notes[(markerIndex + legacyMetadataMarker.Length)..];

        foreach (var segment in rawMetadata.Split(
                     ';',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = segment.IndexOf('=');
            if (separator <= 0)
                continue;

            var key = segment[..separator].Trim();
            var value = segment[(separator + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(key))
                metadata[key] = value;
        }

        return metadata;
    }

    private static Dictionary<string, string> ParseLegacyNoteFields(string? notes)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(notes))
            return result;

        foreach (var segment in notes.Split("; ", StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = segment.IndexOf('=');
            if (eq > 0)
                result[segment[..eq].Trim()] = segment[(eq + 1)..].Trim();
        }

        return result;
    }

    private static string CsvEscape(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}
