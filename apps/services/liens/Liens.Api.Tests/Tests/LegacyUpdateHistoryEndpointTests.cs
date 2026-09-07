using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Liens.Api.Tests.Helpers;
using Liens.Domain.Entities;
using Liens.Domain.Enums;
using Liens.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Liens.Api.Tests.Tests;

public sealed class LegacyUpdateHistoryEndpointTests
{
    [Fact]
    public async Task Disabled_imported_history_still_returns_native_root_lifecycle_history()
    {
        await using var factory = new LiensApiFactory();
        var client = await CreateAuthenticatedClientAsync(factory);
        var (caseId, lienId) = await AddEmptyCaseAndLienAsync(factory);

        await AddUpdateEventsAsync(factory,
            CreateEvent(caseId, null, LegacyUpdateEvent.CaseScope, 10),
            CreateEvent(caseId, lienId, LegacyUpdateEvent.LienScope, 20));

        var caseResponse = await client.PostAsJsonAsync("/api/liens/cases/case-updates/v3", new
        {
            CaseId = caseId,
            page = 1,
            limit = 10,
        });
        caseResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await caseResponse.Content.ReadAsStringAsync()}");
        using var caseBody = JsonDocument.Parse(await caseResponse.Content.ReadAsStringAsync());
        caseBody.RootElement.GetProperty("data").EnumerateArray().Should().ContainSingle(item =>
            item.GetProperty("action").GetString() == "Case Created");
        caseBody.RootElement.GetProperty("totalCount").GetInt32().Should().Be(1);

        var lienResponse = await client.PostAsJsonAsync("/api/liens/cases/liens-updates/v3", new
        {
            CaseId = caseId,
            page = 1,
            limit = 10,
        });
        lienResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var lienBody = JsonDocument.Parse(await lienResponse.Content.ReadAsStringAsync());
        lienBody.RootElement.GetProperty("data").EnumerateArray().Should().ContainSingle(item =>
            item.GetProperty("action").GetString() == "Lien Created");
    }

    [Fact]
    public async Task Enabled_case_history_merges_sources_projects_compatibility_fields_and_pages_deterministically()
    {
        await using var factory = new EnabledLegacyUpdateHistoryFactory();
        var client = await CreateAuthenticatedClientAsync(factory);
        var occurredAtUtc = Utc(2026, 8, 20, 17, 22, 8);

        var native = LienCaseNote.Create(
            SeedHelper.CaseId,
            SeedHelper.TenantId,
            "Native history",
            CaseNoteCategory.Internal,
            SeedHelper.UserId,
            "Native User");
        SetProperty(native, nameof(LienCaseNote.CreatedAtUtc), occurredAtUtc);

        var importedHigh = CreateEvent(
            SeedHelper.CaseId,
            null,
            LegacyUpdateEvent.CaseScope,
            102,
            occurredAtUtc,
            description: "Attorney ÔåÆ Funding ?",
            actor: null);
        var importedLow = CreateEvent(
            SeedHelper.CaseId,
            null,
            LegacyUpdateEvent.CaseScope,
            101,
            occurredAtUtc,
            description: null,
            actor: "Legacy User");
        var crossTenant = LegacyUpdateEvent.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            SeedHelper.CaseId,
            null,
            LegacyUpdateEvent.CaseScope,
            "Case Details Update",
            "must not leak",
            "Other Tenant",
            occurredAtUtc.AddHours(1),
            Utc(2026, 8, 29, 1, 0, 0),
            Guid.NewGuid(),
            "SL-CORE",
            "SL_CASE_UPDATE_LOG",
            "999",
            999);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            db.LienCaseNotes.Add(native);
            db.LegacyUpdateEvents.AddRange(importedLow, importedHigh, crossTenant);
            await db.SaveChangesAsync();
        }

        var firstResponse = await client.PostAsJsonAsync("/api/liens/cases/case-updates/v3", new
        {
            CaseId = SeedHelper.CaseId,
            page = 1,
            limit = 2,
        });
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await firstResponse.Content.ReadAsStringAsync()}");
        using var firstBody = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync());
        firstBody.RootElement.GetProperty("totalCount").GetInt32().Should().Be(4);
        var firstPage = firstBody.RootElement.GetProperty("data").EnumerateArray().ToList();
        firstPage[0].GetProperty("action").GetString().Should().Be("Case Created");
        firstPage[1].GetProperty("id").GetString().Should().Be(native.Id.ToString());

        var secondResponse = await client.PostAsJsonAsync("/api/liens/cases/case-updates/v3", new
        {
            CaseId = SeedHelper.CaseId,
            page = 2,
            limit = 2,
        });
        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var secondBody = JsonDocument.Parse(await secondResponse.Content.ReadAsStringAsync());
        var secondPage = secondBody.RootElement.GetProperty("data").EnumerateArray().ToList();
        secondPage.Select(item => item.GetProperty("id").GetString())
            .Should().Equal(importedHigh.Id.ToString(), importedLow.Id.ToString());

        var imported = secondPage[0];
        imported.GetProperty("caseId").GetString().Should().Be(SeedHelper.CaseId.ToString());
        imported.GetProperty("action").GetString().Should().Be("Case Details Update");
        imported.GetProperty("description").GetString().Should().Be("Attorney → Funding ?");
        imported.GetProperty("note").GetString().Should().Be("Attorney → Funding ?");
        imported.GetProperty("category").GetString().Should().Be("legacy");
        imported.GetProperty("isPinned").GetBoolean().Should().BeFalse();
        imported.GetProperty("isEdited").GetBoolean().Should().BeFalse();
        imported.GetProperty("createdBy").GetString().Should().BeEmpty();
        imported.GetProperty("updatedBy").GetString().Should().BeEmpty();
        imported.GetProperty("updated").GetString().Should().BeEmpty();

        secondPage[1].GetProperty("description").GetString().Should().BeEmpty();
        secondPage[1].GetProperty("updatedBy").GetString().Should().Be("Legacy User");
    }

    [Fact]
    public async Task Native_case_created_history_resolves_updated_by_and_embedded_email_to_full_name()
    {
        await using var factory = new LiensApiFactory();
        var client = await CreateAuthenticatedClientAsync(factory);
        var (caseId, _) = await AddEmptyCaseAndLienAsync(factory);
        var note = LienCaseNote.Create(
            caseId,
            SeedHelper.TenantId,
            "Case created. Code: 26-10001; Client: Demo Plaintiff; Created By: demo@example.com.",
            CaseNoteCategory.CaseCreated,
            SeedHelper.UserId,
            "demo@example.com");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            db.LienCaseNotes.Add(note);
            await db.SaveChangesAsync();
        }

        var response = await client.PostAsJsonAsync("/api/liens/cases/case-updates/v3", new
        {
            CaseId = caseId,
            page = 1,
            limit = 10,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await response.Content.ReadAsStringAsync()}");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = body.RootElement.GetProperty("data").EnumerateArray()
            .Single(update => update.GetProperty("id").GetString() == note.Id.ToString());
        item.GetProperty("createdBy").GetString().Should().Be("Demo User");
        item.GetProperty("updatedBy").GetString().Should().Be("Demo User");
        item.GetProperty("description").GetString().Should().Contain("Created By: Demo User.");
        item.GetProperty("description").GetString().Should().NotContain("demo@example.com");
    }

    [Fact]
    public async Task Enabled_lien_history_returns_imported_rows_and_excludes_other_tenants()
    {
        await using var factory = new EnabledLegacyUpdateHistoryFactory();
        var client = await CreateAuthenticatedClientAsync(factory);
        var imported = CreateEvent(
            SeedHelper.CaseId,
            SeedHelper.LienId,
            LegacyUpdateEvent.LienScope,
            4890,
            Utc(2026, 8, 21, 8, 0, 0),
            "Payee ÔåÆ Created ?",
            "Legacy Actor");
        var crossTenant = LegacyUpdateEvent.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            SeedHelper.CaseId,
            SeedHelper.LienId,
            LegacyUpdateEvent.LienScope,
            "Lien Update",
            "must not leak",
            "Other Tenant",
            Utc(2026, 8, 22, 8, 0, 0),
            Utc(2026, 8, 29, 1, 0, 0),
            Guid.NewGuid(),
            "SL-CORE",
            "SL_LIENS_UPDATE_LOG",
            "999",
            999);
        await AddUpdateEventsAsync(factory, imported, crossTenant);

        var response = await client.PostAsJsonAsync("/api/liens/cases/liens-updates/v3", new
        {
            CaseId = SeedHelper.CaseId,
            page = 1,
            limit = 50,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await response.Content.ReadAsStringAsync()}");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var rows = body.RootElement.GetProperty("data").EnumerateArray()
            .Where(row => row.GetProperty("id").GetString() == imported.Id.ToString())
            .ToList();
        rows.Should().ContainSingle();
        rows[0].GetProperty("caseId").GetString().Should().Be(SeedHelper.CaseId.ToString());
        rows[0].GetProperty("lienId").GetString().Should().Be(SeedHelper.LienId.ToString());
        rows[0].GetProperty("lienCode").GetString().Should().Be("LIEN-TEST-001");
        rows[0].GetProperty("action").GetString().Should().Be("Lien Update");
        rows[0].GetProperty("description").GetString().Should().Be("Payee → Created ?");
        rows[0].GetProperty("updatedBy").GetString().Should().Be("Legacy Actor");
        body.RootElement.GetProperty("data").EnumerateArray()
            .Should().NotContain(row => row.GetProperty("description").GetString() == "must not leak");
    }

    [Fact]
    public async Task Lien_history_replaces_identifier_changes_with_tenant_scoped_descriptions()
    {
        await using var factory = new LiensApiFactory();
        var client = await CreateAuthenticatedClientAsync(factory);
        var historyId = Guid.NewGuid();
        var unknownFacilityId = Guid.NewGuid();
        var description =
            $"Lien Update. Changes: Status: Draft → Active; Seller Status: Active → Draft; " +
            $"Note: Existing note → blank; Organization ID: blank → {SeedHelper.OrgId}; " +
            $"Case ID: blank → {SeedHelper.CaseId}; " +
            $"Selling Case ID: blank → {SeedHelper.CaseId}; " +
            $"Facility ID: {unknownFacilityId} → {SeedHelper.FacilityId}; " +
            $"Subject Party ID: blank → {SeedHelper.LeadContactId}; " +
            $"Funding Company ID: blank → {SeedHelper.FundingCompanyId}; " +
            $"Funding Company Contact ID: blank → {SeedHelper.FundingCompanyId}; " +
            $"Medical Provider ID: blank → {SeedHelper.MedicalProviderId}; " +
            $"Medical Facility ID: blank → {SeedHelper.MedicalFacilityContactId}; " +
            $"Selling Organization ID: blank → {SeedHelper.OrgId}; " +
            $"Buying Organization ID: blank → {SeedHelper.OrgId}; " +
            $"Holding Organization ID: blank → {SeedHelper.OrgId}.";

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var history = LienStatusHistory.Create(
                SeedHelper.TenantId,
                SeedHelper.LienId,
                SeedHelper.CaseId,
                description,
                SeedHelper.UserId);
            SetProperty(history, nameof(LienStatusHistory.Id), historyId);
            db.LienStatusHistories.Add(history);
            await db.SaveChangesAsync();
        }

        var response = await client.PostAsJsonAsync("/api/liens/cases/liens-updates/v3", new
        {
            CaseId = SeedHelper.CaseId,
            page = 1,
            limit = 50,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await response.Content.ReadAsStringAsync()}");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var row = body.RootElement.GetProperty("data").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == historyId.ToString());
        var enriched = row.GetProperty("description").GetString();

        row.GetProperty("action").GetString().Should().Be("Liens Details");
        enriched.Should().Contain("Status: \"\" → Active");
        enriched.Should().Contain("Seller Status: Active → \"\"");
        enriched.Should().Contain("Note: Existing note → \"\"");
        enriched.Should().Contain("Organization: \"\" → RL Liens1");
        enriched.Should().Contain("Case: \"\" → CASE-TEST-001 — John Plaintiff");
        enriched.Should().Contain("Selling Case: \"\" → CASE-TEST-001 — John Plaintiff");
        enriched.Should().Contain("Facility: Unavailable facility → Sunrise Clinic");
        enriched.Should().Contain("Subject Party: \"\" → Jane Doe");
        enriched.Should().Contain("Funding Company: \"\" → Capital Fund LLC");
        enriched.Should().Contain("Funding Company Contact: \"\" → Capital Fund");
        enriched.Should().Contain("Medical Provider: \"\" → City Medical Center");
        enriched.Should().Contain("Medical Facility: \"\" → Sunrise Clinic");
        enriched.Should().Contain("Selling Organization: \"\" → RL Liens1");
        enriched.Should().Contain("Buying Organization: \"\" → RL Liens1");
        enriched.Should().Contain("Holding Organization: \"\" → RL Liens1");
        enriched.Should().NotContain(" ID:");
        enriched.Should().NotContain(unknownFacilityId.ToString());
    }

    [Fact]
    public async Task Lien_history_describes_an_actual_facility_reassignment_by_name()
    {
        await using var factory = new LiensApiFactory();
        var client = await CreateAuthenticatedClientAsync(factory);
        Guid newFacilityId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var newFacility = Facility.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                "Valley Clinic",
                SeedHelper.UserId);
            newFacilityId = newFacility.Id;
            db.Facilities.Add(newFacility);

            var lien = await db.Liens.SingleAsync(item => item.Id == SeedHelper.LienId);
            lien.AttachFacility(SeedHelper.FacilityId, SeedHelper.UserId);
            await db.SaveChangesAsync();
            lien.AttachFacility(newFacilityId, SeedHelper.UserId);
            await db.SaveChangesAsync();
        }

        var response = await client.PostAsJsonAsync("/api/liens/cases/liens-updates/v3", new
        {
            CaseId = SeedHelper.CaseId,
            page = 1,
            limit = 50,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await response.Content.ReadAsStringAsync()}");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var rows = body.RootElement.GetProperty("data").EnumerateArray().ToList();
        var descriptions = rows
            .Select(item => item.GetProperty("description").GetString())
            .ToList();

        rows.Should().Contain(item =>
            item.GetProperty("action").GetString() == "Liens Details" &&
            item.GetProperty("description").GetString()!.Contains(
                "Facility: Sunrise Clinic → Valley Clinic",
                StringComparison.Ordinal));
        descriptions.Should().Contain(description =>
            description!.Contains("Facility: Sunrise Clinic → Valley Clinic", StringComparison.Ordinal));
        descriptions.Should().NotContain(description =>
            description!.Contains(SeedHelper.FacilityId.ToString(), StringComparison.Ordinal) ||
            description.Contains(newFacilityId.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Lien_history_excludes_legacy_lien_update_servicing_rows()
    {
        await using var factory = new LiensApiFactory();
        var client = await CreateAuthenticatedClientAsync(factory);
        var (caseId, lienId) = await AddEmptyCaseAndLienAsync(factory);
        Guid hiddenUpdateId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var hiddenUpdate = ServicingItem.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                $"LIEN-UPDATE-{Guid.CreateVersion7():N}"[..36],
                "Lien Update",
                "Duplicate compatibility update",
                "system",
                SeedHelper.UserId,
                caseId: caseId,
                lienId: lienId);
            hiddenUpdateId = hiddenUpdate.Id;
            db.ServicingItems.Add(hiddenUpdate);
            await db.SaveChangesAsync();
        }

        var response = await client.PostAsJsonAsync("/api/liens/cases/liens-updates/v3", new
        {
            CaseId = caseId,
            page = 1,
            limit = 50,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await response.Content.ReadAsStringAsync()}");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("data").EnumerateArray().Should().NotContain(item =>
            item.GetProperty("id").GetString() == hiddenUpdateId.ToString());
        body.RootElement.GetProperty("totalCount").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Lien_history_excludes_case_detail_updates_without_a_lien()
    {
        await using var factory = new EnabledLegacyUpdateHistoryFactory();
        var client = await CreateAuthenticatedClientAsync(factory);
        var (caseId, lienId) = await AddEmptyCaseAndLienAsync(factory);
        var caseUpdate = LienCaseNote.Create(
            caseId,
            SeedHelper.TenantId,
            "Note updated",
            CaseNoteCategory.Internal,
            SeedHelper.UserId,
            "Stale Actor");
        var lienUpdate = CreateEvent(
            caseId,
            lienId,
            LegacyUpdateEvent.LienScope,
            700,
            description: "Lien-specific update");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            db.LienCaseNotes.Add(caseUpdate);
            db.LegacyUpdateEvents.Add(lienUpdate);
            await db.SaveChangesAsync();
        }

        var response = await client.PostAsJsonAsync("/api/liens/cases/liens-updates/v3", new
        {
            CaseId = caseId,
            page = 1,
            limit = 10,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await response.Content.ReadAsStringAsync()}");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("totalCount").GetInt32().Should().Be(2);
        var update = body.RootElement.GetProperty("data").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == lienUpdate.Id.ToString());
        update.GetProperty("id").GetString().Should().Be(lienUpdate.Id.ToString());
        update.GetProperty("caseId").GetString().Should().Be(caseId.ToString());
        update.GetProperty("lienId").GetString().Should().Be(lienId.ToString());
        update.GetProperty("action").GetString().Should().Be("Lien Update");
        update.GetProperty("description").GetString().Should().Be("Lien-specific update");
        body.RootElement.GetProperty("data").EnumerateArray().Should().NotContain(item =>
            item.GetProperty("id").GetString() == caseUpdate.Id.ToString());
    }

    [Fact]
    public async Task Enabled_case_history_pages_a_25000_event_timeline()
    {
        await using var factory = new EnabledLegacyUpdateHistoryFactory();
        var client = await CreateAuthenticatedClientAsync(factory);
        const int eventCount = 25_000;
        var firstAtUtc = Utc(2025, 1, 1, 0, 0, 0);
        var events = Enumerable.Range(1, eventCount)
            .Select(sequence => CreateEvent(
                SeedHelper.CaseId,
                null,
                LegacyUpdateEvent.CaseScope,
                sequence,
                firstAtUtc.AddSeconds(sequence)))
            .ToArray();
        await AddUpdateEventsAsync(factory, events);

        var response = await client.PostAsJsonAsync("/api/liens/cases/case-updates/v3", new
        {
            CaseId = SeedHelper.CaseId,
            page = 1,
            limit = 25,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await response.Content.ReadAsStringAsync()}");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("totalCount").GetInt32().Should().Be(eventCount + 1);
        var page = body.RootElement.GetProperty("data").EnumerateArray().ToList();
        page.Should().HaveCount(25);
        page[0].GetProperty("action").GetString().Should().Be("Case Created");
        page[1].GetProperty("id").GetString().Should().Be(events[^1].Id.ToString());
        page[^1].GetProperty("id").GetString().Should().Be(events[^24].Id.ToString());
    }

    [Theory]
    [InlineData("/api/liens/cases/case-updates/v3", 1, 201)]
    [InlineData("/api/liens/cases/case-updates/v3", 126, 200)]
    [InlineData("/api/liens/cases/liens-updates/v3", 1, 201)]
    [InlineData("/api/liens/cases/liens-updates/v3", 126, 200)]
    public async Task History_endpoints_reject_unbounded_pagination_windows(string path, int page, int limit)
    {
        await using var factory = new EnabledLegacyUpdateHistoryFactory();
        var client = await CreateAuthenticatedClientAsync(factory);

        var response = await client.PostAsJsonAsync(path, new
        {
            CaseId = SeedHelper.CaseId,
            page,
            limit,
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("isSuccess").GetBoolean().Should().BeFalse();
        body.RootElement.GetProperty("message").GetString().Should().Contain("Pagination is limited");
    }

    private static async Task<HttpClient> CreateAuthenticatedClientAsync(LiensApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        await SeedHelper.SeedAsync(scope.ServiceProvider);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            JwtTokenHelper.CreateFullAccessToken(SeedHelper.TenantId, SeedHelper.UserId));
        return client;
    }

    private static async Task AddUpdateEventsAsync(
        LiensApiFactory factory,
        params LegacyUpdateEvent[] events)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
        db.LegacyUpdateEvents.AddRange(events);
        await db.SaveChangesAsync();
    }

    private static async Task<(Guid CaseId, Guid LienId)> AddEmptyCaseAndLienAsync(LiensApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
        var caseEntity = Case.Create(
            SeedHelper.TenantId,
            SeedHelper.OrgId,
            $"UPDATE-HISTORY-{Guid.NewGuid():N}"[..28],
            "Legacy",
            "History",
            SeedHelper.UserId);
        var lien = Lien.Create(
            SeedHelper.TenantId,
            SeedHelper.OrgId,
            $"UPDATE-LIEN-{Guid.NewGuid():N}"[..28],
            LienType.MedicalLien,
            100m,
            SeedHelper.UserId,
            caseId: caseEntity.Id);
        db.Cases.Add(caseEntity);
        db.Liens.Add(lien);
        await db.SaveChangesAsync();
        return (caseEntity.Id, lien.Id);
    }

    private static LegacyUpdateEvent CreateEvent(
        Guid caseId,
        Guid? lienId,
        string scope,
        long sequence,
        DateTime? occurredAtUtc = null,
        string? description = "legacy description",
        string? actor = "Legacy Actor") =>
        LegacyUpdateEvent.Create(
            SeedHelper.TenantId,
            SeedHelper.OrgId,
            caseId,
            lienId,
            scope,
            scope == LegacyUpdateEvent.CaseScope ? "Case Details Update" : "Lien Update",
            description,
            actor,
            occurredAtUtc ?? Utc(2026, 8, 20, 17, 22, 8),
            Utc(2026, 8, 29, 1, 0, 0),
            Guid.NewGuid(),
            "SL-CORE",
            scope == LegacyUpdateEvent.CaseScope ? "SL_CASE_UPDATE_LOG" : "SL_LIENS_UPDATE_LOG",
            sequence.ToString(),
            sequence);

    private static void SetProperty<T>(T entity, string propertyName, object value) where T : class =>
        typeof(T).GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(entity, value);

    private static DateTime Utc(int year, int month, int day, int hour, int minute, int second) =>
        new(year, month, day, hour, minute, second, DateTimeKind.Utc);

    private sealed class EnabledLegacyUpdateHistoryFactory : LiensApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["LegacyUpdateHistory:Enabled"] = "true",
                }));
        }
    }
}
