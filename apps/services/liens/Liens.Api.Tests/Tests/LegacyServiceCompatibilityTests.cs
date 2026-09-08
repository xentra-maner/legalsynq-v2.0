using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Liens.Api.Endpoints;
using Liens.Api.Tests.Helpers;
using Liens.Domain.Entities;
using Liens.Domain.Enums;
using Liens.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Liens.Api.Tests.Tests;

public class LegacyServiceCompatibilityTests : IClassFixture<LiensApiFactory>, IAsyncLifetime
{
    private readonly LiensApiFactory _factory;
    private HttpClient _client = null!;

    public LegacyServiceCompatibilityTests(LiensApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        await SeedHelper.SeedAsync(scope.ServiceProvider);

        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer",
                JwtTokenHelper.CreateFullAccessToken(SeedHelper.TenantId, SeedHelper.UserId));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ServiceCase_routes_return_seeded_case_data()
    {
        var getResponse = await _client.GetAsync("/service/case");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await getResponse.Content.ReadAsStringAsync()}");

        var postResponse = await _client.PostAsJsonAsync("/service/case/v3", new
        {
            page = 1,
            limit = 10,
        });
        postResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await postResponse.Content.ReadAsStringAsync()}");

        var getBody = JsonNode.Parse(await getResponse.Content.ReadAsStringAsync())!;
        getBody["isSuccess"]!.GetValue<bool>().Should().BeTrue();
        getBody["data"]!.AsArray().Should().Contain(item =>
            item!["caseId"]!.GetValue<string>() == SeedHelper.CaseId.ToString());

        var postBody = JsonNode.Parse(await postResponse.Content.ReadAsStringAsync())!;
        postBody["isSuccess"]!.GetValue<bool>().Should().BeTrue();
        postBody["data"]!.AsArray().Should().Contain(item =>
            item!["caseId"]!.GetValue<string>() == SeedHelper.CaseId.ToString());
    }

    [Fact]
    public async Task ServiceCase_v3_returns_case_manager_fields_when_available()
    {
        var caseManagerId = Guid.CreateVersion7();
        var caseNumber = $"CASE-SVC-V3-{Guid.CreateVersion7():N}";

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();

            var caseManager = Contact.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                ContactType.CaseManager,
                "John",
                "Doe",
                SeedHelper.UserId);
            typeof(Contact).GetProperty(nameof(Contact.Id))!.SetValue(caseManager, caseManagerId);

            db.Contacts.Add(caseManager);
            db.Cases.Add(Case.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                caseNumber,
                "Legacy",
                "Service",
                SeedHelper.UserId,
                notes: $"lawFirmId={SeedHelper.LawFirmId}; caseManagerId={caseManagerId}"));

            await db.SaveChangesAsync();
        }

        var response = await _client.PostAsJsonAsync("/service/case/v3", new
        {
            keyword = caseNumber,
            page = 1,
            limit = 10,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await response.Content.ReadAsStringAsync()}");

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var item = body["data"]!.AsArray().Single(node =>
            node!["caseCode"]!.GetValue<string>() == caseNumber)!;

        item["caseManagerId"]!.GetValue<string>().Should().Be(caseManagerId.ToString());
        item["caseManager"]!.GetValue<string>().Should().Be("John Doe");
        item["lawFirmId"]!.GetValue<string>().Should().Be(SeedHelper.LawFirmId.ToString());
        item["lawfirm"]!.GetValue<string>().Should().Be("Smith & Associates LLP");
    }

    [Fact]
    public async Task ServiceCase_v3_returns_settlement_and_financial_fields()
    {
        var caseNumber = $"CASE-SVC-METRICS-{Guid.CreateVersion7():N}"[..30];

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var caseEntity = Case.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                caseNumber,
                "Settlement",
                "Plaintiff",
                SeedHelper.UserId);
            var firstLien = Lien.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                $"LIEN-SVC-METRICS-A-{Guid.CreateVersion7():N}"[..30],
                LienType.MedicalLien,
                1_000m,
                SeedHelper.UserId,
                caseId: caseEntity.Id);
            var secondLien = Lien.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                $"LIEN-SVC-METRICS-B-{Guid.CreateVersion7():N}"[..30],
                LienType.MedicalLien,
                400m,
                SeedHelper.UserId,
                caseId: caseEntity.Id);
            var firstMedicalCode = ServicingItem.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                $"LMC-SVC-METRICS-A-{Guid.CreateVersion7():N}"[..40],
                "LegacyMedicalCode",
                "First medical code amount",
                "system",
                SeedHelper.UserId,
                caseId: caseEntity.Id,
                lienId: firstLien.Id,
                notes: "billingAmount=600.75; purchaseAmount=275.50");
            var secondMedicalCode = ServicingItem.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                $"LMC-SVC-METRICS-B-{Guid.CreateVersion7():N}"[..40],
                "LegacyMedicalCode",
                "Second medical code amount",
                "system",
                SeedHelper.UserId,
                caseId: caseEntity.Id,
                lienId: firstLien.Id,
                notes: "billingAmount=99.25; purchaseAmount=24.50");
            var firstSettlement = LienSettlement.Create(
                SeedHelper.TenantId,
                caseEntity.Id,
                firstLien.Id,
                1,
                0m,
                SeedHelper.UserId,
                "full_payment",
                note: "legacySettlementId=123; totalSettledAmount=180",
                settlementDate: new DateOnly(2025, 4, 1));
            var secondSettlement = LienSettlement.Create(
                SeedHelper.TenantId,
                caseEntity.Id,
                secondLien.Id,
                1,
                20m,
                SeedHelper.UserId,
                "full_payment",
                settlementDate: new DateOnly(2025, 4, 2));
            var payment = SettlementPaymentDetail.Create(
                SeedHelper.TenantId,
                caseEntity.Id,
                secondLien.Id,
                1,
                20m,
                SeedHelper.UserId,
                new DateOnly(2025, 4, 2),
                "Test Payor",
                "CHK-SVC-METRICS",
                "[legacy-meta]\nnetProfit=0.00; type=by_attorney; status=full_payment");

            db.Cases.Add(caseEntity);
            db.Liens.AddRange(firstLien, secondLien);
            db.ServicingItems.AddRange(firstMedicalCode, secondMedicalCode);
            db.LienSettlements.AddRange(firstSettlement, secondSettlement);
            db.SettlementPaymentDetails.Add(payment);
            await db.SaveChangesAsync();
        }

        var response = await _client.PostAsJsonAsync("/service/case/v3", new
        {
            keyword = caseNumber,
            page = 1,
            limit = 10,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await response.Content.ReadAsStringAsync()}");

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var item = body["data"]!.AsArray().Single(node =>
            node!["caseCode"]!.GetValue<string>() == caseNumber)!;

        item["settlementStatus"]!.GetValue<string>().Should().Be("Full Payment");
        item["settlementDate"]!.GetValue<string>().Should().Be("04/02/2025");
        item["settlementAmount"]!.GetValue<decimal>().Should().Be(200m);
        item["billingAmount"]!.GetValue<decimal>().Should().Be(1_100m);
        item["purchaseAmount"]!.GetValue<decimal>().Should().Be(300m);
    }

    [Fact]
    public async Task ServiceCase_v3_uses_payment_amount_when_no_settlement_exists()
    {
        var caseNumber = $"CASE-SVC-PAYMENT-{Guid.CreateVersion7():N}"[..30];

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var caseEntity = Case.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                caseNumber,
                "Payment",
                "Fallback",
                SeedHelper.UserId);
            var lien = Lien.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                $"LIEN-SVC-PAYMENT-{Guid.CreateVersion7():N}"[..30],
                LienType.MedicalLien,
                3_295m,
                SeedHelper.UserId,
                caseId: caseEntity.Id);
            var payment = SettlementPaymentDetail.Create(
                SeedHelper.TenantId,
                caseEntity.Id,
                lien.Id,
                1,
                750m,
                SeedHelper.UserId,
                new DateOnly(2026, 8, 6),
                "Test Payor",
                "CHK-SVC-PAYMENT",
                "[legacy-meta]\nnetProfit=0.00; type=by_attorney; status=full_payment");

            db.Cases.Add(caseEntity);
            db.Liens.Add(lien);
            db.SettlementPaymentDetails.Add(payment);
            await db.SaveChangesAsync();
        }

        var response = await _client.PostAsJsonAsync("/service/case/v3", new
        {
            keyword = caseNumber,
            page = 1,
            limit = 10,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await response.Content.ReadAsStringAsync()}");

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var item = body["data"]!.AsArray().Single(node =>
            node!["caseCode"]!.GetValue<string>() == caseNumber)!;

        item["settlementStatus"]!.GetValue<string>().Should().Be("Full Payment");
        item["settlementDate"]!.GetValue<string>().Should().NotBeEmpty();
        item["settlementAmount"]!.GetValue<decimal>().Should().Be(750m);
    }

    [Fact]
    public async Task ServiceCase_v3_returns_empty_success_payload_when_no_cases_match()
    {
        var response = await _client.PostAsJsonAsync("/service/case/v3", new
        {
            keyword = $"NO-MATCH-{Guid.CreateVersion7():N}",
            page = 1,
            limit = 10,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await response.Content.ReadAsStringAsync()}");

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        body["isSuccess"]!.GetValue<bool>().Should().BeTrue();
        body["data"]!.AsArray().Should().BeEmpty();
        body["page"]!.GetValue<int>().Should().Be(1);
        body["limit"]!.GetValue<int>().Should().Be(10);
        body["totalCount"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task ServiceLien_routes_return_seeded_lien_data()
    {
        var listResponse = await _client.GetAsync($"/service/all-liens/{SeedHelper.CaseId}");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await listResponse.Content.ReadAsStringAsync()}");

        var searchResponse = await _client.PostAsJsonAsync("/service/liens/v3", new
        {
            caseId = SeedHelper.CaseId,
            page = 1,
            limit = 10,
        });
        searchResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await searchResponse.Content.ReadAsStringAsync()}");

        var listBody = JsonNode.Parse(await listResponse.Content.ReadAsStringAsync())!;
        listBody["data"]!.AsArray().Should().Contain(item =>
            item!["liensId"]!.GetValue<string>() == SeedHelper.LienId.ToString());

        var searchBody = JsonNode.Parse(await searchResponse.Content.ReadAsStringAsync())!;
        searchBody["data"]!.AsArray().Should().Contain(item =>
            item!["liensId"]!.GetValue<string>() == SeedHelper.LienId.ToString());
    }

    [Fact]
    public async Task Global_and_v3_searches_rank_reversed_fuzzy_plaintiff_names()
    {
        var caseNumber = $"CASE-FUZZY-{Guid.CreateVersion7():N}"[..40];
        var lienNumber = $"LIEN-FUZZY-{Guid.CreateVersion7():N}"[..40];

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var caseEntity = Case.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                caseNumber,
                "Jude",
                "Hannah",
                SeedHelper.UserId);
            var lien = Lien.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                lienNumber,
                LienType.MedicalLien,
                1000m,
                SeedHelper.UserId,
                caseId: caseEntity.Id);

            db.Cases.Add(caseEntity);
            db.Liens.Add(lien);
            await db.SaveChangesAsync();
        }

        const string keyword = "Hannab Judx";

        var globalResponse = await _client.PostAsJsonAsync("/api/liens/cases/global-search", new
        {
            query = keyword,
            page = 1,
            limit = 20,
        });
        globalResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await globalResponse.Content.ReadAsStringAsync()}");

        var global = JsonNode.Parse(await globalResponse.Content.ReadAsStringAsync())!;
        global["cases"]!["items"]!.AsArray().Should().Contain(item =>
            item!["caseNumber"]!.GetValue<string>() == caseNumber);
        global["liens"]!["items"]!.AsArray().Should().Contain(item =>
            item!["lienNumber"]!.GetValue<string>() == lienNumber);

        var caseResponse = await _client.PostAsJsonAsync("/api/liens/cases/v3", new
        {
            keyword,
            page = 1,
            limit = 20,
        });
        caseResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await caseResponse.Content.ReadAsStringAsync()}");
        var caseBody = JsonNode.Parse(await caseResponse.Content.ReadAsStringAsync())!;
        caseBody["data"]!.AsArray().Should().Contain(item =>
            item!["caseNumber"]!.GetValue<string>() == caseNumber);

        var lienResponse = await _client.PostAsJsonAsync("/api/liens/cases/liens/v3", new
        {
            keyword,
            page = 1,
            limit = 20,
        });
        lienResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await lienResponse.Content.ReadAsStringAsync()}");
        var lienBody = JsonNode.Parse(await lienResponse.Content.ReadAsStringAsync())!;
        lienBody["items"]!.AsArray().Should().Contain(item =>
            item!["lienNumber"]!.GetValue<string>() == lienNumber);

        var serviceCaseResponse = await _client.PostAsJsonAsync("/service/case/v3", new
        {
            keyword,
            page = 1,
            limit = 20,
        });
        serviceCaseResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await serviceCaseResponse.Content.ReadAsStringAsync()}");
        var serviceCase = JsonNode.Parse(await serviceCaseResponse.Content.ReadAsStringAsync())!;
        serviceCase["data"]!.AsArray().Should().Contain(item =>
            item!["caseCode"]!.GetValue<string>() == caseNumber);

        var serviceLienResponse = await _client.PostAsJsonAsync("/service/liens/v3", new
        {
            keyword,
            page = 1,
            limit = 20,
        });
        serviceLienResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await serviceLienResponse.Content.ReadAsStringAsync()}");
        var serviceLien = JsonNode.Parse(await serviceLienResponse.Content.ReadAsStringAsync())!;
        serviceLien["data"]!.AsArray().Should().Contain(item =>
            item!["lienCode"]!.GetValue<string>() == lienNumber);
    }

    [Fact]
    public async Task Global_search_returns_legacy_result_categories_with_v3_cases_and_liens()
    {
        var response = await _client.PostAsJsonAsync("/api/liens/cases/global-search", new
        {
            page = 1,
            limit = 20,
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await response.Content.ReadAsStringAsync()}");

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        body["cases"]!["items"]!.AsArray().Should().Contain(item =>
            item!["id"]!.GetValue<Guid>() == SeedHelper.CaseId);
        body["liens"]!["items"]!.AsArray().Should().Contain(item =>
            item!["id"]!.GetValue<Guid>() == SeedHelper.LienId);
        body["plaintiffs"]!.AsArray().Should().Contain(item =>
            item!["caseId"]!.GetValue<string>() == SeedHelper.CaseId.ToString() &&
            item["plaintiffName"]!.GetValue<string>() == "John Plaintiff");
        body["lawFirms"]!.AsArray().Should().Contain(item =>
            item!["contactId"]!.GetValue<string>() == SeedHelper.LawFirmId.ToString());
        body["medicalFacilities"]!.AsArray().Should().Contain(item =>
            item!["contactId"]!.GetValue<string>() == SeedHelper.MedicalFacilityContactId.ToString());
        body["medicalProviders"]!.AsArray().Should().Contain(item =>
            item!["contactId"]!.GetValue<string>() == SeedHelper.MedicalProviderId.ToString());
        body["fundingCompanies"]!.AsArray().Should().Contain(item =>
            item!["contactId"]!.GetValue<string>() == SeedHelper.FundingCompanyId.ToString());
        body["Leads"]!.AsArray().Should().Contain(item =>
            item!["contactId"]!.GetValue<string>() == SeedHelper.LeadContactId.ToString());
        body["servicing"]!.AsArray().Should().Contain(item =>
            item!["caseId"]!.GetValue<string>() == SeedHelper.CaseId.ToString());
    }

    [Fact]
    public async Task Global_search_accepts_legacy_keyword_field()
    {
        var response = await _client.PostAsJsonAsync("/api/liens/cases/global-search", new
        {
            keyword = "Jane Doe",
            page = 1,
            limit = 20,
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await response.Content.ReadAsStringAsync()}");

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        body["Leads"]!.AsArray().Should().ContainSingle(item =>
            item!["contactId"]!.GetValue<string>() == SeedHelper.LeadContactId.ToString());
    }

    [Fact]
    public async Task ServiceCase_v3_search_alias_preserves_fuzzy_ranking_filters_tenant_and_paging()
    {
        var nameToken = string.Concat(Guid.CreateVersion7().ToString("N").Select(value =>
            (char)('a' + (value <= '9' ? value - '0' : value - 'a' + 10))));
        var targetFirstName = $"Zorvella{nameToken}";
        var targetLastName = $"Quendrix{nameToken}";

        var targetCase = Case.Create(
            SeedHelper.TenantId,
            SeedHelper.OrgId,
            $"CASE-SEARCH-TARGET-{Guid.CreateVersion7():N}"[..40],
            targetFirstName,
            targetLastName,
            SeedHelper.UserId);
        var unrelatedCase = Case.Create(
            SeedHelper.TenantId,
            SeedHelper.OrgId,
            $"CASE-SEARCH-LOWER-{Guid.CreateVersion7():N}"[..40],
            $"Mara{nameToken}",
            targetLastName,
            SeedHelper.UserId);
        var filteredCase = Case.Create(
            SeedHelper.TenantId,
            SeedHelper.OrgId,
            $"CASE-SEARCH-FILTER-{Guid.CreateVersion7():N}"[..40],
            targetFirstName,
            targetLastName,
            SeedHelper.UserId);
        filteredCase.TransitionStatus(CaseStatus.DemandSent, SeedHelper.UserId);

        var otherTenantCase = Case.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            $"CASE-SEARCH-TENANT-{Guid.CreateVersion7():N}"[..40],
            targetFirstName,
            targetLastName,
            SeedHelper.UserId);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            db.Cases.AddRange(targetCase, unrelatedCase, filteredCase, otherTenantCase);
            await db.SaveChangesAsync();
        }

        var broadResponse = await _client.PostAsJsonAsync("/service/case/v3", new
        {
            search = $"{targetLastName} {targetFirstName}",
            statusId = CaseStatus.PreDemand,
            page = 1,
            limit = 100,
        });
        broadResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await broadResponse.Content.ReadAsStringAsync()}");

        var broad = JsonNode.Parse(await broadResponse.Content.ReadAsStringAsync())!;
        var broadItems = broad["data"]!.AsArray();
        broadItems.Should().HaveCount(2);
        broadItems[0]!["caseId"]!.GetValue<string>().Should().Be(targetCase.Id.ToString());
        broadItems.Should().Contain(item =>
            item!["caseId"]!.GetValue<string>() == unrelatedCase.Id.ToString());
        broadItems.Should().NotContain(item =>
            item!["caseId"]!.GetValue<string>() == filteredCase.Id.ToString() ||
            item["caseId"]!.GetValue<string>() == otherTenantCase.Id.ToString());
        broad["totalCount"]!.GetValue<int>().Should().Be(2);

        var forwardExactResponse = await _client.PostAsJsonAsync("/service/case/v3", new
        {
            search = $"{targetFirstName} {targetLastName}",
            statusId = CaseStatus.PreDemand,
            page = 1,
            limit = 100,
        });
        forwardExactResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await forwardExactResponse.Content.ReadAsStringAsync()}");

        var forwardExact = JsonNode.Parse(await forwardExactResponse.Content.ReadAsStringAsync())!;
        forwardExact["data"]!.AsArray()[0]!["caseId"]!.GetValue<string>()
            .Should().Be(targetCase.Id.ToString());

        var firstPageResponse = await _client.PostAsJsonAsync("/service/case/v3", new
        {
            search = $"{targetLastName} {targetFirstName}",
            statusId = CaseStatus.PreDemand,
            page = 1,
            limit = 1,
        });
        firstPageResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await firstPageResponse.Content.ReadAsStringAsync()}");

        var firstPage = JsonNode.Parse(await firstPageResponse.Content.ReadAsStringAsync())!;
        firstPage["data"]!.AsArray().Should().ContainSingle();
        firstPage["data"]![0]!["caseId"]!.GetValue<string>().Should().Be(targetCase.Id.ToString());
        firstPage["page"]!.GetValue<int>().Should().Be(1);
        firstPage["limit"]!.GetValue<int>().Should().Be(1);
        firstPage["totalCount"]!.GetValue<int>().Should().Be(2);

        var secondPageResponse = await _client.PostAsJsonAsync("/service/case/v3", new
        {
            search = $"{targetLastName} {targetFirstName}",
            statusId = CaseStatus.PreDemand,
            page = 2,
            limit = 1,
        });
        secondPageResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await secondPageResponse.Content.ReadAsStringAsync()}");

        var secondPage = JsonNode.Parse(await secondPageResponse.Content.ReadAsStringAsync())!;
        secondPage["data"]!.AsArray().Should().ContainSingle();
        secondPage["data"]![0]!["caseId"]!.GetValue<string>().Should().Be(unrelatedCase.Id.ToString());
        secondPage["totalCount"]!.GetValue<int>().Should().Be(2);

        var typoResponse = await _client.PostAsJsonAsync("/service/case/v3", new
        {
            search = $"Quendri{nameToken} Zorvell{nameToken}",
            statusId = CaseStatus.PreDemand,
            page = 1,
            limit = 10,
        });
        typoResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await typoResponse.Content.ReadAsStringAsync()}");

        var typo = JsonNode.Parse(await typoResponse.Content.ReadAsStringAsync())!;
        typo["data"]!.AsArray()[0]!["caseId"]!.GetValue<string>().Should().Be(targetCase.Id.ToString());
        typo["data"]!.AsArray().Should().NotContain(item =>
            item!["caseId"]!.GetValue<string>() == filteredCase.Id.ToString() ||
            item["caseId"]!.GetValue<string>() == otherTenantCase.Id.ToString());

        var keywordPrecedenceResponse = await _client.PostAsJsonAsync("/service/case/v3", new
        {
            keyword = targetCase.CaseNumber,
            search = "does not match",
            statusId = CaseStatus.PreDemand,
            page = 1,
            limit = 10,
        });
        keywordPrecedenceResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await keywordPrecedenceResponse.Content.ReadAsStringAsync()}");

        var keywordPrecedence = JsonNode.Parse(await keywordPrecedenceResponse.Content.ReadAsStringAsync())!;
        keywordPrecedence["data"]!.AsArray().Should().ContainSingle(item =>
            item!["caseId"]!.GetValue<string>() == targetCase.Id.ToString());
    }

    [Fact]
    public async Task ServiceSettlementCompatibility_routes_return_data()
    {
        var historyResponse = await _client.PostAsJsonAsync("/service/settlement/history/v3", new
        {
            caseId = SeedHelper.CaseId,
            page = 1,
            limit = 10,
        });
        historyResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await historyResponse.Content.ReadAsStringAsync()}");

        var paymentsResponse = await _client.GetAsync($"/service/liens/settlement/payment-details/{SeedHelper.CaseId}");
        paymentsResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await paymentsResponse.Content.ReadAsStringAsync()}");

        var settlementResponse = await _client.GetAsync($"/service/liens/settlement-details/{SeedHelper.CaseId}");
        settlementResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await settlementResponse.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task ServiceSettlementHistory_v3_returns_lien_code_for_history_items()
    {
        var historyResponse = await _client.PostAsJsonAsync("/service/settlement/history/v3", new
        {
            caseId = SeedHelper.CaseId,
            page = 1,
            limit = 10,
        });
        historyResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await historyResponse.Content.ReadAsStringAsync()}");

        var body = JsonNode.Parse(await historyResponse.Content.ReadAsStringAsync())!;
        var hasExpectedItem = body["data"]!.AsArray().Any(item =>
        {
            var lienId = item?["lienId"]?.GetValue<string>();
            var lienCode = item?["lienCode"]?.GetValue<string>();
            var updatedBy = item?["updatedBy"]?.GetValue<string>();
            return lienId == "LIEN-TEST-001"
                && lienCode == "LIEN-TEST-001"
                && updatedBy == "Demo User";
        });

        hasExpectedItem.Should().BeTrue();
    }

    [Fact]
    public async Task ServiceSettlementHistory_v3_preserves_payment_note_and_updater_when_identity_does_not_return_the_user()
    {
        var updatingUserId = Guid.CreateVersion7();
        SettlementPaymentDetail payment;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            payment = SettlementPaymentDetail.Create(
                SeedHelper.TenantId,
                SeedHelper.CaseId,
                SeedHelper.LienId,
                3626,
                100m,
                updatingUserId,
                checkNumber: "3626",
                note: "Paid with CK#3626");

            db.SettlementPaymentDetails.Add(payment);
            await db.SaveChangesAsync();
        }

        var historyResponse = await _client.PostAsJsonAsync("/service/settlement/history/v3", new
        {
            caseId = SeedHelper.CaseId,
            page = 1,
            limit = 100,
        });
        historyResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await historyResponse.Content.ReadAsStringAsync()}");

        var body = JsonNode.Parse(await historyResponse.Content.ReadAsStringAsync())!;
        var item = body["data"]!.AsArray().Single(historyItem =>
            historyItem!["id"]!.GetValue<string>() == payment.Id.ToString())!;

        item["note"]!.GetValue<string>().Should().Be("Paid with CK#3626");
        item["checkNumber"]!.GetValue<string>().Should().Be("3626");
        item["updatedBy"]!.GetValue<string>().Should().Be(updatingUserId.ToString());
    }

    [Fact]
    public async Task ServiceSettlementHistory_v3_returns_legacy_law_firm_change_history_without_duplicates()
    {
        var oldLawFirmId = Guid.CreateVersion7();
        var newLawFirmId = Guid.CreateVersion7();
        Guid caseId;

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            JwtTokenHelper.CreateFullAccessToken(
                SeedHelper.TenantId,
                SeedHelper.UserId,
                email: "demo.user@legalsynq.test"));

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var oldLawFirm = Contact.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                ContactType.LawFirm,
                "Legacy",
                "Old",
                SeedHelper.UserId,
                organization: "Legacy Old Law LLP");
            var newLawFirm = Contact.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                ContactType.LawFirm,
                "Legacy",
                "New",
                SeedHelper.UserId,
                organization: "Legacy New Law LLP");
            typeof(Contact).GetProperty(nameof(Contact.Id))!.SetValue(oldLawFirm, oldLawFirmId);
            typeof(Contact).GetProperty(nameof(Contact.Id))!.SetValue(newLawFirm, newLawFirmId);

            var caseNumber = $"CASE-LF-HISTORY-{Guid.CreateVersion7():N}"[..30];
            var caseEntity = Case.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                caseNumber,
                "LawFirm",
                "History",
                SeedHelper.UserId,
                notes: $"lawFirmId={oldLawFirmId}");
            caseId = caseEntity.Id;

            db.Contacts.AddRange(oldLawFirm, newLawFirm);
            db.Cases.Add(caseEntity);
            await db.SaveChangesAsync();
        }

        var scheduledUpdate = await _client.PatchAsJsonAsync("/service/update-details", new
        {
            caseId,
            lawFirmId = newLawFirmId,
            switchedDate = "2099-01-15",
        });
        scheduledUpdate.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await scheduledUpdate.Content.ReadAsStringAsync()}");

        var scheduledRetry = await _client.PatchAsJsonAsync("/service/update-details", new
        {
            caseId,
            lawFirmId = newLawFirmId,
            switchedDate = "01/15/2099",
        });
        scheduledRetry.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await scheduledRetry.Content.ReadAsStringAsync()}");

        var scheduledCaseResponse = await _client.GetAsync($"/api/liens/cases/{caseId}");
        scheduledCaseResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var scheduledCase = JsonNode.Parse(await scheduledCaseResponse.Content.ReadAsStringAsync())!;
        scheduledCase["lawFirmId"]!.GetValue<string>().Should().Be(oldLawFirmId.ToString());
        scheduledCase["pendingLawFirmId"]!.GetValue<string>().Should().Be(newLawFirmId.ToString());
        scheduledCase["switchedDate"]!.GetValue<string>().Should().Be("2099-01-15");

        var immediateUpdate = await _client.PatchAsJsonAsync("/service/update-details", new
        {
            caseId,
            lawFirmId = newLawFirmId,
            switchedDate = "2025-01-15",
        });
        immediateUpdate.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await immediateUpdate.Content.ReadAsStringAsync()}");

        var duplicateUpdate = await _client.PatchAsJsonAsync("/service/update-details", new
        {
            caseId,
            lawFirmId = newLawFirmId,
            switchedDate = "2025-01-15",
        });
        duplicateUpdate.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await duplicateUpdate.Content.ReadAsStringAsync()}");

        var clearUpdate = await _client.PatchAsJsonAsync("/service/update-details", new
        {
            caseId,
            lawFirmId = string.Empty,
            switchedDate = "2025-01-15",
        });
        clearUpdate.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await clearUpdate.Content.ReadAsStringAsync()}");

        var historyResponse = await _client.PostAsJsonAsync("/service/settlement/history/v3", new
        {
            caseId,
            page = 1,
            limit = 10,
        });
        historyResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await historyResponse.Content.ReadAsStringAsync()}");

        var body = JsonNode.Parse(await historyResponse.Content.ReadAsStringAsync())!;
        body["totalCount"]!.GetValue<int>().Should().Be(3);
        var items = body["data"]!.AsArray();
        items.Should().OnlyContain(item =>
            item!["type"]!.GetValue<string>() == "law-firm-change" &&
            item["updatedBy"]!.GetValue<string>() == "Demo User" &&
            item["user"]!.GetValue<string>() == "Demo User" &&
            !string.IsNullOrWhiteSpace(item["date"]!.GetValue<string>()));
        items.Should().Contain(item =>
            item!["description"]!.GetValue<string>() ==
            "Scheduled law firm switch from Legacy Old Law LLP to Legacy New Law LLP on 01/15/2099 by demo.user@legalsynq.test");
        items.Should().Contain(item =>
            item!["description"]!.GetValue<string>() ==
            "Law firm switched from Legacy Old Law LLP to Legacy New Law LLP by demo.user@legalsynq.test");
        items.Should().Contain(item =>
            item!["description"]!.GetValue<string>() ==
            "Law firm switched from Legacy New Law LLP to Unassigned by demo.user@legalsynq.test");

        using var verificationScope = _factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<LiensDbContext>();
        verificationDb.LienCaseNotes.Count(note =>
            note.TenantId == SeedHelper.TenantId &&
            note.CaseId == caseId &&
            note.Category == CaseNoteCategory.SettlementHistory).Should().Be(3);
    }

    [Fact]
    public async Task ServiceSettlementHistory_v3_records_law_firm_change_when_legacy_ids_have_no_contact_name()
    {
        var oldLawFirmId = Guid.CreateVersion7();
        var newLawFirmId = Guid.CreateVersion7();
        Guid caseId;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var caseEntity = Case.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                $"CASE-LF-UNRESOLVED-{Guid.CreateVersion7():N}"[..30],
                "Unresolved",
                "LawFirm",
                SeedHelper.UserId,
                notes: $"lawFirmId={oldLawFirmId}");
            caseId = caseEntity.Id;
            db.Cases.Add(caseEntity);
            await db.SaveChangesAsync();
        }

        var updateResponse = await _client.PatchAsJsonAsync("/service/update-details", new
        {
            caseId,
            lawFirmId = newLawFirmId,
            switchedDate = "2025-01-15",
        });
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await updateResponse.Content.ReadAsStringAsync()}");

        var historyResponse = await _client.PostAsJsonAsync("/service/settlement/history/v3", new
        {
            caseId,
            page = 1,
            limit = 10,
        });
        historyResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await historyResponse.Content.ReadAsStringAsync()}");

        var body = JsonNode.Parse(await historyResponse.Content.ReadAsStringAsync())!;
        body["totalCount"]!.GetValue<int>().Should().Be(1);
        var historyItem = body["data"]!.AsArray().Single()!;
        historyItem["type"]!.GetValue<string>().Should().Be("law-firm-change");
        historyItem["description"]!.GetValue<string>().Should().Contain(oldLawFirmId.ToString());
        historyItem["description"]!.GetValue<string>().Should().Contain(newLawFirmId.ToString());
    }

    [Fact]
    public async Task UpdateServicingDetails_allows_law_firm_change_when_case_status_is_negotiations()
    {
        var oldLawFirmId = Guid.CreateVersion7();
        var newLawFirmId = Guid.CreateVersion7();
        Guid caseId;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var caseEntity = Case.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                $"CASE-LF-NEGOTIATIONS-{Guid.CreateVersion7():N}"[..30],
                "Negotiations",
                "LawFirm",
                SeedHelper.UserId,
                notes: $"lawFirmId={oldLawFirmId}");
            caseId = caseEntity.Id;
            db.Cases.Add(caseEntity);
            await db.SaveChangesAsync();
        }

        // Legacy CASE STATUS lookup exposes the code "Negotiations" (canonical: InNegotiation).
        // The servicing screen sends that raw code alongside the law firm switch.
        var updateResponse = await _client.PatchAsJsonAsync("/service/update-details", new
        {
            caseId,
            caseStatusId = "Negotiations",
            lawFirmId = newLawFirmId,
        });
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await updateResponse.Content.ReadAsStringAsync()}");

        var caseResponse = await _client.GetAsync($"/api/liens/cases/{caseId}");
        caseResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var caseBody = JsonNode.Parse(await caseResponse.Content.ReadAsStringAsync())!;
        caseBody["lawFirmId"]!.GetValue<string>().Should().Be(newLawFirmId.ToString());
        caseBody["status"]!.GetValue<string>().Should()
            .BeOneOf(CaseStatus.InNegotiation, "Negotiations");
    }

    [Fact]
    public async Task Case_update_routes_record_law_firm_changes_in_servicing_history()
    {
        var oldLawFirmId = Guid.CreateVersion7();
        var legacyUpdateLawFirmId = Guid.CreateVersion7();
        var putUpdateLawFirmId = Guid.CreateVersion7();
        Guid caseId;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var caseEntity = Case.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                $"CASE-LF-ROUTES-{Guid.CreateVersion7():N}"[..30],
                "Route",
                "History",
                SeedHelper.UserId,
                notes: $"lawFirmId={oldLawFirmId}");
            caseId = caseEntity.Id;
            db.Cases.Add(caseEntity);
            await db.SaveChangesAsync();
        }

        var legacyUpdate = await _client.PatchAsJsonAsync($"/api/liens/cases/update/{caseId}", new
        {
            firstname = "Route",
            lastname = "History",
            lawFirmId = legacyUpdateLawFirmId,
        });
        legacyUpdate.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await legacyUpdate.Content.ReadAsStringAsync()}");

        var putUpdate = await _client.PutAsJsonAsync($"/api/liens/cases/{caseId}", new
        {
            clientFirstName = "Route",
            clientLastName = "History",
            lawFirmId = putUpdateLawFirmId,
        });
        putUpdate.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await putUpdate.Content.ReadAsStringAsync()}");

        using var verificationScope = _factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<LiensDbContext>();
        var history = await verificationDb.LienCaseNotes
            .Where(note =>
                note.TenantId == SeedHelper.TenantId &&
                note.CaseId == caseId &&
                note.Category == CaseNoteCategory.SettlementHistory)
            .OrderBy(note => note.CreatedAtUtc)
            .ToListAsync();

        history.Should().HaveCount(2);
        history[0].Content.Should().Contain(oldLawFirmId.ToString());
        history[0].Content.Should().Contain(legacyUpdateLawFirmId.ToString());
        history[1].Content.Should().Contain(legacyUpdateLawFirmId.ToString());
        history[1].Content.Should().Contain(putUpdateLawFirmId.ToString());
    }

    [Fact]
    public async Task Generic_case_update_schedules_and_promotes_due_law_firm_switch()
    {
        var oldLawFirmId = Guid.CreateVersion7();
        var newLawFirmId = Guid.CreateVersion7();
        Guid caseId;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var caseEntity = Case.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                $"CASE-LF-DUE-{Guid.CreateVersion7():N}"[..30],
                "Scheduled",
                "Switch",
                SeedHelper.UserId,
                notes: $"lawFirmId={oldLawFirmId}");
            caseId = caseEntity.Id;
            db.Cases.Add(caseEntity);
            await db.SaveChangesAsync();
        }

        var scheduleResponse = await _client.PutAsJsonAsync($"/api/liens/cases/{caseId}", new
        {
            clientFirstName = "Scheduled",
            clientLastName = "Switch",
            pendingLawFirmId = newLawFirmId,
            switchedDate = "2099-01-15",
        });
        scheduleResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await scheduleResponse.Content.ReadAsStringAsync()}");

        var scheduledCase = JsonNode.Parse(await scheduleResponse.Content.ReadAsStringAsync())!;
        scheduledCase["lawFirmId"]!.GetValue<string>().Should().Be(oldLawFirmId.ToString());
        scheduledCase["pendingLawFirmId"]!.GetValue<string>().Should().Be(newLawFirmId.ToString());

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var applied = await LawFirmChangeHistory.ApplyDueScheduledSwitchesAsync(
                db,
                new DateOnly(2099, 1, 15),
                CancellationToken.None);
            applied.Should().Be(1);
        }

        var promotedResponse = await _client.GetAsync($"/api/liens/cases/{caseId}");
        promotedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var promotedCase = JsonNode.Parse(await promotedResponse.Content.ReadAsStringAsync())!;
        promotedCase["lawFirmId"]!.GetValue<string>().Should().Be(newLawFirmId.ToString());
        promotedCase["pendingLawFirmId"].Should().BeNull();
        promotedCase["switchedDate"].Should().BeNull();

        using var verificationScope = _factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<LiensDbContext>();
        var history = await verificationDb.LienCaseNotes.SingleAsync(note =>
            note.TenantId == SeedHelper.TenantId &&
            note.CaseId == caseId &&
            note.Category == CaseNoteCategory.SettlementHistory);
        history.Content.Should().Contain("Scheduled law firm switch");
        history.Content.Should().Contain(oldLawFirmId.ToString());
        history.Content.Should().Contain(newLawFirmId.ToString());
    }

    [Fact]
    public async Task ServiceSettlementHistory_v3_includes_case_reassign_law_firm_changes()
    {
        var oldLawFirmOrgId = Guid.CreateVersion7();
        var newLawFirmOrgId = Guid.CreateVersion7();
        Guid caseId;

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            JwtTokenHelper.CreateFullAccessToken(
                SeedHelper.TenantId,
                SeedHelper.UserId,
                email: "demo.user@legalsynq.test"));

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var oldLawFirm = Contact.Create(
                SeedHelper.TenantId,
                oldLawFirmOrgId,
                ContactType.LawFirm,
                "Old",
                "Firm",
                SeedHelper.UserId,
                organization: "Old Reassignment Law LLP");
            var newLawFirm = Contact.Create(
                SeedHelper.TenantId,
                newLawFirmOrgId,
                ContactType.LawFirm,
                "New",
                "Firm",
                SeedHelper.UserId,
                organization: "New Reassignment Law LLP");
            var caseEntity = Case.Create(
                SeedHelper.TenantId,
                oldLawFirmOrgId,
                $"CASE-LF-REASSIGN-{Guid.CreateVersion7():N}"[..30],
                "Reassign",
                "History",
                SeedHelper.UserId);
            caseId = caseEntity.Id;

            db.Contacts.AddRange(oldLawFirm, newLawFirm);
            db.Cases.Add(caseEntity);
            await db.SaveChangesAsync();
        }

        var reassignResponse = await _client.PostAsJsonAsync("/api/liens/cases/reassign/lawfirm", new
        {
            caseId,
            lawfirm = newLawFirmOrgId,
        });
        reassignResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await reassignResponse.Content.ReadAsStringAsync()}");

        var historyResponse = await _client.PostAsJsonAsync("/service/settlement/history/v3", new
        {
            caseId,
            page = 1,
            limit = 10,
        });
        historyResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await historyResponse.Content.ReadAsStringAsync()}");

        var body = JsonNode.Parse(await historyResponse.Content.ReadAsStringAsync())!;
        body["totalCount"]!.GetValue<int>().Should().Be(1);
        body["data"]!.AsArray().Single()!["description"]!.GetValue<string>().Should().Be(
            "Law firm switched from Old Reassignment Law LLP to New Reassignment Law LLP by demo.user@legalsynq.test");
    }

    [Fact]
    public async Task ServiceDeletePayment_post_route_deletes_payment()
    {
        var createResponse = await _client.PostAsJsonAsync("/service/liens/settlement/payment", new
        {
            caseId = SeedHelper.CaseId,
            lienId = SeedHelper.LienId,
            paymentNumber = 77,
            amount = 123m,
            paymentDate = "2025-04-16",
            payee = "Delete Me",
            checkNumber = "CHK-DEL",
        });
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created,
            $"Body: {await createResponse.Content.ReadAsStringAsync()}");

        var createBody = await createResponse.Content.ReadFromJsonAsync<JsonDocument>();
        var paymentId = createBody!.RootElement.GetProperty("id").GetGuid();

        var deleteResponse = await _client.PostAsJsonAsync("/service/delete-payment", new
        {
            caseId = SeedHelper.CaseId,
            paymentId,
        });
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await deleteResponse.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task LegacyTask_routes_support_create_get_and_delete()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/liens/cases/task/create", new
        {
            caseId = SeedHelper.CaseId,
            title = "Legacy follow-up",
            description = "Call counsel",
            dueDate = "06/30/2026",
            priority = "Normal",
            status = "Open",
            assignedTo = "qa@test.local",
        });
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await createResponse.Content.ReadAsStringAsync()}");

        var listResponse = await _client.GetAsync($"/api/liens/cases/get-task/{SeedHelper.CaseId}");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await listResponse.Content.ReadAsStringAsync()}");

        var listBody = JsonNode.Parse(await listResponse.Content.ReadAsStringAsync())!;
        var task = listBody["data"]!.AsArray().Single(item =>
            item!["title"]!.GetValue<string>() == "Legacy follow-up")!;
        var taskId = Guid.Parse(task["taskId"]!.GetValue<string>());

        var deleteResponse = await _client.DeleteAsync($"/api/liens/cases/task/delete/{taskId}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await deleteResponse.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task LegacyCaseNote_routes_support_add_and_delete()
    {
        var addResponse = await _client.PostAsJsonAsync("/api/liens/cases/add-note", new
        {
            caseId = SeedHelper.CaseId,
            note = "Legacy case note",
        });
        addResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await addResponse.Content.ReadAsStringAsync()}");

        Guid noteId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            noteId = db.LienCaseNotes.Single(n => n.CaseId == SeedHelper.CaseId && n.Content == "Legacy case note").Id;
        }

        var deleteResponse = await _client.PostAsJsonAsync("/api/liens/cases/delete-note", new
        {
            noteId,
        });
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await deleteResponse.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task LegacyCaseNotes_route_keeps_details_update_notes_as_history()
    {
        const string firstDetailsNote = "First details update note";
        const string secondDetailsNote = "Second details update note";
        var firstUpdateResponse = await _client.PatchAsJsonAsync("/api/liens/cases/details-update", new
        {
            caseId = SeedHelper.CaseId,
            notes = firstDetailsNote,
        });
        firstUpdateResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await firstUpdateResponse.Content.ReadAsStringAsync()}");

        var secondUpdateResponse = await _client.PatchAsJsonAsync("/api/liens/cases/details-update", new
        {
            caseId = SeedHelper.CaseId,
            notes = secondDetailsNote,
        });
        secondUpdateResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await secondUpdateResponse.Content.ReadAsStringAsync()}");

        var response = await _client.GetAsync($"/api/liens/cases/notes/{SeedHelper.CaseId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await response.Content.ReadAsStringAsync()}");

        var data = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["data"]!.AsArray();
        data.Should().Contain(item => item!["note"]!.GetValue<string>() == firstDetailsNote);
        data.Should().Contain(item => item!["note"]!.GetValue<string>() == secondDetailsNote);
    }

    [Fact]
    public async Task LegacyCaseDocument_route_returns_uploaded_case_documents()
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(SeedHelper.CaseId.ToString()), "caseId");
        form.Add(new StringContent("14"), "DocFileTypeId");
        form.Add(new StringContent("legacy-case-doc"), "DocName");
        var file = new ByteArrayContent("%PDF-1.4 test"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", "legacy-case-doc.pdf");

        var uploadResponse = await _client.PostAsync("/api/liens/cases/upload/document", form);
        uploadResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await uploadResponse.Content.ReadAsStringAsync()}");

        var getResponse = await _client.GetAsync($"/api/liens/cases/get-casedocument/{SeedHelper.CaseId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await getResponse.Content.ReadAsStringAsync()}");

        var body = JsonNode.Parse(await getResponse.Content.ReadAsStringAsync())!;
        body["data"]!.AsArray().Should().Contain(item =>
            item!["filename"]!.GetValue<string>() == "legacy-case-doc");
    }

    [Fact]
    public async Task LegacyDashboardMetric_routes_return_200()
    {
        var deployedResponse = await _client.PostAsJsonAsync("/api/liens/cases/dashboard/deployed", new
        {
            startDate = "01/01/2024",
            endDate = "12/31/2026",
        });
        deployedResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await deployedResponse.Content.ReadAsStringAsync()}");

        var cashReceivedResponse = await _client.PostAsJsonAsync("/api/liens/cases/dashboard/cash-received", new
        {
            startDate = "01/01/2024",
            endDate = "12/31/2026",
        });
        cashReceivedResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await cashReceivedResponse.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task LegacyDashboardMetric_routes_keep_purchase_and_filtered_settlement_date_history()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var datedLien = Lien.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                "LIEN-DATED-DASHBOARD",
                LienType.MedicalLien,
                750m,
                SeedHelper.UserId,
                caseId: SeedHelper.CaseId,
                purchaseDate: new DateOnly(2025, 2, 1));

            db.Liens.Add(datedLien);
            db.ServicingItems.Add(ServicingItem.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                "LMC-DATED-DASHBOARD",
                "LegacyMedicalCode",
                "Dashboard deployed amount",
                "system",
                SeedHelper.UserId,
                lienId: datedLien.Id,
                notes: "billingAmount=750; purchaseAmount=750"));
            db.LienSettlements.Add(LienSettlement.Create(
                SeedHelper.TenantId,
                SeedHelper.CaseId,
                datedLien.Id,
                2,
                750m,
                SeedHelper.UserId,
                settlementDate: new DateOnly(2025, 2, 1)));
            await db.SaveChangesAsync();
        }

        var deployedResponse = await _client.PostAsJsonAsync("/api/liens/cases/dashboard/deployed", new
        {
            page = 1,
            limit = 1000,
        });
        deployedResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await deployedResponse.Content.ReadAsStringAsync()}");

        var deployed = JsonNode.Parse(await deployedResponse.Content.ReadAsStringAsync())!["data"]!;
        deployed["periodStart"]!.GetValue<string>().Should().BeEmpty();
        deployed["periodEnd"]!.GetValue<string>().Should().BeEmpty();
        deployed["totalCount"]!.GetValue<int>().Should().Be(1);

        var cashReceivedResponse = await _client.PostAsJsonAsync("/api/liens/cases/dashboard/cash-received", new
        {
            startDate = "02/01/2025",
            endDate = "02/01/2025",
        });
        cashReceivedResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await cashReceivedResponse.Content.ReadAsStringAsync()}");

        var cashReceived = JsonNode.Parse(await cashReceivedResponse.Content.ReadAsStringAsync())!["data"]!;
        cashReceived["periodStart"]!.GetValue<string>().Should().Be("02/01/2025");
        cashReceived["periodEnd"]!.GetValue<string>().Should().Be("02/01/2025");
        cashReceived["totalAmount"]!.GetValue<string>().Should().Be("750.00");
        cashReceived["totalCount"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task LegacyDashboardDeployed_uses_active_medical_code_purchase_amounts()
    {
        var tenantId = Guid.CreateVersion7();
        var orgId = Guid.CreateVersion7();
        var otherTenantId = Guid.CreateVersion7();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var medicalCodeLien = Lien.Create(
                tenantId,
                orgId,
                "LIEN-DASHBOARD-PURCHASE-MEDICAL",
                LienType.MedicalLien,
                1_000m,
                SeedHelper.UserId,
                purchaseDate: new DateOnly(2025, 2, 1));
            medicalCodeLien.SetFinancials(1_000m, SeedHelper.UserId, purchasePrice: 100m);

            var fallbackLien = Lien.Create(
                tenantId,
                orgId,
                "LIEN-DASHBOARD-PURCHASE-FALLBACK",
                LienType.MedicalLien,
                500m,
                SeedHelper.UserId,
                purchaseDate: new DateOnly(2025, 2, 1));
            fallbackLien.SetFinancials(500m, SeedHelper.UserId, purchasePrice: 300m);

            var futureLien = Lien.Create(
                tenantId,
                orgId,
                "LIEN-DASHBOARD-PURCHASE-FUTURE",
                LienType.MedicalLien,
                600m,
                SeedHelper.UserId,
                purchaseDate: new DateOnly(2099, 1, 1));
            futureLien.SetFinancials(600m, SeedHelper.UserId, purchasePrice: 400m);

            var undatedLien = Lien.Create(
                tenantId,
                orgId,
                "LIEN-DASHBOARD-PURCHASE-UNDATED",
                LienType.MedicalLien,
                700m,
                SeedHelper.UserId);

            var otherTenantLien = Lien.Create(
                otherTenantId,
                orgId,
                "LIEN-DASHBOARD-PURCHASE-OTHER",
                LienType.MedicalLien,
                10_000m,
                SeedHelper.UserId,
                purchaseDate: new DateOnly(2025, 2, 1));
            otherTenantLien.SetFinancials(10_000m, SeedHelper.UserId, purchasePrice: 10_000m);

            db.Liens.AddRange(medicalCodeLien, fallbackLien, futureLien, undatedLien, otherTenantLien);
            db.ServicingItems.AddRange(
                ServicingItem.Create(
                    tenantId,
                    orgId,
                    "LMC-DASHBOARD-PURCHASE-1",
                    "LegacyMedicalCode",
                    "First purchase amount",
                    "system",
                    SeedHelper.UserId,
                    lienId: medicalCodeLien.Id,
                    notes: "billingAmount=1,500; purchaseAmount=250"),
                ServicingItem.Create(
                    tenantId,
                    orgId,
                    "LMC-DASHBOARD-PURCHASE-2",
                    "LegacyMedicalCode",
                    "Second purchase amount",
                    "system",
                    SeedHelper.UserId,
                    lienId: medicalCodeLien.Id,
                    notes: "billingAmount=500; purchaseAmount=50"),
                ServicingItem.Create(
                    tenantId,
                    orgId,
                    "LMC-DASHBOARD-PURCHASE-FUTURE",
                    "LegacyMedicalCode",
                    "Future purchase amount",
                    "system",
                    SeedHelper.UserId,
                    lienId: futureLien.Id,
                    notes: "billingAmount=600; purchaseAmount=500"),
                ServicingItem.Create(
                    tenantId,
                    orgId,
                    "LMC-DASHBOARD-PURCHASE-UNDATED",
                    "LegacyMedicalCode",
                    "Undated purchase amount",
                    "system",
                    SeedHelper.UserId,
                    lienId: undatedLien.Id,
                    notes: "billingAmount=700; purchaseAmount=600"),
                ServicingItem.Create(
                    otherTenantId,
                    orgId,
                    "LMC-DASHBOARD-PURCHASE-OTHER",
                    "LegacyMedicalCode",
                    "Other-tenant purchase amount",
                    "system",
                    SeedHelper.UserId,
                    lienId: otherTenantLien.Id,
                    notes: "billingAmount=10,000; purchaseAmount=10,000"));
            await db.SaveChangesAsync();
        }

        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                JwtTokenHelper.CreateFullAccessToken(tenantId, SeedHelper.UserId, orgId));

        var response = await _client.PostAsJsonAsync("/api/liens/cases/dashboard/deployed", new { });
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await response.Content.ReadAsStringAsync()}");

        var deployed = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["data"]!;
        deployed["periodStart"]!.GetValue<string>().Should().BeEmpty();
        deployed["periodEnd"]!.GetValue<string>().Should().BeEmpty();
        deployed["totalAmount"]!.GetValue<string>().Should().Be("300.00");
        deployed["totalCount"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task LegacyDashboardCashReceived_without_date_range_uses_completed_settlement_amounts()
    {
        var tenantId = Guid.CreateVersion7();
        var orgId = Guid.CreateVersion7();
        var otherTenantId = Guid.CreateVersion7();
        var settlementDate = new DateOnly(2025, 2, 1);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var dashboardCase = Case.Create(
                tenantId,
                orgId,
                $"CASE-DASH-CASH-{Guid.CreateVersion7():N}",
                "Dashboard",
                "Cash",
                SeedHelper.UserId);
            var otherTenantCase = Case.Create(
                otherTenantId,
                orgId,
                $"CASE-DASH-OTHER-{Guid.CreateVersion7():N}",
                "Dashboard",
                "Other",
                SeedHelper.UserId);
            var metadataLien = Lien.Create(
                tenantId,
                orgId,
                "LIEN-DASHBOARD-METADATA",
                LienType.MedicalLien,
                1_000m,
                SeedHelper.UserId,
                caseId: dashboardCase.Id);
            metadataLien.SetFinancials(1_000m, SeedHelper.UserId, payoffAmount: 900m);

            var payoffLien = Lien.Create(
                tenantId,
                orgId,
                "LIEN-DASHBOARD-PAYOFF",
                LienType.MedicalLien,
                500m,
                SeedHelper.UserId,
                caseId: dashboardCase.Id);
            payoffLien.SetFinancials(500m, SeedHelper.UserId, payoffAmount: 220m);

            var paymentLien = Lien.Create(
                tenantId,
                orgId,
                "LIEN-DASHBOARD-PAYMENT",
                LienType.MedicalLien,
                600m,
                SeedHelper.UserId,
                caseId: dashboardCase.Id);

            var otherTenantLien = Lien.Create(
                otherTenantId,
                orgId,
                "LIEN-DASHBOARD-OTHER-TENANT",
                LienType.MedicalLien,
                10_000m,
                SeedHelper.UserId,
                caseId: otherTenantCase.Id);

            var deletedMetadata = LienSettlement.Create(
                tenantId,
                dashboardCase.Id,
                paymentLien.Id,
                2,
                700m,
                SeedHelper.UserId,
                settlementDate: settlementDate,
                note: "legacySettlementId=deleted; totalSettledAmount=700");
            deletedMetadata.SoftDelete(SeedHelper.UserId);

            var deletedPayment = SettlementPaymentDetail.Create(
                tenantId,
                dashboardCase.Id,
                paymentLien.Id,
                3,
                900m,
                SeedHelper.UserId);
            deletedPayment.SoftDelete(SeedHelper.UserId);

            db.Cases.AddRange(dashboardCase, otherTenantCase);
            db.Liens.AddRange(metadataLien, payoffLien, paymentLien, otherTenantLien);
            db.LienSettlements.AddRange(
                LienSettlement.Create(
                    tenantId,
                    dashboardCase.Id,
                    metadataLien.Id,
                    1,
                    50m,
                    SeedHelper.UserId,
                    settlementDate: settlementDate,
                    note: "legacySettlementId=1; totalSettledAmount=180"),
                LienSettlement.Create(
                    tenantId,
                    dashboardCase.Id,
                    metadataLien.Id,
                    2,
                    25m,
                    SeedHelper.UserId,
                    settlementDate: settlementDate,
                    note: "legacySettlementId=2; totalSettledAmount=20"),
                LienSettlement.Create(
                    tenantId,
                    dashboardCase.Id,
                    payoffLien.Id,
                    1,
                    60m,
                    SeedHelper.UserId,
                    settlementDate: settlementDate),
                LienSettlement.Create(
                    tenantId,
                    dashboardCase.Id,
                    paymentLien.Id,
                    1,
                    500m,
                    SeedHelper.UserId,
                    settlementDate: settlementDate),
                LienSettlement.Create(
                    otherTenantId,
                    otherTenantCase.Id,
                    otherTenantLien.Id,
                    1,
                    10_000m,
                    SeedHelper.UserId,
                    settlementDate: settlementDate),
                LienSettlement.Create(
                    tenantId,
                    dashboardCase.Id,
                    paymentLien.Id,
                    3,
                    1_000m,
                    SeedHelper.UserId,
                    settlementDate: new DateOnly(2099, 1, 1)),
                LienSettlement.Create(
                    tenantId,
                    dashboardCase.Id,
                    paymentLien.Id,
                    4,
                    2_000m,
                    SeedHelper.UserId),
                deletedMetadata);
            db.SettlementPaymentDetails.AddRange(
                SettlementPaymentDetail.Create(
                    tenantId,
                    dashboardCase.Id,
                    metadataLien.Id,
                    1,
                    1_000m,
                    SeedHelper.UserId),
                SettlementPaymentDetail.Create(
                    tenantId,
                    dashboardCase.Id,
                    payoffLien.Id,
                    1,
                    800m,
                    SeedHelper.UserId),
                SettlementPaymentDetail.Create(
                    tenantId,
                    dashboardCase.Id,
                    paymentLien.Id,
                    1,
                    300m,
                    SeedHelper.UserId),
                SettlementPaymentDetail.Create(
                    tenantId,
                    dashboardCase.Id,
                    paymentLien.Id,
                    2,
                    45m,
                    SeedHelper.UserId),
                deletedPayment,
                SettlementPaymentDetail.Create(
                    otherTenantId,
                    otherTenantCase.Id,
                    otherTenantLien.Id,
                    1,
                    10_000m,
                    SeedHelper.UserId));
            await db.SaveChangesAsync();
        }

        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                JwtTokenHelper.CreateFullAccessToken(tenantId, SeedHelper.UserId));

        var response = await _client.PostAsJsonAsync("/api/liens/cases/dashboard/cash-received", new
        {
            page = 1,
            limit = 1000,
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await response.Content.ReadAsStringAsync()}");

        var cashReceived = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["data"]!;
        cashReceived["periodStart"]!.GetValue<string>().Should().BeEmpty();
        cashReceived["periodEnd"]!.GetValue<string>().Should().BeEmpty();
        cashReceived["totalAmount"]!.GetValue<string>().Should().Be("635.00");
        cashReceived["totalCount"]!.GetValue<int>().Should().Be(4);
    }
}
