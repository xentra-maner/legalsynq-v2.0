using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Liens.Api.Tests.Helpers;
using Liens.Domain.Entities;
using Liens.Domain.Enums;
using Liens.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Liens.Api.Tests.Tests;

public class LegacyMedicalEndpointTests : IClassFixture<LiensApiFactory>, IAsyncLifetime
{
    private readonly LiensApiFactory _factory;
    private HttpClient _client = null!;

    public LegacyMedicalEndpointTests(LiensApiFactory factory) => _factory = factory;

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
    public async Task UpdateMedical_persists_legacy_service_and_servicing_fields()
    {
        var payload = new
        {
            id = SeedHelper.LienId.ToString(),
            caseId = SeedHelper.CaseId.ToString(),
            status = "Offered",
            purchaseDate = "06/22/2026",
            initialServiceDate = "07/07/2026",
            endServiceDate = "07/03/2026",
            note = "test",
            isBulk = "Yes",
            isServicing = "Yes",
            fundingCompanyId = string.Empty,
        };

        var updateResponse = await _client.PostAsJsonAsync(
            "/api/liens/cases/liens/update-medical",
            payload);

        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var getResponse = await _client.GetAsync($"/api/liens/cases/liens/get-medical/{SeedHelper.LienId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = JsonNode.Parse(await getResponse.Content.ReadAsStringAsync())!;
        body["isSuccess"]!.GetValue<bool>().Should().BeTrue();

        var data = body["data"]!;
        data["status"]!.GetValue<string>().Should().Be("Offered");
        data["purchaseDate"]!.GetValue<string>().Should().Be("06/22/2026");
        data["initialServiceDate"]!.GetValue<string>().Should().Be("07/07/2026");
        data["endServiceDate"]!.GetValue<string>().Should().Be("07/03/2026");
        data["note"]!.GetValue<string>().Should().Be("test");
        data["isBulk"]!.GetValue<string>().Should().Be("Yes");
        data["isServicing"]!.GetValue<string>().Should().Be("Yes");
        data["fundingCompanyId"]!.GetValue<string>().Should().BeEmpty();

        var updatesResponse = await _client.PostAsJsonAsync(
            "/api/liens/cases/liens-updates/v3",
            new { caseId = SeedHelper.CaseId, page = 1, limit = 50 });
        updatesResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await updatesResponse.Content.ReadAsStringAsync()}");

        var updates = JsonNode.Parse(await updatesResponse.Content.ReadAsStringAsync())!;
        var fieldUpdate = updates["data"]!.AsArray().Single(item =>
            item!["description"]!.GetValue<string>().StartsWith("Lien Update.", StringComparison.Ordinal));
        var description = fieldUpdate!["description"]!.GetValue<string>();
        fieldUpdate["action"]!.GetValue<string>().Should().Be("Liens Details");
        fieldUpdate["lienCode"]!.GetValue<string>().Should().Be("LIEN-TEST-001");
        description.Should().Contain("Purchase Date: \"\" → 06/22/2026");
        description.Should().Contain("Initial Service Date: \"\" → 07/07/2026");
        description.Should().Contain("End Service Date: \"\" → 07/03/2026");
        description.Should().Contain("Bulk: \"\" → Yes");
        description.Should().Contain("Servicing: \"\" → Yes");
        description.Should().Contain("Note: \"\" → test");
    }

    [Fact]
    public async Task UpdateMedical_when_only_note_changes_includes_note_in_lien_updates()
    {
        var note = $"Medical update note {Guid.CreateVersion7():N}";
        var updateResponse = await _client.PostAsJsonAsync(
            "/api/liens/cases/liens/update-medical",
            new
            {
                id = SeedHelper.LienId.ToString(),
                note,
            });

        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await updateResponse.Content.ReadAsStringAsync()}");

        var repeatedResponse = await _client.PostAsJsonAsync(
            "/api/liens/cases/liens/update-medical",
            new
            {
                id = SeedHelper.LienId.ToString(),
                note,
            });
        repeatedResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await repeatedResponse.Content.ReadAsStringAsync()}");

        var updatesResponse = await _client.PostAsJsonAsync(
            "/api/liens/cases/liens-updates/v3",
            new
            {
                caseId = SeedHelper.CaseId,
                page = 1,
                limit = 50,
            });
        updatesResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await updatesResponse.Content.ReadAsStringAsync()}");

        var body = JsonNode.Parse(await updatesResponse.Content.ReadAsStringAsync())!;
        var update = body["data"]!
            .AsArray()
            .Single(item =>
                item!["lienId"]!.GetValue<string>() == SeedHelper.LienId.ToString() &&
                item["description"]!.GetValue<string>().StartsWith("Lien Update.", StringComparison.Ordinal));

        update!["action"]!.GetValue<string>().Should().Be("Liens Details");
        update["description"]!.GetValue<string>().Should().Contain($"Note: \"\" → {note}");
        update["lienId"]!.GetValue<string>().Should().Be(SeedHelper.LienId.ToString());
        update["lienCode"]!.GetValue<string>().Should().Be("LIEN-TEST-001");
        update["updatedBy"]!.GetValue<string>().Should().Be("Demo User");

        body["data"]!.AsArray()
            .Count(item =>
                item!["lienId"]!.GetValue<string>() == SeedHelper.LienId.ToString() &&
                item["description"]!.GetValue<string>().StartsWith("Lien Update.", StringComparison.Ordinal))
            .Should().Be(1);
    }

    [Fact]
    public async Task UpdateMedical_logs_the_legacy_note_value_the_user_replaced()
    {
        var lien = Lien.Create(
            SeedHelper.TenantId,
            SeedHelper.OrgId,
            $"LIEN-NOTE-{Guid.CreateVersion7():N}"[..28],
            LienType.MedicalLien,
            100m,
            SeedHelper.UserId,
            caseId: SeedHelper.CaseId,
            notes: "Legacy medical note");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            db.Liens.Add(lien);
            await db.SaveChangesAsync();
        }

        var updateResponse = await _client.PostAsJsonAsync(
            "/api/liens/cases/liens/update-medical",
            new
            {
                id = lien.Id.ToString(),
                caseId = SeedHelper.CaseId.ToString(),
                note = "Updated medical note",
            });
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await updateResponse.Content.ReadAsStringAsync()}");

        var medicalResponse = await _client.GetAsync($"/api/liens/cases/liens/get-medical/{lien.Id}");
        medicalResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonNode.Parse(await medicalResponse.Content.ReadAsStringAsync())!["data"]!["note"]!
            .GetValue<string>()
            .Should().Be("Updated medical note");

        var updatesResponse = await _client.PostAsJsonAsync(
            "/api/liens/cases/liens-updates/v3",
            new { caseId = SeedHelper.CaseId, page = 1, limit = 50 });
        updatesResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await updatesResponse.Content.ReadAsStringAsync()}");

        var row = JsonNode.Parse(await updatesResponse.Content.ReadAsStringAsync())!["data"]!
            .AsArray()
            .Single(item =>
                item!["lienId"]!.GetValue<string>() == lien.Id.ToString() &&
                item["description"]!.GetValue<string>().StartsWith("Lien Update.", StringComparison.Ordinal));
        row!["description"]!.GetValue<string>()
            .Should().Contain("Note: Legacy medical note → Updated medical note");
    }

    [Fact]
    public async Task UpdateMedical_allows_settled_lien_and_logs_every_changed_field()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var lien = db.Liens.Single(item => item.Id == SeedHelper.LienId);
            lien.SetLegacyMedicalStatus(LienStatus.Settled, SeedHelper.UserId);
            await db.SaveChangesAsync();
        }

        var updateResponse = await _client.PostAsJsonAsync(
            "/api/liens/cases/liens/update-medical",
            new
            {
                id = SeedHelper.LienId.ToString(),
                status = LienStatus.Settled,
                purchaseDate = "06/22/2026",
                initialServiceDate = "07/07/2026",
                endServiceDate = "07/03/2026",
                note = "Settled lien servicing correction",
                isBulk = "Yes",
                isServicing = "Yes",
            });

        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await updateResponse.Content.ReadAsStringAsync()}");

        var getResponse = await _client.GetAsync($"/api/liens/cases/liens/get-medical/{SeedHelper.LienId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var medical = JsonNode.Parse(await getResponse.Content.ReadAsStringAsync())!["data"]!;
        medical["status"]!.GetValue<string>().Should().Be(LienStatus.Settled);
        medical["purchaseDate"]!.GetValue<string>().Should().Be("06/22/2026");
        medical["initialServiceDate"]!.GetValue<string>().Should().Be("07/07/2026");
        medical["endServiceDate"]!.GetValue<string>().Should().Be("07/03/2026");
        medical["note"]!.GetValue<string>().Should().Be("Settled lien servicing correction");
        medical["isBulk"]!.GetValue<string>().Should().Be("Yes");
        medical["isServicing"]!.GetValue<string>().Should().Be("Yes");

        var updatesResponse = await _client.PostAsJsonAsync(
            "/api/liens/cases/liens-updates/v3",
            new { caseId = SeedHelper.CaseId, page = 1, limit = 50 });
        updatesResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await updatesResponse.Content.ReadAsStringAsync()}");

        var updates = JsonNode.Parse(await updatesResponse.Content.ReadAsStringAsync())!;
        var fieldUpdate = updates["data"]!.AsArray().Single(item =>
            item!["description"]!.GetValue<string>().Contains("Purchase Date:", StringComparison.Ordinal));
        var description = fieldUpdate!["description"]!.GetValue<string>();
        fieldUpdate["action"]!.GetValue<string>().Should().Be("Liens Details");
        fieldUpdate["updatedBy"]!.GetValue<string>().Should().Be("Demo User");
        description.Should().Contain("Purchase Date: \"\" → 06/22/2026");
        description.Should().Contain("Initial Service Date: \"\" → 07/07/2026");
        description.Should().Contain("End Service Date: \"\" → 07/03/2026");
        description.Should().Contain("Bulk: \"\" → Yes");
        description.Should().Contain("Servicing: \"\" → Yes");
        description.Should().Contain("Note: \"\" → Settled lien servicing correction");
    }

    [Fact]
    public async Task UpdateMedical_compares_every_submitted_field_and_does_not_duplicate_unchanged_logs()
    {
        var lien = Lien.Create(
            SeedHelper.TenantId,
            SeedHelper.OrgId,
            $"LIEN-UPDATE-{Guid.CreateVersion7():N}"[..28],
            LienType.MedicalLien,
            100m,
            SeedHelper.UserId);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            db.Liens.Add(lien);
            await db.SaveChangesAsync();
        }

        var payload = new
        {
            id = lien.Id.ToString(),
            caseId = SeedHelper.CaseId.ToString(),
            status = "Closed",
            purchaseDate = "06/22/2026",
            initialServiceDate = "07/07/2026",
            endServiceDate = "07/03/2026",
            note = "Complete medical servicing update",
            isBulk = "Yes",
            isServicing = "Yes",
            fundingCompanyId = SeedHelper.FundingCompanyId.ToString(),
        };

        var firstResponse = await _client.PostAsJsonAsync(
            "/api/liens/cases/liens/update-medical",
            payload);
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await firstResponse.Content.ReadAsStringAsync()}");

        var repeatedResponse = await _client.PostAsJsonAsync(
            "/api/liens/cases/liens/update-medical",
            payload);
        repeatedResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await repeatedResponse.Content.ReadAsStringAsync()}");

        var medicalResponse = await _client.GetAsync($"/api/liens/cases/liens/get-medical/{lien.Id}");
        medicalResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var medical = JsonNode.Parse(await medicalResponse.Content.ReadAsStringAsync())!["data"]!;
        medical["caseId"]!.GetValue<string>().Should().Be(SeedHelper.CaseId.ToString());
        medical["status"]!.GetValue<string>().Should().Be(LienStatus.Settled);
        medical["purchaseDate"]!.GetValue<string>().Should().Be("06/22/2026");
        medical["initialServiceDate"]!.GetValue<string>().Should().Be("07/07/2026");
        medical["endServiceDate"]!.GetValue<string>().Should().Be("07/03/2026");
        medical["note"]!.GetValue<string>().Should().Be("Complete medical servicing update");
        medical["isBulk"]!.GetValue<string>().Should().Be("Yes");
        medical["isServicing"]!.GetValue<string>().Should().Be("Yes");
        medical["fundingCompanyId"]!.GetValue<string>().Should().Be(SeedHelper.FundingCompanyId.ToString());

        var updatesResponse = await _client.PostAsJsonAsync(
            "/api/liens/cases/liens-updates/v3",
            new { caseId = SeedHelper.CaseId, page = 1, limit = 50 });
        updatesResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await updatesResponse.Content.ReadAsStringAsync()}");

        var rows = JsonNode.Parse(await updatesResponse.Content.ReadAsStringAsync())!["data"]!
            .AsArray()
            .Where(item => item!["lienId"]!.GetValue<string>() == lien.Id.ToString())
            .ToList();
        rows.Should().ContainSingle();
        rows.Should().OnlyContain(item => item!["updatedBy"]!.GetValue<string>() == "Demo User");

        var row = rows.Single()!;
        row["action"]!.GetValue<string>().Should().Be("Liens Details");
        var fieldDescription = row["description"]!.GetValue<string>();
        fieldDescription.Should().StartWith("Lien Update. Changes:");
        fieldDescription.Should().Contain("Funding Company: \"\" → Capital Fund LLC");
        fieldDescription.Should().Contain("Case: \"\" → CASE-TEST-001 — John Plaintiff");
        fieldDescription.Should().NotContain(SeedHelper.FundingCompanyId.ToString());
        fieldDescription.Should().NotContain(SeedHelper.CaseId.ToString());
        fieldDescription.Should().Contain("Status: Open → Closed");
        fieldDescription.Should().Contain("Purchase Date: \"\" → 06/22/2026");
        fieldDescription.Should().Contain("Initial Service Date: \"\" → 07/07/2026");
        fieldDescription.Should().Contain("End Service Date: \"\" → 07/03/2026");
        fieldDescription.Should().Contain("Bulk: \"\" → Yes");
        fieldDescription.Should().Contain("Servicing: \"\" → Yes");
        fieldDescription.Should().Contain("Note: \"\" → Complete medical servicing update");
    }

    [Fact]
    public async Task UpdateMedical_when_history_write_fails_rolls_back_lien_and_audit()
    {
        using var factory = new TransactionalLiensApiFactory();
        using (var setupScope = factory.Services.CreateScope())
            await SeedHelper.SeedAsync(setupScope.ServiceProvider);

        factory.Services.GetRequiredService<CapturingAuditPublisher>().Clear();

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                JwtTokenHelper.CreateFullAccessToken(SeedHelper.TenantId, SeedHelper.UserId));

        var response = await client.PostAsJsonAsync(
            "/api/liens/cases/liens/update-medical",
            new
            {
                id = SeedHelper.LienId.ToString(),
                caseId = SeedHelper.CaseId.ToString(),
                note = "This change must roll back",
            });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict,
            $"Body: {await response.Content.ReadAsStringAsync()}");

        using var verificationScope = factory.Services.CreateScope();
        var db = verificationScope.ServiceProvider.GetRequiredService<LiensDbContext>();
        var storedLien = await db.Liens
            .AsNoTracking()
            .SingleAsync(item => item.Id == SeedHelper.LienId);
        storedLien.Description.Should().BeNull();
        (await db.LienStatusHistories
                .AsNoTracking()
                .Where(item =>
                    item.LienId == SeedHelper.LienId &&
                    item.Description.Contains("This change must roll back"))
                .ToListAsync())
            .Should().BeEmpty();
        verificationScope.ServiceProvider
            .GetRequiredService<CapturingAuditPublisher>()
            .Events.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateMedical_persists_and_resolves_funding_company()
    {
        var payload = new
        {
            id = SeedHelper.LienId.ToString(),
            caseId = SeedHelper.CaseId.ToString(),
            status = "Offered",
            purchaseDate = "06/22/2026",
            initialServiceDate = "07/07/2026",
            endServiceDate = "07/03/2026",
            note = "test",
            isBulk = "Yes",
            isServicing = "Yes",
            fundingCompanyId = SeedHelper.FundingCompanyId.ToString(),
        };

        var updateResponse = await _client.PostAsJsonAsync(
            "/api/liens/cases/liens/update-medical",
            payload);

        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var getResponse = await _client.GetAsync($"/api/liens/cases/liens/get-medical/{SeedHelper.LienId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = JsonNode.Parse(await getResponse.Content.ReadAsStringAsync())!;
        body["isSuccess"]!.GetValue<bool>().Should().BeTrue();

        var data = body["data"]!;
        data["fundingCompanyId"]!.GetValue<string>().Should().Be(SeedHelper.FundingCompanyId.ToString());
        data["fundingCompany"]!.GetValue<string>().Should().Be("Capital Fund LLC");
    }

    [Fact]
    public async Task UpdateFacility_persists_medical_provider_and_facility_contact_metadata()
    {
        var facilityContactId = Guid.CreateVersion7();

        var payload = new
        {
            liensId = SeedHelper.LienId.ToString(),
            facilityId = SeedHelper.MedicalFacilityContactId.ToString(),
            facility = "Sunrise Clinic",
            facilityContactId = facilityContactId.ToString(),
            facilityContact = "MedicalFacility Primary Staff I",
            email = "",
            phone = "555-0101",
            medicalProviderId = SeedHelper.MedicalProviderId.ToString(),
            medicalProvider = "Dr. Anthony Ashworth, MD",
        };

        var updateResponse = await _client.PostAsJsonAsync(
            "/api/liens/cases/liens/update-facility",
            payload);

        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await updateResponse.Content.ReadAsStringAsync()}");

        var getResponse = await _client.GetAsync($"/api/liens/cases/liens/get-facility/{SeedHelper.LienId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await getResponse.Content.ReadAsStringAsync()}");

        var body = JsonNode.Parse(await getResponse.Content.ReadAsStringAsync())!;
        body["isSuccess"]!.GetValue<bool>().Should().BeTrue();

        var data = body["data"]!;
        data["facilityId"]!.GetValue<string>().Should().Be(SeedHelper.MedicalFacilityContactId.ToString());
        data["facilityContactId"]!.GetValue<string>().Should().Be(facilityContactId.ToString());
        data["medicalProviderId"]!.GetValue<string>().Should().Be(SeedHelper.MedicalProviderId.ToString());
        data["phone"]!.GetValue<string>().Should().Be("555-0101");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
        var info = db.ServicingItems.Single(item =>
            item.LienId == SeedHelper.LienId &&
            item.TaskType == "LegacyMedicalFacilityInfo");

        info.Notes.Should().Contain($"facilityId={SeedHelper.MedicalFacilityContactId}");
        info.Notes.Should().Contain($"facilityContactId={facilityContactId}");
        info.Notes.Should().Contain($"medicalProviderId={SeedHelper.MedicalProviderId}");
        info.Notes.Should().Contain("medicalProvider=Dr. Anthony Ashworth, MD");
    }

    [Fact]
    public async Task UpdateFacility_does_not_touch_medical_information_when_values_are_unchanged()
    {
        var payload = new
        {
            liensId = SeedHelper.LienId.ToString(),
            facilityId = SeedHelper.MedicalFacilityContactId.ToString(),
            facility = "Sunrise Clinic",
            facilityContactId = SeedHelper.FacilityContactId.ToString(),
            facilityContact = "Medical Facility Primary Staff",
            email = "staff@sunrise.example",
            phone = "555-0102",
            medicalProviderId = SeedHelper.MedicalProviderId.ToString(),
            medicalProvider = "Dr. Anthony Ashworth, MD",
        };

        var firstResponse = await _client.PostAsJsonAsync(
            "/api/liens/cases/liens/update-facility",
            payload);
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await firstResponse.Content.ReadAsStringAsync()}");

        Guid infoId;
        DateTime firstUpdatedAtUtc;
        using (var firstScope = _factory.Services.CreateScope())
        {
            var db = firstScope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var info = await db.ServicingItems.AsNoTracking().SingleAsync(item =>
                item.LienId == SeedHelper.LienId &&
                item.TaskType == "LegacyMedicalFacilityInfo");
            infoId = info.Id;
            firstUpdatedAtUtc = info.UpdatedAtUtc;
        }

        var secondResponse = await _client.PostAsJsonAsync(
            "/api/liens/cases/liens/update-facility",
            payload);
        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await secondResponse.Content.ReadAsStringAsync()}");

        using var verificationScope = _factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<LiensDbContext>();
        var storedRows = await verificationDb.ServicingItems.AsNoTracking()
            .Where(item => item.LienId == SeedHelper.LienId &&
                           item.TaskType == "LegacyMedicalFacilityInfo")
            .ToListAsync();

        storedRows.Should().ContainSingle();
        storedRows[0].Id.Should().Be(infoId);
        storedRows[0].UpdatedAtUtc.Should().Be(firstUpdatedAtUtc);
    }

    [Fact]
    public async Task UpdateFacility_allows_servicing_corrections_for_settled_lien()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var lien = db.Liens.Single(item => item.Id == SeedHelper.LienId);
            lien.SetLegacyMedicalStatus(LienStatus.Settled, SeedHelper.UserId);
            await db.SaveChangesAsync();
        }

        var payload = new
        {
            liensId = SeedHelper.LienId.ToString(),
            facilityId = SeedHelper.MedicalFacilityContactId.ToString(),
            facility = "Sunrise Clinic",
            facilityContactId = SeedHelper.FacilityContactId.ToString(),
            facilityContact = "Medical Facility Primary Staff",
            email = "staff@sunrise.example",
            phone = "555-0102",
            medicalProviderId = SeedHelper.MedicalProviderId.ToString(),
            medicalProvider = "Dr. Anthony Ashworth, MD",
        };

        var updateResponse = await _client.PostAsJsonAsync(
            "/api/liens/cases/liens/update-facility",
            payload);

        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await updateResponse.Content.ReadAsStringAsync()}");

        var getResponse = await _client.GetAsync(
            $"/api/liens/cases/liens/get-facility/{SeedHelper.LienId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var data = JsonNode.Parse(await getResponse.Content.ReadAsStringAsync())!["data"]!;
        data["facilityId"]!.GetValue<string>()
            .Should().Be(SeedHelper.MedicalFacilityContactId.ToString());
        data["facilityContactId"]!.GetValue<string>()
            .Should().Be(SeedHelper.FacilityContactId.ToString());
        data["medicalProviderId"]!.GetValue<string>()
            .Should().Be(SeedHelper.MedicalProviderId.ToString());
    }

    [Fact]
    public async Task UpdateMedical_accepts_legacy_open_status()
    {
        var payload = new
        {
            id = SeedHelper.LienId.ToString(),
            caseId = SeedHelper.CaseId.ToString(),
            status = "Open",
            purchaseDate = "07/06/2026",
            initialServiceDate = "07/07/2026",
            endServiceDate = "",
            note = "",
            isBulk = "N",
            isServicing = "N",
            fundingCompanyId = string.Empty,
        };

        var updateResponse = await _client.PostAsJsonAsync(
            "/api/liens/cases/liens/update-medical",
            payload);

        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await updateResponse.Content.ReadAsStringAsync()}");

        var getResponse = await _client.GetAsync($"/api/liens/cases/liens/get-medical/{SeedHelper.LienId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = JsonNode.Parse(await getResponse.Content.ReadAsStringAsync())!;
        body["isSuccess"]!.GetValue<bool>().Should().BeTrue();
        body["data"]!["status"]!.GetValue<string>().Should().Be("Open");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
        (await db.Liens.FindAsync(SeedHelper.LienId))!.Status
            .Should().Be(LienStatus.Active);
    }

    [Fact]
    public async Task CreateMedical_accepts_legacy_open_status()
    {
        var createResponse = await _client.PostAsJsonAsync(
            "/api/liens/cases/liens/medical",
            new
            {
                id = (string?)null,
                caseId = SeedHelper.CaseId.ToString(),
                status = "Open",
                purchaseDate = "09/01/2026",
                initialServiceDate = "09/09/2026",
                endServiceDate = "09/14/2026",
                note = "",
                isBulk = "N",
                isServicing = "Y",
                fundingCompanyId = "",
            });
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await createResponse.Content.ReadAsStringAsync()}");

        var createBody = JsonNode.Parse(await createResponse.Content.ReadAsStringAsync())!;
        createBody["isSuccess"]!.GetValue<bool>().Should().BeTrue();
        var lienId = Guid.Parse(createBody["data"]!.GetValue<string>());

        var getResponse = await _client.GetAsync($"/api/liens/cases/liens/get-medical/{lienId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await getResponse.Content.ReadAsStringAsync()}");
        var medical = JsonNode.Parse(await getResponse.Content.ReadAsStringAsync())!["data"]!;
        medical["status"]!.GetValue<string>().Should().Be("Open");
        medical["purchaseDate"]!.GetValue<string>().Should().Be("09/01/2026");
        medical["initialServiceDate"]!.GetValue<string>().Should().Be("09/09/2026");
        medical["endServiceDate"]!.GetValue<string>().Should().Be("09/14/2026");
        medical["isBulk"]!.GetValue<string>().Should().Be("N");
        medical["isServicing"]!.GetValue<string>().Should().Be("Y");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
        (await db.Liens.FindAsync(lienId))!.Status
            .Should().Be(LienStatus.Active);
    }

    [Fact]
    public async Task MedicalCode_create_can_be_retrieved_by_lien_id()
    {
        var code = "99213";
        var description = "Office Visit";
        var payload = new
        {
            id = (string?)null,
            liensId = SeedHelper.LienId.ToString(),
            code,
            medicareCost = "100.00",
            billingAmount = "100.00",
            purchaseAmount = "100.00",
            payee = "test payee",
            outboundCheckNumber = "chck-1000",
        };

        var createResponse = await _client.PostAsJsonAsync(
            "/api/liens/cases/liens/medicalcode",
            payload);

        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var getResponse = await _client.GetAsync($"/api/liens/cases/liens/get-medicalcode/{SeedHelper.LienId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = JsonNode.Parse(await getResponse.Content.ReadAsStringAsync())!;
        body["isSuccess"]!.GetValue<bool>().Should().BeTrue();

        var data = body["data"]!.AsArray();
        var item = data.Single(item => item!["code"]!.GetValue<string>() == code)!;
        item["liensId"]!.GetValue<string>().Should().Be(SeedHelper.LienId.ToString());
        item["code"]!.GetValue<string>().Should().Be(code);
        item["description"]!.GetValue<string>().Should().Be(description);
        item["medicareCost"]!.GetValue<string>().Should().Be("100.00");
        item["billingAmount"]!.GetValue<string>().Should().Be("100.00");
        item["purchaseAmount"]!.GetValue<string>().Should().Be("100.00");
        item["payee"]!.GetValue<string>().Should().Be("test payee");
        item["outboundCheckNumber"]!.GetValue<string>().Should().Be("chck-1000");
    }

    [Fact]
    public async Task MedicalCode_create_attributes_lien_update_to_authenticated_user()
    {
        const string code = "99214";
        var createResponse = await _client.PostAsJsonAsync(
            "/api/liens/cases/liens/medicalcode",
            new
            {
                id = (string?)null,
                liensId = SeedHelper.LienId.ToString(),
                code,
                medicareCost = "125.00",
                billingAmount = "150.00",
                purchaseAmount = "100.00",
                payee = "test payee",
                outboundCheckNumber = "check-1001",
            });
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await createResponse.Content.ReadAsStringAsync()}");

        var updatesResponse = await _client.PostAsJsonAsync(
            "/api/liens/cases/liens-updates/v3",
            new
            {
                caseId = SeedHelper.CaseId,
                page = 1,
                limit = 50,
            });
        updatesResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await updatesResponse.Content.ReadAsStringAsync()}");

        var body = JsonNode.Parse(await updatesResponse.Content.ReadAsStringAsync())!;
        var update = body["data"]!
            .AsArray()
            .Single(item =>
                item!["action"]!.GetValue<string>() == "LegacyMedicalCode" &&
                item["description"]!.GetValue<string>() == $"Medical code {code}");

        update!["updatedBy"]!.GetValue<string>().Should().Be("Demo User");
    }

    [Fact]
    public async Task MedicalCode_create_uses_authenticated_name_when_identity_lookup_omits_user()
    {
        var productionUserId = Guid.CreateVersion7();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer",
                JwtTokenHelper.CreateFullAccessToken(
                    SeedHelper.TenantId,
                    productionUserId,
                    name: "Production User"));

        const string code = "99215";
        var createResponse = await _client.PostAsJsonAsync(
            "/api/liens/cases/liens/medicalcode",
            new
            {
                id = (string?)null,
                liensId = SeedHelper.LienId.ToString(),
                code,
                medicareCost = "175.00",
                billingAmount = "200.00",
                purchaseAmount = "150.00",
                payee = "test payee",
                outboundCheckNumber = "check-1002",
            });
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await createResponse.Content.ReadAsStringAsync()}");

        var updatesResponse = await _client.PostAsJsonAsync(
            "/api/liens/cases/liens-updates/v3",
            new
            {
                caseId = SeedHelper.CaseId,
                page = 1,
                limit = 50,
            });
        updatesResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await updatesResponse.Content.ReadAsStringAsync()}");

        var body = JsonNode.Parse(await updatesResponse.Content.ReadAsStringAsync())!;
        var update = body["data"]!
            .AsArray()
            .Single(item =>
                item!["action"]!.GetValue<string>() == "LegacyMedicalCode" &&
                item["description"]!.GetValue<string>() == $"Medical code {code}");

        update!["updatedBy"]!.GetValue<string>().Should().Be("Production User");
    }

    [Fact]
    public async Task MedicalCode_history_resolves_historical_actor_through_identity_user_lookup()
    {
        var historicalUserId = SeedHelper.IdentityOnlyUserId;
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer",
                JwtTokenHelper.CreateFullAccessToken(
                    SeedHelper.TenantId,
                    historicalUserId,
                    name: "Historical User"));

        const string code = "99216";
        var createResponse = await _client.PostAsJsonAsync(
            "/api/liens/cases/liens/medicalcode",
            new
            {
                id = (string?)null,
                liensId = SeedHelper.LienId.ToString(),
                code,
                medicareCost = "225.00",
                billingAmount = "250.00",
                purchaseAmount = "200.00",
                payee = "test payee",
                outboundCheckNumber = "check-1003",
            });
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await createResponse.Content.ReadAsStringAsync()}");

        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer",
                JwtTokenHelper.CreateFullAccessToken(SeedHelper.TenantId, SeedHelper.UserId));

        var updatesResponse = await _client.PostAsJsonAsync(
            "/api/liens/cases/liens-updates/v3",
            new
            {
                caseId = SeedHelper.CaseId,
                page = 1,
                limit = 50,
            });
        updatesResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await updatesResponse.Content.ReadAsStringAsync()}");

        var body = JsonNode.Parse(await updatesResponse.Content.ReadAsStringAsync())!;
        var update = body["data"]!
            .AsArray()
            .Single(item =>
                item!["action"]!.GetValue<string>() == "LegacyMedicalCode" &&
                item["description"]!.GetValue<string>() == $"Medical code {code}");

        update!["updatedBy"]!.GetValue<string>().Should().Be("Identity Only");
    }

    [Fact]
    public async Task MedicalCode_history_does_not_expose_id_when_actor_cannot_be_resolved()
    {
        var missingUserId = Guid.CreateVersion7();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer",
                JwtTokenHelper.CreateFullAccessToken(
                    SeedHelper.TenantId,
                    missingUserId,
                    name: "Missing User"));

        const string code = "99217";
        var createResponse = await _client.PostAsJsonAsync(
            "/api/liens/cases/liens/medicalcode",
            new
            {
                id = (string?)null,
                liensId = SeedHelper.LienId.ToString(),
                code,
                medicareCost = "275.00",
                billingAmount = "300.00",
                purchaseAmount = "250.00",
                payee = "test payee",
                outboundCheckNumber = "check-1004",
            });
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await createResponse.Content.ReadAsStringAsync()}");

        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer",
                JwtTokenHelper.CreateFullAccessToken(SeedHelper.TenantId, SeedHelper.UserId));

        var updatesResponse = await _client.PostAsJsonAsync(
            "/api/liens/cases/liens-updates/v3",
            new
            {
                caseId = SeedHelper.CaseId,
                page = 1,
                limit = 50,
            });
        updatesResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await updatesResponse.Content.ReadAsStringAsync()}");

        var body = JsonNode.Parse(await updatesResponse.Content.ReadAsStringAsync())!;
        var update = body["data"]!
            .AsArray()
            .Single(item =>
                item!["action"]!.GetValue<string>() == "LegacyMedicalCode" &&
                item["description"]!.GetValue<string>() == $"Medical code {code}");

        update!["updatedBy"]!.GetValue<string>().Should().Be("Unknown user");
    }

    [Fact]
    public async Task MedicalCode_update_falls_back_to_lien_and_code_when_row_id_is_stale()
    {
        var code = "45385";
        var createResponse = await _client.PostAsJsonAsync(
            "/api/liens/cases/liens/medicalcode",
            new
            {
                id = (string?)null,
                liensId = SeedHelper.LienId.ToString(),
                code,
                medicareCost = "879.00",
                billingAmount = "1000.00",
                purchaseAmount = "750.00",
                payee = "",
                outboundCheckNumber = "",
            });
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await createResponse.Content.ReadAsStringAsync()}");

        var updateResponse = await _client.PostAsJsonAsync(
            "/api/liens/cases/liens/update-medicalcode",
            new
            {
                id = Guid.CreateVersion7().ToString(),
                liensId = SeedHelper.LienId.ToString(),
                code,
                medicareCost = "879.00",
                billingAmount = "1000.00",
                purchaseAmount = "1000.00",
                payee = "",
                outboundCheckNumber = "",
            });
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await updateResponse.Content.ReadAsStringAsync()}");

        var getResponse = await _client.GetAsync($"/api/liens/cases/liens/get-medicalcode/{SeedHelper.LienId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await getResponse.Content.ReadAsStringAsync()}");

        var body = JsonNode.Parse(await getResponse.Content.ReadAsStringAsync())!;
        var item = body["data"]!
            .AsArray()
            .Single(item => item!["code"]!.GetValue<string>() == code)!;
        item["purchaseAmount"]!.GetValue<string>().Should().Be("1000.00");
    }

    [Fact]
    public async Task MedicalCode_update_touches_the_row_only_when_values_change()
    {
        Guid lienId;
        using (var setupScope = _factory.Services.CreateScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var lien = Lien.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                $"MC-NOOP-{Guid.NewGuid():N}",
                LienType.MedicalLien,
                0m,
                SeedHelper.UserId,
                caseId: SeedHelper.CaseId);
            db.Liens.Add(lien);
            await db.SaveChangesAsync();
            lienId = lien.Id;
        }

        var payload = new
        {
            id = (string?)null,
            liensId = lienId.ToString(),
            code = "99218",
            description = "Initial hospital care",
            medicareCost = "175.00",
            billingAmount = "250.00",
            purchaseAmount = "200.00",
            payee = "Test Payee",
            outboundCheckNumber = "CHK-2000",
        };
        var createResponse = await _client.PostAsJsonAsync(
            "/api/liens/cases/liens/medicalcode",
            payload);
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await createResponse.Content.ReadAsStringAsync()}");
        var medicalCodeId = Guid.Parse(
            JsonNode.Parse(await createResponse.Content.ReadAsStringAsync())!["data"]!.GetValue<string>());

        DateTime createdTimestamp;
        using (var createdScope = _factory.Services.CreateScope())
        {
            var db = createdScope.ServiceProvider.GetRequiredService<LiensDbContext>();
            createdTimestamp = (await db.ServicingItems.AsNoTracking()
                .SingleAsync(item => item.Id == medicalCodeId)).UpdatedAtUtc;
        }

        var unchangedResponse = await _client.PostAsJsonAsync(
            "/api/liens/cases/liens/update-medicalcode",
            new
            {
                id = medicalCodeId.ToString(),
                payload.liensId,
                payload.code,
                payload.description,
                payload.medicareCost,
                payload.billingAmount,
                payload.purchaseAmount,
                payload.payee,
                payload.outboundCheckNumber,
            });
        unchangedResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await unchangedResponse.Content.ReadAsStringAsync()}");

        using (var unchangedScope = _factory.Services.CreateScope())
        {
            var db = unchangedScope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var unchanged = await db.ServicingItems.AsNoTracking()
                .SingleAsync(item => item.Id == medicalCodeId);
            unchanged.UpdatedAtUtc.Should().Be(createdTimestamp);
        }

        var changedResponse = await _client.PostAsJsonAsync(
            "/api/liens/cases/liens/update-medicalcode",
            new
            {
                id = medicalCodeId.ToString(),
                payload.liensId,
                payload.code,
                payload.description,
                payload.medicareCost,
                payload.billingAmount,
                purchaseAmount = "225.00",
                payload.payee,
                payload.outboundCheckNumber,
            });
        changedResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await changedResponse.Content.ReadAsStringAsync()}");

        using var changedScope = _factory.Services.CreateScope();
        var changedDb = changedScope.ServiceProvider.GetRequiredService<LiensDbContext>();
        var changed = await changedDb.ServicingItems.AsNoTracking()
            .SingleAsync(item => item.Id == medicalCodeId);
        changed.UpdatedAtUtc.Should().BeAfter(createdTimestamp);
        changed.Notes.Should().Contain("purchaseAmount=225.00");
    }

    [Fact]
    public async Task MedicalPayment_adds_or_updates_only_when_values_change()
    {
        Guid lienId;
        using (var setupScope = _factory.Services.CreateScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var lien = Lien.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                $"MP-NOOP-{Guid.NewGuid():N}",
                LienType.MedicalLien,
                0m,
                SeedHelper.UserId,
                caseId: SeedHelper.CaseId);
            db.Liens.Add(lien);
            await db.SaveChangesAsync();
            lienId = lien.Id;
        }

        var blankResponse = await _client.PostAsJsonAsync(
            "/api/liens/cases/liens/payment",
            new { liensId = lienId, payee = "", outboundCheckNumber = "" });
        blankResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await blankResponse.Content.ReadAsStringAsync()}");

        using (var blankScope = _factory.Services.CreateScope())
        {
            var db = blankScope.ServiceProvider.GetRequiredService<LiensDbContext>();
            (await db.ServicingItems.AsNoTracking().CountAsync(item =>
                item.LienId == lienId && item.TaskType == "LegacyMedicalPayment"))
                .Should().Be(0);
        }

        var payment = new
        {
            liensId = lienId,
            payee = "Legacy Payee",
            outboundCheckNumber = "OB-9001",
        };
        var createResponse = await _client.PostAsJsonAsync(
            "/api/liens/cases/liens/payment",
            payment);
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await createResponse.Content.ReadAsStringAsync()}");

        Guid paymentId;
        DateTime createdTimestamp;
        using (var createdScope = _factory.Services.CreateScope())
        {
            var db = createdScope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var created = await db.ServicingItems.SingleAsync(item =>
                item.LienId == lienId && item.TaskType == "LegacyMedicalPayment");
            paymentId = created.Id;
            db.Entry(created).Property(item => item.CaseId).CurrentValue = null;
            await db.SaveChangesAsync();
            createdTimestamp = created.UpdatedAtUtc;
        }

        var unchangedResponse = await _client.PostAsJsonAsync(
            "/api/liens/cases/liens/payment",
            payment);
        unchangedResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await unchangedResponse.Content.ReadAsStringAsync()}");

        using (var unchangedScope = _factory.Services.CreateScope())
        {
            var db = unchangedScope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var unchanged = await db.ServicingItems.AsNoTracking()
                .SingleAsync(item => item.Id == paymentId);
            unchanged.UpdatedAtUtc.Should().Be(createdTimestamp);
        }

        var changedResponse = await _client.PostAsJsonAsync(
            "/api/liens/cases/liens/payment",
            new { payment.liensId, payment.payee, outboundCheckNumber = "OB-9002" });
        changedResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await changedResponse.Content.ReadAsStringAsync()}");

        using var changedScope = _factory.Services.CreateScope();
        var changedDb = changedScope.ServiceProvider.GetRequiredService<LiensDbContext>();
        var changed = await changedDb.ServicingItems.AsNoTracking()
            .SingleAsync(item => item.Id == paymentId);
        changed.UpdatedAtUtc.Should().BeAfter(createdTimestamp);
        changed.Notes.Should().Contain("outboundCheckNumber=OB-9002");
        (await changedDb.ServicingItems.AsNoTracking().CountAsync(item =>
            item.LienId == lienId && item.TaskType == "LegacyMedicalPayment"))
            .Should().Be(1);
    }

    [Fact]
    public async Task DeleteMedicalCode_deletes_single_row_when_given_medical_code_id()
    {
        var codeA = $"A-{Guid.NewGuid():N}"[..10];
        var codeB = $"B-{Guid.NewGuid():N}"[..10];

        foreach (var code in new[] { codeA, codeB })
        {
            var createResponse = await _client.PostAsJsonAsync(
                "/api/liens/cases/liens/medicalcode",
                new
                {
                    id = (string?)null,
                    liensId = SeedHelper.LienId.ToString(),
                    code,
                    medicareCost = "100.00",
                    billingAmount = "100.00",
                    purchaseAmount = "100.00",
                    payee = "test payee",
                    outboundCheckNumber = "chck-1000",
                });

            createResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var beforeDelete = JsonNode.Parse(await (await _client.GetAsync(
            $"/api/liens/cases/liens/get-medicalcode/{SeedHelper.LienId}"))
            .Content.ReadAsStringAsync())!;

        var createdRows = beforeDelete["data"]!
            .AsArray()
            .Where(item => item is not null)
            .ToList();

        var rowToDelete = createdRows.Single(item => item!["code"]!.GetValue<string>() == codeA)!;
        var rowToKeep = createdRows.Single(item => item!["code"]!.GetValue<string>() == codeB)!;

        var deleteResponse = await _client.DeleteAsync(
            $"/api/liens/cases/liens/delete-medicalcode/{rowToDelete["id"]!.GetValue<string>()}");

        deleteResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var afterDeleteResponse = await _client.GetAsync(
            $"/api/liens/cases/liens/get-medicalcode/{SeedHelper.LienId}");
        afterDeleteResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var afterDelete = JsonNode.Parse(await afterDeleteResponse.Content.ReadAsStringAsync())!;
        var remainingRows = afterDelete["data"]!
            .AsArray()
            .Where(item => item is not null)
            .ToList();

        remainingRows.Should().NotContain(item =>
            item!["id"]!.GetValue<string>() == rowToDelete["id"]!.GetValue<string>());
        remainingRows.Should().Contain(item =>
            item!["id"]!.GetValue<string>() == rowToKeep["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetMedicalDocument_returns_uploaded_lien_documents()
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(SeedHelper.LienId.ToString()), "liensId");
        form.Add(new StringContent("14"), "DocFileTypeId");
        form.Add(new StringContent("medical-doc"), "DocName");
        var file = new ByteArrayContent("%PDF-1.4 test"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", "medical-doc.pdf");

        var uploadResponse = await _client.PostAsync("/api/liens/cases/liens/upload/document", form);
        uploadResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var getResponse = await _client.GetAsync($"/api/liens/cases/liens/get-medicaldocument/{SeedHelper.LienId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = JsonNode.Parse(await getResponse.Content.ReadAsStringAsync())!;
        body["isSuccess"]!.GetValue<bool>().Should().BeTrue();
        body["message"]!.GetValue<string>().Should().Be("Successfully retrieved Medical Documents.");

        var data = body["data"]!.AsArray();
        data.Count.Should().BeGreaterThan(0);

        var item = data.Single(item =>
            item!["filename"]!.GetValue<string>() == "medical-doc")!;
        item["liensId"]!.GetValue<string>().Should().Be(SeedHelper.LienId.ToString());
        item["filename"]!.GetValue<string>().Should().Be("medical-doc");
        item["typeId"]!.GetValue<string>().Should().Be("14");
        item["url"]!.GetValue<string>().Should().StartWith("/documents/");
    }

    [Fact]
    public async Task GetAllCaseDocument_returns_case_and_lien_documents_for_case()
    {
        using var caseForm = new MultipartFormDataContent();
        caseForm.Add(new StringContent(SeedHelper.CaseId.ToString()), "caseId");
        caseForm.Add(new StringContent("14"), "DocFileTypeId");
        caseForm.Add(new StringContent("case-doc"), "DocName");
        var caseFile = new ByteArrayContent("%PDF-1.4 test"u8.ToArray());
        caseFile.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        caseForm.Add(caseFile, "file", "case-doc.pdf");

        var caseUploadResponse = await _client.PostAsync("/api/liens/cases/upload/document", caseForm);
        caseUploadResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var lienForm = new MultipartFormDataContent();
        lienForm.Add(new StringContent(SeedHelper.LienId.ToString()), "liensId");
        lienForm.Add(new StringContent("7"), "DocFileTypeId");
        lienForm.Add(new StringContent("lien-doc"), "DocName");
        var lienFile = new ByteArrayContent("name,amount\none,1"u8.ToArray());
        lienFile.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        lienForm.Add(lienFile, "file", "lien-doc.csv");

        var lienUploadResponse = await _client.PostAsync("/api/liens/cases/liens/upload/document", lienForm);
        lienUploadResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var getResponse = await _client.GetAsync($"/api/liens/cases/get-allcasedocument/{SeedHelper.CaseId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = JsonNode.Parse(await getResponse.Content.ReadAsStringAsync())!;
        body["isSuccess"]!.GetValue<bool>().Should().BeTrue();
        body["message"]!.GetValue<string>().Should().Be("Successfully retrieved Documents.");

        var data = body["data"]!.AsArray();
        data.Count.Should().BeGreaterThanOrEqualTo(2);

        var caseDocument = data.Single(item =>
            item!["filename"]!.GetValue<string>() == "case-doc");
        var lienDocument = data.Single(item =>
            item!["filename"]!.GetValue<string>() == "lien-doc");

        caseDocument!["liensId"].Should().BeNull();
        caseDocument["typeId"]!.GetValue<string>().Should().Be("14");
        caseDocument["documentTypeId"]!.GetValue<string>()
            .Should().Be("10000000-0000-0000-0000-000000000005");
        lienDocument!["liensId"]!.GetValue<string>().Should().Be(SeedHelper.LienId.ToString());
        lienDocument["typeId"]!.GetValue<string>().Should().Be("7");
        lienDocument["documentTypeId"]!.GetValue<string>()
            .Should().Be("10000000-0000-0000-0000-000000000007");
    }

    [Fact]
    public async Task GetAllCaseDocument_defaults_missing_document_types_to_other()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            db.ServicingItems.Add(ServicingItem.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                $"DOC-UNTYPED-{Guid.CreateVersion7():N}"[..36],
                "LegacyCaseDocument",
                "Imported document without type metadata",
                "Legacy import",
                SeedHelper.UserId,
                caseId: SeedHelper.CaseId,
                notes: "url=/documents/untyped; filename=untyped.pdf"));
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync(
            $"/api/liens/cases/get-allcasedocument/{SeedHelper.CaseId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await response.Content.ReadAsStringAsync()}");

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var document = body["data"]!.AsArray().Single(item =>
            item!["filename"]!.GetValue<string>() == "untyped.pdf")!;

        document["typeId"]!.GetValue<string>()
            .Should().Be("10000000-0000-0000-0000-000000000005");
        document["documentTypeId"]!.GetValue<string>()
            .Should().Be("10000000-0000-0000-0000-000000000005");
    }

    [Theory]
    [InlineData("LegacyMedicalDocument")]
    [InlineData("LegacyLienDocument")]
    public async Task DeleteMedicalDocument_accepts_listed_legacy_document_types(string taskType)
    {
        Guid documentId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var document = ServicingItem.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                $"DOC-MEDICAL-{Guid.CreateVersion7():N}"[..36],
                taskType,
                "Medical document",
                "Legacy import",
                SeedHelper.UserId,
                caseId: SeedHelper.CaseId,
                lienId: SeedHelper.LienId,
                notes: "url=/documents/medical; filename=medical.pdf");
            documentId = document.Id;
            db.ServicingItems.Add(document);
            await db.SaveChangesAsync();
        }

        var response = await _client.DeleteAsync($"/liens/delete-medicaldocument/{documentId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await response.Content.ReadAsStringAsync()}");

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<LiensDbContext>();
        (await verifyDb.ServicingItems.FindAsync(documentId)).Should().BeNull();
    }
}
