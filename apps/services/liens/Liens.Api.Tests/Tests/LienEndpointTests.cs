using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BuildingBlocks.Authentication.ServiceTokens;
using Liens.Api.Tests.Helpers;
using Liens.Application.DTOs;
using Liens.Application.Interfaces;
using Liens.Domain.Entities;
using Liens.Domain.Enums;
using Liens.Infrastructure.Persistence;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Liens.Api.Tests.Tests;

public class LienEndpointTests : IClassFixture<LiensApiFactory>, IAsyncLifetime
{
    private readonly LiensApiFactory _factory;
    private HttpClient _client = null!;

    public LienEndpointTests(LiensApiFactory factory) => _factory = factory;

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
    public async Task CreateLien_reuses_the_same_record_for_a_repeated_idempotency_key()
    {
        using var firstRequest = new HttpRequestMessage(
            HttpMethod.Post, "/api/liens/liens")
        {
            Content = JsonContent.Create(new
            {
                lienNumber = $"B14-IDEMP-{Guid.NewGuid():N}",
                lienType = LienType.MedicalLien,
                caseId = SeedHelper.CaseId,
                originalAmount = 125m,
            }),
        };
        firstRequest.Headers.Add("Idempotency-Key", "b14-lien-idempotency-test");
        var first = await _client.SendAsync(firstRequest);
        first.StatusCode.Should().Be(HttpStatusCode.Created,
            $"Body: {await first.Content.ReadAsStringAsync()}");
        var firstBody = await first.Content.ReadFromJsonAsync<LienResponseBody>();

        using var secondRequest = new HttpRequestMessage(
            HttpMethod.Post, "/api/liens/liens")
        {
            Content = JsonContent.Create(new
            {
                lienNumber = $"B14-IDEMP-DIFFERENT-{Guid.NewGuid():N}",
                lienType = LienType.MedicalLien,
                caseId = SeedHelper.CaseId,
                originalAmount = 999m,
            }),
        };
        secondRequest.Headers.Add("Idempotency-Key", "b14-lien-idempotency-test");
        var second = await _client.SendAsync(secondRequest);
        second.StatusCode.Should().Be(HttpStatusCode.Created,
            $"Body: {await second.Content.ReadAsStringAsync()}");
        var secondBody = await second.Content.ReadFromJsonAsync<LienResponseBody>();

        secondBody!.Id.Should().Be(firstBody!.Id);
        secondBody.LienNumber.Should().Be(firstBody.LienNumber);
    }

    [Fact]
    public async Task SynqLien_document_association_is_service_token_protected_idempotent_and_relationship_checked()
    {
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CreateSynqLienServiceToken());
        var request = new
        {
            documentId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            targetType = "LIEN",
            targetId = SeedHelper.LienId,
            documentRole = "MEDICAL_BILL",
            documentReference = "documents:test",
            relatedCaseId = SeedHelper.CaseId,
        };

        using var first = new HttpRequestMessage(
            HttpMethod.Post, "/api/internal/synqlien/document-associations")
        {
            Content = JsonContent.Create(request),
        };
        first.Headers.Add("Idempotency-Key", "b15-association-idempotency");
        var firstResponse = await _client.SendAsync(first);
        firstResponse.StatusCode.Should().Be(HttpStatusCode.Created,
            $"Body: {await firstResponse.Content.ReadAsStringAsync()}");
        var firstBody = await firstResponse.Content.ReadFromJsonAsync<AssociationResponse>();

        using var replay = new HttpRequestMessage(
            HttpMethod.Post, "/api/internal/synqlien/document-associations")
        {
            Content = JsonContent.Create(new
            {
                request.documentId,
                request.targetType,
                request.targetId,
                documentRole = "different-role",
                request.documentReference,
                request.relatedCaseId,
            }),
        };
        replay.Headers.Add("Idempotency-Key", "b15-association-idempotency");
        var replayResponse = await _client.SendAsync(replay);
        replayResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var replayBody = await replayResponse.Content.ReadFromJsonAsync<AssociationResponse>();
        replayBody!.Data.AssociationId.Should().Be(firstBody!.Data.AssociationId);

        using var mismatch = new HttpRequestMessage(
            HttpMethod.Post, "/api/internal/synqlien/document-associations")
        {
            Content = JsonContent.Create(new
            {
                request.documentId,
                request.targetType,
                request.targetId,
                request.documentRole,
                request.documentReference,
                relatedCaseId = Guid.Parse("61111111-1111-1111-1111-111111111111"),
            }),
        };
        mismatch.Headers.Add("Idempotency-Key", "b15-association-relationship-mismatch");
        var mismatchResponse = await _client.SendAsync(mismatch);
        mismatchResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    private static string CreateSynqLienServiceToken() =>
        new ServiceTokenIssuer(Options.Create(new ServiceTokenOptions
        {
            SigningKey = JwtTokenHelper.SigningKey,
            ServiceName = "intake",
        })).IssueToken(
            SeedHelper.TenantId.ToString(),
            SeedHelper.UserId.ToString(),
            audience: "liens-service");

    private sealed record AssociationResponse(AssociationData Data);
    private sealed record AssociationData(Guid AssociationId);

    [Fact]
    public async Task CreateLien_defaults_lien_number_without_reusing_a_detached_historical_number()
    {
        var caseId = Guid.CreateVersion7();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            db.Cases.Add(Case.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                "26-000001",
                "Sequence",
                "Patient",
                SeedHelper.UserId));

            var caseEntity = db.Cases.Local.Single(c => c.CaseNumber == "26-000001");
            typeof(Case).GetProperty(nameof(Case.Id))!.SetValue(caseEntity, caseId);
            var historicalLien = Lien.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                "26-000001-01",
                LienType.MedicalLien,
                50m,
                SeedHelper.UserId);
            historicalLien.SetLegacyMedicalStatus(LienStatus.Cancelled, SeedHelper.UserId);
            db.Liens.Add(historicalLien);
            await db.SaveChangesAsync();
        }

        var first = await _client.PostAsJsonAsync("/api/liens/liens", new
        {
            lienNumber = "",
            lienType = LienType.MedicalLien,
            caseId,
            originalAmount = 100m,
        });

        first.StatusCode.Should().Be(HttpStatusCode.Created,
            $"Body: {await first.Content.ReadAsStringAsync()}");
        var firstBody = await first.Content.ReadFromJsonAsync<LienResponseBody>();
        firstBody!.LienNumber.Should().Be("26-000001-02");

        var second = await _client.PostAsJsonAsync("/api/liens/liens", new
        {
            lienNumber = "",
            lienType = LienType.MedicalLien,
            caseId,
            originalAmount = 200m,
        });

        second.StatusCode.Should().Be(HttpStatusCode.Created,
            $"Body: {await second.Content.ReadAsStringAsync()}");
        var secondBody = await second.Content.ReadFromJsonAsync<LienResponseBody>();
        secondBody!.LienNumber.Should().Be("26-000001-03");
    }

    [Fact]
    public async Task ListLiens_by_caseId_includes_formatted_dates_totalPurchase_and_totalBilling()
    {
        var caseId = Guid.CreateVersion7();
        var lienId = Guid.CreateVersion7();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();

            var caseEntity = Case.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                "26-410001",
                "Billing",
                "Case",
                SeedHelper.UserId);
            typeof(Case).GetProperty(nameof(Case.Id))!.SetValue(caseEntity, caseId);
            db.Cases.Add(caseEntity);

            var lien = Lien.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                "26-410001-01",
                LienType.MedicalLien,
                150m,
                SeedHelper.UserId,
                caseId: caseId,
                facilityId: SeedHelper.FacilityId,
                subjectFirstName: "Billing",
                subjectLastName: "Case",
                incidentDate: new DateOnly(2024, 6, 15),
                initialServiceDate: new DateOnly(2024, 6, 10),
                purchaseDate: new DateOnly(2024, 6, 15));
            typeof(Lien).GetProperty(nameof(Lien.Id))!.SetValue(lien, lienId);
            db.Liens.Add(lien);

            db.ServicingItems.Add(ServicingItem.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                "SVC-LIEN-001",
                "LegacyMedicalCode",
                "Medical code for lien list",
                "system",
                SeedHelper.UserId,
                caseId: caseId,
                lienId: lienId,
                notes: "code=12345; medicareCost=75.00; billingAmount=150.00; purchaseAmount=100.00; payee=Health System; outboundCheckNumber=CHK-100"));
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync($"/api/liens/liens?caseId={caseId}&page=1&pageSize=20");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await response.Content.ReadAsStringAsync()}");

        var body = await response.Content.ReadFromJsonAsync<PaginatedLiensResponseBody>();
        body.Should().NotBeNull();
        body!.Items.Should().ContainSingle();
        body.Items[0].PurchaseDate.Should().Be("06/15/2024");
        body.Items[0].InitialServiceDate.Should().Be("06/10/2024");
        body.Items[0].TotalPurchase.Should().Be(100m);
        body.Items[0].TotalBilling.Should().Be(150m);
    }

    [Fact]
    public async Task ListLiens_serializes_datetime_fields_in_utc()
    {
        var response = await _client.GetAsync("/api/liens/liens?page=1&pageSize=1");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await response.Content.ReadAsStringAsync()}");

        var json = await response.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var createdAtUtc = doc.RootElement
            .GetProperty("items")[0]
            .GetProperty("createdAtUtc")
            .GetString();

        createdAtUtc.Should().NotBeNullOrWhiteSpace();
        createdAtUtc!.EndsWith("Z", StringComparison.Ordinal)
            .Should().BeTrue($"expected UTC timestamp but got '{createdAtUtc}'");
    }

    [Fact]
    public async Task ListLiens_includes_plaintiff_law_firm_medical_facility_and_case_manager()
    {
        var caseManagerId = Guid.CreateVersion7();
        var lienNumber = "LIEN-LIST-CONTEXT-001";
        var lienId = Guid.CreateVersion7();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();

            var caseManager = Contact.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                ContactType.CaseManager,
                "Jamie",
                "Manager",
                SeedHelper.UserId,
                lawFirmId: SeedHelper.LawFirmId,
                contactSubtype: ContactSubtype.LawFirmCaseManager);
            typeof(Contact).GetProperty(nameof(Contact.Id))!.SetValue(caseManager, caseManagerId);
            db.Contacts.Add(caseManager);

            var caseEntity = db.Cases.Single(c => c.Id == SeedHelper.CaseId);
            caseEntity.Update(
                caseEntity.ClientFirstName,
                caseEntity.ClientLastName,
                SeedHelper.UserId,
                title: caseEntity.Title,
                externalReference: caseEntity.ExternalReference,
                clientDob: caseEntity.ClientDob,
                clientPhone: caseEntity.ClientPhone,
                clientEmail: caseEntity.ClientEmail,
                clientAddress: caseEntity.ClientAddress,
                dateOfIncident: caseEntity.DateOfIncident,
                insuranceCarrier: caseEntity.InsuranceCarrier,
                policyNumber: caseEntity.PolicyNumber,
                claimNumber: caseEntity.ClaimNumber,
                description: caseEntity.Description,
                notes: "[legacy-meta]\nlawFirmId=40000000-0000-0000-0000-000000000010;caseManagerId=" + caseManagerId);

            var lien = Lien.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                lienNumber,
                LienType.MedicalLien,
                150m,
                SeedHelper.UserId,
                caseId: SeedHelper.CaseId,
                facilityId: SeedHelper.FacilityId,
                subjectFirstName: "Context",
                subjectLastName: "Lien");
            typeof(Lien).GetProperty(nameof(Lien.Id))!.SetValue(lien, lienId);
            db.Liens.Add(lien);

            db.ServicingItems.Add(ServicingItem.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                "LMFI-LIST-001",
                "LegacyMedicalFacilityInfo",
                "Legacy medical facility information",
                "system",
                SeedHelper.UserId,
                caseId: SeedHelper.CaseId,
                lienId: lienId,
                notes: $"facilityId={SeedHelper.FacilityId};facilityName=Sunrise Clinic"));

            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync($"/api/liens/liens?search={lienNumber}&page=1&pageSize=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await response.Content.ReadAsStringAsync()}");

        var body = await response.Content.ReadFromJsonAsync<PaginatedLiensResponseBody>();
        body.Should().NotBeNull();
        var match = body!.Items.Single(item => item.LienNumber == lienNumber);
        match.Plaintiff.Should().Be("John Plaintiff");
        match.LawFirm.Should().Be("Smith & Associates LLP");
        match.MedicalFacility.Should().Be("Sunrise Clinic");
        match.CaseManager.Should().Be("Jamie Manager");
    }

    [Theory]
    [InlineData("Open")]
    [InlineData("Closed")]
    public async Task ListLiens_returns_business_status_label(string requestedStatusLabel)
    {
        var lienNumber = $"LIEN-STATUS-{requestedStatusLabel}";

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();

            var lien = Lien.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                lienNumber,
                LienType.MedicalLien,
                125m,
                SeedHelper.UserId,
                caseId: SeedHelper.CaseId);

            lien.SetLegacyMedicalStatus(requestedStatusLabel, SeedHelper.UserId);

            db.Liens.Add(lien);
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync($"/api/liens/liens?search={lienNumber}&page=1&pageSize=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await response.Content.ReadAsStringAsync()}");

        var body = await response.Content.ReadFromJsonAsync<PaginatedLiensResponseBody>();
        body.Should().NotBeNull();
        var match = body!.Items.Single(item => item.LienNumber == lienNumber);
        match.Status.Should().Be(requestedStatusLabel);
        match.StatusLabel.Should().Be(requestedStatusLabel);
    }

    [Fact]
    public async Task ListLiens_excludes_rejected_and_cancelled_before_pagination()
    {
        var prefix = $"LIEN-LIST-HIDE-{Guid.NewGuid():N}";

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();

            var openLien = Lien.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                $"{prefix}-OPEN",
                LienType.MedicalLien,
                125m,
                SeedHelper.UserId,
                caseId: SeedHelper.CaseId);

            var rejectedLien = Lien.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                $"{prefix}-REJECTED",
                LienType.MedicalLien,
                125m,
                SeedHelper.UserId,
                caseId: SeedHelper.CaseId);
            rejectedLien.SetLegacyMedicalStatus("Rejected", SeedHelper.UserId);

            var cancelledLien = Lien.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                $"{prefix}-CANCELLED",
                LienType.MedicalLien,
                125m,
                SeedHelper.UserId,
                caseId: SeedHelper.CaseId);
            cancelledLien.SetLegacyMedicalStatus("Cancelled", SeedHelper.UserId);

            db.Liens.AddRange(openLien, rejectedLien, cancelledLien);
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync(
            $"/api/liens/liens?search={prefix}&page=1&pageSize=1");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await response.Content.ReadAsStringAsync()}");

        var body = await response.Content.ReadFromJsonAsync<PaginatedLiensResponseBody>();
        body.Should().NotBeNull();
        body!.TotalCount.Should().Be(1);
        body.Items.Should().ContainSingle();
        body.Items[0].LienNumber.Should().Be($"{prefix}-OPEN");
        body.Items[0].Status.Should().NotBe("Rejected").And.NotBe("Cancelled");
    }

    [Fact]
    public async Task Lien_status_group_open_expands_for_get_and_advanced_search()
    {
        var prefix = $"LIEN-OPEN-GROUP-{Guid.CreateVersion7():N}";

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var draft = Lien.Create(
                SeedHelper.TenantId, SeedHelper.OrgId, $"{prefix}-DRAFT", LienType.MedicalLien,
                125m, SeedHelper.UserId, caseId: SeedHelper.CaseId);
            var active = Lien.Create(
                SeedHelper.TenantId, SeedHelper.OrgId, $"{prefix}-ACTIVE", LienType.MedicalLien,
                125m, SeedHelper.UserId, caseId: SeedHelper.CaseId);
            active.SetLegacyMedicalStatus(LienStatus.Active, SeedHelper.UserId);
            var settled = Lien.Create(
                SeedHelper.TenantId, SeedHelper.OrgId, $"{prefix}-SETTLED", LienType.MedicalLien,
                125m, SeedHelper.UserId, caseId: SeedHelper.CaseId);
            settled.SetLegacyMedicalStatus(LienStatus.Settled, SeedHelper.UserId);

            db.Liens.AddRange(draft, active, settled);
            await db.SaveChangesAsync();
        }

        var getResponse = await _client.GetAsync(
            $"/api/liens/liens?search={prefix}&status=Open&page=1&pageSize=20");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await getResponse.Content.ReadAsStringAsync()}");
        var getBody = await getResponse.Content.ReadFromJsonAsync<PaginatedLiensResponseBody>();
        getBody!.Items.Select(item => item.LienNumber)
            .Should().Contain([ $"{prefix}-DRAFT", $"{prefix}-ACTIVE" ]);
        getBody.Items.Should().NotContain(item => item.LienNumber == $"{prefix}-SETTLED");

        var postResponse = await _client.PostAsJsonAsync("/api/liens/liens/search", new
        {
            search = prefix,
            lienStatusIds = new[] { "Open" },
            page = 1,
            pageSize = 20,
        });
        postResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await postResponse.Content.ReadAsStringAsync()}");
        var postBody = await postResponse.Content.ReadFromJsonAsync<PaginatedLiensResponseBody>();
        postBody!.Items.Select(item => item.LienNumber)
            .Should().Contain([ $"{prefix}-DRAFT", $"{prefix}-ACTIVE" ]);
        postBody.Items.Should().NotContain(item => item.LienNumber == $"{prefix}-SETTLED");
    }

    [Fact]
    public async Task SearchLiens_status_filter_enriches_only_the_requested_page()
    {
        const int pageSize = 3;
        const int matchingLienCount = 8;
        var prefix = $"LIEN-STATUS-PAGING-{Guid.CreateVersion7():N}";
        var servicingItems = new CountingServicingItemService(pageSize);

        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IServicingItemService>();
                services.AddSingleton<IServicingItemService>(servicingItems);
            }));

        using (var scope = factory.Services.CreateScope())
        {
            await SeedHelper.SeedAsync(scope.ServiceProvider);
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();

            for (var index = 0; index < matchingLienCount; index++)
            {
                var lien = Lien.Create(
                    SeedHelper.TenantId,
                    SeedHelper.OrgId,
                    $"{prefix}-{index:D2}",
                    LienType.MedicalLien,
                    125m,
                    SeedHelper.UserId,
                    caseId: SeedHelper.CaseId);
                lien.SetLegacyMedicalStatus(LienStatus.Active, SeedHelper.UserId);
                db.Liens.Add(lien);
            }

            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer",
                JwtTokenHelper.CreateFullAccessToken(SeedHelper.TenantId, SeedHelper.UserId));

        var response = await client.PostAsJsonAsync("/api/liens/liens/search", new
        {
            search = prefix,
            lienStatusIds = new[] { LienStatus.Active },
            page = 1,
            pageSize,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await response.Content.ReadAsStringAsync()}");

        var body = await response.Content.ReadFromJsonAsync<PaginatedLiensResponseBody>();
        body.Should().NotBeNull();
        body!.TotalCount.Should().Be(matchingLienCount);
        body.Items.Should().HaveCount(pageSize);
        servicingItems.SearchCallCount.Should().Be(pageSize);
    }

    [Fact]
    public async Task SearchLiens_uses_lookup_status_display_name_when_legacy_code_is_not_a_status()
    {
        var prefix = $"LIEN-OPEN-LOOKUP-{Guid.CreateVersion7():N}";
        Guid openStatusLookupId;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var openStatus = LookupValue.Create(
                LookupCategory.LienStatus,
                "1",
                "Open",
                SeedHelper.UserId,
                tenantId: SeedHelper.TenantId);
            var draft = Lien.Create(
                SeedHelper.TenantId, SeedHelper.OrgId, $"{prefix}-DRAFT", LienType.MedicalLien,
                125m, SeedHelper.UserId, caseId: SeedHelper.CaseId);
            var active = Lien.Create(
                SeedHelper.TenantId, SeedHelper.OrgId, $"{prefix}-ACTIVE", LienType.MedicalLien,
                125m, SeedHelper.UserId, caseId: SeedHelper.CaseId);
            active.SetLegacyMedicalStatus(LienStatus.Active, SeedHelper.UserId);
            var settled = Lien.Create(
                SeedHelper.TenantId, SeedHelper.OrgId, $"{prefix}-SETTLED", LienType.MedicalLien,
                125m, SeedHelper.UserId, caseId: SeedHelper.CaseId);
            settled.SetLegacyMedicalStatus(LienStatus.Settled, SeedHelper.UserId);

            openStatusLookupId = openStatus.Id;
            db.LookupValues.Add(openStatus);
            db.Liens.AddRange(draft, active, settled);
            await db.SaveChangesAsync();
        }

        var response = await _client.PostAsJsonAsync("/api/liens/liens/search", new
        {
            page = 1,
            pageSize = 10,
            lawFirmIds = Array.Empty<string>(),
            medicalFacilityIds = Array.Empty<string>(),
            caseManagerIds = Array.Empty<string>(),
            lienStatusIds = new[] { openStatusLookupId.ToString() },
            search = prefix,
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await response.Content.ReadAsStringAsync()}");

        var body = await response.Content.ReadFromJsonAsync<PaginatedLiensResponseBody>();
        body!.Items.Select(item => item.LienNumber)
            .Should().Contain([ $"{prefix}-DRAFT", $"{prefix}-ACTIVE" ]);
        body.Items.Should().NotContain(item => item.LienNumber == $"{prefix}-SETTLED");
    }

    [Fact]
    public async Task SearchLiens_expands_legacy_lookup_id_backed_by_a_canonical_open_status()
    {
        var prefix = $"LIEN-OPEN-LOOKUP-CANONICAL-{Guid.CreateVersion7():N}";
        Guid openStatusLookupId;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            // The legacy lookup endpoint returns this row's ID for its
            // business-level "Open" option, even though the stored code is
            // the first canonical Open lifecycle status (Draft).
            var openStatus = LookupValue.Create(
                LookupCategory.LienStatus,
                LienStatus.Draft,
                "Draft",
                SeedHelper.UserId,
                tenantId: SeedHelper.TenantId);
            var draft = Lien.Create(
                SeedHelper.TenantId, SeedHelper.OrgId, $"{prefix}-DRAFT", LienType.MedicalLien,
                125m, SeedHelper.UserId, caseId: SeedHelper.CaseId);
            var active = Lien.Create(
                SeedHelper.TenantId, SeedHelper.OrgId, $"{prefix}-ACTIVE", LienType.MedicalLien,
                125m, SeedHelper.UserId, caseId: SeedHelper.CaseId);
            active.SetLegacyMedicalStatus(LienStatus.Active, SeedHelper.UserId);
            var settled = Lien.Create(
                SeedHelper.TenantId, SeedHelper.OrgId, $"{prefix}-SETTLED", LienType.MedicalLien,
                125m, SeedHelper.UserId, caseId: SeedHelper.CaseId);
            settled.SetLegacyMedicalStatus(LienStatus.Settled, SeedHelper.UserId);

            openStatusLookupId = openStatus.Id;
            db.LookupValues.Add(openStatus);
            db.Liens.AddRange(draft, active, settled);
            await db.SaveChangesAsync();
        }

        var response = await _client.PostAsJsonAsync("/api/liens/liens/search", new
        {
            page = 1,
            pageSize = 10,
            lienStatusIds = new[] { openStatusLookupId.ToString() },
            search = prefix,
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await response.Content.ReadAsStringAsync()}");

        var body = await response.Content.ReadFromJsonAsync<PaginatedLiensResponseBody>();
        body!.Items.Select(item => item.LienNumber)
            .Should().Contain([ $"{prefix}-DRAFT", $"{prefix}-ACTIVE" ]);
        body.Items.Should().NotContain(item => item.LienNumber == $"{prefix}-SETTLED");
    }

    [Fact]
    public async Task Lien_status_canonical_value_remains_exact()
    {
        var prefix = $"LIEN-CANONICAL-STATUS-{Guid.CreateVersion7():N}";

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var draft = Lien.Create(
                SeedHelper.TenantId, SeedHelper.OrgId, $"{prefix}-DRAFT", LienType.MedicalLien,
                125m, SeedHelper.UserId, caseId: SeedHelper.CaseId);
            var active = Lien.Create(
                SeedHelper.TenantId, SeedHelper.OrgId, $"{prefix}-ACTIVE", LienType.MedicalLien,
                125m, SeedHelper.UserId, caseId: SeedHelper.CaseId);
            active.SetLegacyMedicalStatus(LienStatus.Active, SeedHelper.UserId);
            db.Liens.AddRange(draft, active);
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync(
            $"/api/liens/liens?search={prefix}&status=Draft&page=1&pageSize=20");
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await response.Content.ReadAsStringAsync()}");
        var body = await response.Content.ReadFromJsonAsync<PaginatedLiensResponseBody>();
        body!.Items.Should().ContainSingle(item => item.LienNumber == $"{prefix}-DRAFT");
        body.Items.Should().NotContain(item => item.LienNumber == $"{prefix}-ACTIVE");
    }

    [Fact]
    public async Task Lien_status_group_rejected_includes_cancelled_in_all_list_paths()
    {
        var prefix = $"LIEN-REJECTED-GROUP-{Guid.CreateVersion7():N}";

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var declined = Lien.Create(
                SeedHelper.TenantId, SeedHelper.OrgId, $"{prefix}-DECLINED", LienType.MedicalLien,
                125m, SeedHelper.UserId, caseId: SeedHelper.CaseId);
            declined.SetLegacyMedicalStatus(LienStatus.Declined, SeedHelper.UserId);
            var withdrawn = Lien.Create(
                SeedHelper.TenantId, SeedHelper.OrgId, $"{prefix}-WITHDRAWN", LienType.MedicalLien,
                125m, SeedHelper.UserId, caseId: SeedHelper.CaseId);
            withdrawn.SetLegacyMedicalStatus(LienStatus.Withdrawn, SeedHelper.UserId);
            var cancelled = Lien.Create(
                SeedHelper.TenantId, SeedHelper.OrgId, $"{prefix}-CANCELLED", LienType.MedicalLien,
                125m, SeedHelper.UserId, caseId: SeedHelper.CaseId);
            cancelled.SetLegacyMedicalStatus(LienStatus.Cancelled, SeedHelper.UserId);
            db.Liens.AddRange(declined, withdrawn, cancelled);
            await db.SaveChangesAsync();
        }

        var expected = new[] { $"{prefix}-DECLINED", $"{prefix}-WITHDRAWN", $"{prefix}-CANCELLED" };

        var getResponse = await _client.GetAsync(
            $"/api/liens/liens?search={prefix}&status=Rejected&page=1&pageSize=20");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await getResponse.Content.ReadAsStringAsync()}");
        var getBody = await getResponse.Content.ReadFromJsonAsync<PaginatedLiensResponseBody>();
        getBody!.Items.Select(item => item.LienNumber).Should().Contain(expected);

        var searchResponse = await _client.PostAsJsonAsync("/api/liens/liens/search", new
        {
            search = prefix,
            lienStatusIds = new[] { "Rejected" },
            page = 1,
            pageSize = 20,
        });
        searchResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await searchResponse.Content.ReadAsStringAsync()}");
        var searchBody = await searchResponse.Content.ReadFromJsonAsync<PaginatedLiensResponseBody>();
        searchBody!.Items.Select(item => item.LienNumber).Should().Contain(expected);

        var advancedResponse = await _client.GetAsync(
            $"/api/liens/liens?search={prefix}&lienStatusIds=Rejected&page=1&pageSize=20");
        advancedResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await advancedResponse.Content.ReadAsStringAsync()}");
        var advancedBody = await advancedResponse.Content.ReadFromJsonAsync<PaginatedLiensResponseBody>();
        advancedBody!.Items.Select(item => item.LienNumber).Should().Contain(expected);
    }

    [Theory]
    [InlineData("Open")]
    [InlineData("Closed")]
    public async Task SearchLiensV3_returns_business_status_label(string requestedStatusLabel)
    {
        var lienNumber = $"CASE-LIEN-STATUS-{requestedStatusLabel}";

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();

            var lien = Lien.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                lienNumber,
                LienType.MedicalLien,
                125m,
                SeedHelper.UserId,
                caseId: SeedHelper.CaseId);

            lien.SetLegacyMedicalStatus(requestedStatusLabel, SeedHelper.UserId);

            db.Liens.Add(lien);
            await db.SaveChangesAsync();
        }

        var response = await _client.PostAsJsonAsync("/api/liens/cases/liens/v3", new
        {
            caseId = SeedHelper.CaseId,
            page = 1,
            limit = 50,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await response.Content.ReadAsStringAsync()}");

        var body = await response.Content.ReadFromJsonAsync<PaginatedLiensResponseBody>();
        body.Should().NotBeNull();
        var match = body!.Items.Single(item => item.LienNumber == lienNumber);
        match.Status.Should().Be(requestedStatusLabel);
        match.StatusLabel.Should().Be(requestedStatusLabel);
    }

    [Fact]
    public async Task ListLiens_by_caseId_excludes_rejected_liens_from_response()
    {
        var lienNumber = "CASE-LIEN-HIDE-REJECTED";

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();

            var lien = Lien.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                lienNumber,
                LienType.MedicalLien,
                125m,
                SeedHelper.UserId,
                caseId: SeedHelper.CaseId);

            lien.SetLegacyMedicalStatus("Rejected", SeedHelper.UserId);

            db.Liens.Add(lien);
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync($"/api/liens/liens?caseId={SeedHelper.CaseId}&page=1&pageSize=50");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await response.Content.ReadAsStringAsync()}");

        var body = await response.Content.ReadFromJsonAsync<PaginatedLiensResponseBody>();
        body.Should().NotBeNull();
        body!.Items.Should().NotContain(item => item.LienNumber == lienNumber);
        body.TotalCount.Should().Be(body.Items.Count);
    }

    [Fact]
    public async Task ListLiens_supports_advanced_get_filters()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();

            var caseEntity = db.Cases.Single(c => c.Id == SeedHelper.CaseId);
            caseEntity.Update(
                caseEntity.ClientFirstName,
                caseEntity.ClientLastName,
                SeedHelper.UserId,
                title: caseEntity.Title,
                externalReference: caseEntity.ExternalReference,
                clientDob: caseEntity.ClientDob,
                clientPhone: caseEntity.ClientPhone,
                clientEmail: caseEntity.ClientEmail,
                clientAddress: caseEntity.ClientAddress,
                dateOfIncident: caseEntity.DateOfIncident,
                insuranceCarrier: caseEntity.InsuranceCarrier,
                policyNumber: caseEntity.PolicyNumber,
                claimNumber: caseEntity.ClaimNumber,
                description: caseEntity.Description,
                notes: $"[legacy-meta]{Environment.NewLine}lawFirmId={SeedHelper.LawFirmId}");

            var lien = db.Liens.Single(l => l.Id == SeedHelper.LienId);
            lien.Update(
                lien.LienType,
                lien.OriginalAmount,
                SeedHelper.UserId,
                externalReference: lien.ExternalReference,
                subjectFirstName: lien.SubjectFirstName,
                subjectLastName: lien.SubjectLastName,
                isConfidential: lien.IsConfidential,
                jurisdiction: lien.Jurisdiction,
                incidentDate: new DateOnly(2026, 7, 16),
                initialServiceDate: lien.InitialServiceDate,
                endServiceDate: lien.EndServiceDate,
                isBulk: lien.IsBulk,
                isServicing: lien.IsServicing,
                description: lien.Description,
                notes: lien.Notes,
                purchaseDate: new DateOnly(2026, 7, 16));

            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync(
            $"/api/liens/liens?page=1&pageSize=10" +
            $"&lawFirmIds={SeedHelper.LawFirmId}" +
            $"&purchaseDateFrom=2026-07-16" +
            $"&purchaseDateTo=2026-07-16");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await response.Content.ReadAsStringAsync()}");

        var body = await response.Content.ReadFromJsonAsync<PaginatedLiensResponseBody>();
        body.Should().NotBeNull();
        body!.Items.Should().ContainSingle();
        body.TotalCount.Should().Be(1);
        body.Items.Single().LienNumber.Should().Be("LIEN-TEST-001");
    }

    [Fact]
    public async Task SearchLiens_post_supports_advanced_filters()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var lien = db.Liens.Single(l => l.Id == SeedHelper.LienId);
            lien.Update(
                lien.LienType,
                lien.OriginalAmount,
                SeedHelper.UserId,
                externalReference: lien.ExternalReference,
                subjectFirstName: lien.SubjectFirstName,
                subjectLastName: lien.SubjectLastName,
                isConfidential: lien.IsConfidential,
                jurisdiction: lien.Jurisdiction,
                incidentDate: new DateOnly(2024, 6, 15),
                initialServiceDate: lien.InitialServiceDate,
                endServiceDate: lien.EndServiceDate,
                isBulk: lien.IsBulk,
                isServicing: lien.IsServicing,
                description: lien.Description,
                notes: lien.Notes,
                purchaseDate: new DateOnly(2024, 6, 15));
            await db.SaveChangesAsync();
        }

        var response = await _client.PostAsJsonAsync("/api/liens/liens/search", new
        {
            page = 1,
            pageSize = 10,
            lienStatusIds = Array.Empty<string>(),
            lawFirmIds = Array.Empty<string>(),
            medicalFacilityIds = Array.Empty<string>(),
            caseManagerIds = Array.Empty<string>(),
            purchaseDateFrom = "2024-06-15",
            purchaseDateTo = "2024-06-15",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await response.Content.ReadAsStringAsync()}");

        var body = await response.Content.ReadFromJsonAsync<PaginatedLiensResponseBody>();
        body.Should().NotBeNull();
        body!.Items.Should().NotBeEmpty();
        body.TotalCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ListLiens_supports_sorting_for_enriched_and_amount_fields()
    {
        var secondCaseId = Guid.CreateVersion7();
        var secondLienId = Guid.CreateVersion7();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();

            var secondCase = Case.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                "26-499999",
                "Aaron",
                "Alpha",
                SeedHelper.UserId);
            typeof(Case).GetProperty(nameof(Case.Id))!.SetValue(secondCase, secondCaseId);
            db.Cases.Add(secondCase);

            var secondLien = Lien.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                "26-499999-01",
                LienType.MedicalLien,
                90m,
                SeedHelper.UserId,
                caseId: secondCaseId,
                facilityId: SeedHelper.FacilityId,
                subjectFirstName: "Aaron",
                subjectLastName: "Alpha",
                incidentDate: new DateOnly(2024, 6, 10),
                initialServiceDate: new DateOnly(2024, 4, 1));
            typeof(Lien).GetProperty(nameof(Lien.Id))!.SetValue(secondLien, secondLienId);
            db.Liens.Add(secondLien);

            db.ServicingItems.Add(ServicingItem.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                "SVC-LIEN-SORT-001",
                "LegacyMedicalCode",
                "Medical code for sorting",
                "system",
                SeedHelper.UserId,
                caseId: SeedHelper.CaseId,
                lienId: SeedHelper.LienId,
                notes: "code=12345; medicareCost=75.00; billingAmount=150.00; purchaseAmount=100.00; payee=Health System; outboundCheckNumber=CHK-100"));

            db.ServicingItems.Add(ServicingItem.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                "SVC-LIEN-SORT-002",
                "LegacyMedicalCode",
                "Medical code for sorting",
                "system",
                SeedHelper.UserId,
                caseId: secondCaseId,
                lienId: secondLienId,
                notes: "code=98765; medicareCost=45.00; billingAmount=90.00; purchaseAmount=60.00; payee=Alpha Health; outboundCheckNumber=CHK-200"));

            await db.SaveChangesAsync();
        }

        var plaintiffAscResponse = await _client.GetAsync(
            "/api/liens/liens?page=1&pageSize=10&sortBy=plaintiffName&sortDirection=asc");

        plaintiffAscResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await plaintiffAscResponse.Content.ReadAsStringAsync()}");

        var plaintiffAscBody = await plaintiffAscResponse.Content.ReadFromJsonAsync<PaginatedLiensResponseBody>();
        plaintiffAscBody.Should().NotBeNull();
        plaintiffAscBody!.Items.Should().HaveCountGreaterOrEqualTo(2);
        plaintiffAscBody.Items.Take(2).Select(item => item.Plaintiff)
            .Should().Equal("Aaron Alpha", "John Plaintiff");

        var billingDescResponse = await _client.GetAsync(
            "/api/liens/liens?page=1&pageSize=10&sortBy=billingAmount&sortDirection=desc");

        billingDescResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await billingDescResponse.Content.ReadAsStringAsync()}");

        var billingDescBody = await billingDescResponse.Content.ReadFromJsonAsync<PaginatedLiensResponseBody>();
        billingDescBody.Should().NotBeNull();
        billingDescBody!.Items.Should().HaveCountGreaterOrEqualTo(2);
        billingDescBody.Items.Take(2).Select(item => item.TotalBilling)
            .Should().Equal(150m, 90m);
    }

    [Fact]
    public async Task ListLiens_sorts_by_purchase_date_and_servicing_status()
    {
        var caseId = Guid.CreateVersion7();
        const string earlierPurchaseLienNumber = "26-700001-01";
        const string laterPurchaseLienNumber = "26-700001-02";

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var caseEntity = Case.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                "26-700001",
                "Sort",
                "Tester",
                SeedHelper.UserId);
            typeof(Case).GetProperty(nameof(Case.Id))!.SetValue(caseEntity, caseId);
            db.Cases.Add(caseEntity);

            var earlierPurchaseLien = Lien.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                earlierPurchaseLienNumber,
                LienType.MedicalLien,
                100m,
                SeedHelper.UserId,
                caseId: caseId,
                incidentDate: new DateOnly(2025, 12, 31),
                isServicing: "false",
                purchaseDate: new DateOnly(2024, 1, 15));
            var laterPurchaseLien = Lien.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                laterPurchaseLienNumber,
                LienType.MedicalLien,
                100m,
                SeedHelper.UserId,
                caseId: caseId,
                incidentDate: new DateOnly(2024, 1, 1),
                isServicing: "true",
                purchaseDate: new DateOnly(2024, 12, 15));
            var voidedPayment = SettlementPaymentDetail.Create(
                SeedHelper.TenantId,
                caseId,
                earlierPurchaseLien.Id,
                2,
                500m,
                SeedHelper.UserId);
            voidedPayment.Void(SeedHelper.UserId, "Test voided payment");

            db.Liens.AddRange(earlierPurchaseLien, laterPurchaseLien);
            db.SettlementPaymentDetails.AddRange(
                SettlementPaymentDetail.Create(
                    SeedHelper.TenantId,
                    caseId,
                    earlierPurchaseLien.Id,
                    1,
                    100m,
                    SeedHelper.UserId),
                SettlementPaymentDetail.Create(
                    SeedHelper.TenantId,
                    caseId,
                    laterPurchaseLien.Id,
                    1,
                    300m,
                    SeedHelper.UserId),
                voidedPayment);
            await db.SaveChangesAsync();
        }

        var purchaseDateAsc = await _client.GetFromJsonAsync<PaginatedLiensResponseBody>(
            $"/api/liens/liens?caseId={caseId}&pageSize=20&sortBy=purchaseDate&sortDirection=asc");
        purchaseDateAsc!.Items.Select(item => item.LienNumber)
            .Should().Equal(earlierPurchaseLienNumber, laterPurchaseLienNumber);

        var purchaseDateDesc = await _client.GetFromJsonAsync<PaginatedLiensResponseBody>(
            $"/api/liens/liens?caseId={caseId}&pageSize=20&sortBy=purchaseDate&sortDirection=desc");
        purchaseDateDesc!.Items.Select(item => item.LienNumber)
            .Should().Equal(laterPurchaseLienNumber, earlierPurchaseLienNumber);

        var servicingAsc = await _client.GetFromJsonAsync<PaginatedLiensResponseBody>(
            $"/api/liens/liens?caseId={caseId}&pageSize=20&sortBy=isServicing&sortDirection=asc");
        servicingAsc!.Items.Select(item => item.LienNumber)
            .Should().Equal(earlierPurchaseLienNumber, laterPurchaseLienNumber);

        var servicingDesc = await _client.GetFromJsonAsync<PaginatedLiensResponseBody>(
            $"/api/liens/liens?caseId={caseId}&pageSize=20&sortBy=isServicing&sortDirection=desc");
        servicingDesc!.Items.Select(item => item.LienNumber)
            .Should().Equal(laterPurchaseLienNumber, earlierPurchaseLienNumber);

        var amountReceivedAscResponse = await _client.GetAsync(
            $"/api/liens/liens?caseId={caseId}&pageSize=20&sortBy=amountReceived&sortDirection=asc");
        amountReceivedAscResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await amountReceivedAscResponse.Content.ReadAsStringAsync()}");
        var amountReceivedAsc = await amountReceivedAscResponse.Content
            .ReadFromJsonAsync<PaginatedLiensResponseBody>();
        amountReceivedAsc!.Items.Select(item => item.LienNumber)
            .Should().Equal(earlierPurchaseLienNumber, laterPurchaseLienNumber);

        var amountReceivedDesc = await _client.GetFromJsonAsync<PaginatedLiensResponseBody>(
            $"/api/liens/liens?caseId={caseId}&pageSize=20&sortBy=amountReceived&sortDirection=desc");
        amountReceivedDesc!.Items.Select(item => item.LienNumber)
            .Should().Equal(laterPurchaseLienNumber, earlierPurchaseLienNumber);

        var paymentAliasAsc = await _client.GetFromJsonAsync<PaginatedLiensResponseBody>(
            $"/api/liens/liens?caseId={caseId}&pageSize=20&sortBy=payment&sortDirection=asc");
        paymentAliasAsc!.Items.Select(item => item.LienNumber)
            .Should().Equal(earlierPurchaseLienNumber, laterPurchaseLienNumber);
    }

    [Fact]
    public async Task ListLiens_purchase_date_range_is_inclusive_for_from_and_to()
    {
        var july17CaseId = Guid.CreateVersion7();
        var july18CaseId = Guid.CreateVersion7();
        var july23CaseId = Guid.CreateVersion7();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();

            var july17Case = Case.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                "26-700017",
                "Range",
                "Seventeen",
                SeedHelper.UserId);
            typeof(Case).GetProperty(nameof(Case.Id))!.SetValue(july17Case, july17CaseId);
            db.Cases.Add(july17Case);

            var july18Case = Case.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                "26-700018",
                "Range",
                "Eighteen",
                SeedHelper.UserId);
            typeof(Case).GetProperty(nameof(Case.Id))!.SetValue(july18Case, july18CaseId);
            db.Cases.Add(july18Case);

            var july23Case = Case.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                "26-700023",
                "Range",
                "TwentyThree",
                SeedHelper.UserId);
            typeof(Case).GetProperty(nameof(Case.Id))!.SetValue(july23Case, july23CaseId);
            db.Cases.Add(july23Case);

            db.Liens.Add(Lien.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                "26-700017-01",
                LienType.MedicalLien,
                100m,
                SeedHelper.UserId,
                caseId: july17CaseId,
                incidentDate: new DateOnly(2026, 7, 17),
                purchaseDate: new DateOnly(2026, 7, 17)));

            db.Liens.Add(Lien.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                "26-700018-01",
                LienType.MedicalLien,
                100m,
                SeedHelper.UserId,
                caseId: july18CaseId,
                incidentDate: new DateOnly(2026, 7, 18),
                purchaseDate: new DateOnly(2026, 7, 18)));

            db.Liens.Add(Lien.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                "26-700023-01",
                LienType.MedicalLien,
                100m,
                SeedHelper.UserId,
                caseId: july23CaseId,
                incidentDate: new DateOnly(2026, 7, 23),
                purchaseDate: new DateOnly(2026, 7, 23)));

            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync(
            "/api/liens/liens?page=1&pageSize=20&purchaseDateFrom=2026-07-17&purchaseDateTo=2026-07-18&sortBy=lienNumber&sortDirection=asc");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await response.Content.ReadAsStringAsync()}");

        var body = await response.Content.ReadFromJsonAsync<PaginatedLiensResponseBody>();
        body.Should().NotBeNull();
        body!.Items.Select(item => item.LienNumber)
            .Should().Contain(["26-700017-01", "26-700018-01"]);
        body.Items.Select(item => item.LienNumber)
            .Should().NotContain("26-700023-01");
    }

    [Fact]
    public async Task ListLiens_purchase_date_filters_support_from_only_and_to_only()
    {
        var july17CaseId = Guid.CreateVersion7();
        var july18CaseId = Guid.CreateVersion7();
        var july23CaseId = Guid.CreateVersion7();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();

            var july17Case = Case.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                "26-710017",
                "Only",
                "Seventeen",
                SeedHelper.UserId);
            typeof(Case).GetProperty(nameof(Case.Id))!.SetValue(july17Case, july17CaseId);
            db.Cases.Add(july17Case);

            var july18Case = Case.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                "26-710018",
                "Only",
                "Eighteen",
                SeedHelper.UserId);
            typeof(Case).GetProperty(nameof(Case.Id))!.SetValue(july18Case, july18CaseId);
            db.Cases.Add(july18Case);

            var july23Case = Case.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                "26-710023",
                "Only",
                "TwentyThree",
                SeedHelper.UserId);
            typeof(Case).GetProperty(nameof(Case.Id))!.SetValue(july23Case, july23CaseId);
            db.Cases.Add(july23Case);

            db.Liens.Add(Lien.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                "26-710017-01",
                LienType.MedicalLien,
                100m,
                SeedHelper.UserId,
                caseId: july17CaseId,
                incidentDate: new DateOnly(2026, 7, 17),
                purchaseDate: new DateOnly(2026, 7, 17)));

            db.Liens.Add(Lien.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                "26-710018-01",
                LienType.MedicalLien,
                100m,
                SeedHelper.UserId,
                caseId: july18CaseId,
                incidentDate: new DateOnly(2026, 7, 18),
                purchaseDate: new DateOnly(2026, 7, 18)));

            db.Liens.Add(Lien.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                "26-710023-01",
                LienType.MedicalLien,
                100m,
                SeedHelper.UserId,
                caseId: july23CaseId,
                incidentDate: new DateOnly(2026, 7, 23),
                purchaseDate: new DateOnly(2026, 7, 23)));

            await db.SaveChangesAsync();
        }

        var fromOnlyResponse = await _client.GetAsync(
            "/api/liens/liens?page=1&pageSize=20&purchaseDateFrom=2026-07-18&sortBy=lienNumber&sortDirection=asc");

        fromOnlyResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await fromOnlyResponse.Content.ReadAsStringAsync()}");

        var fromOnlyBody = await fromOnlyResponse.Content.ReadFromJsonAsync<PaginatedLiensResponseBody>();
        fromOnlyBody.Should().NotBeNull();
        fromOnlyBody!.Items.Select(item => item.LienNumber)
            .Should().Contain(["26-710018-01", "26-710023-01"]);
        fromOnlyBody.Items.Select(item => item.LienNumber)
            .Should().NotContain("26-710017-01");

        var toOnlyResponse = await _client.GetAsync(
            "/api/liens/liens?page=1&pageSize=20&purchaseDateTo=2026-07-17&sortBy=lienNumber&sortDirection=asc");

        toOnlyResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await toOnlyResponse.Content.ReadAsStringAsync()}");

        var toOnlyBody = await toOnlyResponse.Content.ReadFromJsonAsync<PaginatedLiensResponseBody>();
        toOnlyBody.Should().NotBeNull();
        toOnlyBody!.Items.Select(item => item.LienNumber)
            .Should().Contain("26-710017-01");
        toOnlyBody.Items.Select(item => item.LienNumber)
            .Should().NotContain(["26-710018-01", "26-710023-01"]);
    }

    [Fact]
    public async Task CreateLien_with_standalone_facility_contact_id_links_backing_facility()
    {
        var response = await _client.PostAsJsonAsync("/api/liens/liens", new
        {
            lienNumber = "LIEN-TEST-FACILITY-CONTACT",
            lienType = LienType.MedicalLien,
            caseId = SeedHelper.CaseId,
            facilityId = SeedHelper.MedicalFacilityContactId,
            originalAmount = 250m,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created,
            $"Body: {await response.Content.ReadAsStringAsync()}");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();

        var createdLien = db.Liens.Single(l => l.LienNumber == "LIEN-TEST-FACILITY-CONTACT");
        createdLien.FacilityId.Should().NotBeNull();
        createdLien.FacilityId.Should().NotBe(SeedHelper.MedicalFacilityContactId);

        var facilityContact = db.Contacts.Single(c => c.Id == SeedHelper.MedicalFacilityContactId);
        facilityContact.FacilityId.Should().Be(createdLien.FacilityId);

        db.Facilities.Single(f => f.Id == createdLien.FacilityId!.Value).Name.Should().Be("Sunrise Clinic");
    }

    [Fact]
    public async Task ReassignFacility_updates_legacy_facility_name_metadata()
    {
        Guid newFacilityContactId;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();

            var newFacilityContact = Contact.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                ContactType.MedicalFacility,
                "Valley",
                "Clinic",
                SeedHelper.UserId,
                organization: "Valley Clinic");
            newFacilityContactId = newFacilityContact.Id;
            db.Contacts.Add(newFacilityContact);

            var lien = db.Liens.Single(l => l.Id == SeedHelper.LienId);
            lien.AttachFacility(SeedHelper.FacilityId, SeedHelper.UserId);

            db.ServicingItems.Add(ServicingItem.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                "LMFI-TEST-001",
                "LegacyMedicalFacilityInfo",
                "Legacy medical facility information",
                "system",
                SeedHelper.UserId,
                caseId: SeedHelper.CaseId,
                lienId: SeedHelper.LienId,
                notes: $"facilityId={SeedHelper.FacilityId}; facilityName=Sunrise Clinic"));

            await db.SaveChangesAsync();
        }

        var response = await _client.PostAsJsonAsync("/api/liens/liens/reassign/facility", new
        {
            facility = newFacilityContactId,
            liensId = SeedHelper.LienId,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Body: {await response.Content.ReadAsStringAsync()}");

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<LiensDbContext>();

        var updatedLien = verifyDb.Liens.Single(l => l.Id == SeedHelper.LienId);
        var updatedFacilityContact = verifyDb.Contacts.Single(c => c.Id == newFacilityContactId);
        var facilityInfo = verifyDb.ServicingItems.Single(i =>
            i.LienId == SeedHelper.LienId &&
            i.TaskType == "LegacyMedicalFacilityInfo");

        updatedLien.FacilityId.Should().Be(updatedFacilityContact.FacilityId);
        updatedFacilityContact.FacilityId.Should().NotBeNull();
        facilityInfo.Notes.Should().Contain($"facilityId={newFacilityContactId}");
        facilityInfo.Notes.Should().Contain("facilityName=Valley Clinic");
    }

    private sealed class LienResponseBody
    {
        public Guid Id { get; init; }
        public string LienNumber { get; init; } = string.Empty;
    }

    private sealed class PaginatedLiensResponseBody
    {
        public List<LienListItemResponseBody> Items { get; init; } = [];
        public int TotalCount { get; init; }
    }

    private sealed class LienListItemResponseBody
    {
        public string LienNumber { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string StatusLabel { get; init; } = string.Empty;
        public string PurchaseDate { get; init; } = string.Empty;
        public string InitialServiceDate { get; init; } = string.Empty;
        public decimal? TotalPurchase { get; init; }
        public decimal? TotalBilling { get; init; }
        public string? Plaintiff { get; init; }
        public string? LawFirm { get; init; }
        public string? MedicalFacility { get; init; }
        public string? CaseManager { get; init; }
    }

    private sealed class CountingServicingItemService(int maxSearchCalls) : IServicingItemService
    {
        public int SearchCallCount { get; private set; }

        public Task<PaginatedResult<ServicingItemResponse>> SearchAsync(
            Guid tenantId,
            string? search,
            string? status,
            string? priority,
            string? assignedTo,
            Guid? caseId,
            Guid? lienId,
            int page,
            int pageSize,
            CancellationToken ct = default)
        {
            SearchCallCount++;
            if (SearchCallCount > maxSearchCalls)
            {
                throw new InvalidOperationException(
                    "The status filter enriched records outside the requested page.");
            }

            return Task.FromResult(new PaginatedResult<ServicingItemResponse>
            {
                Items = [],
                Page = page,
                PageSize = pageSize,
                TotalCount = 0,
            });
        }

        public Task<ServicingItemResponse?> GetByIdAsync(
            Guid tenantId,
            Guid id,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ServicingItemResponse> CreateAsync(
            Guid tenantId,
            Guid orgId,
            Guid actingUserId,
            CreateServicingItemRequest request,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ServicingItemResponse> UpdateAsync(
            Guid tenantId,
            Guid id,
            Guid actingUserId,
            UpdateServicingItemRequest request,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ServicingItemResponse> UpdateStatusAsync(
            Guid tenantId,
            Guid id,
            Guid actingUserId,
            string status,
            string? resolution = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task DeleteAsync(
            Guid tenantId,
            Guid id,
            Guid actingUserId,
            CancellationToken ct = default) => throw new NotSupportedException();
    }
}
