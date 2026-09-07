using System.Data;
using System.Globalization;
using BuildingBlocks.Authorization;
using BuildingBlocks.Authorization.Filters;
using BuildingBlocks.Context;
using Liens.Application.DTOs;
using Liens.Application.Interfaces;
using Liens.Domain;
using Liens.Domain.Entities;
using Liens.Domain.Enums;
using Liens.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Liens.Api.Endpoints;

public static class CasePaymentEndpoints
{
    private const int MaximumPageSize = 100;
    private const string CreateRoute = "/api/liens/cases/{caseId}/payments";
    private const string VoidRoute = "/api/liens/cases/{caseId}/payments/{paymentId}/void";

    public static void MapCasePaymentEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/liens/cases/{caseId:guid}/payments")
            .RequireAuthorization(Policies.AuthenticatedUser)
            .RequireProductAccess(LiensPermissions.ProductCode);

        group.MapGet("/", GetPayments)
            .RequirePermission(LiensPermissions.LienRead);

        group.MapPost("/", RecordPayment)
            .RequirePermission(LiensPermissions.LienSettle);

        group.MapPost("/{paymentId:guid}/void", VoidPayment)
            .RequirePermission(LiensPermissions.LienSettle);
    }

    private static async Task<IResult> GetPayments(
        Guid caseId,
        LiensDbContext db,
        ICurrentRequestContext context,
        CancellationToken ct,
        string? search = null,
        string? paymentMethod = null,
        string? postingStatus = null,
        string sortBy = "paymentDate",
        string sortDirection = "desc",
        int page = 1,
        int pageSize = 10)
    {
        var query = new CasePaymentQuery
        {
            Search = search,
            PaymentMethod = paymentMethod,
            PostingStatus = postingStatus,
            SortBy = sortBy,
            SortDirection = sortDirection,
            Page = page,
            PageSize = pageSize,
        };
        if (query.Page < 1 || query.PageSize < 1 || query.PageSize > MaximumPageSize)
            return ValidationError("page", $"page must be at least 1 and pageSize must be between 1 and {MaximumPageSize}.");

        var tenantId = CaseEndpoints.RequireTenantId(context);
        var caseExists = await db.Cases.AsNoTracking()
            .AnyAsync(item => item.Id == caseId && item.TenantId == tenantId, ct);
        if (!caseExists)
            return Results.NotFound(new { error = new { code = "case_not_found", message = "Case was not found." } });

        var visibleLiens = LienVisibilityPolicy.Apply(
            db.Liens.AsNoTracking().Where(lien => lien.TenantId == tenantId && lien.CaseId == caseId),
            LienVisibilityPolicy.Resolve(context));
        var lienSummaries = await visibleLiens
            .Select(lien => new
            {
                lien.Id,
                lien.LienNumber,
                SellingAmount = lien.PurchasePrice ?? lien.AskAmount ?? lien.OriginalAmount,
                lien.ReceivableDueDate,
            })
            .ToListAsync(ct);

        var visibleLienIds = lienSummaries.Select(item => item.Id).ToList();
        var lienNumbers = lienSummaries.ToDictionary(item => item.Id, item => item.LienNumber);
        var payments = db.SettlementPaymentDetails.AsNoTracking()
            .Where(payment => payment.TenantId == tenantId &&
                              payment.CaseId == caseId &&
                              !payment.IsDeleted &&
                              visibleLienIds.Contains(payment.LienId));

        var postedTotal = await payments
            .Where(payment => payment.PostingStatus == SettlementPaymentDetail.PostedStatus)
            .Select(payment => (decimal?)payment.Amount)
            .SumAsync(ct) ?? 0m;
        var sellingAmount = lienSummaries.Sum(item => item.SellingAmount);
        var dueDate = lienSummaries
            .Where(item => item.ReceivableDueDate.HasValue)
            .Select(item => item.ReceivableDueDate!.Value)
            .OrderBy(value => value)
            .Cast<DateOnly?>()
            .FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(query.PaymentMethod))
        {
            var method = query.PaymentMethod.Trim();
            payments = payments.Where(payment => payment.PaymentMethod == method);
        }

        if (!string.IsNullOrWhiteSpace(query.PostingStatus))
        {
            var status = query.PostingStatus.Trim();
            payments = payments.Where(payment => payment.PostingStatus == status);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var searchTerm = query.Search.Trim();
            var pattern = $"%{searchTerm}%";
            var matchingLienIds = lienSummaries
                .Where(item => item.LienNumber.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Id)
                .ToList();
            payments = payments.Where(payment =>
                matchingLienIds.Contains(payment.LienId) ||
                (payment.PaymentMethod != null && EF.Functions.Like(payment.PaymentMethod, pattern)) ||
                (payment.CheckNumber != null && EF.Functions.Like(payment.CheckNumber, pattern)) ||
                (payment.DetailsContext != null && EF.Functions.Like(payment.DetailsContext, pattern)) ||
                (payment.Note != null && EF.Functions.Like(payment.Note, pattern)));
        }

        var totalCount = await payments.CountAsync(ct);
        payments = ApplySort(payments, query.SortBy, query.SortDirection);
        var rows = await payments
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(ct);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var response = new CasePaymentListResponse
        {
            Summary = new CasePaymentSummary
            {
                LienSellingAmount = sellingAmount,
                TotalPaid = postedTotal,
                RemainingBalance = Math.Max(0m, sellingAmount - postedTotal),
                OverpaidAmount = Math.Max(0m, postedTotal - sellingAmount),
                LienAgingDays = dueDate.HasValue ? Math.Max(0, today.DayNumber - dueDate.Value.DayNumber) : null,
            },
            Items = rows.Select(payment => MapPayment(payment, lienNumbers.GetValueOrDefault(payment.LienId, string.Empty))).ToList(),
            Page = query.Page,
            PageSize = query.PageSize,
            TotalCount = totalCount,
        };
        return Results.Ok(response);
    }

    private static async Task<IResult> RecordPayment(
        Guid caseId,
        RecordCasePaymentRequest request,
        LiensDbContext db,
        IAuditPublisher audit,
        ICurrentRequestContext context,
        HttpRequest httpRequest,
        CancellationToken ct)
    {
        if (!SellingIdempotency.TryGetKey(httpRequest, out var idempotencyKey, out var keyError))
            return keyError!;

        var validation = ValidateRecordRequest(request);
        if (validation is not null)
            return validation;

        var tenantId = CaseEndpoints.RequireTenantId(context);
        var userId = CaseEndpoints.RequireUserId(context);
        var replay = await SellingIdempotency.GetReplayAsync(
            db, tenantId, "user", userId, CreateRoute, "case-payment", caseId.ToString(), idempotencyKey!, request, ct);
        if (replay is not null)
            return replay;

        var caseExists = await db.Cases.AnyAsync(item => item.Id == caseId && item.TenantId == tenantId, ct);
        if (!caseExists)
            return Results.NotFound(new { error = new { code = "case_not_found", message = "Case was not found." } });

        var allocationIds = request.Allocations.Select(item => item.LienId).Distinct().ToList();
        var visibleLiens = await LienVisibilityPolicy.Apply(
                db.Liens.Where(lien => lien.TenantId == tenantId && lien.CaseId == caseId && allocationIds.Contains(lien.Id)),
                LienVisibilityPolicy.Resolve(context))
            .ToListAsync(ct);
        if (visibleLiens.Count != allocationIds.Count)
            return ValidationError("allocations", "Every allocation must reference a visible lien belonging to this case.");

        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
            : null;
        var previouslyPaid = await db.SettlementPaymentDetails.AsNoTracking()
            .Where(payment => payment.TenantId == tenantId &&
                              allocationIds.Contains(payment.LienId) &&
                              !payment.IsDeleted &&
                              payment.PostingStatus == SettlementPaymentDetail.PostedStatus)
            .GroupBy(payment => payment.LienId)
            .Select(group => new { LienId = group.Key, Amount = group.Sum(payment => payment.Amount) })
            .ToDictionaryAsync(item => item.LienId, item => item.Amount, ct);

        var availableBalance = visibleLiens.Sum(lien =>
        {
            var basis = lien.PurchasePrice ?? lien.AskAmount ?? lien.OriginalAmount;
            return Math.Max(0m, basis - previouslyPaid.GetValueOrDefault(lien.Id));
        });
        if (request.Amount > availableBalance)
        {
            if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
            var message = $"Payment amount cannot exceed the available balance of {availableBalance.ToString("C2", CultureInfo.GetCultureInfo("en-US"))}.";
            return ValidationError("amount", message, message);
        }

        foreach (var allocation in request.Allocations)
        {
            var lien = visibleLiens.Single(item => item.Id == allocation.LienId);
            var basis = lien.PurchasePrice ?? lien.AskAmount ?? lien.OriginalAmount;
            var outstanding = Math.Max(0m, basis - previouslyPaid.GetValueOrDefault(lien.Id));
            if (allocation.Amount > outstanding)
            {
                if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
                return ValidationError("allocations", $"Allocation for lien {lien.LienNumber} exceeds its outstanding balance of {outstanding:0.00}.");
            }
        }

        using var auditBuffer = audit.BeginBuffer();
        try
        {
            var start = await SellingIdempotency.TryBeginAsync(
                db, tenantId, "user", userId, CreateRoute, "case-payment", caseId.ToString(), idempotencyKey!, request, ct);
            if (start.Result is not null)
            {
                if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
                return start.Result;
            }

            var paymentNumber = (await db.SettlementPaymentDetails
                .Where(payment => payment.TenantId == tenantId && payment.CaseId == caseId)
                .MaxAsync(payment => (int?)payment.PaymentNumber, ct) ?? 0) + 1;
            var receiptId = Guid.CreateVersion7();
            var entities = new List<SettlementPaymentDetail>();
            foreach (var allocation in request.Allocations)
            {
                var entity = SettlementPaymentDetail.Create(
                    tenantId,
                    caseId,
                    allocation.LienId,
                    paymentNumber,
                    allocation.Amount,
                    userId,
                    request.PaymentDate,
                    checkNumber: request.ReferenceNumber,
                    note: request.Notes,
                    receiptId: receiptId,
                    paymentMethod: request.PaymentMethod,
                    settlementType: request.SettlementType,
                    settlementStatus: request.SettlementStatus,
                    detailsContext: request.DetailsContext);
                entities.Add(entity);
            }
            db.SettlementPaymentDetails.AddRange(entities);

            if (!string.IsNullOrWhiteSpace(request.LienStatus))
            {
                foreach (var lien in visibleLiens)
                {
                    var previousStatus = lien.Status;
                    lien.SetLegacyMedicalStatus(request.LienStatus, userId);
                    if (!string.Equals(previousStatus, lien.Status, StringComparison.Ordinal))
                    {
                        db.LienStatusHistories.Add(LienStatusHistory.Create(
                            tenantId, lien.Id, caseId, $"Status changed from {previousStatus} to {lien.Status} while recording payment.", userId));
                    }
                }
            }

            await db.SaveChangesAsync(ct);
            var lienNumbers = visibleLiens.ToDictionary(item => item.Id, item => item.LienNumber);
            var response = new RecordCasePaymentResponse
            {
                ReceiptId = receiptId,
                PaymentNumber = paymentNumber,
                Amount = request.Amount,
                Allocations = entities.Select(entity => MapPayment(entity, lienNumbers[entity.LienId])).ToList(),
            };
            audit.Publish(
                "lien.payment.recorded",
                "record",
                $"Recorded payment {paymentNumber} with {entities.Count} allocation(s).",
                tenantId,
                userId,
                "CasePaymentReceipt",
                receiptId.ToString(),
                metadata: $"caseId={caseId};amount={request.Amount.ToString(CultureInfo.InvariantCulture)}");
            var result = await SellingIdempotency.CompleteAsync(db, start.Record!, userId, StatusCodes.Status201Created, response, ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
            auditBuffer.Commit();
            return result;
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task<IResult> VoidPayment(
        Guid caseId,
        Guid paymentId,
        VoidCasePaymentRequest request,
        LiensDbContext db,
        IAuditPublisher audit,
        ICurrentRequestContext context,
        HttpRequest httpRequest,
        CancellationToken ct)
    {
        if (!SellingIdempotency.TryGetKey(httpRequest, out var idempotencyKey, out var keyError))
            return keyError!;
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 500)
            return ValidationError("reason", "reason is required and cannot exceed 500 characters.");

        var tenantId = CaseEndpoints.RequireTenantId(context);
        var userId = CaseEndpoints.RequireUserId(context);
        var resourceKey = $"{caseId}:{paymentId}";
        var replay = await SellingIdempotency.GetReplayAsync(
            db, tenantId, "user", userId, VoidRoute, "case-payment-void", resourceKey, idempotencyKey!, request, ct);
        if (replay is not null)
            return replay;

        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
            : null;
        var selected = await db.SettlementPaymentDetails
            .FirstOrDefaultAsync(payment => payment.Id == paymentId && payment.TenantId == tenantId && payment.CaseId == caseId && !payment.IsDeleted, ct);
        if (selected is null)
        {
            if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
            return Results.NotFound(new { error = new { code = "payment_not_found", message = "Payment was not found." } });
        }

        var canSeeLien = await LienVisibilityPolicy.Apply(
                db.Liens.AsNoTracking().Where(lien => lien.Id == selected.LienId && lien.TenantId == tenantId && lien.CaseId == caseId),
                LienVisibilityPolicy.Resolve(context))
            .AnyAsync(ct);
        if (!canSeeLien)
        {
            if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
            return Results.NotFound(new { error = new { code = "payment_not_found", message = "Payment was not found." } });
        }

        var receiptPayments = selected.ReceiptId.HasValue
            ? await db.SettlementPaymentDetails.Where(payment =>
                payment.TenantId == tenantId && payment.CaseId == caseId &&
                payment.ReceiptId == selected.ReceiptId && !payment.IsDeleted).ToListAsync(ct)
            : [selected];
        if (receiptPayments.Any(payment => payment.PostingStatus == SettlementPaymentDetail.VoidedStatus))
        {
            if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
            return Results.Conflict(new { error = new { code = "payment_already_voided", message = "Payment is already voided." } });
        }

        using var auditBuffer = audit.BeginBuffer();
        try
        {
            var start = await SellingIdempotency.TryBeginAsync(
                db, tenantId, "user", userId, VoidRoute, "case-payment-void", resourceKey, idempotencyKey!, request, ct);
            if (start.Result is not null)
            {
                if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
                return start.Result;
            }

            foreach (var payment in receiptPayments)
                payment.Void(userId, request.Reason);
            await db.SaveChangesAsync(ct);

            var response = new
            {
                receiptId = selected.ReceiptId,
                paymentId,
                voidedAllocations = receiptPayments.Count,
                postingStatus = SettlementPaymentDetail.VoidedStatus,
            };
            audit.Publish(
                "lien.payment.voided",
                "void",
                $"Voided payment {selected.PaymentNumber} with {receiptPayments.Count} allocation(s).",
                tenantId,
                userId,
                "CasePaymentReceipt",
                selected.ReceiptId?.ToString() ?? selected.Id.ToString(),
                metadata: $"caseId={caseId}");
            var result = await SellingIdempotency.CompleteAsync(db, start.Record!, userId, StatusCodes.Status200OK, response, ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
            auditBuffer.Commit();
            return result;
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static IResult? ValidateRecordRequest(RecordCasePaymentRequest request)
    {
        if (request.Amount <= 0m)
            return ValidationError("amount", "amount must be greater than zero.");
        if (string.IsNullOrWhiteSpace(request.PaymentMethod) || request.PaymentMethod.Trim().Length > 50)
            return ValidationError("paymentMethod", "paymentMethod is required and cannot exceed 50 characters.");
        if (string.IsNullOrWhiteSpace(request.ReferenceNumber) || request.ReferenceNumber.Trim().Length > 100)
            return ValidationError("referenceNumber", "referenceNumber is required and cannot exceed 100 characters.");
        if (request.DetailsContext?.Trim().Length > 300)
            return ValidationError("detailsContext", "detailsContext cannot exceed 300 characters.");
        if (request.Notes?.Trim().Length > 1000)
            return ValidationError("notes", "notes cannot exceed 1000 characters.");
        if (request.SettlementType?.Trim().Length > 80 || request.SettlementStatus?.Trim().Length > 80)
            return ValidationError("settlement", "settlementType and settlementStatus cannot exceed 80 characters.");
        if (!string.IsNullOrWhiteSpace(request.LienStatus) &&
            !string.Equals(request.LienStatus.Trim(), "Open", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(request.LienStatus.Trim(), "Closed", StringComparison.OrdinalIgnoreCase) &&
            !LienStatus.All.Contains(request.LienStatus.Trim()))
            return ValidationError("lienStatus", "lienStatus must be Open, Closed, or a canonical lien status.");
        if (request.Allocations.Count == 0 || request.Allocations.Count > 100)
            return ValidationError("allocations", "Between 1 and 100 allocations are required.");
        if (request.Allocations.Any(item => item.LienId == Guid.Empty || item.Amount <= 0m))
            return ValidationError("allocations", "Each allocation requires a lienId and an amount greater than zero.");
        if (request.Allocations.Select(item => item.LienId).Distinct().Count() != request.Allocations.Count)
            return ValidationError("allocations", "Each lien may be allocated only once per payment.");
        if (request.Allocations.Sum(item => item.Amount) != request.Amount)
            return ValidationError("allocations", "Allocation amounts must equal the payment amount.");
        return null;
    }

    private static IQueryable<SettlementPaymentDetail> ApplySort(
        IQueryable<SettlementPaymentDetail> query,
        string sortBy,
        string sortDirection)
    {
        var descending = !string.Equals(sortDirection, "asc", StringComparison.OrdinalIgnoreCase);
        return (sortBy.Trim().ToLowerInvariant(), descending) switch
        {
            ("amount", false) => query.OrderBy(item => item.Amount).ThenBy(item => item.Id),
            ("amount", true) => query.OrderByDescending(item => item.Amount).ThenByDescending(item => item.Id),
            ("paymentmethod", false) => query.OrderBy(item => item.PaymentMethod).ThenBy(item => item.Id),
            ("paymentmethod", true) => query.OrderByDescending(item => item.PaymentMethod).ThenByDescending(item => item.Id),
            ("paymentdate", false) => query.OrderBy(item => item.PaymentDate).ThenBy(item => item.CreatedAtUtc).ThenBy(item => item.Id),
            _ => query.OrderByDescending(item => item.PaymentDate).ThenByDescending(item => item.CreatedAtUtc).ThenByDescending(item => item.Id),
        };
    }

    private static CasePaymentItemResponse MapPayment(SettlementPaymentDetail payment, string lienNumber) => new()
    {
        Id = payment.Id,
        ReceiptId = payment.ReceiptId,
        LienId = payment.LienId,
        LienNumber = lienNumber,
        PaymentNumber = payment.PaymentNumber,
        PaymentDate = payment.PaymentDate,
        PaymentMethod = payment.PaymentMethod ?? "Other",
        ReferenceNumber = payment.CheckNumber,
        Amount = payment.Amount,
        DetailsContext = payment.DetailsContext,
        Notes = payment.Note,
        SettlementType = payment.SettlementType,
        SettlementStatus = payment.SettlementStatus,
        PostingStatus = payment.PostingStatus,
        CreatedAtUtc = payment.CreatedAtUtc,
        UpdatedAtUtc = payment.UpdatedAtUtc,
    };

    private static IResult ValidationError(string field, string message, string? responseMessage = null) => Results.BadRequest(new
    {
        error = new
        {
            code = "validation_failed",
            message = responseMessage ?? "One or more payment fields are invalid.",
            fields = new Dictionary<string, string[]> { [field] = [message] },
        },
    });
}
