using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Liens.Api.Tests.Helpers;
using Liens.Domain.Entities;
using Liens.Domain.Enums;
using Liens.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Liens.Api.Tests.Tests;

public class LegacySettlementEndpointTests : IClassFixture<LiensApiFactory>, IAsyncLifetime
{
    private readonly LiensApiFactory _factory;
    private HttpClient _client = null!;

    public LegacySettlementEndpointTests(LiensApiFactory factory) => _factory = factory;

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

    // ── POST /service/liens/update/reduction ──────────────────────────────────

    [Fact]
    public async Task CreateReduction_returns201()
    {
        var resp = await _client.PostAsJsonAsync("/service/liens/update/reduction", new
        {
            caseId        = SeedHelper.CaseId,
            lienId        = SeedHelper.LienId,
            reductionDate = "2025-03-01",
            amount        = 250.00m,
            note          = "Test reduction",
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created,
            $"Body: {await resp.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task CreateReduction_accepts_legacy_bulk_payload()
    {
        var resp = await _client.PostAsJsonAsync("/service/liens/update/reduction", new
        {
            caseId = SeedHelper.CaseId,
            data = new[]
            {
                new
                {
                    liensId = SeedHelper.LienId,
                    reductionAmount = 111.1m,
                },
            },
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await resp.Content.ReadAsStringAsync()}");

        var historyResp = await _client.GetAsync($"/service/settlement/history/{SeedHelper.CaseId}");
        historyResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await historyResp.Content.ReadAsStringAsync()}");

        var doc = await historyResp.Content.ReadFromJsonAsync<JsonDocument>();
        doc!.RootElement.GetProperty("reductions")
            .EnumerateArray()
            .Should()
            .Contain(item =>
                item.GetProperty("lienId").GetGuid() == SeedHelper.LienId &&
                item.GetProperty("amount").GetDecimal() == 111.1m);
    }

    [Fact]
    public async Task GetReductionsByCase_returns_only_latest_reduction_per_lien_after_repeated_bulk_posts()
    {
        foreach (var reduction in new[]
                 {
                     new { reductionDate = "2099-12-31", amount = 750m },
                     new { reductionDate = "2020-01-01", amount = 900m },
                 })
        {
            var createResponse = await _client.PostAsJsonAsync("/service/liens/update/reduction", new
            {
                caseId = SeedHelper.CaseId,
                data = new[]
                {
                    new
                    {
                        liensId = SeedHelper.LienId,
                        reductionAmount = reduction.amount,
                        reduction.reductionDate,
                    },
                },
            });
            createResponse.StatusCode.Should().Be(HttpStatusCode.OK,
                $"Body: {await createResponse.Content.ReadAsStringAsync()}");
        }

        var response = await _client.GetAsync(
            $"/api/liens/settlement/reductions/case/{SeedHelper.CaseId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await response.Content.ReadAsStringAsync()}");
        var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
        var reductionsForLien = body!.RootElement
            .EnumerateArray()
            .Where(item => item.GetProperty("lienId").GetGuid() == SeedHelper.LienId)
            .ToArray();

        reductionsForLien.Should().ContainSingle();
        reductionsForLien[0].GetProperty("amount").GetDecimal().Should().Be(900m);
        reductionsForLien[0].GetProperty("reductionDate").GetString().Should().Be("2020-01-01");
    }

    [Fact]
    public async Task GetReductionsByCase_omits_legacy_settlement_reduction_without_a_date()
    {
        Lien lien;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            lien = Lien.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                $"LEGACY-RED-{Guid.CreateVersion7():N}",
                LienType.MedicalLien,
                2_000m,
                SeedHelper.UserId,
                caseId: SeedHelper.CaseId);
            var settlement = LienSettlement.Create(
                SeedHelper.TenantId,
                SeedHelper.CaseId,
                lien.Id,
                1,
                0m,
                SeedHelper.UserId,
                status: "Pending",
                note: "legacySettlementId=123; reductionAmount=425.50; reductionDate=; totalSettledAmount=");
            db.Liens.Add(lien);
            db.LienSettlements.Add(settlement);
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync(
            $"/api/liens/settlement/reductions/case/{SeedHelper.CaseId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await response.Content.ReadAsStringAsync()}");
        var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
        body!.RootElement.EnumerateArray()
            .Should()
            .NotContain(item => item.GetProperty("lienId").GetGuid() == lien.Id);
    }

    [Fact]
    public async Task GetReductionsByCase_returns_legacy_settlement_reduction_with_a_date()
    {
        Lien lien;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            lien = Lien.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                $"LEGACY-DATED-RED-{Guid.CreateVersion7():N}",
                LienType.MedicalLien,
                2_000m,
                SeedHelper.UserId,
                caseId: SeedHelper.CaseId);
            var settlement = LienSettlement.Create(
                SeedHelper.TenantId,
                SeedHelper.CaseId,
                lien.Id,
                1,
                0m,
                SeedHelper.UserId,
                status: "Pending",
                note: "legacySettlementId=124; reductionAmount=425.50; reductionDate=2026-04-27; totalSettledAmount=");
            db.Liens.Add(lien);
            db.LienSettlements.Add(settlement);
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync(
            $"/api/liens/settlement/reductions/case/{SeedHelper.CaseId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await response.Content.ReadAsStringAsync()}");
        var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
        var reduction = body!.RootElement.EnumerateArray()
            .Single(item => item.GetProperty("lienId").GetGuid() == lien.Id);
        reduction.GetProperty("amount").GetDecimal().Should().Be(425.50m);
        reduction.GetProperty("reductionDate").GetString().Should().Be("2026-04-27");
    }

    // ── POST /service/liens/update/settlement ─────────────────────────────────

    [Fact]
    public async Task CreateSettlement_returns201()
    {
        var resp = await _client.PostAsJsonAsync("/service/liens/update/settlement", new
        {
            caseId        = SeedHelper.CaseId,
            lienId        = SeedHelper.LienId,
            paymentNumber = 2,
            amount        = 2000m,
            status        = "Pending",
            note          = "Second payment",
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created,
            $"Body: {await resp.Content.ReadAsStringAsync()}");
    }

    [Theory]
    [InlineData("Open", LienStatus.Active)]
    [InlineData("Closed", LienStatus.Settled)]
    public async Task CreateSettlement_updates_lien_status_for_open_and_closed(
        string settlementStatus,
        string expectedLienStatus)
    {
        Lien lien;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            lien = Lien.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                $"SETTLEMENT-STATUS-{Guid.CreateVersion7():N}",
                LienType.MedicalLien,
                1_000m,
                SeedHelper.UserId,
                caseId: SeedHelper.CaseId);
            db.Liens.Add(lien);
            await db.SaveChangesAsync();
        }

        var resp = await _client.PostAsJsonAsync("/api/liens/settlement/create", new
        {
            caseId = SeedHelper.CaseId,
            lienId = lien.Id,
            paymentNumber = 1,
            amount = 1_000m,
            status = settlementStatus,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Created,
            $"Body: {await resp.Content.ReadAsStringAsync()}");

        using var verificationScope = _factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<LiensDbContext>();
        var persistedLien = await verificationDb.Liens.FindAsync(lien.Id);
        persistedLien!.Status.Should().Be(expectedLienStatus);
    }

    // ── POST /service/liens/settlement/payment ────────────────────────────────

    [Fact]
    public async Task CreatePayment_returns201()
    {
        var resp = await _client.PostAsJsonAsync("/service/liens/settlement/payment", new
        {
            caseId        = SeedHelper.CaseId,
            lienId        = SeedHelper.LienId,
            paymentNumber = 1,
            amount        = 1000m,
            paymentDate   = "2025-04-15",
            payee         = "Smith Law",
            checkNumber   = "CHK-9001",
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created,
            $"Body: {await resp.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task CreatePayment_with_closed_lien_status_preserves_settlement_fields_and_moves_lien_to_closed_list()
    {
        Lien lien;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            lien = Lien.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                $"PAYMENT-CLOSED-{Guid.CreateVersion7():N}",
                LienType.MedicalLien,
                1_000m,
                SeedHelper.UserId,
                caseId: SeedHelper.CaseId);
            lien.SetLegacyMedicalStatus("Open", SeedHelper.UserId);
            db.Liens.Add(lien);
            await db.SaveChangesAsync();
        }

        var response = await _client.PostAsJsonAsync("/api/liens/settlement/payments", new
        {
            amount = 0,
            caseId = SeedHelper.CaseId,
            lienId = lien.Id,
            notes = "",
            paymentDate = "2026-08-05",
            paymentMethod = "Check",
            referenceNumber = "123123123",
            lienStatus = "Closed",
            settlementType = "by_attorney",
            settlementStatus = "full_payment",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created,
            $"Body: {await response.Content.ReadAsStringAsync()}");
        var createdPayment = await response.Content.ReadFromJsonAsync<JsonDocument>();
        var createdPaymentId = createdPayment!.RootElement.GetProperty("id").GetGuid();
        var createdPaymentNumber = createdPayment.RootElement.GetProperty("paymentNumber").GetInt32();
        createdPaymentNumber.Should().BePositive();

        using var verificationScope = _factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<LiensDbContext>();
        var persistedLien = await verificationDb.Liens.FindAsync(lien.Id);
        persistedLien!.Status.Should().Be(LienStatus.Settled);
        persistedLien.ClosedAtUtc.Should().NotBeNull();

        var closedListResponse = await _client.GetAsync(
            $"/api/liens/liens/?search={Uri.EscapeDataString(lien.LienNumber)}&status=Closed&page=1&pageSize=20");
        closedListResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await closedListResponse.Content.ReadAsStringAsync()}");

        var closedList = await closedListResponse.Content.ReadFromJsonAsync<JsonDocument>();
        closedList!.RootElement.GetProperty("items").EnumerateArray()
            .Should().Contain(item => item.GetProperty("id").GetGuid() == lien.Id);

        var paymentDetailsResponse = await _client.GetAsync(
            $"/service/liens/settlement/payment-details/{SeedHelper.CaseId}");
        paymentDetailsResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await paymentDetailsResponse.Content.ReadAsStringAsync()}");

        var paymentDetails = await paymentDetailsResponse.Content.ReadFromJsonAsync<JsonDocument>();
        var recordedPayment = paymentDetails!.RootElement.GetProperty("data").EnumerateArray()
            .Single(item => item.GetProperty("checkNumber").GetString() == "123123123");
        recordedPayment.GetProperty("id").GetGuid().Should().Be(createdPaymentId);
        recordedPayment.GetProperty("paymentNumber").GetString().Should().Be(createdPaymentNumber.ToString());
        recordedPayment.GetProperty("amount").GetString().Should().Be("0.00");
        recordedPayment.GetProperty("amountToSettle").GetString().Should().Be("1000.00");
        recordedPayment.GetProperty("checkAmount").GetString().Should().Be("1000.00");
        recordedPayment.GetProperty("lienStatus").GetString().Should().Be("Closed");
        recordedPayment.GetProperty("lienStatusId").GetString().Should().Be("Closed");
        recordedPayment.GetProperty("typeId").GetString().Should().Be("by_attorney");
        recordedPayment.GetProperty("type").GetString().Should().Be("By Attorney");
        recordedPayment.GetProperty("statusId").GetString().Should().Be("full_payment");
        recordedPayment.GetProperty("status").GetString().Should().Be("Full Payment");
    }

    [Fact]
    public async Task CreatePayment_current_frontend_payload_keeps_full_payment_separate_from_closed_lien_status()
    {
        Lien lien;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            lien = Lien.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                $"PAYMENT-CURRENT-PAYLOAD-{Guid.CreateVersion7():N}",
                LienType.MedicalLien,
                3_590m,
                SeedHelper.UserId,
                caseId: SeedHelper.CaseId);
            lien.SetLegacyMedicalStatus("Open", SeedHelper.UserId);
            db.Liens.Add(lien);
            await db.SaveChangesAsync();
        }

        var response = await _client.PostAsJsonAsync("/api/liens/settlement/payments", new
        {
            caseId = SeedHelper.CaseId,
            lienId = lien.Id,
            amount = 3_590m,
            paymentDate = "2026-08-06",
            paymentMethod = "Check",
            referenceNumber = "453346",
            notes = "",
            settlementType = "by_attorney",
            settlementStatus = "full_payment",
            lienStatus = "Closed",
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created,
            $"Body: {await response.Content.ReadAsStringAsync()}");

        var created = await response.Content.ReadFromJsonAsync<JsonDocument>();
        created!.RootElement.GetProperty("settlementTypeId").GetString().Should().Be("by_attorney");
        created.RootElement.GetProperty("settlementStatusId").GetString().Should().Be("full_payment");
        var paymentId = created.RootElement.GetProperty("id").GetGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var persistedPayment = await db.SettlementPaymentDetails.FindAsync(paymentId);
            persistedPayment!.Note.Should().Contain("type=by_attorney");
            persistedPayment.Note.Should().Contain("status=full_payment");
            persistedPayment.Note.Should().NotContain("status=Closed");
        }

        var detailsResponse = await _client.GetAsync(
            $"/service/liens/settlement/payment-details/{SeedHelper.CaseId}");
        detailsResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await detailsResponse.Content.ReadAsStringAsync()}");
        var details = await detailsResponse.Content.ReadFromJsonAsync<JsonDocument>();
        var payment = details!.RootElement.GetProperty("data").EnumerateArray().Single(item =>
            item.GetProperty("checkNumber").GetString() == "453346");

        payment.GetProperty("typeId").GetString().Should().Be("by_attorney");
        payment.GetProperty("type").GetString().Should().Be("By Attorney");
        payment.GetProperty("statusId").GetString().Should().Be("full_payment");
        payment.GetProperty("status").GetString().Should().Be("Full Payment");
        payment.GetProperty("lienStatus").GetString().Should().Be("Closed");
    }

    [Fact]
    public async Task GetCaseById_returns_latest_settlement_status_value_and_id()
    {
        Guid lienStatusId;
        Guid settlementStatusId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var lienStatus = LookupValue.Create(
                LookupCategory.LienStatus,
                LienStatus.Settled,
                "Settled",
                SeedHelper.UserId,
                tenantId: SeedHelper.TenantId);
            lienStatusId = lienStatus.Id;
            db.LookupValues.Add(lienStatus);
            await db.SaveChangesAsync();

            settlementStatusId = db.LookupValues.Single(value =>
                value.Category == LookupCategory.SettlementType &&
                value.Code == "Full").Id;
        }

        var createResponse = await _client.PostAsJsonAsync("/api/liens/settlement/payments", new
        {
            caseId = SeedHelper.CaseId,
            lienId = SeedHelper.LienId,
            amount = 100m,
            paymentDate = "2026-08-20",
            paymentMethod = "Check",
            referenceNumber = "CHK-CASE-STATUS",
            notes = "",
            settlementType = "by_attorney",
            settlementStatus = settlementStatusId,
            lienStatus = "Closed",
        });
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created,
            $"Body: {await createResponse.Content.ReadAsStringAsync()}");

        var caseResponse = await _client.GetAsync($"/api/liens/cases/{SeedHelper.CaseId}");
        caseResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await caseResponse.Content.ReadAsStringAsync()}");

        var caseBody = await caseResponse.Content.ReadFromJsonAsync<JsonDocument>();
        caseBody!.RootElement.GetProperty("lienStatusId").GetString()
            .Should().Be(lienStatusId.ToString());
        caseBody.RootElement.GetProperty("lienStatus").GetString()
            .Should().Be("Closed");
        caseBody.RootElement.GetProperty("settlementStatusId").GetString()
            .Should().Be(settlementStatusId.ToString());
        caseBody.RootElement.GetProperty("settlementStatus").GetString()
            .Should().Be("Full Settlement");
    }

    [Fact]
    public async Task GetCaseById_suppresses_settlement_status_when_it_repeats_the_lien_status_rollup()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var lien = await db.Liens.FindAsync(SeedHelper.LienId);
            lien!.SetLegacyMedicalStatus("Closed", SeedHelper.UserId);
            await db.SaveChangesAsync();
        }

        var createResponse = await _client.PostAsJsonAsync("/api/liens/settlement/payments", new
        {
            caseId = SeedHelper.CaseId,
            lienId = SeedHelper.LienId,
            amount = 250m,
            paymentDate = "2026-08-25",
            paymentMethod = "Check",
            referenceNumber = "CHK-CLOSED-CLOSED",
            notes = "",
            settlementType = "by_attorney",
            settlementStatus = "Closed",
            lienStatus = "Closed",
        });
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created,
            $"Body: {await createResponse.Content.ReadAsStringAsync()}");

        var caseResponse = await _client.GetAsync($"/api/liens/cases/{SeedHelper.CaseId}");
        caseResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await caseResponse.Content.ReadAsStringAsync()}");

        var caseBody = await caseResponse.Content.ReadFromJsonAsync<JsonDocument>();
        caseBody!.RootElement.GetProperty("lienStatus").GetString().Should().Be("Closed");
        // Would otherwise be "Closed", which the UI renders as a redundant "Closed-Closed" chip.
        caseBody.RootElement.GetProperty("settlementStatus").GetString().Should().BeEmpty();
        caseBody.RootElement.GetProperty("settlementStatusId").GetString().Should().BeEmpty();
    }

    [Fact]
    public async Task GetCaseById_returns_open_lien_status_when_newest_lien_is_closed()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var closedLien = Lien.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                $"CLOSED-LIEN-{Guid.CreateVersion7():N}",
                LienType.MedicalLien,
                500m,
                SeedHelper.UserId,
                caseId: SeedHelper.CaseId);
            closedLien.SetLegacyMedicalStatus("Closed", SeedHelper.UserId);
            db.Liens.Add(closedLien);
            await db.SaveChangesAsync();
        }

        var caseResponse = await _client.GetAsync($"/api/liens/cases/{SeedHelper.CaseId}");
        caseResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await caseResponse.Content.ReadAsStringAsync()}");

        var caseBody = await caseResponse.Content.ReadFromJsonAsync<JsonDocument>();
        caseBody!.RootElement.GetProperty("status").GetString()
            .Should().Be(CaseStatus.PreDemand);
        caseBody.RootElement.GetProperty("lienStatus").GetString()
            .Should().Be("Open");
    }

    [Fact]
    public async Task GetCaseById_hides_settlement_status_until_all_liens_are_closed()
    {
        Guid settlementStatusId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            settlementStatusId = db.LookupValues.Single(value =>
                value.Category == LookupCategory.SettlementType &&
                value.Code == "Full").Id;

            var openLien = Lien.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                $"OPEN-LIEN-{Guid.CreateVersion7():N}",
                LienType.MedicalLien,
                500m,
                SeedHelper.UserId,
                caseId: SeedHelper.CaseId);
            db.Liens.Add(openLien);
            await db.SaveChangesAsync();
        }

        var createResponse = await _client.PostAsJsonAsync("/api/liens/settlement/payments", new
        {
            caseId = SeedHelper.CaseId,
            lienId = SeedHelper.LienId,
            amount = 100m,
            paymentDate = "2026-08-20",
            paymentMethod = "Check",
            referenceNumber = "CHK-PARTIAL-CASE-STATUS",
            notes = "",
            settlementType = "by_attorney",
            settlementStatus = settlementStatusId,
            lienStatus = "Closed",
        });
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created,
            $"Body: {await createResponse.Content.ReadAsStringAsync()}");

        var caseResponse = await _client.GetAsync($"/api/liens/cases/{SeedHelper.CaseId}");
        caseResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await caseResponse.Content.ReadAsStringAsync()}");

        var caseBody = await caseResponse.Content.ReadFromJsonAsync<JsonDocument>();
        caseBody!.RootElement.GetProperty("settlementStatusId").GetString()
            .Should().BeEmpty();
        caseBody.RootElement.GetProperty("settlementStatus").GetString()
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData("no_recovery")]
    [InlineData("no-recovery")]
    [InlineData("4")]
    public async Task GetCaseById_returns_no_recovery_status_while_liens_remain_open(
        string settlementStatus)
    {
        Guid caseId;
        Guid lienId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var caseEntity = Case.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                $"NO-RECOVERY-ALIAS-{Guid.CreateVersion7():N}"[..30],
                "Alias",
                "Test",
                SeedHelper.UserId);
            var lien = Lien.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                $"NO-RECOVERY-LIEN-{Guid.CreateVersion7():N}"[..30],
                LienType.MedicalLien,
                1_000m,
                SeedHelper.UserId,
                caseId: caseEntity.Id);
            caseId = caseEntity.Id;
            lienId = lien.Id;
            db.Cases.Add(caseEntity);
            db.Liens.Add(lien);
            await db.SaveChangesAsync();
        }

        var createResponse = await _client.PostAsJsonAsync("/api/liens/settlement/payments", new
        {
            caseId,
            lienId,
            amount = 0m,
            paymentDate = "2026-08-20",
            paymentMethod = "Other",
            referenceNumber = $"NO-RECOVERY-{settlementStatus}",
            notes = "No recovery declared",
            settlementType = "other",
            settlementStatus,
        });
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created,
            $"Body: {await createResponse.Content.ReadAsStringAsync()}");

        var caseResponse = await _client.GetAsync($"/api/liens/cases/{caseId}");
        caseResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await caseResponse.Content.ReadAsStringAsync()}");

        var caseBody = await caseResponse.Content.ReadFromJsonAsync<JsonDocument>();
        caseBody!.RootElement.GetProperty("settlementStatusId").GetString()
            .Should().Be("4");
        caseBody.RootElement.GetProperty("settlementStatus").GetString()
            .Should().Be("No Recovery");
    }

    [Fact]
    public async Task GetCaseById_returns_closed_when_a_no_recovery_case_has_a_payment()
    {
        foreach (var (status, referenceNumber) in new[]
                 {
                     ("no_recovery", "NO-RECOVERY-EARLIER"),
                     ("full_payment", "FULL-PAYMENT-LATER"),
                 })
        {
            var createResponse = await _client.PostAsJsonAsync("/api/liens/settlement/payments", new
            {
                caseId = SeedHelper.CaseId,
                lienId = SeedHelper.LienId,
                amount = status == "no_recovery" ? 0m : 100m,
                paymentDate = "2026-08-20",
                paymentMethod = "Other",
                referenceNumber,
                notes = "",
                settlementType = "other",
                settlementStatus = status,
            });
            createResponse.StatusCode.Should().Be(HttpStatusCode.Created,
                $"Body: {await createResponse.Content.ReadAsStringAsync()}");
        }

        var caseResponse = await _client.GetAsync($"/api/liens/cases/{SeedHelper.CaseId}");
        caseResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await caseResponse.Content.ReadAsStringAsync()}");

        var caseBody = await caseResponse.Content.ReadFromJsonAsync<JsonDocument>();
        caseBody!.RootElement.GetProperty("settlementStatusId").GetString()
            .Should().Be("Closed");
        caseBody.RootElement.GetProperty("settlementStatus").GetString()
            .Should().Be("Closed");
    }

    [Fact]
    public async Task UpdateLiensStatus_marks_a_paid_lien_and_case_closed()
    {
        Lien openLien;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            openLien = Lien.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                $"NO-RECOVERY-OPEN-{Guid.CreateVersion7():N}",
                LienType.MedicalLien,
                1_000m,
                SeedHelper.UserId,
                caseId: SeedHelper.CaseId);
            openLien.SetLegacyMedicalStatus("Open", SeedHelper.UserId);
            db.Liens.Add(openLien);
            await db.SaveChangesAsync();
        }

        var updateResponse = await _client.PostAsJsonAsync("/service/update-liens-status", new
        {
            caseId = SeedHelper.CaseId,
            lienIds = SeedHelper.LienId.ToString(),
            lienStatus = "Closed",
            closedDate = "08/21/2026",
            note = "Recovery exhausted",
        });
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await updateResponse.Content.ReadAsStringAsync()}");

        var caseResponse = await _client.GetAsync($"/api/liens/cases/{SeedHelper.CaseId}");
        caseResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await caseResponse.Content.ReadAsStringAsync()}");

        var caseBody = await caseResponse.Content.ReadFromJsonAsync<JsonDocument>();
        caseBody!.RootElement.GetProperty("settlementStatus").GetString()
            .Should().Be("Closed");
        caseBody.RootElement.GetProperty("settlementStatusId").GetString()
            .Should().Be("Closed");

        var servicingResponse = await _client.PostAsJsonAsync("/service/case/v3", new
        {
            keyword = "CASE-TEST-001",
            page = 1,
            limit = 10,
        });
        servicingResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await servicingResponse.Content.ReadAsStringAsync()}");
        var servicingBody = await servicingResponse.Content.ReadFromJsonAsync<JsonDocument>();
        servicingBody!.RootElement.GetProperty("data").EnumerateArray()
            .Single(item => item.GetProperty("caseId").GetGuid() == SeedHelper.CaseId)
            .GetProperty("settlementStatus").GetString()
            .Should().Be("Closed");

        using var verificationScope = _factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<LiensDbContext>();
        (await verificationDb.Liens.FindAsync(SeedHelper.LienId))!.Status
            .Should().Be(LienStatus.Settled);
        (await verificationDb.Liens.FindAsync(openLien.Id))!.Status
            .Should().Be(LienStatus.Active);

        var declaration = await verificationDb.SettlementPaymentDetails
            .SingleAsync(payment =>
                payment.TenantId == SeedHelper.TenantId &&
                payment.LienId == SeedHelper.LienId &&
                payment.PaymentDate == new DateOnly(2026, 8, 21));
        declaration.Amount.Should().Be(0m);
        declaration.Note.Should().Contain("Recovery exhausted");
        declaration.Note.Should().Contain("status=Closed");

        var paymentDetailsResponse = await _client.GetAsync(
            $"/service/liens/settlement/payment-details/{SeedHelper.CaseId}");
        paymentDetailsResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await paymentDetailsResponse.Content.ReadAsStringAsync()}");

        var paymentDetails = await paymentDetailsResponse.Content.ReadFromJsonAsync<JsonDocument>();
        var noRecoveryPayment = paymentDetails!.RootElement.GetProperty("data").EnumerateArray()
            .Single(item => item.GetProperty("id").GetGuid() == declaration.Id);
        noRecoveryPayment.GetProperty("amountToSettle").GetString().Should().Be("5000.00");
        noRecoveryPayment.GetProperty("status").GetString().Should().Be("Closed");
        noRecoveryPayment.GetProperty("checkAmount").GetString().Should().Be("5000.00");
        noRecoveryPayment.GetProperty("checkDate").GetString().Should().Be("08/21/2026");
    }

    [Fact]
    public async Task UpdateLiensStatus_marks_an_unpaid_lien_and_case_no_recovery()
    {
        Guid caseId;
        Guid lienId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var caseEntity = Case.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                $"NO-RECOVERY-{Guid.CreateVersion7():N}"[..30],
                "No",
                "Recovery",
                SeedHelper.UserId);
            var lien = Lien.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                $"UNPAID-LIEN-{Guid.CreateVersion7():N}"[..30],
                LienType.MedicalLien,
                1_000m,
                SeedHelper.UserId,
                caseId: caseEntity.Id);
            caseId = caseEntity.Id;
            lienId = lien.Id;
            db.Cases.Add(caseEntity);
            db.Liens.Add(lien);
            await db.SaveChangesAsync();
        }

        var updateResponse = await _client.PostAsJsonAsync("/service/update-liens-status", new
        {
            caseId,
            lienIds = lienId.ToString(),
            lienStatus = "Closed",
            closedDate = "08/21/2026",
            note = "No amount received",
        });
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await updateResponse.Content.ReadAsStringAsync()}");

        var caseResponse = await _client.GetAsync($"/api/liens/cases/{caseId}");
        caseResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await caseResponse.Content.ReadAsStringAsync()}");
        var caseBody = await caseResponse.Content.ReadFromJsonAsync<JsonDocument>();
        caseBody!.RootElement.GetProperty("settlementStatus").GetString()
            .Should().Be("No Recovery");
        caseBody.RootElement.GetProperty("settlementStatusId").GetString()
            .Should().Be("4");

        using var verificationScope = _factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<LiensDbContext>();
        (await verificationDb.Liens.FindAsync(lienId))!.Status
            .Should().Be(LienStatus.Settled);
        var declaration = await verificationDb.SettlementPaymentDetails
            .SingleAsync(payment =>
                payment.TenantId == SeedHelper.TenantId &&
                payment.LienId == lienId);
        declaration.Amount.Should().Be(0m);
        declaration.Note.Should().Contain("status=4");

        var paymentDetailsResponse = await _client.GetAsync(
            $"/service/liens/settlement/payment-details/{caseId}");
        paymentDetailsResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await paymentDetailsResponse.Content.ReadAsStringAsync()}");
        var paymentDetails = await paymentDetailsResponse.Content.ReadFromJsonAsync<JsonDocument>();
        var noRecoveryPayment = paymentDetails!.RootElement.GetProperty("data").EnumerateArray().Single();
        noRecoveryPayment.GetProperty("status").GetString().Should().Be("No Recovery");
        noRecoveryPayment.GetProperty("checkAmount").GetString().Should().BeEmpty();
        noRecoveryPayment.GetProperty("checkDate").GetString().Should().BeEmpty();
    }

    [Fact]
    public async Task GetCaseById_does_not_treat_settlement_type_id_as_no_recovery()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/liens/settlement/payments", new
        {
            caseId = SeedHelper.CaseId,
            lienId = SeedHelper.LienId,
            amount = 100m,
            paymentDate = "2026-08-20",
            paymentMethod = "Other",
            referenceNumber = "SETTLEMENT-TYPE-FOUR",
            notes = "",
            settlementType = "4",
            settlementStatus = "full_payment",
        });
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created,
            $"Body: {await createResponse.Content.ReadAsStringAsync()}");

        var caseResponse = await _client.GetAsync($"/api/liens/cases/{SeedHelper.CaseId}");
        caseResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await caseResponse.Content.ReadAsStringAsync()}");

        var caseBody = await caseResponse.Content.ReadFromJsonAsync<JsonDocument>();
        caseBody!.RootElement.GetProperty("settlementStatusId").GetString()
            .Should().BeEmpty();
        caseBody.RootElement.GetProperty("settlementStatus").GetString()
            .Should().BeEmpty();
    }

    [Fact]
    public async Task PaymentDetails_uses_recorded_amount_when_closed_lien_balance_is_zero()
    {
        Lien lien;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            lien = Lien.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                $"PAYMENT-ZERO-BALANCE-{Guid.CreateVersion7():N}",
                LienType.MedicalLien,
                1_200m,
                SeedHelper.UserId,
                caseId: SeedHelper.CaseId);
            lien.SetLegacyMedicalStatus("Open", SeedHelper.UserId);
            lien.Settle(1_200m, SeedHelper.UserId);
            db.Liens.Add(lien);
            await db.SaveChangesAsync();
        }

        var response = await _client.PostAsJsonAsync("/api/liens/settlement/payments", new
        {
            amount = 1_200m,
            caseId = SeedHelper.CaseId,
            lienId = lien.Id,
            paymentDate = "2026-08-06",
            paymentMethod = "Check",
            referenceNumber = "CHK-ZERO-BALANCE",
            settlementType = "by_attorney",
            settlementStatus = "full_payment",
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created,
            $"Body: {await response.Content.ReadAsStringAsync()}");

        var detailsResponse = await _client.GetAsync(
            $"/service/liens/settlement/payment-details/{SeedHelper.CaseId}");
        var details = await detailsResponse.Content.ReadFromJsonAsync<JsonDocument>();
        var payment = details!.RootElement.GetProperty("data").EnumerateArray().Single(item =>
            item.GetProperty("checkNumber").GetString() == "CHK-ZERO-BALANCE");

        payment.GetProperty("paymentNumber").GetString().Should().NotBe("0");
        payment.GetProperty("amountToSettle").GetString().Should().Be("1200.00");
        payment.GetProperty("checkAmount").GetString().Should().Be("1200.00");
    }

    [Fact]
    public async Task PaymentDetails_assigns_distinct_display_numbers_to_historical_zero_number_rows()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            db.SettlementPaymentDetails.AddRange(
                SettlementPaymentDetail.Create(
                    SeedHelper.TenantId,
                    SeedHelper.CaseId,
                    SeedHelper.LienId,
                    0,
                    250m,
                    SeedHelper.UserId,
                    new DateOnly(2026, 8, 6),
                    checkNumber: "CHK-HISTORICAL-ZERO-1"),
                SettlementPaymentDetail.Create(
                    SeedHelper.TenantId,
                    SeedHelper.CaseId,
                    SeedHelper.LienId,
                    0,
                    300m,
                    SeedHelper.UserId,
                    new DateOnly(2026, 8, 6),
                    checkNumber: "CHK-HISTORICAL-ZERO-2"));
            await db.SaveChangesAsync();
        }

        var detailsResponse = await _client.GetAsync(
            $"/service/liens/settlement/payment-details/{SeedHelper.CaseId}");
        var details = await detailsResponse.Content.ReadFromJsonAsync<JsonDocument>();
        var payments = details!.RootElement.GetProperty("data").EnumerateArray()
            .Where(item => item.GetProperty("checkNumber").GetString() is
                "CHK-HISTORICAL-ZERO-1" or "CHK-HISTORICAL-ZERO-2")
            .ToList();

        payments.Should().HaveCount(2);
        payments.Select(item => item.GetProperty("paymentNumber").GetString())
            .Should().OnlyHaveUniqueItems().And.NotContain("0");
    }

    [Fact]
    public async Task CreatePayment_legacy_closed_settlement_status_still_moves_lien_to_closed_list()
    {
        var response = await _client.PostAsJsonAsync("/api/liens/settlement/payments", new
        {
            amount = 100m,
            caseId = SeedHelper.CaseId,
            lienId = SeedHelper.LienId,
            paymentDate = "2026-08-05",
            paymentMethod = "Check",
            referenceNumber = "CHK-LEGACY-CLOSED",
            settlementStatus = "Closed",
            settlementType = "full_payment",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created,
            $"Body: {await response.Content.ReadAsStringAsync()}");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
        var persistedLien = await db.Liens.FindAsync(SeedHelper.LienId);
        persistedLien!.Status.Should().Be(LienStatus.Settled);

        var detailsResponse = await _client.GetAsync(
            $"/service/liens/settlement/payment-details/{SeedHelper.CaseId}");
        var details = await detailsResponse.Content.ReadFromJsonAsync<JsonDocument>();
        var payment = details!.RootElement.GetProperty("data").EnumerateArray().Single(item =>
            item.GetProperty("checkNumber").GetString() == "CHK-LEGACY-CLOSED");
        payment.GetProperty("typeId").GetString().Should().Be("other");
        payment.GetProperty("type").GetString().Should().Be("Other");
        payment.GetProperty("statusId").GetString().Should().Be("full_payment");
        payment.GetProperty("status").GetString().Should().Be("Full Payment");
    }

    [Theory]
    [InlineData("by_attorney", "By Attorney")]
    [InlineData("by_medical_provider", "By Medical Provider")]
    [InlineData("by_funding_company", "By Funding Company")]
    [InlineData("other", "Other")]
    public async Task PaymentDetails_returns_each_supported_settlement_type(
        string settlementType,
        string expectedDisplayName)
    {
        var checkNumber = $"CHK-TYPE-{settlementType}";
        var createResponse = await _client.PostAsJsonAsync("/api/liens/settlement/payments", new
        {
            caseId = SeedHelper.CaseId,
            lienId = SeedHelper.LienId,
            amount = 100m,
            paymentDate = "2026-08-06",
            paymentMethod = "Check",
            referenceNumber = checkNumber,
            settlementType,
            settlementStatus = "full_payment",
            lienStatus = "Active",
        });
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created,
            $"Body: {await createResponse.Content.ReadAsStringAsync()}");
        var createdPayment = await createResponse.Content.ReadFromJsonAsync<JsonDocument>();
        createdPayment!.RootElement.GetProperty("settlementTypeId").GetString()
            .Should().Be(settlementType);

        var detailsResponse = await _client.GetAsync(
            $"/service/liens/settlement/payment-details/{SeedHelper.CaseId}");
        var details = await detailsResponse.Content.ReadFromJsonAsync<JsonDocument>();
        var payment = details!.RootElement.GetProperty("data").EnumerateArray().Single(item =>
            item.GetProperty("checkNumber").GetString() == checkNumber);

        payment.GetProperty("lienStatus").GetString().Should().Be("Open");
        payment.GetProperty("lienStatusId").GetString().Should().Be("Open");
        payment.GetProperty("typeId").GetString().Should().Be(settlementType);
        payment.GetProperty("type").GetString().Should().Be(expectedDisplayName);
    }

    [Fact]
    public async Task CreatePayment_accepts_legacy_type_and_status_aliases()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/liens/settlement/payments", new
        {
            caseId = SeedHelper.CaseId,
            lienId = SeedHelper.LienId,
            amount = 100m,
            paymentDate = "2026-08-06",
            paymentMethod = "Check",
            referenceNumber = "CHK-LEGACY-ALIASES",
            type = "by_medical_provider",
            status = "full_payment",
            lienStatus = "Active",
        });
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created,
            $"Body: {await createResponse.Content.ReadAsStringAsync()}");

        var detailsResponse = await _client.GetAsync(
            $"/service/liens/settlement/payment-details/{SeedHelper.CaseId}");
        var details = await detailsResponse.Content.ReadFromJsonAsync<JsonDocument>();
        var payment = details!.RootElement.GetProperty("data").EnumerateArray().Single(item =>
            item.GetProperty("checkNumber").GetString() == "CHK-LEGACY-ALIASES");

        payment.GetProperty("typeId").GetString().Should().Be("by_medical_provider");
        payment.GetProperty("type").GetString().Should().Be("By Medical Provider");
        payment.GetProperty("statusId").GetString().Should().Be("full_payment");
        payment.GetProperty("status").GetString().Should().Be("Full Payment");
    }

    [Fact]
    public async Task PaymentDetails_returns_fields_sent_by_the_current_payment_form()
    {
        Guid settlementTypeId;
        Guid settlementStatusId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            settlementTypeId = db.LookupValues.Single(x =>
                x.Category == LookupCategory.SettlementStatus && x.Code == "Pending").Id;
            settlementStatusId = db.LookupValues.Single(x =>
                x.Category == LookupCategory.SettlementType && x.Code == "Full").Id;
        }

        var createResponse = await _client.PostAsJsonAsync("/api/liens/settlement/payments", new
        {
            caseId = SeedHelper.CaseId,
            lienId = SeedHelper.LienId,
            amount = 100m,
            paymentDate = "2026-08-05",
            paymentMethod = "Check",
            referenceNumber = "CHK-CURRENT-UI",
            notes = "Payment received from counsel.",
            settlementType = settlementTypeId,
            settlementStatus = settlementStatusId,
        });
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created,
            $"Body: {await createResponse.Content.ReadAsStringAsync()}");

        var detailsResponse = await _client.GetAsync(
            $"/service/liens/settlement/payment-details/{SeedHelper.CaseId}");
        detailsResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await detailsResponse.Content.ReadAsStringAsync()}");

        var payload = await detailsResponse.Content.ReadFromJsonAsync<JsonDocument>();
        var payment = payload!.RootElement.GetProperty("data").EnumerateArray().Single(item =>
            item.GetProperty("checkNumber").GetString() == "CHK-CURRENT-UI");

        payment.GetProperty("payor").GetString().Should().Be("Check");
        payment.GetProperty("note").GetString().Should().Be("Payment received from counsel.");
        payment.GetProperty("typeId").GetString().Should().Be(settlementTypeId.ToString());
        payment.GetProperty("type").GetString().Should().Be("Pending");
        payment.GetProperty("statusId").GetString().Should().Be(settlementStatusId.ToString());
        payment.GetProperty("status").GetString().Should().Be("Full Settlement");
        payment.GetProperty("netProfit").GetString().Should().Be("0.00");
    }

    [Fact]
    public async Task UpdatePayment_changes_type_and_fields_without_duplicates_or_sibling_changes()
    {
        var target = SettlementPaymentDetail.Create(
            SeedHelper.TenantId,
            SeedHelper.CaseId,
            SeedHelper.LienId,
            11,
            100m,
            SeedHelper.UserId,
            new DateOnly(2026, 8, 1),
            checkNumber: "OLD-REFERENCE",
            note: "Original payment note\n[legacy-meta]\npaymentMethod=Wire; type=by_attorney; status=reduced_payment; netProfit=12.50; customFlag=preserved");
        var sibling = SettlementPaymentDetail.Create(
            SeedHelper.TenantId,
            SeedHelper.CaseId,
            SeedHelper.LienId,
            12,
            75m,
            SeedHelper.UserId,
            new DateOnly(2026, 8, 2),
            checkNumber: "SIBLING-REFERENCE",
            note: "Sibling payment\n[legacy-meta]\npaymentMethod=Cash; type=by_medical_provider; status=partial_loss; customFlag=sibling");
        int paymentCountBefore;
        int settlementCountBefore;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            db.SettlementPaymentDetails.AddRange(target, sibling);
            await db.SaveChangesAsync();
            paymentCountBefore = db.SettlementPaymentDetails.Count();
            settlementCountBefore = db.LienSettlements.Count();
        }

        var response = await _client.PutAsJsonAsync($"/api/liens/settlement/payments/{target.Id}", new
        {
            amount = 530m,
            paymentDate = "2026-08-16",
            paymentMethod = "Check",
            referenceNumber = "123456",
            notes = "Payment Testing",
            settlementType = "by_funding_company",
            settlementStatus = "full_payment",
            lienStatus = "Closed",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await response.Content.ReadAsStringAsync()}");
        var updatedResponse = await response.Content.ReadFromJsonAsync<JsonDocument>();
        var updatedRoot = updatedResponse!.RootElement;
        updatedRoot.GetProperty("id").GetGuid().Should().Be(target.Id);
        updatedRoot.GetProperty("amount").GetDecimal().Should().Be(530m);
        updatedRoot.GetProperty("paymentDate").GetString().Should().Be("2026-08-16");
        updatedRoot.GetProperty("paymentMethod").GetString().Should().Be("Check");
        updatedRoot.GetProperty("checkNumber").GetString().Should().Be("123456");
        updatedRoot.GetProperty("note").GetString().Should().Be("Payment Testing");
        updatedRoot.GetProperty("settlementTypeId").GetString().Should().Be("by_funding_company");
        updatedRoot.GetProperty("settlementStatusId").GetString().Should().Be("full_payment");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            db.SettlementPaymentDetails.Count().Should().Be(paymentCountBefore);
            db.LienSettlements.Count().Should().Be(settlementCountBefore);

            var persistedTarget = await db.SettlementPaymentDetails.FindAsync(target.Id);
            persistedTarget!.Amount.Should().Be(530m);
            persistedTarget.PaymentDate.Should().Be(new DateOnly(2026, 8, 16));
            persistedTarget.CheckNumber.Should().Be("123456");
            persistedTarget.Note.Should().StartWith("Payment Testing");
            persistedTarget.Note.Should().Contain("paymentMethod=Check");
            persistedTarget.Note.Should().Contain("type=by_funding_company");
            persistedTarget.Note.Should().Contain("status=full_payment");
            persistedTarget.Note.Should().Contain("netProfit=12.50");
            persistedTarget.Note.Should().Contain("customFlag=preserved");
            persistedTarget.UpdatedByUserId.Should().Be(SeedHelper.UserId);

            var persistedSibling = await db.SettlementPaymentDetails.FindAsync(sibling.Id);
            persistedSibling!.Amount.Should().Be(75m);
            persistedSibling.CheckNumber.Should().Be("SIBLING-REFERENCE");
            persistedSibling.Note.Should().Contain("type=by_medical_provider");
            persistedSibling.Note.Should().Contain("customFlag=sibling");

            var persistedLien = await db.Liens.FindAsync(SeedHelper.LienId);
            persistedLien!.Status.Should().Be(LienStatus.Settled);
        }

        var detailsResponse = await _client.GetAsync(
            $"/service/liens/settlement/payment-details/{SeedHelper.CaseId}");
        detailsResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await detailsResponse.Content.ReadAsStringAsync()}");
        var details = await detailsResponse.Content.ReadFromJsonAsync<JsonDocument>();
        var payment = details!.RootElement.GetProperty("data").EnumerateArray().Single(item =>
            item.GetProperty("id").GetGuid() == target.Id);
        payment.GetProperty("amount").GetString().Should().Be("530.00");
        payment.GetProperty("checkDate").GetString().Should().Be("08/16/2026");
        payment.GetProperty("checkNumber").GetString().Should().Be("123456");
        payment.GetProperty("payor").GetString().Should().Be("Check");
        payment.GetProperty("note").GetString().Should().Be("Payment Testing");
        payment.GetProperty("typeId").GetString().Should().Be("by_funding_company");
        payment.GetProperty("type").GetString().Should().Be("By Funding Company");
        payment.GetProperty("statusId").GetString().Should().Be("full_payment");
        payment.GetProperty("status").GetString().Should().Be("Full Payment");
        payment.GetProperty("lienStatus").GetString().Should().Be("Closed");
    }

    [Fact]
    public async Task UpdatePayment_returns404_for_missing_or_cross_tenant_payment()
    {
        var otherTenantPayment = SettlementPaymentDetail.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            1,
            100m,
            SeedHelper.UserId,
            new DateOnly(2026, 8, 1),
            checkNumber: "OTHER-TENANT",
            note: "Other tenant\n[legacy-meta]\ntype=by_attorney; status=full_payment");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            db.SettlementPaymentDetails.Add(otherTenantPayment);
            await db.SaveChangesAsync();
        }

        var request = new
        {
            amount = 530m,
            paymentDate = "2026-08-16",
            paymentMethod = "Check",
            referenceNumber = "123456",
            notes = "Payment Testing",
            settlementType = "by_funding_company",
            settlementStatus = "full_payment",
            lienStatus = "Closed",
        };

        var missingResponse = await _client.PutAsJsonAsync(
            $"/api/liens/settlement/payments/{Guid.CreateVersion7()}", request);
        missingResponse.StatusCode.Should().Be(HttpStatusCode.NotFound,
            $"Body: {await missingResponse.Content.ReadAsStringAsync()}");

        var crossTenantResponse = await _client.PutAsJsonAsync(
            $"/api/liens/settlement/payments/{otherTenantPayment.Id}", request);
        crossTenantResponse.StatusCode.Should().Be(HttpStatusCode.NotFound,
            $"Body: {await crossTenantResponse.Content.ReadAsStringAsync()}");

        using var verificationScope = _factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<LiensDbContext>();
        var persistedOtherTenant = await verificationDb.SettlementPaymentDetails.FindAsync(otherTenantPayment.Id);
        persistedOtherTenant!.Amount.Should().Be(100m);
        persistedOtherTenant.CheckNumber.Should().Be("OTHER-TENANT");
        persistedOtherTenant.Note.Should().Contain("type=by_attorney");
    }

    [Fact]
    public async Task UpdatePayment_rejects_unknown_and_invalid_fields_without_changes()
    {
        var payment = SettlementPaymentDetail.Create(
            SeedHelper.TenantId,
            SeedHelper.CaseId,
            SeedHelper.LienId,
            13,
            100m,
            SeedHelper.UserId,
            new DateOnly(2026, 8, 1),
            checkNumber: "STRICT-CONTRACT",
            note: "Strict contract\n[legacy-meta]\ntype=by_attorney; status=full_payment");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            db.SettlementPaymentDetails.Add(payment);
            await db.SaveChangesAsync();
        }

        var unknownFieldResponse = await _client.PutAsJsonAsync(
            $"/api/liens/settlement/payments/{payment.Id}", new
            {
                amount = 530m,
                paymentDate = "2026-08-16",
                paymentMethod = "Check",
                referenceNumber = "123456",
                notes = "Payment Testing",
                settlementType = "by_funding_company",
                settlementStatus = "full_payment",
                lienStatus = "Closed",
                caseId = SeedHelper.CaseId,
            });
        unknownFieldResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            $"Body: {await unknownFieldResponse.Content.ReadAsStringAsync()}");

        var invalidFieldResponse = await _client.PutAsJsonAsync(
            $"/api/liens/settlement/payments/{payment.Id}", new
            {
                amount = -1m,
                paymentDate = "2026-08-16",
                paymentMethod = "Check",
                referenceNumber = "123456",
                notes = "Payment Testing",
                settlementType = "by_funding_company",
                settlementStatus = "full_payment",
                lienStatus = "Closed",
            });
        invalidFieldResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            $"Body: {await invalidFieldResponse.Content.ReadAsStringAsync()}");

        using var verificationScope = _factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<LiensDbContext>();
        var persisted = await verificationDb.SettlementPaymentDetails.FindAsync(payment.Id);
        persisted!.Amount.Should().Be(100m);
        persisted.CheckNumber.Should().Be("STRICT-CONTRACT");
        persisted.Note.Should().Contain("type=by_attorney");
        var persistedLien = await verificationDb.Liens.FindAsync(SeedHelper.LienId);
        persistedLien!.Status.Should().Be(LienStatus.Draft);
    }

    [Fact]
    public async Task UpdatePayment_rejects_missing_null_and_blank_required_strings_without_changes()
    {
        var payment = await SeedEditablePaymentAsync(
            14,
            "REQUIRED-FIELDS",
            "Required fields\n[legacy-meta]\npaymentMethod=Wire; type=by_attorney; status=reduced_payment");
        var invalidRequests = new List<(string Field, Dictionary<string, object?> Body)>();

        foreach (var field in new[]
                 {
                     "paymentMethod",
                     "referenceNumber",
                     "notes",
                     "settlementType",
                     "settlementStatus",
                     "lienStatus",
                 })
        {
            var body = ValidPaymentUpdateRequest();
            body.Remove(field);
            invalidRequests.Add((field, body));
        }

        foreach (var (field, value) in new (string Field, string? Value)[]
                 {
                     ("paymentMethod", null),
                     ("paymentMethod", ""),
                     ("paymentMethod", "   "),
                     ("referenceNumber", null),
                     ("referenceNumber", ""),
                     ("referenceNumber", "   "),
                     ("notes", null),
                     ("settlementType", null),
                     ("settlementType", "   "),
                     ("settlementStatus", null),
                     ("settlementStatus", "   "),
                     ("lienStatus", null),
                     ("lienStatus", "   "),
                 })
        {
            var body = ValidPaymentUpdateRequest();
            body[field] = value;
            invalidRequests.Add((field, body));
        }

        foreach (var (field, body) in invalidRequests)
        {
            var response = await _client.PutAsJsonAsync(
                $"/api/liens/settlement/payments/{payment.Id}", body);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
                $"{field} must be rejected. Body: {await response.Content.ReadAsStringAsync()}");
            (await response.Content.ReadAsStringAsync()).Should().ContainEquivalentOf(field);
        }

        using var verificationScope = _factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<LiensDbContext>();
        var persisted = await verificationDb.SettlementPaymentDetails.FindAsync(payment.Id);
        persisted!.Amount.Should().Be(100m);
        persisted.CheckNumber.Should().Be("REQUIRED-FIELDS");
        persisted.Note.Should().Contain("type=by_attorney");
        var persistedLien = await verificationDb.Liens.FindAsync(SeedHelper.LienId);
        persistedLien!.Status.Should().Be(LienStatus.Draft);
    }

    [Fact]
    public async Task UpdatePayment_rejects_reserved_metadata_syntax_without_changes()
    {
        var payment = await SeedEditablePaymentAsync(
            15,
            "SAFE-METADATA",
            "Safe note\n[legacy-meta]\npaymentMethod=Wire; type=by_attorney; status=reduced_payment; customFlag=preserved");
        var invalidValues = new (string Field, string Value)[]
        {
            ("paymentMethod", "Check; type=by_funding_company"),
            ("paymentMethod", "Check\r\nWire"),
            ("settlementType", "by_funding_company=status"),
            ("settlementStatus", "[legacy-meta]"),
            ("settlementStatus", "full_payment\ncustom=overwritten"),
            ("notes", "User note [legacy-meta] status=full_payment"),
        };

        foreach (var (field, value) in invalidValues)
        {
            var body = ValidPaymentUpdateRequest();
            body[field] = value;
            var response = await _client.PutAsJsonAsync(
                $"/api/liens/settlement/payments/{payment.Id}", body);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
                $"{field} metadata syntax must be rejected. Body: {await response.Content.ReadAsStringAsync()}");
            (await response.Content.ReadAsStringAsync()).Should().ContainEquivalentOf(field);
        }

        using var verificationScope = _factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<LiensDbContext>();
        var persisted = await verificationDb.SettlementPaymentDetails.FindAsync(payment.Id);
        persisted!.Amount.Should().Be(100m);
        persisted.CheckNumber.Should().Be("SAFE-METADATA");
        persisted.Note.Should().Contain("customFlag=preserved");
        persisted.Note.Should().Contain("type=by_attorney");
        var persistedLien = await verificationDb.Liens.FindAsync(SeedHelper.LienId);
        persistedLien!.Status.Should().Be(LienStatus.Draft);
    }

    [Fact]
    public async Task UpdatePayment_enforces_reference_maximum_and_normalizes_open_status()
    {
        var payment = await SeedEditablePaymentAsync(
            16,
            "REFERENCE-BOUNDARY",
            "Boundary note\n[legacy-meta]\npaymentMethod=Wire; type=by_attorney; status=reduced_payment");
        var overMaximum = ValidPaymentUpdateRequest();
        overMaximum["referenceNumber"] = new string('X', 101);

        var invalidResponse = await _client.PutAsJsonAsync(
            $"/api/liens/settlement/payments/{payment.Id}", overMaximum);
        invalidResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            $"Body: {await invalidResponse.Content.ReadAsStringAsync()}");
        (await invalidResponse.Content.ReadAsStringAsync()).Should().ContainEquivalentOf("referenceNumber");

        using (var verificationScope = _factory.Services.CreateScope())
        {
            var verificationDb = verificationScope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var unchanged = await verificationDb.SettlementPaymentDetails.FindAsync(payment.Id);
            unchanged!.CheckNumber.Should().Be("REFERENCE-BOUNDARY");
            var unchangedLien = await verificationDb.Liens.FindAsync(SeedHelper.LienId);
            unchangedLien!.Status.Should().Be(LienStatus.Draft);
        }

        var atMaximum = ValidPaymentUpdateRequest();
        atMaximum["referenceNumber"] = new string('R', 100);
        atMaximum["notes"] = "";
        atMaximum["lienStatus"] = "Open";
        var validResponse = await _client.PutAsJsonAsync(
            $"/api/liens/settlement/payments/{payment.Id}", atMaximum);
        validResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await validResponse.Content.ReadAsStringAsync()}");
        var responseBody = await validResponse.Content.ReadFromJsonAsync<JsonDocument>();
        responseBody!.RootElement.GetProperty("checkNumber").GetString().Should().HaveLength(100);
        responseBody.RootElement.GetProperty("note").ValueKind.Should().Be(JsonValueKind.Null);

        atMaximum["notes"] = "Normal punctuation; equation=a=b";
        var punctuationResponse = await _client.PutAsJsonAsync(
            $"/api/liens/settlement/payments/{payment.Id}", atMaximum);
        punctuationResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await punctuationResponse.Content.ReadAsStringAsync()}");
        var punctuationBody = await punctuationResponse.Content.ReadFromJsonAsync<JsonDocument>();
        punctuationBody!.RootElement.GetProperty("note").GetString().Should().Be("Normal punctuation; equation=a=b");

        using var finalScope = _factory.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<LiensDbContext>();
        var persisted = await finalDb.SettlementPaymentDetails.FindAsync(payment.Id);
        persisted!.CheckNumber.Should().HaveLength(100);
        persisted.Note.Should().StartWith("Normal punctuation; equation=a=b");
        var persistedLien = await finalDb.Liens.FindAsync(SeedHelper.LienId);
        persistedLien!.Status.Should().Be(LienStatus.Active);
    }

    // ── DELETE /service/delete-payment/{id} ───────────────────────────────────

    [Fact]
    public async Task DeletePayment_returns200()
    {
        var lienId = Guid.CreateVersion7();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var lien = Lien.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                $"LIEN-DELETE-PAYMENT-{Guid.CreateVersion7():N}"[..30],
                LienType.MedicalLien,
                99m,
                SeedHelper.UserId,
                caseId: SeedHelper.CaseId);
            typeof(Lien).GetProperty(nameof(Lien.Id))!.SetValue(lien, lienId);
            db.Liens.Add(lien);
            await db.SaveChangesAsync();
        }

        // First create a payment to delete.
        var createResp = await _client.PostAsJsonAsync("/service/liens/settlement/payment", new
        {
            caseId        = SeedHelper.CaseId,
            lienId,
            paymentNumber = 99,
            amount        = 99m,
            lienStatus    = "Closed",
        });
        createResp.EnsureSuccessStatusCode();
        var doc  = await createResp.Content.ReadFromJsonAsync<JsonDocument>();
        var id   = doc!.RootElement.GetProperty("id").GetGuid();

        var deleteResp = await _client.DeleteAsync($"/service/delete-payment/{id}");
        deleteResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await deleteResp.Content.ReadAsStringAsync()}");

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<LiensDbContext>();
        var restoredLien = await verifyDb.Liens.FindAsync(lienId);
        restoredLien!.Status.Should().Be(LienStatus.Active);
        restoredLien.ClosedAtUtc.Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeletePayment_deletes_all_allocations_and_reopens_every_lien(bool useReceiptId)
    {
        var receiptId = useReceiptId ? Guid.CreateVersion7() : (Guid?)null;
        var paymentNumber = 700_000_001;
        var paymentDate = new DateOnly(2026, 8, 30);
        const string referenceNumber = "DELETE-GROUP-REFERENCE";
        var liens = new[]
        {
            Lien.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                $"LIEN-DELETE-GROUP-A-{Guid.CreateVersion7():N}"[..30],
                LienType.MedicalLien,
                150m,
                SeedHelper.UserId,
                caseId: SeedHelper.CaseId),
            Lien.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                $"LIEN-DELETE-GROUP-B-{Guid.CreateVersion7():N}"[..30],
                LienType.MedicalLien,
                250m,
                SeedHelper.UserId,
                caseId: SeedHelper.CaseId),
        };
        foreach (var lien in liens)
            lien.SetLegacyMedicalStatus("Closed", SeedHelper.UserId);

        var allocations = new[]
        {
            SettlementPaymentDetail.Create(
                SeedHelper.TenantId,
                SeedHelper.CaseId,
                liens[0].Id,
                paymentNumber,
                150m,
                SeedHelper.UserId,
                paymentDate,
                checkNumber: referenceNumber,
                receiptId: receiptId),
            SettlementPaymentDetail.Create(
                SeedHelper.TenantId,
                SeedHelper.CaseId,
                liens[1].Id,
                useReceiptId ? paymentNumber : paymentNumber + 1,
                250m,
                SeedHelper.UserId,
                paymentDate,
                checkNumber: referenceNumber,
                receiptId: receiptId),
        };

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            db.Liens.AddRange(liens);
            db.SettlementPaymentDetails.AddRange(allocations);
            await db.SaveChangesAsync();
        }

        var response = await _client.DeleteAsync(
            $"/api/liens/settlement/payments/{allocations[0].Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await response.Content.ReadAsStringAsync()}");

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<LiensDbContext>();
        var persistedAllocations = await verifyDb.SettlementPaymentDetails
            .Where(payment => allocations.Select(allocation => allocation.Id).Contains(payment.Id))
            .ToListAsync();
        persistedAllocations.Should().HaveCount(2);
        persistedAllocations.Should().OnlyContain(payment => payment.IsDeleted);

        var persistedLiens = await verifyDb.Liens
            .Where(lien => liens.Select(item => item.Id).Contains(lien.Id))
            .ToListAsync();
        persistedLiens.Should().HaveCount(2);
        persistedLiens.Should().OnlyContain(lien => lien.Status == LienStatus.Active);
        persistedLiens.Should().OnlyContain(lien => lien.ClosedAtUtc == null);

        var servicingResponse = await _client.GetAsync(
            $"/api/liens/liens?caseId={SeedHelper.CaseId}&pageSize=100");
        servicingResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await servicingResponse.Content.ReadAsStringAsync()}");
        var servicingPayload = await servicingResponse.Content.ReadFromJsonAsync<JsonDocument>();
        var reopenedLienIds = liens.Select(lien => lien.Id).ToHashSet();
        var reopenedItems = servicingPayload!.RootElement
            .GetProperty("items")
            .EnumerateArray()
            .Where(item => reopenedLienIds.Contains(item.GetProperty("id").GetGuid()))
            .ToArray();
        reopenedItems.Should().HaveCount(2);
        reopenedItems.Should().OnlyContain(item => item.GetProperty("status").GetString() == "Open");
    }

    // ── GET /service/settlement/history/{caseId} ──────────────────────────────

    [Fact]
    public async Task GetSettlementHistory_returns200_with_expected_keys()
    {
        var resp = await _client.GetAsync($"/service/settlement/history/{SeedHelper.CaseId}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await resp.Content.ReadAsStringAsync()}");

        var doc = await resp.Content.ReadFromJsonAsync<JsonDocument>();
        doc!.RootElement.TryGetProperty("settlements", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("reductions",   out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("payments",     out _).Should().BeTrue();
    }

    // ── Auth enforcement ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetSettlementHistory_without_auth_returns_401()
    {
        var anonClient = _factory.CreateClient();
        var resp = await anonClient.GetAsync(
            $"/service/settlement/history/{SeedHelper.CaseId}");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task<SettlementPaymentDetail> SeedEditablePaymentAsync(
        int paymentNumber,
        string referenceNumber,
        string note)
    {
        var payment = SettlementPaymentDetail.Create(
            SeedHelper.TenantId,
            SeedHelper.CaseId,
            SeedHelper.LienId,
            paymentNumber,
            100m,
            SeedHelper.UserId,
            new DateOnly(2026, 8, 1),
            checkNumber: referenceNumber,
            note: note);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
        db.SettlementPaymentDetails.Add(payment);
        await db.SaveChangesAsync();
        return payment;
    }

    private static Dictionary<string, object?> ValidPaymentUpdateRequest() => new()
    {
        ["amount"] = 530m,
        ["paymentDate"] = "2026-08-16",
        ["paymentMethod"] = "Check",
        ["referenceNumber"] = "123456",
        ["notes"] = "Payment Testing",
        ["settlementType"] = "by_funding_company",
        ["settlementStatus"] = "full_payment",
        ["lienStatus"] = "Closed",
    };
}
