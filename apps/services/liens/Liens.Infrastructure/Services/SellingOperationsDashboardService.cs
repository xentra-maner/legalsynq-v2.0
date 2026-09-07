using BuildingBlocks.Exceptions;
using Liens.Application.DTOs;
using Liens.Application.Interfaces;
using Liens.Domain.Entities;
using Liens.Domain.Enums;
using Liens.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Liens.Infrastructure.Services;

public sealed class SellingOperationsDashboardService : ISellingOperationsDashboardService
{
    private readonly LiensDbContext _db;
    private readonly TimeProvider _timeProvider;

    private static readonly string[] MonthlyAgingBuckets =
    [
        "1-30",
        "31-60",
        "61-90",
        "91-120",
        "120+",
    ];

    public SellingOperationsDashboardService(LiensDbContext db, TimeProvider timeProvider)
    {
        _db = db;
        _timeProvider = timeProvider;
    }

    public async Task<SellingOperationsDashboardResponse> GetAsync(
        Guid tenantId,
        Guid sellerOrgId,
        SellingOperationsDashboardQuery query,
        CancellationToken ct = default)
    {
        var generatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var period = ResolvePeriod(query, generatedAtUtc);
        DashboardPeriod? comparisonPeriod = string.Equals(query.Compare, "previousPeriod", StringComparison.Ordinal)
            ? PreviousPeriod(period)
            : null;

        var scopedLiens = ScopedLiens(tenantId, sellerOrgId);
        var financials = await GetFinancialsAsync(scopedLiens, ct);
        var payments = await GetPaymentsAsync(scopedLiens, tenantId, ct);

        var lienStatuses = await GetOperationalStatusesAsync(scopedLiens, period, ct);
        var sellerStatuses = await GetSellerStatusesAsync(scopedLiens, period, ct);
        var timeSeries = await GetTimeSeriesAsync(scopedLiens, period.DateTo.Year, ct);
        var topBuyers = await GetTopBuyersAsync(scopedLiens, tenantId, sellerOrgId, ct);
        var acceptedAging = await GetAcceptedBuyerAgingAsync(tenantId, sellerOrgId, period.DateTo, ct);
        var comparisonAcceptedAging = comparisonPeriod.HasValue
            ? await GetAcceptedBuyerAgingAsync(tenantId, sellerOrgId, comparisonPeriod.Value.DateTo, ct)
            : null;

        return new SellingOperationsDashboardResponse
        {
            Period = ToResponse(period),
            ComparisonPeriod = comparisonPeriod.HasValue ? ToResponse(comparisonPeriod.Value) : null,
            Currency = "USD",
            Metrics = new SellingOperationsDashboardMetrics
            {
                TotalLienRevenue = AvailableMetric(
                    financials.LienRevenue,
                    null,
                    "Sum of PurchasePrice for seller-scoped liens with completed sale evidence."),
                TotalOutstanding = AvailableMetric(
                    financials.Outstanding,
                    null,
                    "Sum of CurrentBalance, falling back to OriginalAmount when CurrentBalance is null, for all seller-scoped liens."),
                PastAmountDue = AvailableMetric(
                    acceptedAging.PastDueAmount,
                    comparisonAcceptedAging?.PastDueAmount,
                    "Sum of accepted buyer-response amounts aged 31 days or more as of the dashboard period end date."),
                Payments = AvailableMetric(
                    payments,
                    null,
                    "Sum of all non-deleted, non-voided SettlementPaymentDetail.Amount values for seller-scoped liens."),
            },
            ArAging = acceptedAging.ArAging,
            LienStatuses = lienStatuses,
            SellerStatuses = sellerStatuses,
            TimeSeries = timeSeries,
            TopBuyers = topBuyers,
            BuyerAging = acceptedAging.BuyerAging,
            GeneratedAtUtc = generatedAtUtc,
        };
    }

    private async Task<AcceptedBuyerAgingResult> GetAcceptedBuyerAgingAsync(
        Guid tenantId,
        Guid sellerOrgId,
        DateOnly asOfDate,
        CancellationToken ct)
    {
        var asOfStartUtc = asOfDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var asOfEndUtc = asOfDate == DateOnly.MaxValue
            ? DateTime.MaxValue
            : asOfStartUtc.AddDays(1);
        var acceptedLinks = _db.SellingBuyerAccessLinks.AsNoTracking()
            .Where(link =>
                link.TenantId == tenantId &&
                link.SellerOrgId == sellerOrgId &&
                link.Purpose == SellingAccessLinkPurposes.ConfirmSaleBuyerResponse &&
                link.ResponseStatus == SellingBuyerResponseStatus.Accepted &&
                link.RespondedAtUtc.HasValue &&
                link.RespondedAtUtc.Value < asOfEndUtc);
        var firstAcceptanceTimes = acceptedLinks
            .GroupBy(link => link.LienId)
            .Select(group => new
            {
                LienId = group.Key,
                BuyerAcceptedAtUtc = group.Min(link => link.RespondedAtUtc),
            });
        var firstAcceptanceCandidates = await (
            from link in acceptedLinks
            join first in firstAcceptanceTimes
                on new { link.LienId, link.RespondedAtUtc }
                equals new { first.LienId, RespondedAtUtc = first.BuyerAcceptedAtUtc }
            join lien in _db.Liens.AsNoTracking()
                on new { TenantId = tenantId, link.LienId }
                equals new { lien.TenantId, LienId = lien.Id }
            select new
            {
                link.Id,
                link.LienId,
                link.BuyerOrgId,
                link.BuyerCompanyId,
                link.BuyerContactId,
                BuyerAcceptedAtUtc = link.RespondedAtUtc!.Value,
                Amount = link.ResponseAmount ?? 0m,
            }).ToListAsync(ct);

        var rows = firstAcceptanceCandidates
            .GroupBy(link => link.LienId)
            .Select(group => group
                .OrderByDescending(link => link.Amount)
                .ThenBy(link => link.Id)
                .First())
            .Select(link => new AcceptedBuyerAgingRow(
                link.BuyerOrgId,
                link.BuyerCompanyId,
                link.BuyerContactId,
                link.BuyerAcceptedAtUtc,
                link.Amount,
                AgingBucket(asOfDate, link.BuyerAcceptedAtUtc)))
            .ToList();

        var companyIds = rows
            .Where(row => row.BuyerCompanyId.HasValue)
            .Select(row => row.BuyerCompanyId!.Value)
            .Distinct()
            .ToList();
        var contactIds = rows
            .Where(row => !row.BuyerCompanyId.HasValue)
            .Select(row => row.BuyerContactId)
            .Distinct()
            .ToList();
        var companyNames = companyIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _db.Companies.AsNoTracking()
                .Where(company => company.TenantId == tenantId && companyIds.Contains(company.Id))
                .ToDictionaryAsync(company => company.Id, company => company.Name, ct);
        var contactNames = contactIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _db.Contacts.AsNoTracking()
                .Where(contact => contact.TenantId == tenantId && contactIds.Contains(contact.Id))
                .ToDictionaryAsync(
                    contact => contact.Id,
                    contact => contact.Organization ?? contact.DisplayName,
                    ct);

        var arBuckets = BuildAgingBuckets(rows);
        var buyerItems = rows
            .GroupBy(row => row.BuyerOrgId)
            .Select(group =>
            {
                var total = group.Sum(row => row.Amount);
                var selectedIdentity = group
                    .OrderByDescending(row => row.BuyerCompanyId.HasValue)
                    .ThenBy(row => row.BuyerCompanyId)
                    .ThenBy(row => row.BuyerContactId)
                    .First();
                var buyerName = selectedIdentity.BuyerCompanyId is Guid companyId
                    ? companyNames.GetValueOrDefault(companyId)
                    : contactNames.GetValueOrDefault(selectedIdentity.BuyerContactId);
                var pastDueAmount = group
                    .Where(row => row.Bucket is "31-60" or "61-90" or "91-120" or "120+")
                    .Sum(row => row.Amount);

                return new SellingOperationsBuyerAgingItem
                {
                    BuyerOrgId = group.Key,
                    BuyerCompanyId = selectedIdentity.BuyerCompanyId,
                    BuyerName = buyerName ?? group.Key.ToString(),
                    Total = total,
                    PastDuePercent = total <= 0m
                        ? 0m
                        : decimal.Round(pastDueAmount * 100m / total, 2),
                    Buckets = BuildAgingBuckets(group),
                };
            })
            .OrderByDescending(item => item.Total)
            .ThenBy(item => item.BuyerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.BuyerOrgId)
            .ToList();

        return new AcceptedBuyerAgingResult(
            new SellingOperationsArAgingResponse
            {
                IsAvailable = true,
                Total = rows.Sum(row => row.Amount),
                Buckets = arBuckets,
            },
            new SellingOperationsBuyerAgingResponse
            {
                IsAvailable = true,
                Items = buyerItems,
            },
            rows.Where(row => row.Bucket is "31-60" or "61-90" or "91-120" or "120+")
                .Sum(row => row.Amount));
    }

    private static List<SellingOperationsAgingBucket> BuildAgingBuckets(
        IEnumerable<AcceptedBuyerAgingRow> rows)
    {
        var aggregates = rows
            .GroupBy(row => row.Bucket)
            .ToDictionary(
                group => group.Key,
                group => new { Amount = group.Sum(row => row.Amount), Count = group.Count() },
                StringComparer.Ordinal);

        return MonthlyAgingBuckets.Select(bucket =>
        {
            var aggregate = aggregates.GetValueOrDefault(bucket);
            return new SellingOperationsAgingBucket
            {
                Bucket = bucket,
                Amount = aggregate?.Amount ?? 0m,
                LienCount = aggregate?.Count ?? 0,
            };
        }).ToList();
    }

    private static string AgingBucket(DateOnly asOfDate, DateTime acceptedAtUtc)
    {
        var acceptedDate = DateOnly.FromDateTime(acceptedAtUtc);
        var agingDays = asOfDate.DayNumber - acceptedDate.DayNumber + 1;
        return agingDays switch
        {
            <= 30 => "1-30",
            <= 60 => "31-60",
            <= 90 => "61-90",
            <= 120 => "91-120",
            _ => "120+",
        };
    }

    private IQueryable<Lien> ScopedLiens(Guid tenantId, Guid sellerOrgId)
    {
        return _db.Liens.AsNoTracking()
            .Where(l => l.TenantId == tenantId
                && (l.SellingOrgId == sellerOrgId
                    || (!l.SellingOrgId.HasValue && l.OrgId == sellerOrgId))
                && l.ArchivedAtUtc == null
                && (l.SellerStatus == null || l.SellerStatus != SellingLienStatus.Archived));
    }

    private static IQueryable<Lien> InPeriod(IQueryable<Lien> query, DashboardPeriod period)
    {
        return query.Where(l => l.InitialServiceDate.HasValue
            && l.InitialServiceDate.Value >= period.DateFrom
            && l.InitialServiceDate.Value <= period.DateTo);
    }

    private static async Task<FinancialAggregate> GetFinancialsAsync(
        IQueryable<Lien> scopedLiens,
        CancellationToken ct)
    {
        var row = await scopedLiens
            .GroupBy(_ => 1)
            .Select(group => new
            {
                LienRevenue = group
                    .Where(l =>
                        (l.SellerStatus == SellingLienStatus.Sold ||
                         l.Status == LienStatus.Sold ||
                         l.Status == LienStatus.Active ||
                         l.Status == LienStatus.Settled ||
                         l.Status == LienStatus.Disputed) &&
                        l.SoldAtUtc != null &&
                        l.PurchasePrice.HasValue)
                    .Sum(l => l.PurchasePrice!.Value),
                Outstanding = group.Sum(l => l.CurrentBalance ?? l.OriginalAmount),
            })
            .FirstOrDefaultAsync(ct);

        return row is null
            ? new FinancialAggregate(0m, 0m)
            : new FinancialAggregate(row.LienRevenue, row.Outstanding);
    }

    private async Task<decimal> GetPaymentsAsync(
        IQueryable<Lien> scopedLiens,
        Guid tenantId,
        CancellationToken ct)
    {
        var row = await _db.SettlementPaymentDetails.AsNoTracking()
            .Where(payment => payment.TenantId == tenantId
                && !payment.IsDeleted
                && payment.PostingStatus != SettlementPaymentDetail.VoidedStatus
                && payment.PaymentDate.HasValue)
            .Join(scopedLiens, payment => payment.LienId, lien => lien.Id, (payment, _) => payment)
            .GroupBy(_ => 1)
            .Select(group => new { Amount = group.Sum(payment => payment.Amount) })
            .FirstOrDefaultAsync(ct);

        return row?.Amount ?? 0m;
    }

    private static async Task<List<SellingOperationsStatusItem>> GetOperationalStatusesAsync(
        IQueryable<Lien> scopedLiens,
        DashboardPeriod period,
        CancellationToken ct)
    {
        var groups = await InPeriod(scopedLiens, period)
            .GroupBy(l => l.Status)
            .Select(group => new
            {
                Status = group.Key,
                LienCount = group.Count(),
                OriginalAmount = group.Sum(l => l.OriginalAmount),
                OutstandingAmount = group.Sum(l => l.CurrentBalance ?? l.OriginalAmount),
            })
            .OrderBy(group => group.Status)
            .ToListAsync(ct);
        var totalCount = groups.Sum(item => item.LienCount);

        return groups.Select(item => ToStatusItem(
            item.Status,
            item.LienCount,
            item.OriginalAmount,
            item.OutstandingAmount,
            totalCount)).ToList();
    }

    private static async Task<List<SellingOperationsStatusItem>> GetSellerStatusesAsync(
        IQueryable<Lien> scopedLiens,
        DashboardPeriod period,
        CancellationToken ct)
    {
        var groups = await InPeriod(scopedLiens, period)
            .GroupBy(l => new
            {
                l.SellerStatus,
                l.Status,
                HasSoldAt = l.SoldAtUtc != null,
                HasPurchasePrice = l.PurchasePrice != null,
            })
            .Select(group => new
            {
                group.Key.SellerStatus,
                LienStatus = group.Key.Status,
                group.Key.HasSoldAt,
                group.Key.HasPurchasePrice,
                LienCount = group.Count(),
                OriginalAmount = group.Sum(l => l.OriginalAmount),
                OutstandingAmount = group.Sum(l => l.CurrentBalance ?? l.OriginalAmount),
            })
            .ToListAsync(ct);

        var combined = groups
            .GroupBy(group => EffectiveSellerStatus(
                group.SellerStatus,
                group.LienStatus,
                group.HasSoldAt,
                group.HasPurchasePrice))
            .Select(group => new
            {
                Status = group.Key,
                LienCount = group.Sum(item => item.LienCount),
                OriginalAmount = group.Sum(item => item.OriginalAmount),
                OutstandingAmount = group.Sum(item => item.OutstandingAmount),
            })
            .OrderBy(group => StatusOrder(group.Status))
            .ThenBy(group => group.Status, StringComparer.Ordinal)
            .ToList();
        var totalCount = combined.Sum(item => item.LienCount);

        return combined.Select(item => ToStatusItem(
            item.Status,
            item.LienCount,
            item.OriginalAmount,
            item.OutstandingAmount,
            totalCount)).ToList();
    }

    private static async Task<List<SellingOperationsTimeseriesPoint>> GetTimeSeriesAsync(
        IQueryable<Lien> scopedLiens,
        int year,
        CancellationToken ct)
    {
        var yearStart = new DateOnly(year, 1, 1);
        var yearEnd = new DateOnly(year, 12, 31);
        var dailyGroups = await scopedLiens
            .Where(l => l.InitialServiceDate.HasValue &&
                l.InitialServiceDate.Value >= yearStart &&
                l.InitialServiceDate.Value <= yearEnd)
            .GroupBy(l => l.InitialServiceDate!.Value)
            .Select(group => new
            {
                Date = group.Key,
                LienCount = group.Count(),
                LienRevenue = group.Sum(l => l.OriginalAmount),
                OutstandingAmount = group.Sum(l => l.CurrentBalance ?? l.OriginalAmount),
            })
            .ToListAsync(ct);

        var monthlyGroups = dailyGroups
            .GroupBy(item => new DateOnly(item.Date.Year, item.Date.Month, 1))
            .ToDictionary(
                group => group.Key,
                group => new
                {
                    LienCount = group.Sum(item => item.LienCount),
                    LienRevenue = group.Sum(item => item.LienRevenue),
                    OutstandingAmount = group.Sum(item => item.OutstandingAmount),
                });

        return Enumerable.Range(1, 12)
            .Select(month =>
            {
                var bucketStart = new DateOnly(year, month, 1);
                var aggregate = monthlyGroups.GetValueOrDefault(bucketStart);
                return new SellingOperationsTimeseriesPoint
                {
                    BucketStart = bucketStart,
                    Grain = "month",
                    LienCount = aggregate?.LienCount ?? 0,
                    LienRevenue = aggregate?.LienRevenue ?? 0m,
                    OutstandingAmount = aggregate?.OutstandingAmount ?? 0m,
                };
            })
            .ToList();
    }

    private async Task<List<SellingOperationsTopBuyerItem>> GetTopBuyersAsync(
        IQueryable<Lien> scopedLiens,
        Guid tenantId,
        Guid sellerOrgId,
        CancellationToken ct)
    {
        // Top buyers are limited to funders that accepted a lien offer from this seller;
        // buyers whose holdings never went through an accepted offer are excluded.
        var acceptedBuyerOrgIds = _db.LienOffers.AsNoTracking()
            .Where(offer => offer.TenantId == tenantId
                && offer.SellerOrgId == sellerOrgId
                && offer.Status == OfferStatus.Accepted)
            .Select(offer => offer.BuyerOrgId);
        var buyerBalanceLiens = scopedLiens
            .Where(l => l.BuyingOrgId.HasValue
                && acceptedBuyerOrgIds.Contains(l.BuyingOrgId.Value)
                && l.Status == LienStatus.Active
                && (l.CurrentBalance ?? l.OriginalAmount) > 0m);
        var totalBalanceRow = await buyerBalanceLiens
            .GroupBy(_ => 1)
            .Select(group => new { TotalBalance = group.Sum(l => l.CurrentBalance ?? l.OriginalAmount) })
            .FirstOrDefaultAsync(ct);
        var buyers = await buyerBalanceLiens
            .GroupBy(l => l.BuyingOrgId!.Value)
            .Select(group => new
            {
                BuyerOrgId = group.Key,
                ActiveLienCount = group.Count(),
                TotalBalance = group.Sum(l => l.CurrentBalance ?? l.OriginalAmount),
            })
            .OrderByDescending(item => item.TotalBalance)
            .ThenBy(item => item.BuyerOrgId)
            .Take(5)
            .ToListAsync(ct);

        if (buyers.Count == 0)
            return [];

        var buyerOrgIds = buyers.Select(item => item.BuyerOrgId).ToList();
        var completedPurchases = await scopedLiens
            .Where(l => l.BuyingOrgId.HasValue
                && buyerOrgIds.Contains(l.BuyingOrgId.Value)
                && (l.SellerStatus == SellingLienStatus.Sold
                    || l.Status == LienStatus.Sold
                    || l.Status == LienStatus.Active
                    || l.Status == LienStatus.Settled
                    || l.Status == LienStatus.Disputed)
                && l.SoldAtUtc != null
                && l.PurchasePrice.HasValue)
            .GroupBy(l => l.BuyingOrgId!.Value)
            .Select(group => new
            {
                BuyerOrgId = group.Key,
                Amount = group.Sum(l => l.PurchasePrice!.Value),
            })
            .ToDictionaryAsync(item => item.BuyerOrgId, item => item.Amount, ct);
        var companyLinks = await _db.LienOffers.AsNoTracking()
            .Where(offer => offer.TenantId == tenantId
                && offer.SellerOrgId == sellerOrgId
                && buyerOrgIds.Contains(offer.BuyerOrgId)
                && offer.BuyerCompanyId.HasValue
                && offer.Status == OfferStatus.Accepted)
            .Join(
                buyerBalanceLiens,
                offer => offer.LienId,
                lien => lien.Id,
                (offer, _) => new
                {
                    offer.Id,
                    offer.BuyerOrgId,
                    BuyerCompanyId = offer.BuyerCompanyId!.Value,
                    offer.RespondedAtUtc,
                    offer.OfferedAtUtc,
                })
            .OrderBy(link => link.BuyerOrgId)
            .ThenByDescending(link => link.RespondedAtUtc)
            .ThenByDescending(link => link.OfferedAtUtc)
            .ThenBy(link => link.Id)
            .ToListAsync(ct);
        var companyByBuyerOrg = companyLinks
            .GroupBy(link => link.BuyerOrgId)
            .ToDictionary(group => group.Key, group => (Guid?)group.First().BuyerCompanyId);
        var companyIds = companyByBuyerOrg.Values.Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
        var companyNames = companyIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _db.Companies.AsNoTracking()
                .Where(company => company.TenantId == tenantId
                    && company.OrgId == sellerOrgId
                    && companyIds.Contains(company.Id))
                .ToDictionaryAsync(company => company.Id, company => company.Name, ct);
        var totalBalance = totalBalanceRow?.TotalBalance ?? 0m;

        return buyers.Select(item =>
        {
            var companyId = companyByBuyerOrg.GetValueOrDefault(item.BuyerOrgId);
            return new SellingOperationsTopBuyerItem
            {
                BuyerOrgId = item.BuyerOrgId,
                BuyerCompanyId = companyId,
                BuyerName = companyId.HasValue && companyNames.TryGetValue(companyId.Value, out var name)
                    ? name
                    : item.BuyerOrgId.ToString(),
                ActiveLienCount = item.ActiveLienCount,
                TotalBalance = item.TotalBalance,
                CompletedPurchaseAmount = completedPurchases.GetValueOrDefault(item.BuyerOrgId),
                PercentOfTotalBalance = totalBalance == 0m
                    ? 0m
                    : decimal.Round(item.TotalBalance * 100m / totalBalance, 2),
            };
        }).ToList();
    }

    private static SellingOperationsStatusItem ToStatusItem(
        string status,
        int lienCount,
        decimal originalAmount,
        decimal outstandingAmount,
        int totalCount) => new()
    {
        Status = status,
        LienCount = lienCount,
        OriginalAmount = originalAmount,
        OutstandingAmount = outstandingAmount,
        PercentOfLiens = totalCount == 0
            ? 0m
            : decimal.Round(lienCount * 100m / totalCount, 2),
    };

    private static SellingOperationsMetric AvailableMetric(
        decimal value,
        decimal? comparisonValue,
        string formula)
    {
        decimal? changeAmount = comparisonValue.HasValue ? value - comparisonValue.Value : null;
        decimal? changePercent = comparisonValue is > 0m
            ? decimal.Round((value - comparisonValue.Value) * 100m / comparisonValue.Value, 2)
            : null;

        return new SellingOperationsMetric
        {
            IsAvailable = true,
            Value = value,
            ComparisonValue = comparisonValue,
            ChangeAmount = changeAmount,
            ChangePercent = changePercent,
            Formula = formula,
        };
    }

    private static string EffectiveSellerStatus(
        string? sellerStatus,
        string lienStatus,
        bool hasSoldAt,
        bool hasPurchasePrice)
    {
        var indicatesCompletedLifecycle = sellerStatus == SellingLienStatus.Sold
            || lienStatus is LienStatus.Sold or LienStatus.Active or LienStatus.Settled or LienStatus.Disputed;
        if (indicatesCompletedLifecycle && hasSoldAt && hasPurchasePrice)
            return SellingLienStatus.Sold;

        if (sellerStatus == SellingLienStatus.Sold || lienStatus == LienStatus.Sold)
            return "SaleIncomplete";

        if (!string.IsNullOrWhiteSpace(sellerStatus))
            return sellerStatus;

        return lienStatus switch
        {
            LienStatus.Accepted => SellingLienStatus.Accepted,
            LienStatus.Declined => SellingLienStatus.Declined,
            LienStatus.Withdrawn => SellingLienStatus.Withdrawn,
            LienStatus.Offered or LienStatus.UnderReview => SellingLienStatus.SubmittedForSale,
            _ => SellingLienStatus.Pending,
        };
    }

    private static int StatusOrder(string status) => status switch
    {
        SellingLienStatus.Pending => 0,
        SellingLienStatus.Internal => 1,
        SellingLienStatus.PreparedForSale => 2,
        SellingLienStatus.SubmittedForSale => 3,
        SellingLienStatus.Accepted => 4,
        SellingLienStatus.Sold => 5,
        "SaleIncomplete" => 6,
        SellingLienStatus.Declined => 7,
        SellingLienStatus.Withdrawn => 8,
        _ => 9,
    };

    private static DashboardPeriod ResolvePeriod(
        SellingOperationsDashboardQuery query,
        DateTime generatedAtUtc)
    {
        if (query.StartDate.HasValue && query.EndDate.HasValue)
            return new DashboardPeriod(query.StartDate.Value, query.EndDate.Value);

        var today = DateOnly.FromDateTime(generatedAtUtc);
        return new DashboardPeriod(new DateOnly(today.Year, today.Month, 1), today);
    }

    private static DashboardPeriod PreviousPeriod(DashboardPeriod period)
    {
        var inclusiveDays = period.DateTo.DayNumber - period.DateFrom.DayNumber + 1;
        var previousFromDayNumber = period.DateFrom.DayNumber - inclusiveDays;
        if (previousFromDayNumber < DateOnly.MinValue.DayNumber)
        {
            throw new ValidationException(
                "Selling operations dashboard query is invalid.",
                new Dictionary<string, string[]>
                {
                    ["compare"] = ["The previous comparison period would be before the minimum supported date."],
                });
        }

        return new DashboardPeriod(
            DateOnly.FromDayNumber(previousFromDayNumber),
            DateOnly.FromDayNumber(period.DateFrom.DayNumber - 1));
    }

    private static SellingOperationsDashboardPeriod ToResponse(DashboardPeriod period) => new()
    {
        StartDate = period.DateFrom,
        EndDate = period.DateTo,
        DateBasis = "initialServiceDate",
    };

    private readonly record struct DashboardPeriod(DateOnly DateFrom, DateOnly DateTo);
    private sealed record FinancialAggregate(decimal LienRevenue, decimal Outstanding);
    private sealed record AcceptedBuyerAgingRow(
        Guid BuyerOrgId,
        Guid? BuyerCompanyId,
        Guid BuyerContactId,
        DateTime BuyerAcceptedAtUtc,
        decimal Amount,
        string Bucket);
    private sealed record AcceptedBuyerAgingResult(
        SellingOperationsArAgingResponse ArAging,
        SellingOperationsBuyerAgingResponse BuyerAging,
        decimal PastDueAmount);
}
