using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Liens.Api.Tests.Helpers;
using Liens.Application.DTOs;
using Liens.Application.Interfaces;
using Liens.Domain;
using Liens.Domain.Entities;
using Liens.Domain.Enums;
using Liens.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Liens.Api.Tests.Tests;

public class SellingOperationsDashboardEndpointTests : IClassFixture<LiensApiFactory>, IAsyncLifetime
{
    private readonly LiensApiFactory _factory;
    private HttpClient _client = null!;

    public SellingOperationsDashboardEndpointTests(LiensApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        await SeedHelper.SeedAsync(scope.ServiceProvider);

        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            JwtTokenHelper.CreateFullAccessToken(SeedHelper.TenantId, SeedHelper.UserId));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Dashboard_requires_authentication_analytics_permission_and_sell_mode()
    {
        using var anonymous = _factory.CreateClient();
        (await anonymous.GetAsync("/api/liens/selling/analytics/dashboard"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            JwtTokenHelper.CreateToken(
                SeedHelper.TenantId,
                SeedHelper.UserId,
                [LiensPermissions.LienSaleRead]));
        (await _client.GetAsync("/api/liens/selling/analytics/dashboard"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            JwtTokenHelper.CreateFullAccessToken(
                SeedHelper.TenantId,
                SeedHelper.UserId,
                includeProductAccess: false));
        (await _client.GetAsync("/api/liens/selling/analytics/dashboard"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            JwtTokenHelper.CreateFullAccessToken(
                SeedHelper.TenantId,
                SeedHelper.UserId,
                providerMode: "manage"));
        (await _client.GetAsync("/api/liens/selling/analytics/dashboard"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Dashboard_retains_global_totals_and_returns_empty_period_analytics()
    {
        var response = await _client.GetAsync(
            "/api/liens/selling/analytics/dashboard?startDate=2030-01-01&endDate=2030-01-31&compare=none");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<SellingOperationsDashboardResponse>();
        body.Should().NotBeNull();
        body!.Currency.Should().Be("USD");
        body.ComparisonPeriod.Should().BeNull();
        body.Metrics.TotalLienRevenue.Value.Should().Be(0m);
        body.Metrics.TotalLienRevenue.Formula.Should().Contain("PurchasePrice");
        body.Metrics.TotalOutstanding.Value.Should().Be(5_000m);
        body.Metrics.TotalOutstanding.Formula.Should().Contain("CurrentBalance");
        body.Metrics.Payments.Value.Should().Be(4_500m);
        body.Metrics.PastAmountDue.IsAvailable.Should().BeTrue();
        body.Metrics.PastAmountDue.Value.Should().Be(0m);
        body.ArAging.IsAvailable.Should().BeTrue();
        body.ArAging.Total.Should().Be(0m);
        body.ArAging.Buckets.Should().HaveCount(5);
        body.ArAging.Buckets.Should().OnlyContain(bucket => bucket.Amount == 0m && bucket.LienCount == 0);
        body.BuyerAging.IsAvailable.Should().BeTrue();
        body.BuyerAging.Items.Should().BeEmpty();
        body.LienStatuses.Should().BeEmpty();
        body.SellerStatuses.Should().BeEmpty();
        body.TimeSeries.Should().HaveCount(12);
        body.TimeSeries.Should().OnlyContain(point =>
            point.LienCount == 0 && point.LienRevenue == 0m && point.OutstandingAmount == 0m);
        body.TopBuyers.Should().BeEmpty();
    }

    [Fact]
    public async Task Dashboard_aging_matches_monthly_buyer_acceptance_boundaries_and_accepts_date_aliases()
    {
        var asOfDate = new DateOnly(2026, 1, 31);
        var buyerOrgId = Guid.CreateVersion7();
        var buyerContactId = Guid.CreateVersion7();
        var buyerCompany = Company.Create(
            SeedHelper.TenantId,
            SeedHelper.OrgId,
            CompanyDirectoryReferenceData.FundingCompanyId,
            "Dashboard Aging Capital",
            SeedHelper.UserId);
        var agingRows = new[]
        {
            (Days: 1, Amount: 10m),
            (Days: 30, Amount: 300m),
            (Days: 31, Amount: 310m),
            (Days: 60, Amount: 600m),
            (Days: 61, Amount: 610m),
            (Days: 90, Amount: 900m),
            (Days: 91, Amount: 910m),
            (Days: 120, Amount: 1_200m),
            (Days: 121, Amount: 1_210m),
        };

        await SeedAsync(db =>
        {
            db.Companies.Add(buyerCompany);
            foreach (var row in agingRows)
            {
                var lien = CreateLien(
                    $"AGING-{row.Days}",
                    new DateOnly(2026, 1, 15),
                    row.Amount,
                    row.Amount);
                db.Liens.Add(lien);
                db.SellingBuyerAccessLinks.Add(CreateAcceptedBuyerLink(
                    lien.Id,
                    buyerOrgId,
                    buyerContactId,
                    buyerCompany.Id,
                    row.Amount,
                    asOfDate.AddDays(-(row.Days - 1))));
            }
        });

        var response = await _client.GetAsync(
            "/api/liens/selling/analytics/dashboard?dateFrom=2026-01-01&dateTo=2026-01-31&compare=none");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<SellingOperationsDashboardResponse>();
        body.Should().NotBeNull();
        body!.Period.StartDate.Should().Be(new DateOnly(2026, 1, 1));
        body.Period.EndDate.Should().Be(asOfDate);
        body.ArAging.IsAvailable.Should().BeTrue();
        body.ArAging.Total.Should().Be(6_050m);
        body.ArAging.Buckets.Single(bucket => bucket.Bucket == "1-30")
            .Should().BeEquivalentTo(new { Amount = 310m, LienCount = 2 });
        body.ArAging.Buckets.Single(bucket => bucket.Bucket == "31-60")
            .Should().BeEquivalentTo(new { Amount = 910m, LienCount = 2 });
        body.ArAging.Buckets.Single(bucket => bucket.Bucket == "61-90")
            .Should().BeEquivalentTo(new { Amount = 1_510m, LienCount = 2 });
        body.ArAging.Buckets.Single(bucket => bucket.Bucket == "91-120")
            .Should().BeEquivalentTo(new { Amount = 2_110m, LienCount = 2 });
        body.ArAging.Buckets.Single(bucket => bucket.Bucket == "120+")
            .Should().BeEquivalentTo(new { Amount = 1_210m, LienCount = 1 });

        var buyerAging = body.BuyerAging.Items.Should().ContainSingle().Subject;
        buyerAging.BuyerOrgId.Should().Be(buyerOrgId);
        buyerAging.BuyerCompanyId.Should().Be(buyerCompany.Id);
        buyerAging.BuyerName.Should().Be("Dashboard Aging Capital");
        buyerAging.Total.Should().Be(6_050m);
        buyerAging.PastDuePercent.Should().Be(94.88m);
        buyerAging.Buckets.Should().BeEquivalentTo(body.ArAging.Buckets);
        body.Metrics.PastAmountDue.Value.Should().Be(5_740m);
    }

    [Fact]
    public async Task Dashboard_uses_inclusive_service_and_payment_periods_with_previous_period_comparison()
    {
        var currentStart = CreateLien("CURRENT-START", new DateOnly(2026, 1, 1), 1_000m, 600m);
        var currentEnd = CreateLien("CURRENT-END", new DateOnly(2026, 1, 31), 2_000m, 1_500m);
        var previous = CreateLien("PREVIOUS", new DateOnly(2025, 12, 1), 500m, 400m);
        var outside = CreateLien("OUTSIDE", new DateOnly(2025, 11, 30), 9_000m, 9_000m);
        currentStart.ListForSale(800m, SeedHelper.UserId);
        currentStart.MarkSold(750m, Guid.CreateVersion7(), SeedHelper.UserId);
        currentEnd.ListForSale(1_700m, SeedHelper.UserId);
        currentEnd.MarkSold(1_500m, Guid.CreateVersion7(), SeedHelper.UserId);

        await SeedAsync(db =>
        {
            db.Liens.AddRange(currentStart, currentEnd, previous, outside);
            db.SettlementPaymentDetails.Add(SettlementPaymentDetail.Create(
                SeedHelper.TenantId,
                SeedHelper.CaseId,
                currentStart.Id,
                2,
                250m,
                SeedHelper.UserId,
                new DateOnly(2026, 1, 31)));
            db.SettlementPaymentDetails.Add(SettlementPaymentDetail.Create(
                SeedHelper.TenantId,
                SeedHelper.CaseId,
                previous.Id,
                3,
                100m,
                SeedHelper.UserId,
                new DateOnly(2025, 12, 31)));
        });

        var response = await _client.GetAsync(
            "/api/liens/selling/analytics/dashboard?startDate=2026-01-01&endDate=2026-01-31");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<SellingOperationsDashboardResponse>();
        body.Should().NotBeNull();
        body!.Period.StartDate.Should().Be(new DateOnly(2026, 1, 1));
        body.Period.EndDate.Should().Be(new DateOnly(2026, 1, 31));
        body.Period.DateBasis.Should().Be("initialServiceDate");
        body.ComparisonPeriod!.StartDate.Should().Be(new DateOnly(2025, 12, 1));
        body.ComparisonPeriod.EndDate.Should().Be(new DateOnly(2025, 12, 31));
        body.Metrics.TotalLienRevenue.Value.Should().Be(2_250m);
        body.Metrics.TotalLienRevenue.ComparisonValue.Should().BeNull();
        body.Metrics.TotalOutstanding.Value.Should().Be(16_500m);
        body.Metrics.TotalOutstanding.ComparisonValue.Should().BeNull();
        body.Metrics.Payments.Value.Should().Be(4_850m);
        body.Metrics.Payments.ComparisonValue.Should().BeNull();
        body.TimeSeries.Should().HaveCount(12);
        body.TimeSeries.Single(point => point.BucketStart == new DateOnly(2026, 1, 1))
            .LienRevenue.Should().Be(3_000m);
    }

    [Fact]
    public async Task Dashboard_scopes_all_financials_to_tenant_and_seller_organization()
    {
        var included = CreateLien("CANONICAL-SELLER", new DateOnly(2026, 2, 10), 1_000m, 800m);
        SetLienOwnership(included, Guid.CreateVersion7(), SeedHelper.OrgId);
        included.ListForSale(900m, SeedHelper.UserId);
        included.MarkSold(850m, Guid.CreateVersion7(), SeedHelper.UserId);
        var conflictingLegacyOrg = CreateLien(
            "CONFLICTING-LEGACY-ORG",
            new DateOnly(2026, 2, 10),
            7_000m,
            7_000m);
        SetLienOwnership(conflictingLegacyOrg, SeedHelper.OrgId, Guid.CreateVersion7());
        var otherTenant = Lien.Create(
            Guid.CreateVersion7(),
            SeedHelper.OrgId,
            "OTHER-TENANT",
            LienType.MedicalLien,
            8_000m,
            SeedHelper.UserId,
            initialServiceDate: new DateOnly(2026, 2, 10));

        await SeedAsync(db => db.Liens.AddRange(included, conflictingLegacyOrg, otherTenant));

        var response = await _client.GetAsync(
            "/api/liens/selling/analytics/dashboard?startDate=2026-02-01&endDate=2026-02-28&compare=none");

        var body = await response.Content.ReadFromJsonAsync<SellingOperationsDashboardResponse>();
        body.Should().NotBeNull();
        body!.Metrics.TotalLienRevenue.Value.Should().Be(850m);
        body.Metrics.TotalOutstanding.Value.Should().Be(5_800m);
        body.LienStatuses.Sum(item => item.LienCount).Should().Be(1);
    }

    [Fact]
    public async Task Dashboard_keeps_accepted_distinct_and_requires_sale_evidence_for_sold()
    {
        var accepted = CreateLien("ACCEPTED", new DateOnly(2026, 3, 1), 1_000m, 1_000m);
        accepted.ListForSale(900m, SeedHelper.UserId);
        accepted.TransitionStatus(LienStatus.Accepted, SeedHelper.UserId);
        accepted.UpdateSellingAnalyticsFields(SeedHelper.UserId, sellerStatus: SellingLienStatus.Accepted);

        var sold = CreateLien("SOLD", new DateOnly(2026, 3, 2), 2_000m, 2_000m);
        sold.ListForSale(1_700m, SeedHelper.UserId);
        sold.MarkSold(1_600m, Guid.CreateVersion7(), SeedHelper.UserId);

        var incomplete = CreateLien("INCOMPLETE", new DateOnly(2026, 3, 3), 3_000m, 3_000m);
        incomplete.SetFinancials(3_000m, SeedHelper.UserId, purchasePrice: 2_500m);
        incomplete.UpdateSellingAnalyticsFields(SeedHelper.UserId, sellerStatus: SellingLienStatus.Sold);

        await SeedAsync(db => db.Liens.AddRange(accepted, sold, incomplete));

        var response = await _client.GetAsync(
            "/api/liens/selling/analytics/dashboard?startDate=2026-03-01&endDate=2026-03-31&compare=none");
        var body = await response.Content.ReadFromJsonAsync<SellingOperationsDashboardResponse>();

        body.Should().NotBeNull();
        body!.SellerStatuses.Single(item => item.Status == SellingLienStatus.Accepted).LienCount.Should().Be(1);
        body.SellerStatuses.Single(item => item.Status == SellingLienStatus.Sold).LienCount.Should().Be(1);
        body.SellerStatuses.Single(item => item.Status == "SaleIncomplete").LienCount.Should().Be(1);
        body.LienStatuses.Single(item => item.Status == LienStatus.Accepted).LienCount.Should().Be(1);
        body.LienStatuses.Single(item => item.Status == LienStatus.Sold).LienCount.Should().Be(1);
        body.LienStatuses.Single(item => item.Status == LienStatus.Draft).LienCount.Should().Be(1);
    }

    [Fact]
    public async Task Dashboard_top_buyer_reports_accepted_company_name_total_and_lien_count()
    {
        var buyerOrgId = Guid.CreateVersion7();
        var buyerContactId = Guid.CreateVersion7();
        var buyerCompany = Company.Create(
            SeedHelper.TenantId,
            SeedHelper.OrgId,
            CompanyDirectoryReferenceData.FundingCompanyId,
            "Selected Buyer Capital",
            SeedHelper.UserId);
        var acceptedLienA = CreateLien("BUYER-A1", new DateOnly(2026, 4, 15), 2_500m, 2_500m);
        var acceptedLienB = CreateLien("BUYER-A2", new DateOnly(2026, 4, 16), 1_500m, 1_500m);

        // A different buyer holds a lien but never accepted an offer -> excluded from top buyers.
        var noOfferLien = CreateLien("NO-OFFER", new DateOnly(2026, 4, 17), 9_000m, 8_000m);
        noOfferLien.ListForSale(7_000m, SeedHelper.UserId);
        noOfferLien.MarkSold(6_000m, Guid.CreateVersion7(), SeedHelper.UserId);
        noOfferLien.Activate(SeedHelper.UserId);

        await SeedAsync(db =>
        {
            db.Companies.Add(buyerCompany);
            db.Liens.AddRange(acceptedLienA, acceptedLienB, noOfferLien);
            db.SellingBuyerAccessLinks.Add(CreateAcceptedBuyerLink(
                acceptedLienA.Id, buyerOrgId, buyerContactId, buyerCompany.Id, 2_500m, new DateOnly(2026, 4, 29)));
            db.SellingBuyerAccessLinks.Add(CreateAcceptedBuyerLink(
                acceptedLienB.Id, buyerOrgId, buyerContactId, buyerCompany.Id, 1_500m, new DateOnly(2026, 4, 29)));
        });

        var response = await _client.GetAsync(
            "/api/liens/selling/analytics/dashboard?startDate=2026-04-01&endDate=2026-04-30&compare=none");
        var body = await response.Content.ReadFromJsonAsync<SellingOperationsDashboardResponse>();

        body.Should().NotBeNull();
        body!.TopBuyers.Should().ContainSingle();
        body.TopBuyers[0].BuyerOrgId.Should().Be(buyerOrgId);
        body.TopBuyers[0].BuyerCompanyId.Should().Be(buyerCompany.Id);
        body.TopBuyers[0].BuyerName.Should().Be("Selected Buyer Capital");
        body.TopBuyers[0].ActiveLienCount.Should().Be(2);
        body.TopBuyers[0].TotalBalance.Should().Be(4_000m);
        body.TopBuyers[0].PercentOfTotalBalance.Should().Be(100m);
    }

    [Fact]
    public async Task Dashboard_top_buyers_return_top_five_accepting_funding_companies_by_amount()
    {
        var accepted = new (string Name, decimal Amount)[]
        {
            ("Buyer F", 600m),
            ("Buyer E", 500m),
            ("Buyer D", 400m),
            ("Buyer C", 300m),
            ("Buyer B", 200m),
            ("Buyer A", 100m),
        };
        var orgByName = new Dictionary<string, Guid>();
        var companies = new List<Company>();
        var liens = new List<Lien>();
        var links = new List<SellingBuyerAccessLink>();
        foreach (var (name, amount) in accepted)
        {
            var company = Company.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                CompanyDirectoryReferenceData.FundingCompanyId,
                name,
                SeedHelper.UserId);
            var orgId = Guid.CreateVersion7();
            orgByName[name] = orgId;
            var lien = CreateLien($"TOP5-{name}", new DateOnly(2026, 4, 15), amount, amount);
            companies.Add(company);
            liens.Add(lien);
            links.Add(CreateAcceptedBuyerLink(
                lien.Id, orgId, Guid.CreateVersion7(), company.Id, amount, new DateOnly(2026, 4, 29)));
        }

        await SeedAsync(db =>
        {
            db.Companies.AddRange(companies);
            db.Liens.AddRange(liens);
            db.SellingBuyerAccessLinks.AddRange(links);
        });

        var response = await _client.GetAsync(
            "/api/liens/selling/analytics/dashboard?startDate=2026-04-01&endDate=2026-04-30&compare=none");
        var body = await response.Content.ReadFromJsonAsync<SellingOperationsDashboardResponse>();

        body.Should().NotBeNull();
        body!.TopBuyers.Should().HaveCount(5);
        body.TopBuyers.Select(buyer => buyer.BuyerName)
            .Should().ContainInOrder("Buyer F", "Buyer E", "Buyer D", "Buyer C", "Buyer B");
        body.TopBuyers.Should().NotContain(buyer => buyer.BuyerName == "Buyer A");
        body.TopBuyers[0].BuyerOrgId.Should().Be(orgByName["Buyer F"]);
        body.TopBuyers[0].TotalBalance.Should().Be(600m);
        body.TopBuyers[0].ActiveLienCount.Should().Be(1);
        // Percent is measured against the full accepted pool (2,100), not just the top five.
        body.TopBuyers[0].PercentOfTotalBalance.Should().Be(28.57m);
    }

    [Theory]
    [InlineData("?startDate=2026-01-01")]
    [InlineData("?dateFrom=2026-01-01")]
    [InlineData("?startDate=2026-02-01&endDate=2026-01-01")]
    [InlineData("?startDate=2026-01-01&dateFrom=2026-01-02&endDate=2026-01-31")]
    [InlineData("?startDate=2025-01-01&endDate=2026-01-02")]
    [InlineData("?startDate=0001-01-01&endDate=0001-01-01&compare=previousPeriod")]
    [InlineData("?compare=yearOverYear")]
    public async Task Dashboard_rejects_invalid_period_queries(string query)
    {
        var response = await _client.GetAsync($"/api/liens/selling/analytics/dashboard{query}");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Dashboard_accepts_maximum_366_day_period()
    {
        var response = await _client.GetAsync(
            "/api/liens/selling/analytics/dashboard?startDate=2025-01-01&endDate=2026-01-01&compare=none");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private async Task SeedAsync(Action<LiensDbContext> arrange)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
        arrange(db);
        await db.SaveChangesAsync();
    }

    private static Lien CreateLien(
        string suffix,
        DateOnly serviceDate,
        decimal originalAmount,
        decimal currentBalance,
        Guid? sellerOrgId = null)
    {
        var orgId = sellerOrgId ?? SeedHelper.OrgId;
        var lienNumber = $"DASH-{suffix}-{Guid.NewGuid():N}";
        var lien = Lien.Create(
            SeedHelper.TenantId,
            orgId,
            lienNumber[..Math.Min(32, lienNumber.Length)],
            LienType.MedicalLien,
            originalAmount,
            SeedHelper.UserId,
            caseId: SeedHelper.CaseId,
            initialServiceDate: serviceDate);
        lien.SetFinancials(originalAmount, SeedHelper.UserId, currentBalance: currentBalance);
        return lien;
    }

    private static void SetLienOwnership(Lien lien, Guid orgId, Guid? sellingOrgId)
    {
        SetProperty(lien, nameof(Lien.OrgId), orgId);
        SetProperty(lien, nameof(Lien.SellingOrgId), sellingOrgId);
    }

    private static SellingBuyerAccessLink CreateAcceptedBuyerLink(
        Guid lienId,
        Guid buyerOrgId,
        Guid buyerContactId,
        Guid buyerCompanyId,
        decimal amount,
        DateOnly acceptedDate)
    {
        var link = SellingBuyerAccessLink.Create(
            SeedHelper.TenantId,
            lienId,
            SeedHelper.OrgId,
            buyerOrgId,
            buyerContactId,
            $"dashboard-aging-token-{Guid.CreateVersion7():N}",
            SellingAccessLinkPurposes.ConfirmSaleBuyerResponse,
            "/selling/public",
            $"dashboard-aging-key-{Guid.CreateVersion7():N}",
            DateTime.UtcNow.AddYears(1),
            SeedHelper.UserId);
        link.LinkCanonicalBuyer(buyerCompanyId, null);
        link.RecordResponse(SellingBuyerResponseStatus.Accepted, amount, null);
        SetProperty(
            link,
            nameof(SellingBuyerAccessLink.RespondedAtUtc),
            acceptedDate.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc));
        return link;
    }

    private static void SetProperty<T>(T entity, string propertyName, object? value) where T : class
    {
        var property = typeof(T).GetProperty(propertyName);
        property.Should().NotBeNull();
        property!.SetValue(entity, value);
    }
}
