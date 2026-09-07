using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Liens.Api.Tests.Helpers;
using Liens.Application.Interfaces;
using Liens.Domain;
using Liens.Domain.Entities;
using Liens.Domain.Enums;
using Liens.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Liens.Api.Tests.Tests;

public sealed class SellingV2EndpointTests : IClassFixture<LiensApiFactory>, IAsyncLifetime
{
    private readonly LiensApiFactory _factory;
    private HttpClient _client = null!;

    public SellingV2EndpointTests(LiensApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        await SeedHelper.SeedAsync(scope.ServiceProvider);
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", JwtTokenHelper.CreateFullAccessToken(SeedHelper.TenantId, SeedHelper.UserId));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Create_lien_requires_a_seller_owned_case()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/liens/selling/liens")
        {
            Content = JsonContent.Create(new { sellerStatus = "Pending", source = "Single" }),
        };
        request.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString());
        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadAsStringAsync()).Should().Contain("caseId");
    }

    [Fact]
    public async Task Case_draft_finalization_creates_a_complete_case_that_can_be_attached_to_a_lien()
    {
        Guid accidentTypeId;
        using (var lookupScope = _factory.Services.CreateScope())
        {
            var lookupDb = lookupScope.ServiceProvider.GetRequiredService<LiensDbContext>();
            accidentTypeId = await lookupDb.LookupValues
                .Where(value => value.Category == LookupCategory.AccidentType && value.Code == "MVA")
                .Select(value => value.Id)
                .SingleAsync();
        }

        using var createDraft = new HttpRequestMessage(HttpMethod.Post, "/api/liens/selling/case-drafts")
        {
            Content = JsonContent.Create(new
            {
                accidentTypeId = accidentTypeId.ToString(),
                accidentState = "CA",
                dateOfLoss = "2026-07-19",
                caseTrackingNotes = "Plaintiff intake completed.",
            }),
        };
        var draftResponse = await _client.SendAsync(createDraft);
        draftResponse.StatusCode.Should().Be(HttpStatusCode.Created, await draftResponse.Content.ReadAsStringAsync());
        using var draftJson = JsonDocument.Parse(await draftResponse.Content.ReadAsStringAsync());
        var draftId = draftJson.RootElement.GetProperty("draftId").GetGuid();

        using var finalize = new HttpRequestMessage(HttpMethod.Post, $"/api/liens/selling/case-drafts/{draftId}/plaintiff")
        {
            Content = JsonContent.Create(new
            {
                firstName = "Pat",
                lastName = "Plaintiff",
                birthdate = "1985-02-12",
                email = "pat@example.test",
                phone = "555-100-2000",
                gender = "Nonbinary",
                address = "100 Main Street",
                city = "Los Angeles",
                state = "CA",
                zipcode = "90001",
            }),
        };
        var finalizedResponse = await _client.SendAsync(finalize);
        finalizedResponse.StatusCode.Should().Be(HttpStatusCode.Created, await finalizedResponse.Content.ReadAsStringAsync());
        using var finalizedJson = JsonDocument.Parse(await finalizedResponse.Content.ReadAsStringAsync());
        var caseId = finalizedJson.RootElement.GetProperty("caseId").GetGuid();

        using var createLien = new HttpRequestMessage(HttpMethod.Post, "/api/liens/selling/liens")
        {
            Content = JsonContent.Create(new { caseId, sellerStatus = "Pending", source = "Single" }),
        };
        createLien.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString());
        var lienResponse = await _client.SendAsync(createLien);
        lienResponse.StatusCode.Should().Be(HttpStatusCode.Created, await lienResponse.Content.ReadAsStringAsync());
        using var lienJson = JsonDocument.Parse(await lienResponse.Content.ReadAsStringAsync());
        var lienId = lienJson.RootElement.GetProperty("lienId").GetGuid();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
        var persisted = await db.Cases.SingleAsync(item => item.Id == caseId);
        persisted.ClientFirstName.Should().Be("Pat");
        persisted.ClientEmail.Should().Be("pat@example.test");
        persisted.Status.Should().Be(CaseStatus.PreDemand);
        persisted.IncidentState.Should().Be("CA");
        persisted.Notes.Should().Contain("gender=Nonbinary");
        (await db.Liens.SingleAsync(item => item.Id == lienId)).CaseId.Should().Be(caseId);
    }

    [Fact]
    public async Task Case_draft_returns_field_validation_errors_for_invalid_case_information()
    {
        var invalidRequests = new (object Payload, string Field)[]
        {
            (new { accidentTypeId = new string('A', 101) }, "accidentTypeId"),
            (new { accidentTypeId = Guid.CreateVersion7() }, "accidentTypeId"),
            (new { accidentState = new string('A', 101) }, "accidentState"),
            (new { dateOfLoss = "9999-12-31" }, "dateOfLoss"),
            (new { handlingLawFirmId = Guid.CreateVersion7() }, "handlingLawFirmId"),
            (new { caseManagerId = Guid.CreateVersion7() }, "caseManagerId"),
            (new { caseTrackingNotes = new string('N', 3_501) }, "caseTrackingNotes"),
        };

        foreach (var (payload, field) in invalidRequests)
        {
            var response = await _client.PostAsJsonAsync("/api/liens/selling/case-drafts", payload);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var error = json.RootElement.GetProperty("error");
            error.GetProperty("code").GetString().Should().Be("validation_error");
            error.GetProperty("errors").TryGetProperty(field, out _).Should().BeTrue();
        }
    }

    [Fact]
    public async Task Case_draft_can_be_updated_before_plaintiff_finalization()
    {
        using var createDraft = new HttpRequestMessage(HttpMethod.Post, "/api/liens/selling/case-drafts")
        {
            Content = JsonContent.Create(new
            {
                accidentTypeId = "MVA",
                accidentState = "CA",
                dateOfLoss = "2026-07-19",
                caseTrackingNotes = "Original intake notes.",
            }),
        };
        var createResponse = await _client.SendAsync(createDraft);
        createResponse.EnsureSuccessStatusCode();
        using var createJson = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var draftId = createJson.RootElement.GetProperty("draftId").GetGuid();

        var updateResponse = await _client.PutAsJsonAsync($"/api/liens/selling/case-drafts/{draftId}", new
        {
            accidentTypeId = "MVA",
            accidentState = "NV",
            dateOfLoss = "2026-07-20",
            caseTrackingNotes = "Updated intake notes.",
        });

        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK, await updateResponse.Content.ReadAsStringAsync());
        using var updateJson = JsonDocument.Parse(await updateResponse.Content.ReadAsStringAsync());
        var result = updateJson.RootElement;
        result.GetProperty("draftId").GetGuid().Should().Be(draftId);
        result.GetProperty("accidentState").GetString().Should().Be("NV");
        result.GetProperty("dateOfLoss").GetString().Should().Be("2026-07-20");
        result.GetProperty("caseTrackingNotes").GetString().Should().Be("Updated intake notes.");
    }

    [Fact]
    public async Task Saved_case_draft_can_be_retrieved_only_by_its_seller_organization()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/liens/selling/case-drafts", new
        {
            accidentTypeId = "MVA",
            accidentState = "CA",
            dateOfLoss = "2026-07-19",
            caseTrackingNotes = "Resume this saved case intake.",
        });
        createResponse.EnsureSuccessStatusCode();
        using var createJson = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var draftId = createJson.RootElement.GetProperty("draftId").GetGuid();

        var response = await _client.GetAsync($"/api/liens/selling/case-drafts/{draftId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var draft = json.RootElement;
        draft.GetProperty("draftId").GetGuid().Should().Be(draftId);
        draft.GetProperty("caseStatus").GetString().Should().Be(CaseStatus.PreDemand);
        draft.GetProperty("accidentTypeId").GetString().Should().Be("MVA");
        draft.GetProperty("accidentState").GetString().Should().Be("CA");
        draft.GetProperty("dateOfLoss").GetString().Should().Be("2026-07-19");
        draft.GetProperty("caseTrackingNotes").GetString().Should().Be("Resume this saved case intake.");
        draft.GetProperty("createdAtUtc").GetDateTime().Should().NotBe(default);
        draft.GetProperty("updatedAtUtc").GetDateTime().Should().NotBe(default);

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", JwtTokenHelper.CreateFullAccessToken(SeedHelper.TenantId, SeedHelper.UserId, Guid.CreateVersion7()));
        var foreignOrganizationResponse = await _client.GetAsync($"/api/liens/selling/case-drafts/{draftId}");
        foreignOrganizationResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Retrieved_finalized_case_draft_includes_its_case_id()
    {
        var draftResponse = await _client.PostAsJsonAsync("/api/liens/selling/case-drafts", new { });
        draftResponse.EnsureSuccessStatusCode();
        using var draftJson = JsonDocument.Parse(await draftResponse.Content.ReadAsStringAsync());
        var draftId = draftJson.RootElement.GetProperty("draftId").GetGuid();

        var finalizeResponse = await _client.PostAsJsonAsync(
            $"/api/liens/selling/case-drafts/{draftId}/plaintiff",
            new { firstName = "Finalized", lastName = "Plaintiff" });
        finalizeResponse.EnsureSuccessStatusCode();
        using var finalizeJson = JsonDocument.Parse(await finalizeResponse.Content.ReadAsStringAsync());
        var caseId = finalizeJson.RootElement.GetProperty("caseId").GetGuid();

        var response = await _client.GetAsync($"/api/liens/selling/case-drafts/{draftId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("caseId").GetGuid().Should().Be(caseId);
        json.RootElement.GetProperty("finalizedAtUtc").GetDateTime().Should().NotBe(default);
    }

    [Fact]
    public async Task Finalized_selling_case_can_be_retrieved_and_updated_in_two_steps()
    {
        using var createDraft = new HttpRequestMessage(HttpMethod.Post, "/api/liens/selling/case-drafts")
        {
            Content = JsonContent.Create(new
            {
                accidentTypeId = "MVA",
                accidentState = "CA",
                dateOfLoss = "2026-07-19",
                caseTrackingNotes = "Plaintiff intake completed.",
            }),
        };
        var draftResponse = await _client.SendAsync(createDraft);
        draftResponse.EnsureSuccessStatusCode();
        using var draftJson = JsonDocument.Parse(await draftResponse.Content.ReadAsStringAsync());
        var draftId = draftJson.RootElement.GetProperty("draftId").GetGuid();

        using var finalize = new HttpRequestMessage(HttpMethod.Post, $"/api/liens/selling/case-drafts/{draftId}/plaintiff")
        {
            Content = JsonContent.Create(new
            {
                firstName = "Pat",
                lastName = "Plaintiff",
                birthdate = "1985-02-12",
                email = "pat@example.test",
                phone = "555-100-2000",
                gender = "Nonbinary",
                address = "100 Main Street",
                city = "Los Angeles",
                state = "CA",
                zipcode = "90001",
            }),
        };
        var finalizedResponse = await _client.SendAsync(finalize);
        finalizedResponse.EnsureSuccessStatusCode();
        using var finalizedJson = JsonDocument.Parse(await finalizedResponse.Content.ReadAsStringAsync());
        var caseId = finalizedJson.RootElement.GetProperty("caseId").GetGuid();

        var caseUpdateResponse = await _client.PutAsJsonAsync($"/api/liens/selling/cases/{caseId}", new
        {
            accidentTypeId = "MVA",
            accidentState = "NV",
            dateOfLoss = "2026-07-20",
            caseTrackingNotes = "Case information updated.",
        });
        caseUpdateResponse.StatusCode.Should().Be(HttpStatusCode.OK, await caseUpdateResponse.Content.ReadAsStringAsync());

        var plaintiffUpdateResponse = await _client.PutAsJsonAsync($"/api/liens/selling/cases/{caseId}/plaintiff", new
        {
            firstName = "Updated",
            lastName = "Plaintiff",
            birthdate = "1986-03-13",
            email = "updated@example.test",
            phone = "555-200-3000",
            gender = "Female",
            address = "200 Main Street",
            city = "Las Vegas",
            state = "NV",
            zipcode = "89101",
        });
        plaintiffUpdateResponse.StatusCode.Should().Be(HttpStatusCode.OK, await plaintiffUpdateResponse.Content.ReadAsStringAsync());

        var response = await _client.GetAsync($"/api/liens/selling/cases/{caseId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var result = json.RootElement;
        result.GetProperty("draftId").GetGuid().Should().Be(draftId);
        result.GetProperty("caseId").GetGuid().Should().Be(caseId);
        result.GetProperty("caseStatus").GetString().Should().Be(CaseStatus.PreDemand);
        result.GetProperty("accidentTypeId").GetString().Should().Be("MVA");
        result.GetProperty("accidentState").GetString().Should().Be("NV");
        result.GetProperty("dateOfLoss").GetString().Should().Be("2026-07-20");
        result.GetProperty("caseTrackingNotes").GetString().Should().Be("Case information updated.");
        result.GetProperty("firstName").GetString().Should().Be("Updated");
        result.GetProperty("lastName").GetString().Should().Be("Plaintiff");
        result.GetProperty("birthdate").GetString().Should().Be("1986-03-13");
        result.GetProperty("email").GetString().Should().Be("updated@example.test");
        result.GetProperty("phone").GetString().Should().Be("555-200-3000");
        result.GetProperty("gender").GetString().Should().Be("Female");
        result.GetProperty("address").GetString().Should().Be("200 Main Street");
        result.GetProperty("city").GetString().Should().Be("Las Vegas");
        result.GetProperty("state").GetString().Should().Be("NV");
        result.GetProperty("zipcode").GetString().Should().Be("89101");
    }

    [Fact]
    public async Task Selling_cases_returns_all_finalized_cases_for_the_seller_organization()
    {
        Guid accidentTypeId;
        Company lawFirm;
        CompanyContactPerson caseManager;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            accidentTypeId = await db.LookupValues
                .Where(value => value.Category == LookupCategory.AccidentType && value.Code == "MVA")
                .Select(value => value.Id)
                .SingleAsync();
            var caseManagerRoleId = CompanyDirectoryReferenceData.ContactPersonTypes
                .Single(role =>
                    role.CompanyTypeId == CompanyDirectoryReferenceData.LawFirmId &&
                    role.Code == "CaseManager")
                .Id;
            lawFirm = Company.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                CompanyDirectoryReferenceData.LawFirmId,
                "Selling Case Law LLP",
                SeedHelper.UserId);
            caseManager = CompanyContactPerson.Create(
                SeedHelper.TenantId,
                lawFirm.Id,
                caseManagerRoleId,
                "Casey",
                "Manager",
                SeedHelper.UserId);
            db.AddRange(lawFirm, caseManager);
            await db.SaveChangesAsync();
        }

        var draftResponse = await _client.PostAsJsonAsync("/api/liens/selling/case-drafts", new
        {
            accidentTypeId,
            handlingLawFirmId = lawFirm.Id,
            caseManagerId = caseManager.Id,
        });
        draftResponse.EnsureSuccessStatusCode();
        using var draftJson = JsonDocument.Parse(await draftResponse.Content.ReadAsStringAsync());
        var draftId = draftJson.RootElement.GetProperty("draftId").GetGuid();
        var finalizeResponse = await _client.PostAsJsonAsync(
            $"/api/liens/selling/case-drafts/{draftId}/plaintiff",
            new { firstName = "Alice", lastName = "First" });
        finalizeResponse.EnsureSuccessStatusCode();
        using var finalizeJson = JsonDocument.Parse(await finalizeResponse.Content.ReadAsStringAsync());
        var firstCaseId = finalizeJson.RootElement.GetProperty("caseId").GetGuid();
        var secondCaseId = await FinalizeSellingCaseAsync("Bob", "Second");

        var response = await _client.GetAsync("/api/liens/selling/cases");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var result = json.RootElement;
        result.GetProperty("totalCount").GetInt32().Should().Be(2);
        var items = result.GetProperty("items").EnumerateArray().ToList();
        items.Select(item => item.GetProperty("caseId").GetGuid())
            .Should().BeEquivalentTo([firstCaseId, secondCaseId]);
        items.Select(item => item.GetProperty("firstName").GetString())
            .Should().BeEquivalentTo(["Alice", "Bob"]);
        items.Should().OnlyContain(item =>
            item.GetProperty("caseStatus").GetString() == CaseStatus.PreDemand &&
            item.GetProperty("draftId").GetGuid() != Guid.Empty);
        var caseWithLookups = items.Single(item => item.GetProperty("caseId").GetGuid() == firstCaseId);
        AssertSellingCaseLookupNames(caseWithLookups, accidentTypeId, lawFirm.Id, caseManager.Id);

        var detailResponse = await _client.GetAsync($"/api/liens/selling/cases/{firstCaseId}");
        detailResponse.StatusCode.Should().Be(HttpStatusCode.OK, await detailResponse.Content.ReadAsStringAsync());
        using var detailJson = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync());
        AssertSellingCaseLookupNames(detailJson.RootElement, accidentTypeId, lawFirm.Id, caseManager.Id);
    }

    [Fact]
    public async Task Concurrent_case_draft_finalization_creates_only_one_case()
    {
        using var createDraft = new HttpRequestMessage(HttpMethod.Post, "/api/liens/selling/case-drafts")
        {
            Content = JsonContent.Create(new { }),
        };
        var draftResponse = await _client.SendAsync(createDraft);
        draftResponse.EnsureSuccessStatusCode();
        using var draftJson = JsonDocument.Parse(await draftResponse.Content.ReadAsStringAsync());
        var draftId = draftJson.RootElement.GetProperty("draftId").GetGuid();

        Task<HttpResponseMessage> FinalizeAsync() => _client.SendAsync(new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/liens/selling/case-drafts/{draftId}/plaintiff")
        {
            Content = JsonContent.Create(new
            {
                firstName = "Concurrent",
                lastName = "Plaintiff",
                birthdate = "1985-02-12",
            }),
        });

        var responses = await Task.WhenAll(FinalizeAsync(), FinalizeAsync());
        try
        {
            responses.Should().OnlyContain(response =>
                response.StatusCode == HttpStatusCode.Created ||
                response.StatusCode == HttpStatusCode.OK ||
                response.StatusCode == HttpStatusCode.Conflict);
            var caseIds = new List<Guid>();
            foreach (var response in responses.Where(response =>
                         response.StatusCode == HttpStatusCode.Created || response.StatusCode == HttpStatusCode.OK))
            {
                using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                caseIds.Add(payload.RootElement.GetProperty("caseId").GetGuid());
            }
            caseIds.Distinct().Should().ContainSingle();

            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            (await db.Cases.CountAsync(item =>
                item.ClientFirstName == "Concurrent" && item.ClientLastName == "Plaintiff"))
                .Should().Be(1);
        }
        finally
        {
            foreach (var response in responses)
                response.Dispose();
        }
    }

    [Fact]
    public async Task Activity_history_includes_the_lien_status_at_the_time_of_each_update()
    {
        var lienId = await CreateSellingLienAsync();
        var updateResponse = await _client.PutAsJsonAsync(
            $"/api/liens/selling/liens/{lienId}/lien-information",
            new
            {
                sellerStatus = SellingLienStatus.Internal,
                listingVisibility = SellingListingVisibility.Private,
                notes = "Status history test",
            });
        updateResponse.EnsureSuccessStatusCode();

        var activityResponse = await _client.GetAsync($"/api/liens/selling/liens/{lienId}/activity");
        activityResponse.EnsureSuccessStatusCode();
        using var activity = JsonDocument.Parse(await activityResponse.Content.ReadAsStringAsync());
        var descriptions = activity.RootElement.GetProperty("items")
            .EnumerateArray()
            .Select(item => item.GetProperty("description").GetString())
            .ToList();

        descriptions.Should().Contain(description =>
            description.StartsWith(
                "Lien Created. Lien Status: Pending. Selling lien created with status Pending.",
                StringComparison.Ordinal) &&
            description.Contains("Changes:", StringComparison.Ordinal));
        descriptions.Should().Contain(description =>
            description != null &&
            description.StartsWith("Lien Status: Internal. Selling lien information updated. Changes:", StringComparison.Ordinal) &&
            description.Contains("Seller Status: Pending → Internal", StringComparison.Ordinal) &&
            description.Contains("Note: Single → Status history test", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Lien_information_preserves_omitted_optional_fields_and_clears_explicit_nulls()
    {
        var lienId = await CreateSellingLienAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var lien = await db.Liens.SingleAsync(item => item.Id == lienId);
            lien.SetPurchaseDate(new DateOnly(2026, 6, 20), SeedHelper.UserId);
            await db.SaveChangesAsync();
        }

        var initialSave = await _client.PutAsJsonAsync(
            $"/api/liens/selling/liens/{lienId}/lien-information",
            new
            {
                sellerStatus = SellingLienStatus.Pending,
                listingVisibility = SellingListingVisibility.Private,
                initialServiceDate = "2026-07-01",
                endServiceDate = "2026-07-15",
                receivableDueDate = "2026-08-01",
                notes = "Preserve these values",
            });
        initialSave.EnsureSuccessStatusCode();

        var partialUpdate = await _client.PutAsJsonAsync(
            $"/api/liens/selling/liens/{lienId}/lien-information",
            new
            {
                sellerStatus = SellingLienStatus.Internal,
                listingVisibility = SellingListingVisibility.Private,
            });
        partialUpdate.StatusCode.Should().Be(HttpStatusCode.OK, await partialUpdate.Content.ReadAsStringAsync());

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var persisted = await db.Liens.SingleAsync(item => item.Id == lienId);
            persisted.InitialServiceDate.Should().Be(new DateOnly(2026, 7, 1));
            persisted.EndServiceDate.Should().Be(new DateOnly(2026, 7, 15));
            persisted.ReceivableDueDate.Should().Be(new DateOnly(2026, 8, 1));
            persisted.PurchaseDate.Should().Be(new DateOnly(2026, 6, 20));
            persisted.Notes.Should().Be("Preserve these values");
        }

        var clearUpdate = await _client.PutAsJsonAsync(
            $"/api/liens/selling/liens/{lienId}/lien-information",
            new
            {
                sellerStatus = SellingLienStatus.Internal,
                listingVisibility = SellingListingVisibility.Private,
                initialServiceDate = (string?)null,
                endServiceDate = (string?)null,
                receivableDueDate = (string?)null,
                notes = (string?)null,
            });
        clearUpdate.StatusCode.Should().Be(HttpStatusCode.OK, await clearUpdate.Content.ReadAsStringAsync());

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var persisted = await db.Liens.SingleAsync(item => item.Id == lienId);
            persisted.InitialServiceDate.Should().BeNull();
            persisted.EndServiceDate.Should().BeNull();
            persisted.ReceivableDueDate.Should().BeNull();
            persisted.PurchaseDate.Should().Be(new DateOnly(2026, 6, 20));
            persisted.Notes.Should().BeNull();
        }
    }

    [Fact]
    public async Task Save_medical_pricing_persists_multiple_rows_with_unique_task_numbers()
    {
        var lienId = await CreateSellingLienAsync();

        var response = await _client.PutAsJsonAsync($"/api/liens/selling/liens/{lienId}/medical-pricing", new
        {
            askAmount = 5000m,
            billingAmount = 3000m,
            rows = new[]
            {
                new { medicalCode = "45385", description = "Colonoscopy", billingAmount = 3000m, medicareCost = 675m, targetSaleAmount = 1000m },
                new { medicalCode = "96372", description = "Therapeutic injection", billingAmount = 0m, medicareCost = 476m, targetSaleAmount = 4000m },
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
        var pricingRows = db.ServicingItems
            .Where(item => item.LienId == lienId && item.TaskType == "SellingMedicalPricing")
            .ToList();

        pricingRows.Should().HaveCount(2);
        pricingRows.Select(item => item.TaskNumber).Should().OnlyHaveUniqueItems();
        pricingRows.Select(item => item.TaskNumber).Should().OnlyContain(taskNumber => taskNumber.Length == 36);
        pricingRows.Select(item => item.Description).Should().BeEquivalentTo("45385", "96372");
    }

    [Fact]
    public async Task Pending_lien_confirm_sale_sets_offered_and_submitted_not_sold()
    {
        var (_, buyerContactId) = await SeedConfirmSaleContactsAsync(
            "buyer.prepared-confirm@capital.test",
            "seller.prepared-confirm@smithlaw.test");
        var lienId = await CreateSellingLienAsync();

        var lienInfo = await _client.PutAsJsonAsync($"/api/liens/selling/liens/{lienId}/lien-information", new
        {
            sellerStatus = "Pending",
            initialServiceDate = "2026-07-19",
            listingVisibility = "Private",
            notes = "V2 test",
        });
        lienInfo.EnsureSuccessStatusCode();

        using (var setupScope = _factory.Services.CreateScope())
        {
            var setupDb = setupScope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var lien = await setupDb.Liens.FindAsync(lienId);
            lien!.AttachCase(SeedHelper.CaseId, SeedHelper.UserId);
            await setupDb.SaveChangesAsync();
        }

        var pricing = await _client.PutAsJsonAsync($"/api/liens/selling/liens/{lienId}/medical-pricing", new
        {
            askAmount = 1250m,
            billingAmount = 1800m,
            rows = new[] { new { medicalCode = "99213", billingAmount = 600m, medicareCost = 180m, targetSaleAmount = 350m } },
        });
        pricing.EnsureSuccessStatusCode();

        var documents = await _client.PutAsJsonAsync($"/api/liens/selling/liens/{lienId}/documents", new
        {
            documents = new[] { new { documentId = Guid.CreateVersion7(), documentType = "MedicalBill", displayName = "bill.pdf" } },
        });
        documents.EnsureSuccessStatusCode();

        var prepare = new HttpRequestMessage(HttpMethod.Post, $"/api/liens/selling/liens/{lienId}/prepare-sale")
        {
            Content = JsonContent.Create(new
            {
                buyerContactId,
                askAmount = 1250m,
                listingVisibility = "Private",
            }),
        };
        prepare.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString());
        (await _client.SendAsync(prepare)).EnsureSuccessStatusCode();

        using (var preparedScope = _factory.Services.CreateScope())
        {
            var preparedDb = preparedScope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var preparedLien = await preparedDb.Liens.FindAsync(lienId);
            preparedLien!.SellerStatus.Should().Be(SellingLienStatus.Pending);
        }

        var confirm = new HttpRequestMessage(HttpMethod.Post, $"/api/liens/selling/liens/{lienId}/confirm-sale")
        {
            Content = JsonContent.Create(new { confirmationAccepted = true, sendBuyerNotification = false }),
        };
        confirm.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString());
        var confirmResponse = await _client.SendAsync(confirm);
        confirmResponse.StatusCode.Should().Be(HttpStatusCode.OK, await confirmResponse.Content.ReadAsStringAsync());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
        var persisted = await db.Liens.FindAsync(lienId);
        var buyerContact = await db.Contacts.FindAsync(buyerContactId);
        persisted!.Status.Should().Be(LienStatus.Offered);
        persisted.SellerStatus.Should().Be(SellingLienStatus.SubmittedForSale);
        persisted.FundingCompanyId.Should().Be(buyerContact!.OrgId);
        persisted.FundingCompanyContactId.Should().Be(buyerContactId);
        persisted.OfferPrice.Should().Be(1250m);
        persisted.SoldAtUtc.Should().BeNull();
        db.LienStatusHistories.Should().Contain(item =>
            item.LienId == lienId &&
            item.Description == "Lien Status: SubmittedForSale. Lien submitted for sale.");
    }

    [Fact]
    public async Task Prepare_sale_without_buyer_contact_keeps_pending_when_confirmation_fails()
    {
        var lienId = await CreateSellingLienAsync();

        (await _client.PutAsJsonAsync($"/api/liens/selling/liens/{lienId}/lien-information", new
        {
            sellerStatus = "Pending", initialServiceDate = "2026-07-19", listingVisibility = "Private",
        })).EnsureSuccessStatusCode();

        using (var setupScope = _factory.Services.CreateScope())
        {
            var setupDb = setupScope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var lien = await setupDb.Liens.FindAsync(lienId);
            lien!.AttachCase(SeedHelper.CaseId, SeedHelper.UserId);
            await setupDb.SaveChangesAsync();
        }

        (await _client.PutAsJsonAsync($"/api/liens/selling/liens/{lienId}/medical-pricing", new
        {
            askAmount = 1250m, billingAmount = 1800m,
            rows = new[] { new { medicalCode = "99213", billingAmount = 600m, medicareCost = 180m, targetSaleAmount = 350m } },
        })).EnsureSuccessStatusCode();
        (await _client.PutAsJsonAsync($"/api/liens/selling/liens/{lienId}/documents", new
        {
            documents = new[] { new { documentId = Guid.CreateVersion7(), documentType = "MedicalBill", displayName = "bill.pdf" } },
        })).EnsureSuccessStatusCode();

        using var prepare = new HttpRequestMessage(HttpMethod.Post, $"/api/liens/selling/liens/{lienId}/prepare-sale")
        {
            Content = JsonContent.Create(new { buyerContactId = Guid.Empty, askAmount = 1250m, listingVisibility = "Private" }),
        };
        prepare.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString());
        (await _client.SendAsync(prepare)).EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
        var persisted = await db.Liens.FindAsync(lienId);
        persisted!.SellerStatus.Should().Be(SellingLienStatus.Pending);
        persisted.FundingCompanyId.Should().BeNull();
        persisted.FundingCompanyContactId.Should().BeNull();

        using var confirm = new HttpRequestMessage(HttpMethod.Post, $"/api/liens/selling/liens/{lienId}/confirm-sale")
        {
            Content = JsonContent.Create(new { confirmationAccepted = true, sendBuyerNotification = true }),
        };
        confirm.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString());

        var confirmResponse = await _client.SendAsync(confirm);

        confirmResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest, await confirmResponse.Content.ReadAsStringAsync());
        await db.Entry(persisted).ReloadAsync();
        persisted.SellerStatus.Should().Be(SellingLienStatus.Pending);
        persisted.Status.Should().Be(LienStatus.Draft);
    }

    [Fact]
    public async Task Seller_lien_detail_does_not_cross_organization_boundary()
    {
        var lienId = await CreateSellingLienAsync();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", JwtTokenHelper.CreateFullAccessToken(SeedHelper.TenantId, SeedHelper.UserId, Guid.CreateVersion7()));

        var response = await _client.GetAsync($"/api/liens/selling/liens/{lienId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Seller_lien_detail_includes_funding_contact_and_case_assignments()
    {
        var fundingContactId = Guid.CreateVersion7();
        var caseManagerId = Guid.CreateVersion7();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var fundingContact = Contact.Create(
                SeedHelper.TenantId, SeedHelper.OrgId, ContactType.Lead,
                "Fiona", "Funder", SeedHelper.UserId, email: "fiona@capital-fund.test");
            SetId(fundingContact, fundingContactId);
            var caseManager = Contact.Create(
                SeedHelper.TenantId, SeedHelper.OrgId, ContactType.CaseManager,
                "Casey", "Manager", SeedHelper.UserId, lawFirmId: SeedHelper.LawFirmId);
            SetId(caseManager, caseManagerId);
            db.Contacts.AddRange(fundingContact, caseManager);
            await db.SaveChangesAsync();
        }

        var lienId = await CreateSellingLienAsync();
        var caseInformation = await _client.PutAsJsonAsync($"/api/liens/selling/liens/{lienId}/case-information", new
        {
            fundingCompanyId = SeedHelper.FundingCompanyId,
            fundingCompanyContactId = fundingContactId,
            facilityId = SeedHelper.FacilityId,
            handlingLawFirmId = SeedHelper.LawFirmId,
            caseManagerId,
            caseId = SeedHelper.CaseId,
            createCaseIfMissing = false,
        });
        caseInformation.EnsureSuccessStatusCode();

        var response = await _client.GetAsync($"/api/liens/selling/liens/{lienId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var facility = payload.RootElement.GetProperty("facility");
        facility.GetProperty("id").GetGuid().Should().Be(SeedHelper.FacilityId);
        facility.GetProperty("name").GetString().Should().Be("Sunrise Clinic");
        var fundingCompany = payload.RootElement.GetProperty("fundingCompany");
        fundingCompany.GetProperty("contactPerson").GetString().Should().Be("Fiona Funder");
        fundingCompany.GetProperty("emailAddress").GetString().Should().Be("fiona@capital-fund.test");
        var caseInfo = payload.RootElement.GetProperty("caseInformation");
        caseInfo.GetProperty("caseManagerId").GetGuid().Should().Be(caseManagerId);
        caseInfo.GetProperty("caseManagerName").GetString().Should().Be("Casey Manager");
        caseInfo.GetProperty("lawFirmId").GetGuid().Should().Be(SeedHelper.LawFirmId);
        caseInfo.GetProperty("lawFirm").GetString().Should().Be("Smith & Associates LLP");
    }

    [Fact]
    public async Task Case_information_accepts_facility_without_funding_company_references()
    {
        var lienId = await CreateSellingLienAsync();

        var response = await _client.PutAsJsonAsync(
            $"/api/liens/selling/liens/{lienId}/case-information",
            new
            {
                facilityId = SeedHelper.FacilityId,
                handlingLawFirmId = SeedHelper.LawFirmId,
                caseId = SeedHelper.CaseId,
                createCaseIfMissing = false,
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var savedPayload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        savedPayload.RootElement.GetProperty("facilityId").GetGuid().Should().Be(SeedHelper.FacilityId);
        savedPayload.RootElement.GetProperty("fundingCompanyId").ValueKind.Should().Be(JsonValueKind.Null);
        savedPayload.RootElement.GetProperty("fundingCompanyContactId").ValueKind.Should().Be(JsonValueKind.Null);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
        var persisted = await db.Liens.SingleAsync(item => item.Id == lienId);
        persisted.FacilityId.Should().Be(SeedHelper.FacilityId);
        persisted.FundingCompanyId.Should().BeNull();
        persisted.FundingCompanyContactId.Should().BeNull();
        persisted.FundingCompanyCompanyId.Should().BeNull();
        persisted.FundingCompanyContactPersonId.Should().BeNull();
    }

    [Fact]
    public async Task Case_information_accepts_a_seller_owned_company_directory_medical_facility()
    {
        Company facility;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            facility = Company.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                CompanyDirectoryReferenceData.MedicalFacilityId,
                "Directory Facility",
                SeedHelper.UserId);
            db.Companies.Add(facility);
            await db.SaveChangesAsync();
        }

        var lienId = await CreateSellingLienAsync();
        var response = await _client.PutAsJsonAsync(
            $"/api/liens/selling/liens/{lienId}/case-information",
            new { facilityId = facility.Id });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var savedPayload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        savedPayload.RootElement.GetProperty("facilityId").GetGuid().Should().Be(facility.Id);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var persisted = await db.Liens.SingleAsync(item => item.Id == lienId);
            persisted.MedicalFacilityCompanyId.Should().Be(facility.Id);
            persisted.FacilityId.Should().BeNull();
        }

        var detailResponse = await _client.GetAsync($"/api/liens/selling/liens/{lienId}");
        detailResponse.StatusCode.Should().Be(HttpStatusCode.OK, await detailResponse.Content.ReadAsStringAsync());
        using var detailPayload = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync());
        var savedFacility = detailPayload.RootElement.GetProperty("facility");
        savedFacility.GetProperty("id").GetGuid().Should().Be(facility.Id);
        savedFacility.GetProperty("name").GetString().Should().Be("Directory Facility");
    }

    [Fact]
    public async Task Case_information_creates_case_without_funding_company_references()
    {
        var lienId = await CreateSellingLienAsync();

        var response = await _client.PutAsJsonAsync(
            $"/api/liens/selling/liens/{lienId}/case-information",
            new
            {
                fundingCompanyId = (Guid?)null,
                fundingCompanyContactId = (Guid?)null,
                handlingLawFirmId = SeedHelper.LawFirmId,
                createCaseIfMissing = true,
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var savedPayload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var caseId = savedPayload.RootElement.GetProperty("caseId").GetGuid();
        savedPayload.RootElement.GetProperty("fundingCompanyId").ValueKind.Should().Be(JsonValueKind.Null);
        savedPayload.RootElement.GetProperty("fundingCompanyContactId").ValueKind.Should().Be(JsonValueKind.Null);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
        var persisted = await db.Liens.SingleAsync(item => item.Id == lienId);
        persisted.CaseId.Should().Be(caseId);
        persisted.FundingCompanyId.Should().BeNull();
        persisted.FundingCompanyCompanyId.Should().BeNull();
    }

    [Fact]
    public async Task Case_information_accepts_and_reads_back_company_directory_references()
    {
        Company fundingCompany;
        CompanyContactPerson fundingContact;
        Company medicalProvider;
        Company lawFirm;
        CompanyContactPerson caseManager;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var fundingRoleId = CompanyDirectoryReferenceData.ContactPersonTypes
                .First(role => role.CompanyTypeId == CompanyDirectoryReferenceData.FundingCompanyId)
                .Id;
            var caseManagerRoleId = CompanyDirectoryReferenceData.ContactPersonTypes
                .Single(role => role.CompanyTypeId == CompanyDirectoryReferenceData.LawFirmId && role.Code == "CaseManager")
                .Id;

            fundingCompany = Company.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                CompanyDirectoryReferenceData.FundingCompanyId,
                "Directory Capital LLC",
                SeedHelper.UserId);
            fundingContact = CompanyContactPerson.Create(
                SeedHelper.TenantId,
                fundingCompany.Id,
                fundingRoleId,
                "Diana",
                "Funder",
                SeedHelper.UserId,
                email: "diana@directory-capital.test");
            medicalProvider = Company.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                CompanyDirectoryReferenceData.MedicalProviderId,
                "Directory Medical Group",
                SeedHelper.UserId);
            lawFirm = Company.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                CompanyDirectoryReferenceData.LawFirmId,
                "Directory Law LLP",
                SeedHelper.UserId);
            caseManager = CompanyContactPerson.Create(
                SeedHelper.TenantId,
                lawFirm.Id,
                caseManagerRoleId,
                "Cameron",
                "Manager",
                SeedHelper.UserId,
                email: "cameron@directory-law.test");
            db.AddRange(fundingCompany, fundingContact, medicalProvider, lawFirm, caseManager);
            await db.SaveChangesAsync();
        }

        var lienId = await CreateSellingLienAsync();
        var response = await _client.PutAsJsonAsync(
            $"/api/liens/selling/liens/{lienId}/case-information",
            new
            {
                fundingCompanyId = fundingCompany.Id,
                fundingCompanyContactId = fundingContact.Id,
                medicalProviderId = medicalProvider.Id,
                handlingLawFirmId = lawFirm.Id,
                caseManagerId = caseManager.Id,
                createCaseIfMissing = true,
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var savedPayload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        savedPayload.RootElement.GetProperty("fundingCompanyId").GetGuid().Should().Be(fundingCompany.Id);
        savedPayload.RootElement.GetProperty("fundingCompanyContactId").GetGuid().Should().Be(fundingContact.Id);
        savedPayload.RootElement.GetProperty("medicalProviderId").GetGuid().Should().Be(medicalProvider.Id);
        savedPayload.RootElement.GetProperty("handlingLawFirmId").GetGuid().Should().Be(lawFirm.Id);
        savedPayload.RootElement.GetProperty("caseManagerId").GetGuid().Should().Be(caseManager.Id);
        var caseId = savedPayload.RootElement.GetProperty("caseId").GetGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var persistedLien = await db.Liens.SingleAsync(item => item.Id == lienId);
            persistedLien.FundingCompanyCompanyId.Should().Be(fundingCompany.Id);
            persistedLien.FundingCompanyContactPersonId.Should().Be(fundingContact.Id);
            persistedLien.MedicalProviderCompanyId.Should().Be(medicalProvider.Id);
            persistedLien.FundingCompanyId.Should().BeNull();
            persistedLien.FundingCompanyContactId.Should().BeNull();

            var persistedCase = await db.Cases.SingleAsync(item => item.Id == caseId);
            persistedCase.HandlingLawFirmCompanyId.Should().Be(lawFirm.Id);
            persistedCase.CaseManagerContactPersonId.Should().Be(caseManager.Id);
        }

        var detailResponse = await _client.GetAsync($"/api/liens/selling/liens/{lienId}");
        detailResponse.StatusCode.Should().Be(HttpStatusCode.OK, await detailResponse.Content.ReadAsStringAsync());
        using var detailPayload = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync());
        var companyDetail = detailPayload.RootElement.GetProperty("fundingCompany");
        companyDetail.GetProperty("id").GetGuid().Should().Be(fundingCompany.Id);
        companyDetail.GetProperty("name").GetString().Should().Be("Directory Capital LLC");
        companyDetail.GetProperty("contactPerson").GetString().Should().Be("Diana Funder");
        companyDetail.GetProperty("emailAddress").GetString().Should().Be("diana@directory-capital.test");
        var medicalProviderDetail = detailPayload.RootElement.GetProperty("medicalProvider");
        medicalProviderDetail.GetProperty("id").GetGuid().Should().Be(medicalProvider.Id);
        medicalProviderDetail.GetProperty("name").GetString().Should().Be("Directory Medical Group");
        var caseDetail = detailPayload.RootElement.GetProperty("caseInformation");
        caseDetail.GetProperty("lawFirmId").GetGuid().Should().Be(lawFirm.Id);
        caseDetail.GetProperty("lawFirm").GetString().Should().Be("Directory Law LLP");
        caseDetail.GetProperty("caseManagerId").GetGuid().Should().Be(caseManager.Id);
        caseDetail.GetProperty("caseManagerName").GetString().Should().Be("Cameron Manager");
    }

    [Fact]
    public async Task Case_information_rejects_a_medical_provider_with_the_wrong_company_type()
    {
        Company fundingCompany;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            fundingCompany = Company.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                CompanyDirectoryReferenceData.FundingCompanyId,
                "Not A Medical Provider",
                SeedHelper.UserId);
            db.Companies.Add(fundingCompany);
            await db.SaveChangesAsync();
        }

        var lienId = await CreateSellingLienAsync();
        var response = await _client.PutAsJsonAsync(
            $"/api/liens/selling/liens/{lienId}/case-information",
            new
            {
                fundingCompanyId = SeedHelper.FundingCompanyId,
                medicalProviderId = fundingCompany.Id,
                caseId = SeedHelper.CaseId,
                createCaseIfMissing = false,
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadAsStringAsync()).Should().Contain("medicalProviderId");
    }

    [Fact]
    public async Task Handling_law_firm_lookup_and_save_accept_only_standalone_law_firms()
    {
        var lawFirmContactId = Guid.CreateVersion7();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var lawFirmContact = Contact.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                ContactType.LawFirm,
                "Alex",
                "Attorney",
                SeedHelper.UserId,
                lawFirmId: SeedHelper.LawFirmId,
                contactSubtype: ContactSubtype.LawFirmAttorney,
                organization: "Smith & Associates LLP");
            SetId(lawFirmContact, lawFirmContactId);
            db.Contacts.Add(lawFirmContact);
            await db.SaveChangesAsync();
        }

        var lookupResponse = await _client.GetAsync("/api/liens/selling/lookups/law-firms");
        lookupResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            await lookupResponse.Content.ReadAsStringAsync());
        using var lookupJson = JsonDocument.Parse(await lookupResponse.Content.ReadAsStringAsync());
        var lawFirmIds = lookupJson.RootElement.GetProperty("items")
            .EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid())
            .ToList();
        lawFirmIds.Should().Contain(SeedHelper.LawFirmId);
        lawFirmIds.Should().NotContain(lawFirmContactId);

        var lienId = await CreateSellingLienAsync();
        var saveResponse = await _client.PutAsJsonAsync(
            $"/api/liens/selling/liens/{lienId}/case-information",
            new
            {
                fundingCompanyId = SeedHelper.FundingCompanyId,
                handlingLawFirmId = lawFirmContactId,
                caseId = SeedHelper.CaseId,
                createCaseIfMissing = false,
            });

        saveResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            await saveResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Document_type_lookup_returns_all_selling_document_codes()
    {
        var response = await _client.GetAsync("/api/liens/selling/lookups/document-types");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        payload.RootElement.GetProperty("items")
            .EnumerateArray()
            .Select(item => item.GetString())
            .Should()
            .Equal(
                "MedicalBill",
                "MedicalRecord",
                "LienAgreement",
                "SettlementStatement",
                "Other",
                "ItemizedBill",
                "HCFA-1500",
                "SignedLien",
                "LetterOfProtection");
    }

    [Fact]
    public async Task Confirm_sale_notification_uses_buyer_organization_and_never_persists_portal_capability_in_idempotency_replay()
    {
        var buyerOrgId = Guid.CreateVersion7();
        var buyerCompanyId = Guid.CreateVersion7();
        var buyerEmployeeId = Guid.CreateVersion7();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var buyerCompany = Contact.Create(
                SeedHelper.TenantId, buyerOrgId, ContactType.FundingCompany,
                "Buyer", "Capital", SeedHelper.UserId, organization: "Buyer Capital LLC");
            SetId(buyerCompany, buyerCompanyId);
            var buyerEmployee = Contact.Create(
                SeedHelper.TenantId, buyerOrgId, ContactType.Lead,
                "Erin", "Buyer", SeedHelper.UserId, organization: "Buyer Capital LLC", email: "erin@buyer-capital.test");
            SetId(buyerEmployee, buyerEmployeeId);
            var sellerContact = Contact.Create(
                SeedHelper.TenantId, SeedHelper.OrgId, ContactType.LawFirm,
                "Seller", "Representative", SeedHelper.UserId, organization: "Seller Law LLP", email: "seller@seller-law.test");
            db.Contacts.AddRange(buyerCompany, buyerEmployee, sellerContact);
            await db.SaveChangesAsync();
        }

        var lienId = await PrepareSellingLienAsync(buyerCompanyId, buyerEmployeeId, "Please review this time-sensitive lien.");
        Guid legacyDocumentId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var medicalDocumentTypeId = db.LookupValues.Single(value =>
                value.TenantId == SeedHelper.TenantId &&
                value.Category == LookupCategory.DocumentCategory &&
                value.Code == "Medical").Id;
            legacyDocumentId = Guid.CreateVersion7();
            db.ServicingItems.Add(ServicingItem.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                $"DOC-{Guid.CreateVersion7():N}"[..36],
                "LegacyLienDocument",
                "Lien document uploaded: creation-records",
                "Seller Operator",
                SeedHelper.UserId,
                lienId: lienId,
                notes:
                    $"documentId={legacyDocumentId}; url=/documents/{legacyDocumentId:D}; filename=creation-records; originalFileName=creation-records.pdf; documentTypeId={medicalDocumentTypeId:D}"));
            await db.SaveChangesAsync();
        }
        var idempotencyKey = Guid.CreateVersion7().ToString();
        using var confirm = new HttpRequestMessage(HttpMethod.Post, $"/api/liens/selling/liens/{lienId}/confirm-sale")
        {
            Content = JsonContent.Create(new { confirmationAccepted = true, sendBuyerNotification = true }),
        };
        confirm.Headers.Add("Idempotency-Key", idempotencyKey);
        var response = await _client.SendAsync(confirm);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var responseJson = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var portalUrl = responseJson.RootElement.GetProperty("notification").GetProperty("buyerPortalUrl").GetString();
        portalUrl.Should().NotBeNullOrWhiteSpace();

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<LiensDbContext>();
        var link = verifyDb.SellingBuyerAccessLinks.Single(item =>
            item.LienId == lienId &&
            item.Purpose == SellingAccessLinkPurposes.ConfirmSaleBuyerResponse);
        link.BuyerOrgId.Should().Be(buyerOrgId);
        link.BuyerContactId.Should().Be(buyerEmployeeId);
        var replay = verifyDb.SellingIdempotencyRecords.Single(item =>
            item.Route == "/api/liens/selling/liens/{lienId}/confirm-sale" && item.IdempotencyKey == idempotencyKey);
        replay.ResponseBody.Should().NotContain(portalUrl!);
        replay.ResponseBody.Should().NotContain(portalUrl!.Split('/').Last());
        replay.ResponseBody.Should().Contain("\"buyerPortalUrl\":null");
        var notification = verifyScope.ServiceProvider.GetRequiredService<CapturingNotificationPublisher>().Emails
            .Single(email => email.RecipientEmail == "erin@buyer-capital.test");
        notification.Options!.TemplateData!.Should().NotContainKey("buyerMessage");
        notification.Body.Should().Contain("Itemized Bill / HCFA-1500 Form: bill.pdf");
        notification.Options.HtmlBody.Should().Contain("Itemized Bill / HCFA-1500 Form");
        notification.Options.HtmlBody.Should().Contain("bill.pdf");
        notification.Body.Should().Contain("Medical Records: creation-records.pdf");
        notification.Options.HtmlBody.Should().Contain("Medical Records");
        notification.Options.HtmlBody.Should().Contain("creation-records.pdf");
        notification.Body.Should().NotContain("Please review this time-sensitive lien.");
        notification.Options.HtmlBody.Should().NotContain("Please review this time-sensitive lien.");
        notification.Options.HtmlBody.Should().NotContain("Seller Message");

        var token = portalUrl!.Split('/').Last();
        using var anonClient = _factory.CreateClient();
        var publicResponse = await anonClient.GetAsync($"/api/liens/selling/public/{token}");
        publicResponse.StatusCode.Should().Be(HttpStatusCode.OK, await publicResponse.Content.ReadAsStringAsync());
        using var publicJson = JsonDocument.Parse(await publicResponse.Content.ReadAsStringAsync());
        var documents = publicJson.RootElement.GetProperty("documents").EnumerateArray().ToList();
        documents.Should().HaveCount(2);
        var sellerWizardDocument = documents.Single(document => document.GetProperty("fileName").GetString() == "bill.pdf");
        sellerWizardDocument.GetProperty("category").GetString().Should().Be("Itemized Bill / HCFA-1500 Form");
        var sellerWizardDocumentId = sellerWizardDocument.GetProperty("id").GetGuid();
        sellerWizardDocument.GetProperty("viewUrl").GetString()
            .Should().Be($"/api/lien/api/liens/selling/public/{token}/documents/{sellerWizardDocumentId:D}/view");
        sellerWizardDocument.GetProperty("downloadUrl").GetString()
            .Should().Be($"/api/lien/api/liens/selling/public/{token}/documents/{sellerWizardDocumentId:D}/download");

        var legacyDocument = documents.Single(document => document.GetProperty("fileName").GetString() == "creation-records.pdf");
        legacyDocument.GetProperty("category").GetString().Should().Be("Medical Records");
        legacyDocument.GetProperty("id").GetGuid().Should().Be(legacyDocumentId);
        legacyDocument.GetProperty("viewUrl").GetString()
            .Should().Be($"/api/lien/api/liens/selling/public/{token}/documents/{legacyDocument.GetProperty("id").GetGuid():D}/view");
        legacyDocument.GetProperty("downloadUrl").GetString()
            .Should().Be($"/api/lien/api/liens/selling/public/{token}/documents/{legacyDocument.GetProperty("id").GetGuid():D}/download");
    }

    [Fact]
    public async Task Prepare_and_confirm_sale_accept_company_directory_buyer_contact()
    {
        Company buyerCompany;
        CompanyContactPerson buyerContact;
        Company lawFirmCompany;
        CompanyContactPerson caseManagerContact;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var fundingRoleId = CompanyDirectoryReferenceData.ContactPersonTypes
                .First(role => role.CompanyTypeId == CompanyDirectoryReferenceData.FundingCompanyId)
                .Id;
            var caseManagerRoleId = CompanyDirectoryReferenceData.ContactPersonTypes
                .First(role =>
                    role.CompanyTypeId == CompanyDirectoryReferenceData.LawFirmId &&
                    role.Code == "CaseManager")
                .Id;
            buyerCompany = Company.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                CompanyDirectoryReferenceData.FundingCompanyId,
                "Canonical Buyer Capital",
                SeedHelper.UserId);
            buyerContact = CompanyContactPerson.Create(
                SeedHelper.TenantId,
                buyerCompany.Id,
                fundingRoleId,
                "Carla",
                "Buyer",
                SeedHelper.UserId,
                email: "carla@canonical-buyer.test");
            lawFirmCompany = Company.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                CompanyDirectoryReferenceData.LawFirmId,
                "Canonical Handling Law Firm",
                SeedHelper.UserId,
                email: "canonical-handling@lawfirm.test");
            caseManagerContact = CompanyContactPerson.Create(
                SeedHelper.TenantId,
                lawFirmCompany.Id,
                caseManagerRoleId,
                "Case",
                "Manager",
                SeedHelper.UserId,
                email: "case.manager@lawfirm.test");
            var sellerContact = Contact.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                ContactType.LawFirm,
                "Seller",
                "Representative",
                SeedHelper.UserId,
                organization: "Seller Law LLP",
                email: "seller@canonical-buyer.test");
            db.AddRange(buyerCompany, buyerContact, lawFirmCompany, caseManagerContact, sellerContact);
            await db.SaveChangesAsync();
        }

        var lienId = await PrepareSellingLienAsync(buyerCompany.Id, buyerContact.Id);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var preparedLien = await db.Liens.SingleAsync(item => item.Id == lienId);
            preparedLien.FundingCompanyCompanyId.Should().Be(buyerCompany.Id);
            preparedLien.FundingCompanyContactPersonId.Should().Be(buyerContact.Id);
            preparedLien.FundingCompanyId.Should().BeNull();
            preparedLien.FundingCompanyContactId.Should().BeNull();

            var caseEntity = await db.Cases.SingleAsync(item => item.Id == SeedHelper.CaseId);
            caseEntity.SetCanonicalCaseParties(lawFirmCompany.Id, caseManagerContact.Id, SeedHelper.UserId);
            await db.SaveChangesAsync();
        }

        using var confirm = new HttpRequestMessage(HttpMethod.Post, $"/api/liens/selling/liens/{lienId}/confirm-sale")
        {
            Content = JsonContent.Create(new { confirmationAccepted = true, sendBuyerNotification = true }),
        };
        confirm.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString());
        var confirmResponse = await _client.SendAsync(confirm);
        confirmResponse.StatusCode.Should().Be(HttpStatusCode.OK, await confirmResponse.Content.ReadAsStringAsync());
        using var confirmJson = JsonDocument.Parse(await confirmResponse.Content.ReadAsStringAsync());
        var portalUrl = confirmJson.RootElement.GetProperty("notification").GetProperty("buyerPortalUrl").GetString();
        portalUrl.Should().NotBeNullOrWhiteSpace();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var link = await db.SellingBuyerAccessLinks.SingleAsync(item =>
                item.LienId == lienId &&
                item.Purpose == SellingAccessLinkPurposes.ConfirmSaleBuyerResponse);
            link.BuyerOrgId.Should().Be(buyerCompany.Id);
            link.BuyerContactId.Should().Be(buyerContact.Id);
            link.BuyerCompanyId.Should().Be(buyerCompany.Id);
            link.BuyerCompanyContactPersonId.Should().Be(buyerContact.Id);
        }

        var token = portalUrl!.Split('/').Last();
        using var anonymousClient = _factory.CreateClient();
        var publicResponse = await anonymousClient.GetAsync($"/api/liens/selling/public/{token}");
        publicResponse.StatusCode.Should().Be(HttpStatusCode.OK, await publicResponse.Content.ReadAsStringAsync());
        using var publicJson = JsonDocument.Parse(await publicResponse.Content.ReadAsStringAsync());
        var buyer = publicJson.RootElement.GetProperty("buyer");
        buyer.GetProperty("contactName").GetString().Should().Be("Carla Buyer");
        buyer.GetProperty("company").GetString().Should().Be("Canonical Buyer Capital");
        buyer.GetProperty("email").GetString().Should().Be("carla@canonical-buyer.test");
        var caseInfo = publicJson.RootElement.GetProperty("case");
        caseInfo.GetProperty("handlingLawFirm").GetString().Should().Be("Canonical Handling Law Firm");
        caseInfo.GetProperty("caseManager").GetString().Should().Be("Case Manager");
    }

    [Fact]
    public async Task Public_offer_replays_identical_request_and_rejects_body_mismatch()
    {
        var (token, lienId) = await SeedPublicAccessLinkAsync();
        var key = Guid.CreateVersion7().ToString();

        var first = await PostPublicAsync(token, "offers", key, new { offerAmount = 450m, message = "First offer" });
        first.StatusCode.Should().Be(HttpStatusCode.Created, await first.Content.ReadAsStringAsync());
        var firstBody = await first.Content.ReadAsStringAsync();

        var replay = await PostPublicAsync(token, "offers", key, new { offerAmount = 450m, message = "First offer" });
        replay.StatusCode.Should().Be(HttpStatusCode.Created, await replay.Content.ReadAsStringAsync());
        (await replay.Content.ReadAsStringAsync()).Should().Be(firstBody);

        var mismatch = await PostPublicAsync(token, "offers", key, new { offerAmount = 451m, message = "Changed offer" });
        mismatch.StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
        db.LienOffers.Count(offer => offer.LienId == lienId).Should().Be(1);
        db.SellingNotificationOutboxItems.Count(item => item.EventKey == "lien.offer.submitted")
            .Should().Be(0, "a public contact id must not be treated as an Identity platform user");
    }

    [Fact]
    public async Task Public_decline_replays_identical_request_and_rejects_body_mismatch()
    {
        var (token, lienId) = await SeedPublicAccessLinkAsync();
        var key = Guid.CreateVersion7().ToString();

        var first = await PostPublicAsync(token, "decline", key, new { reason = "Not within mandate" });
        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        var firstBody = await first.Content.ReadAsStringAsync();

        var replay = await PostPublicAsync(token, "decline", key, new { reason = "Not within mandate" });
        replay.StatusCode.Should().Be(HttpStatusCode.OK, await replay.Content.ReadAsStringAsync());
        (await replay.Content.ReadAsStringAsync()).Should().Be(firstBody);

        var mismatch = await PostPublicAsync(token, "decline", key, new { reason = "Changed reason" });
        mismatch.StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
        var link = db.SellingBuyerAccessLinks.Single(link => link.LienId == lienId);
        link.ResponseStatus.Should().Be("Declined");
        var outbox = db.SellingNotificationOutboxItems.Single(item =>
            item.EventKey == "lien.offer.rejected" && item.IdempotencyKey.Contains(link.Id.ToString("N")));
        outbox.RecipientUserId.Should().Be(link.CreatedByUserId!.Value);
    }

    [Fact]
    public async Task Public_response_transition_gate_excludes_competing_accept_and_decline()
    {
        var (token, lienId) = await SeedPublicAccessLinkAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var link = db.SellingBuyerAccessLinks.Single(item => item.LienId == lienId);
            var nullRequestHash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("null"))).ToLowerInvariant();
            db.SellingIdempotencyRecords.Add(SellingIdempotencyRecord.Create(
                SeedHelper.TenantId,
                "BuyerLinkResponseTransition",
                link.Id,
                "/api/liens/selling/public/{token}/response",
                "BuyerAccessLink",
                link.Id.ToString(),
                "buyer-response-transition-v1",
                nullRequestHash));
            await db.SaveChangesAsync();
        }

        var accept = await PostPublicAsync(token, "accept", Guid.CreateVersion7().ToString(), new { });
        var decline = await PostPublicAsync(token, "decline", Guid.CreateVersion7().ToString(), new { reason = "Competing response" });

        accept.StatusCode.Should().Be(HttpStatusCode.Conflict);
        decline.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var verifyScope = _factory.Services.CreateScope();
        verifyScope.ServiceProvider.GetRequiredService<LiensDbContext>()
            .SellingBuyerAccessLinks.Single(item => item.LienId == lienId).ResponseStatus.Should().BeNull();
    }

    [Fact]
    public async Task Authenticated_buyer_decline_shares_the_public_response_transition_gate()
    {
        var (token, lienId) = await SeedPublicAccessLinkAsync();
        Guid buyerOrgId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var link = db.SellingBuyerAccessLinks.Single(item => item.LienId == lienId);
            buyerOrgId = link.BuyerOrgId;
            var nullRequestHash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("null"))).ToLowerInvariant();
            db.SellingIdempotencyRecords.Add(SellingIdempotencyRecord.Create(
                SeedHelper.TenantId,
                "BuyerLinkResponseTransition",
                link.Id,
                "/api/liens/selling/public/{token}/response",
                "BuyerAccessLink",
                link.Id.ToString(),
                "buyer-response-transition-v1",
                nullRequestHash));
            await db.SaveChangesAsync();
        }

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", JwtTokenHelper.CreateFullAccessToken(SeedHelper.TenantId, SeedHelper.UserId, buyerOrgId));
        var accept = await PostPublicAsync(token, "accept", Guid.CreateVersion7().ToString(), new { });
        using var decline = new HttpRequestMessage(HttpMethod.Post, $"/api/liens/selling/buyer/liens/by-lien/{lienId}/decline")
        {
            Content = JsonContent.Create(new { reason = "Competing authenticated response" }),
        };
        decline.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString());
        var declineResponse = await _client.SendAsync(decline);

        accept.StatusCode.Should().Be(HttpStatusCode.Conflict);
        declineResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var verifyScope = _factory.Services.CreateScope();
        verifyScope.ServiceProvider.GetRequiredService<LiensDbContext>()
            .SellingBuyerAccessLinks.Single(item => item.LienId == lienId).ResponseStatus.Should().BeNull();
    }

    [Fact]
    public void Buyer_response_transition_subject_type_fits_the_persisted_column()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
        var maxLength = db.Model.FindEntityType(typeof(SellingIdempotencyRecord))!
            .FindProperty(nameof(SellingIdempotencyRecord.SubjectType))!
            .GetMaxLength();

        "BuyerLinkResponseTransition".Length.Should().BeLessThanOrEqualTo(maxLength!.Value);
    }

    [Fact]
    public async Task Lien_transition_gate_excludes_competing_public_accept_and_seller_withdraw()
    {
        var (token, lienId) = await SeedPublicAccessLinkAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var nullRequestHash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("null"))).ToLowerInvariant();
            db.SellingIdempotencyRecords.Add(SellingIdempotencyRecord.Create(
                SeedHelper.TenantId,
                "LienStateTransition",
                lienId,
                "/api/liens/selling/liens/{lienId}/state-transition",
                "Lien",
                lienId.ToString(),
                "lien-state-transition-v1",
                nullRequestHash));
            await db.SaveChangesAsync();
        }

        var accept = await PostPublicAsync(token, "accept", Guid.CreateVersion7().ToString(), new { });
        using var withdraw = new HttpRequestMessage(HttpMethod.Post, $"/api/liens/selling/liens/{lienId}/withdraw-sale")
        {
            Content = JsonContent.Create(new { reason = "Competing seller action" }),
        };
        withdraw.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString());
        var withdrawResponse = await _client.SendAsync(withdraw);

        accept.StatusCode.Should().Be(HttpStatusCode.Conflict);
        withdrawResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var verifyScope = _factory.Services.CreateScope();
        var lien = verifyScope.ServiceProvider.GetRequiredService<LiensDbContext>().Liens.Single(item => item.Id == lienId);
        lien.Status.Should().Be(LienStatus.Offered);
        lien.SellerStatus.Should().Be(SellingLienStatus.SubmittedForSale);
    }

    [Fact]
    public async Task Withdraw_sale_returns_lien_to_pending_and_removes_it_from_the_buyer()
    {
        var (_, lienId) = await SeedPublicAccessLinkAsync("Imported Funding Company");
        Guid accessLinkId;
        Guid buyerOrgId;
        Guid offerId;
        string lienNumber;
        using (var setupScope = _factory.Services.CreateScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var lien = db.Liens.Single(item => item.Id == lienId);
            lienNumber = lien.LienNumber;
            var accessLink = db.SellingBuyerAccessLinks.Single(item => item.LienId == lienId);
            accessLinkId = accessLink.Id;
            buyerOrgId = accessLink.BuyerOrgId;
            lien.SetSellingFundingReferences(
                accessLink.BuyerOrgId,
                accessLink.BuyerContactId,
                null,
                null,
                SeedHelper.UserId);
            lien.UpdateSellingAnalyticsFields(SeedHelper.UserId, highestBidAmount: 425m);
            var offer = LienOffer.Create(
                SeedHelper.TenantId,
                lienId,
                accessLink.BuyerOrgId,
                SeedHelper.OrgId,
                425m,
                SeedHelper.UserId);
            offerId = offer.Id;
            db.LienOffers.Add(offer);
            await db.SaveChangesAsync();
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/liens/selling/liens/{lienId}/withdraw-sale")
        {
            Content = JsonContent.Create(new { reason = "Seller changed plans" }),
        };
        request.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString());
        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var responseJson = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        responseJson.RootElement.GetProperty("status").GetString().Should().Be(LienStatus.Draft);
        responseJson.RootElement.GetProperty("sellerStatus").GetString().Should().Be(SellingLienStatus.Pending);
        responseJson.RootElement.GetProperty("withdrawnAtUtc").GetDateTime().Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));

        using (var verifyScope = _factory.Services.CreateScope())
        {
            var db = verifyScope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var lien = db.Liens.Single(item => item.Id == lienId);
            lien.Status.Should().Be(LienStatus.Draft);
            lien.SellerStatus.Should().Be(SellingLienStatus.Pending);
            lien.FundingCompanyId.Should().BeNull();
            lien.FundingCompanyContactId.Should().BeNull();
            lien.FundingCompanyCompanyId.Should().BeNull();
            lien.FundingCompanyContactPersonId.Should().BeNull();
            lien.ExternalReference.Should().Be("Imported Funding Company");
            lien.HighestBidAmount.Should().BeNull();
            lien.SubmittedForSaleAtUtc.Should().BeNull();
            lien.WithdrawnAtUtc.Should().NotBeNull();
            db.SellingBuyerAccessLinks.Single(item => item.Id == accessLinkId)
                .RevokedAtUtc.Should().NotBeNull();
            db.LienOffers.Single(item => item.Id == offerId)
                .Status.Should().Be(OfferStatus.Withdrawn);
            db.SellingIdempotencyRecords.Should().NotContain(item =>
                item.SubjectType == "LienStateTransition" && item.SubjectId == lienId);
        }

        var pendingList = await _client.GetAsync(
            $"/api/liens/selling/liens?tab=pending&search={Uri.EscapeDataString(lienNumber)}&page=1&pageSize=25");
        pendingList.StatusCode.Should().Be(HttpStatusCode.OK, await pendingList.Content.ReadAsStringAsync());
        using var pendingJson = JsonDocument.Parse(await pendingList.Content.ReadAsStringAsync());
        pendingJson.RootElement.GetProperty("items")
            .EnumerateArray()
            .Select(item => item.GetProperty("lienId").GetGuid())
            .Should().Contain(lienId);

        var detail = await _client.GetAsync($"/api/liens/selling/liens/{lienId}");
        detail.StatusCode.Should().Be(HttpStatusCode.OK, await detail.Content.ReadAsStringAsync());
        using var detailJson = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        detailJson.RootElement.GetProperty("fundingCompany").ValueKind.Should().Be(JsonValueKind.Null);
        detailJson.RootElement.GetProperty("availableActions")
            .EnumerateArray()
            .Select(action => action.GetString())
            .Should().Contain("prepare-sale");

        using var buyerClient = _factory.CreateClient();
        buyerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            JwtTokenHelper.CreateFullAccessToken(
                SeedHelper.TenantId,
                Guid.CreateVersion7(),
                buyerOrgId,
                "public.buyer@test.local"));
        var buyerList = await buyerClient.GetAsync(
            $"/api/liens/selling/buyer/liens?search={Uri.EscapeDataString(lienNumber)}");
        buyerList.StatusCode.Should().Be(HttpStatusCode.OK, await buyerList.Content.ReadAsStringAsync());
        using var buyerListJson = JsonDocument.Parse(await buyerList.Content.ReadAsStringAsync());
        buyerListJson.RootElement.GetProperty("total").GetInt32().Should().Be(0);

        var buyerDetail = await buyerClient.GetAsync($"/api/liens/selling/buyer/liens/{accessLinkId}");
        buyerDetail.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Contact_case_reassignment_denies_cross_organization_target()
    {
        var sourceId = Guid.CreateVersion7();
        var targetId = Guid.CreateVersion7();
        var caseId = Guid.CreateVersion7();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var source = Contact.Create(SeedHelper.TenantId, SeedHelper.OrgId, ContactType.CaseManager, "Source", "Manager", SeedHelper.UserId);
            SetId(source, sourceId);
            var target = Contact.Create(SeedHelper.TenantId, Guid.CreateVersion7(), ContactType.CaseManager, "Other", "Manager", SeedHelper.UserId);
            SetId(target, targetId);
            var caseEntity = Case.Create(SeedHelper.TenantId, SeedHelper.OrgId, $"CASE-{Guid.CreateVersion7():N}"[..15], "Client", "Name", SeedHelper.UserId, notes: $"caseManagerId={sourceId}");
            SetId(caseEntity, caseId);
            db.Contacts.AddRange(source, target);
            db.Cases.Add(caseEntity);
            await db.SaveChangesAsync();
        }

        var response = await _client.PostAsJsonAsync($"/api/liens/contacts/{sourceId}/reassign-cases", new
        {
            targetContactId = targetId,
            relationshipType = "CaseManager",
            scope = "Selected",
            caseIds = new[] { caseId },
        });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Documents_endpoint_rejects_unavailable_or_foreign_document_reference()
    {
        var lienId = await CreateSellingLienAsync();
        var documentId = Guid.CreateVersion7();
        using (var scope = _factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<CapturingSellingDocumentReferenceValidator>()
                .DeniedDocumentIds.Add(documentId);
        }

        var response = await _client.PutAsJsonAsync($"/api/liens/selling/liens/{lienId}/documents", new
        {
            documents = new[] { new { documentId, documentType = "MedicalBill" } },
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Documents_endpoint_saves_required_and_supporting_documents_with_unique_task_numbers()
    {
        var lienId = await CreateSellingLienAsync();
        var documents = new[]
        {
            new { documentId = Guid.CreateVersion7(), documentType = "MedicalBill", displayName = "bill.pdf" },
            new { documentId = Guid.CreateVersion7(), documentType = "MedicalRecord", displayName = "record.pdf" },
            new { documentId = Guid.CreateVersion7(), documentType = "PoliceReport", displayName = "report.pdf" },
        };

        var response = await _client.PutAsJsonAsync($"/api/liens/selling/liens/{lienId}/documents", new { documents });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
        var savedReferences = db.ServicingItems
            .Where(item => item.LienId == lienId && item.TaskType == "SellingDocumentReference")
            .ToList();
        savedReferences.Should().HaveCount(documents.Length);
        savedReferences.Select(item => item.TaskNumber).Should().OnlyHaveUniqueItems();
        savedReferences.Should().OnlyContain(item => item.TaskNumber.StartsWith("SDR-") && item.TaskNumber.Length == 36);
    }

    [Fact]
    public async Task Intake_writes_remain_available_after_prepare_sale_until_confirmation()
    {
        var lienId = await PrepareSellingLienAsync(SeedHelper.FundingCompanyId, SeedHelper.FundingCompanyId);

        var response = await _client.PutAsJsonAsync($"/api/liens/selling/liens/{lienId}/lien-information", new
        {
            sellerStatus = "Pending", initialServiceDate = "2026-07-20", listingVisibility = "Private",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
        var persisted = await db.Liens.FindAsync(lienId);
        persisted!.SellerStatus.Should().Be(SellingLienStatus.Pending);
        persisted.InitialServiceDate.Should().Be(new DateOnly(2026, 7, 20));
    }

    [Fact]
    public async Task Lien_detail_available_actions_include_keep_only_when_management_move_is_allowed()
    {
        var lienId = await CreateSellingLienAsync();

        var pendingResponse = await _client.GetAsync($"/api/liens/selling/liens/{lienId}");
        pendingResponse.StatusCode.Should().Be(HttpStatusCode.OK, await pendingResponse.Content.ReadAsStringAsync());
        using (var pendingJson = JsonDocument.Parse(await pendingResponse.Content.ReadAsStringAsync()))
        {
            pendingJson.RootElement.GetProperty("availableActions")
                .EnumerateArray()
                .Select(action => action.GetString())
                .Should().Equal("prepare-sale", "archive", "keep");
        }

        using (var keep = new HttpRequestMessage(HttpMethod.Post, $"/api/liens/selling/liens/{lienId}/move-to-management-v2")
        {
            Content = JsonContent.Create(new { reason = "Retained internally" }),
        })
        {
            keep.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString());
            var keepResponse = await _client.SendAsync(keep);
            keepResponse.StatusCode.Should().Be(HttpStatusCode.OK, await keepResponse.Content.ReadAsStringAsync());
        }

        var keptResponse = await _client.GetAsync($"/api/liens/selling/liens/{lienId}");
        keptResponse.StatusCode.Should().Be(HttpStatusCode.OK, await keptResponse.Content.ReadAsStringAsync());
        using var keptJson = JsonDocument.Parse(await keptResponse.Content.ReadAsStringAsync());
        keptJson.RootElement.GetProperty("availableActions")
            .EnumerateArray()
            .Select(action => action.GetString())
            .Should().NotContain("keep");

        using var repeatKeep = new HttpRequestMessage(HttpMethod.Post, $"/api/liens/selling/liens/{lienId}/move-to-management-v2")
        {
            Content = JsonContent.Create(new { reason = "Retained internally again" }),
        };
        repeatKeep.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString());
        var repeatResponse = await _client.SendAsync(repeatKeep);
        repeatResponse.StatusCode.Should().Be(HttpStatusCode.Conflict, await repeatResponse.Content.ReadAsStringAsync());
        (await repeatResponse.Content.ReadAsStringAsync()).Should().Contain("lien_already_moved_to_management");
    }

    [Fact]
    public async Task Archive_status_and_restore_keep_lien_record_with_history()
    {
        var lienId = await CreateSellingLienAsync();

        using (var archive = new HttpRequestMessage(HttpMethod.Post, $"/api/liens/selling/liens/{lienId}/archive")
        {
            Content = JsonContent.Create(new { reason = "No longer active" }),
        })
        {
            archive.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString());
            var archiveResponse = await _client.SendAsync(archive);
            archiveResponse.StatusCode.Should().Be(HttpStatusCode.OK, await archiveResponse.Content.ReadAsStringAsync());
        }

        var statusResponse = await _client.GetAsync($"/api/liens/selling/liens/{lienId}/archived-status");
        statusResponse.StatusCode.Should().Be(HttpStatusCode.OK, await statusResponse.Content.ReadAsStringAsync());
        using (var statusJson = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync()))
        {
            statusJson.RootElement.GetProperty("isArchived").GetBoolean().Should().BeTrue();
            statusJson.RootElement.GetProperty("sellerStatus").GetString().Should().Be(SellingLienStatus.Archived);
            statusJson.RootElement.GetProperty("archivedReason").GetString().Should().Be("No longer active");
        }

        using (var restore = new HttpRequestMessage(HttpMethod.Post, $"/api/liens/selling/liens/{lienId}/restore")
        {
            Content = JsonContent.Create(new { }),
        })
        {
            restore.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString());
            var restoreResponse = await _client.SendAsync(restore);
            restoreResponse.StatusCode.Should().Be(HttpStatusCode.OK, await restoreResponse.Content.ReadAsStringAsync());
        }

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
        var persisted = await db.Liens.FindAsync(lienId);
        persisted.Should().NotBeNull();
        persisted!.SellerStatus.Should().Be(SellingLienStatus.Pending);
        persisted.ArchivedAtUtc.Should().BeNull();
        persisted.ArchivedReason.Should().BeNull();
        var historyDescriptions = db.LienStatusHistories
            .Where(item => item.LienId == lienId)
            .Select(item => item.Description)
            .ToList();
        historyDescriptions.Should().HaveCountGreaterThanOrEqualTo(2);
        historyDescriptions.Should().Contain(description =>
            description.StartsWith("Lien Status: Archived. Lien archived. Changes:", StringComparison.Ordinal) &&
            description.Contains("Seller Status: Pending → Archived", StringComparison.Ordinal) &&
            description.Contains("Archived At UTC: blank →", StringComparison.Ordinal) &&
            description.Contains("Archived Reason: blank → No longer active", StringComparison.Ordinal));
        historyDescriptions.Should().Contain(description =>
            description.StartsWith("Lien Status: Pending. Lien restored from archive. Changes:", StringComparison.Ordinal) &&
            description.Contains("Seller Status: Archived → Pending", StringComparison.Ordinal) &&
            description.Contains("Archived At UTC:", StringComparison.Ordinal) &&
            description.Contains("→ blank", StringComparison.Ordinal) &&
            description.Contains("Archived Reason: No longer active → blank", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Invalid_confirmation_does_not_reserve_the_transition_or_idempotency_key()
    {
        var (buyerCompanyId, buyerContactId) = await SeedConfirmSaleContactsAsync(
            "buyer.invalid-confirm@capital.test",
            "seller.invalid-confirm@smithlaw.test");
        var lienId = await PrepareSellingLienAsync(buyerCompanyId, buyerContactId);
        var key = Guid.CreateVersion7().ToString();

        using (var invalid = new HttpRequestMessage(HttpMethod.Post, $"/api/liens/selling/liens/{lienId}/confirm-sale")
        {
            Content = JsonContent.Create(new { confirmationAccepted = false, sendBuyerNotification = false }),
        })
        {
            invalid.Headers.Add("Idempotency-Key", key);
            (await _client.SendAsync(invalid)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        using var valid = new HttpRequestMessage(HttpMethod.Post, $"/api/liens/selling/liens/{lienId}/confirm-sale")
        {
            Content = JsonContent.Create(new { confirmationAccepted = true, sendBuyerNotification = false }),
        };
        valid.Headers.Add("Idempotency-Key", key);
        (await _client.SendAsync(valid)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Prepare_sale_preserves_internal_notes_and_exposes_buyer_message_only_to_seller_detail()
    {
        var lienId = await PrepareSellingLienAsync(SeedHelper.FundingCompanyId, SeedHelper.FundingCompanyId, "Buyer-only review message");

        var response = await _client.GetAsync($"/api/liens/selling/liens/{lienId}");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("lienInformation").GetProperty("buyerMessage").GetString()
            .Should().Be("Buyer-only review message");
    }

    [Fact]
    public async Task Move_to_management_preserves_the_existing_case_and_sets_the_lien_internal()
    {
        var lienId = await CreateSellingLienAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var lien = await db.Liens.FindAsync(lienId);
            lien!.AttachCase(SeedHelper.CaseId, SeedHelper.UserId);
            await db.SaveChangesAsync();
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/liens/selling/liens/{lienId}/move-to-management")
        {
            Content = JsonContent.Create(new { reason = "Retained internally" }),
        };
        request.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString());
        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var lien = await db.Liens.FindAsync(lienId);
            lien!.CaseId.Should().Be(SeedHelper.CaseId);
            lien.SellingCaseId.Should().Be(SeedHelper.CaseId);
            lien.MovedToManagementAtUtc.Should().NotBeNull();
            lien.SellerStatus.Should().Be(SellingLienStatus.Internal);
            lien.Status.Should().Be(LienStatus.Draft);
            db.LienStatusHistories.Should().Contain(item => item.LienId == lienId && item.Description!.Contains("moved to management", StringComparison.OrdinalIgnoreCase));
        }

        var managementResponse = await _client.GetAsync($"/api/liens/liens/{lienId}");
        managementResponse.EnsureSuccessStatusCode();
        using var managementJson = JsonDocument.Parse(await managementResponse.Content.ReadAsStringAsync());
        managementJson.RootElement.GetProperty("caseId").GetGuid().Should().Be(SeedHelper.CaseId);
        managementJson.RootElement.GetProperty("sellingCaseId").GetGuid().Should().Be(SeedHelper.CaseId);
        managementJson.RootElement.GetProperty("sellerStatus").GetString().Should().Be(SellingLienStatus.Internal);
    }

    [Fact]
    public async Task Move_to_management_exposes_selling_billing_and_purchase_amounts_in_management()
    {
        var lienId = await CreateSellingLienAsync();
        (await _client.PutAsJsonAsync($"/api/liens/selling/liens/{lienId}/medical-pricing", new
        {
            askAmount = 1250m,
            billingAmount = 1800m,
            rows = new[] { new { medicalCode = "99213", billingAmount = 1800m, medicareCost = 180m, targetSaleAmount = 1250m } },
        })).EnsureSuccessStatusCode();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var lien = await db.Liens.FindAsync(lienId);
            lien!.AttachCase(SeedHelper.CaseId, SeedHelper.UserId);
            db.ServicingItems.Add(ServicingItem.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                $"LMC-{Guid.CreateVersion7():N}".ToUpperInvariant(),
                "LegacyMedicalCode",
                "Imported pricing",
                "system",
                SeedHelper.UserId,
                caseId: SeedHelper.CaseId,
                lienId: lienId,
                notes: "billingAmount=1; purchaseAmount=1"));
            await db.SaveChangesAsync();
        }

        using var move = new HttpRequestMessage(HttpMethod.Post, $"/api/liens/selling/liens/{lienId}/move-to-management")
        {
            Content = JsonContent.Create(new { reason = "Retained internally" }),
        };
        move.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString());
        (await _client.SendAsync(move)).EnsureSuccessStatusCode();

        var managementResponse = await _client.GetAsync($"/api/liens/liens/{lienId}");
        managementResponse.EnsureSuccessStatusCode();
        using var managementJson = JsonDocument.Parse(await managementResponse.Content.ReadAsStringAsync());
        managementJson.RootElement.GetProperty("originalAmount").GetDecimal().Should().Be(1800m);
        managementJson.RootElement.GetProperty("totalBilling").GetDecimal().Should().Be(1800m);
        managementJson.RootElement.GetProperty("totalPurchase").GetDecimal().Should().Be(1250m);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Move_to_management_preserves_existing_management_medical_codes_when_selling_pricing_is_absent(bool useV2)
    {
        var lienId = await CreateSellingLienAsync();
        Guid medicalCodeId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var medicalCode = ServicingItem.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                $"LMC-{Guid.CreateVersion7():N}".ToUpperInvariant(),
                "LegacyMedicalCode",
                "Medical code 99213",
                "system",
                SeedHelper.UserId,
                caseId: SeedHelper.CaseId,
                lienId: lienId,
                notes: "code=99213; description=Office visit; medicareCost=180; billingAmount=1800; purchaseAmount=1250");
            medicalCodeId = medicalCode.Id;
            db.ServicingItems.Add(medicalCode);
            await db.SaveChangesAsync();
        }

        var route = useV2 ? "move-to-management-v2" : "move-to-management";
        using var move = new HttpRequestMessage(HttpMethod.Post, $"/api/liens/selling/liens/{lienId}/{route}")
        {
            Content = JsonContent.Create(new { reason = "Retained internally" }),
        };
        move.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString());

        var response = await _client.SendAsync(move);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var preservedCode = await db.ServicingItems.SingleAsync(item => item.Id == medicalCodeId);
            preservedCode.CaseId.Should().Be(SeedHelper.CaseId);
            preservedCode.Notes.Should().Contain("code=99213");
        }

        var medicalCodesResponse = await _client.GetAsync($"/api/liens/cases/liens/get-medicalcode/{lienId}");
        medicalCodesResponse.StatusCode.Should().Be(HttpStatusCode.OK, await medicalCodesResponse.Content.ReadAsStringAsync());
        using var medicalCodesJson = JsonDocument.Parse(await medicalCodesResponse.Content.ReadAsStringAsync());
        var medicalCodeJson = medicalCodesJson.RootElement.GetProperty("data").EnumerateArray().Single();
        medicalCodeJson.GetProperty("code").GetString().Should().Be("99213");
        medicalCodeJson.GetProperty("description").GetString().Should().Be("Office visit");
        medicalCodeJson.GetProperty("medicareCost").GetString().Should().Be("180");
        medicalCodeJson.GetProperty("billingAmount").GetString().Should().Be("1800");
        medicalCodeJson.GetProperty("purchaseAmount").GetString().Should().Be("1250");
    }

    [Fact]
    public async Task Management_medical_code_read_recovers_a_blank_legacy_row_from_selling_pricing()
    {
        var lienId = await CreateSellingLienAsync();
        (await _client.PutAsJsonAsync($"/api/liens/selling/liens/{lienId}/medical-pricing", new
        {
            askAmount = 1250m,
            billingAmount = 1800m,
            rows = new[]
            {
                new
                {
                    medicalCode = "99213",
                    description = "Office visit",
                    billingAmount = 1800m,
                    medicareCost = 180m,
                    targetSaleAmount = 1250m,
                },
            },
        })).EnsureSuccessStatusCode();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            db.ServicingItems.Add(ServicingItem.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                $"LMC-{Guid.CreateVersion7():N}".ToUpperInvariant(),
                "LegacyMedicalCode",
                "Imported pricing",
                "system",
                SeedHelper.UserId,
                caseId: SeedHelper.CaseId,
                lienId: lienId,
                notes: "code=; description=; medicareCost=0; billingAmount=0; purchaseAmount=0"));
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync($"/api/liens/cases/liens/get-medicalcode/{lienId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var medicalCode = payload.RootElement.GetProperty("data").EnumerateArray().Single();
        medicalCode.GetProperty("code").GetString().Should().Be("99213");
        medicalCode.GetProperty("description").GetString().Should().Be("Office visit");
        medicalCode.GetProperty("medicareCost").GetString().Should().Be("180");
        medicalCode.GetProperty("billingAmount").GetString().Should().Be("1800");
        medicalCode.GetProperty("purchaseAmount").GetString().Should().Be("1250");
    }

    [Fact]
    public async Task Management_medical_code_read_recovers_each_blank_legacy_row_from_matching_selling_pricing()
    {
        var lienId = await CreateSellingLienAsync();
        (await _client.PutAsJsonAsync($"/api/liens/selling/liens/{lienId}/medical-pricing", new
        {
            askAmount = 1850m,
            billingAmount = 2500m,
            rows = new[]
            {
                new { medicalCode = "62323", description = "Injection", billingAmount = 800m, medicareCost = 100m, targetSaleAmount = 600m },
                new { medicalCode = "64483", description = "Anesthetic injection", billingAmount = 900m, medicareCost = 200m, targetSaleAmount = 700m },
                new { medicalCode = "43239", description = "Endoscopy", billingAmount = 800m, medicareCost = 300m, targetSaleAmount = 550m },
            },
        })).EnsureSuccessStatusCode();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            foreach (var code in new[] { "62323", "64483", "43239" })
            {
                db.ServicingItems.Add(ServicingItem.Create(
                    SeedHelper.TenantId,
                    SeedHelper.OrgId,
                    $"LMC-{Guid.CreateVersion7():N}".ToUpperInvariant(),
                    "LegacyMedicalCode",
                    $"Medical code {code}",
                    "system",
                    SeedHelper.UserId,
                    caseId: SeedHelper.CaseId,
                    lienId: lienId,
                    notes: $"code={code}; description=; medicareCost=0; billingAmount=0; purchaseAmount=0"));
            }
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync($"/api/liens/cases/liens/get-medicalcode/{lienId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var medicalCodes = payload.RootElement.GetProperty("data")
            .EnumerateArray()
            .ToDictionary(code => code.GetProperty("code").GetString()!);
        medicalCodes["62323"].GetProperty("medicareCost").GetString().Should().Be("100");
        medicalCodes["62323"].GetProperty("billingAmount").GetString().Should().Be("800");
        medicalCodes["62323"].GetProperty("purchaseAmount").GetString().Should().Be("600");
        medicalCodes["64483"].GetProperty("medicareCost").GetString().Should().Be("200");
        medicalCodes["64483"].GetProperty("billingAmount").GetString().Should().Be("900");
        medicalCodes["64483"].GetProperty("purchaseAmount").GetString().Should().Be("700");
        medicalCodes["43239"].GetProperty("medicareCost").GetString().Should().Be("300");
        medicalCodes["43239"].GetProperty("billingAmount").GetString().Should().Be("800");
        medicalCodes["43239"].GetProperty("purchaseAmount").GetString().Should().Be("550");
    }

    [Fact]
    public async Task Move_to_management_sets_today_purchase_date_and_creates_management_facility_provider_info()
    {
        var lienId = await CreateSellingLienAsync();
        Company facility;
        Company medicalProvider;

        using (var setupScope = _factory.Services.CreateScope())
        {
            var setupDb = setupScope.ServiceProvider.GetRequiredService<LiensDbContext>();
            facility = Company.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                CompanyDirectoryReferenceData.MedicalFacilityId,
                "Management Facility",
                SeedHelper.UserId,
                email: "facility@management.test",
                phone: "555-0300");
            medicalProvider = Company.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                CompanyDirectoryReferenceData.MedicalProviderId,
                "Management Provider",
                SeedHelper.UserId);
            setupDb.Companies.AddRange(facility, medicalProvider);

            var lien = await setupDb.Liens.SingleAsync(item => item.Id == lienId);
            lien.SetPurchaseDate(new DateOnly(2025, 1, 1), SeedHelper.UserId);
            await setupDb.SaveChangesAsync();

            setupDb.ServicingItems.Should().NotContain(item =>
                item.LienId == lienId && item.TaskType == "LegacyMedicalFacilityInfo");
            setupDb.Companies.Should().NotContain(item =>
                item.TenantId == SeedHelper.TenantId &&
                item.OrgId == SeedHelper.OrgId &&
                item.CompanyTypeId == CompanyDirectoryReferenceData.FundingCompanyId &&
                item.NormalizedName == Company.NormalizeName("RL Liens1"));
        }

        var caseInformationResponse = await _client.PutAsJsonAsync(
            $"/api/liens/selling/liens/{lienId}/case-information",
            new
            {
                facilityId = facility.Id,
                medicalProviderId = medicalProvider.Id,
            });
        caseInformationResponse.StatusCode.Should().Be(HttpStatusCode.OK, await caseInformationResponse.Content.ReadAsStringAsync());

        (await _client.PutAsJsonAsync($"/api/liens/selling/liens/{lienId}/medical-pricing", new
        {
            askAmount = 1250m,
            billingAmount = 1800m,
            rows = new[]
            {
                new
                {
                    medicalCode = "99213",
                    description = "Office visit",
                    billingAmount = 1800m,
                    medicareCost = 180m,
                    targetSaleAmount = 1250m,
                },
            },
        })).EnsureSuccessStatusCode();

        var todayBeforeMove = DateOnly.FromDateTime(DateTime.UtcNow);
        using var move = new HttpRequestMessage(HttpMethod.Post, $"/api/liens/selling/liens/{lienId}/move-to-management")
        {
            Content = JsonContent.Create(new { reason = "Retained internally" }),
        };
        move.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString());

        var response = await _client.SendAsync(move);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var todayAfterMove = DateOnly.FromDateTime(DateTime.UtcNow);
        Guid managementFacilityId;
        Guid managementProviderId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var lien = await db.Liens.SingleAsync(item => item.Id == lienId);
            lien.PurchaseDate.Should().BeOneOf(todayBeforeMove, todayAfterMove);
            lien.MedicalFacilityCompanyId.Should().Be(facility.Id);
            lien.MedicalProviderCompanyId.Should().Be(medicalProvider.Id);
            lien.FundingCompanyId.Should().BeNull();
            lien.FundingCompanyCompanyId.Should().NotBeNull();
            lien.FacilityId.Should().NotBeNull();
            managementFacilityId = lien.FacilityId!.Value;

            var managementProvider = await db.Contacts.SingleAsync(item =>
                item.TenantId == SeedHelper.TenantId &&
                item.OrgId == SeedHelper.OrgId &&
                item.ContactType == ContactType.Provider &&
                item.Notes == $"SellingCompanyId={medicalProvider.Id}");
            managementProviderId = managementProvider.Id;

            var fundingCompany = await db.Companies.SingleAsync(item =>
                item.Id == lien.FundingCompanyCompanyId &&
                item.CompanyTypeId == CompanyDirectoryReferenceData.FundingCompanyId);
            fundingCompany.Name.Should().Be("RL Liens1");
            fundingCompany.LinkedTenantId.Should().Be(SeedHelper.TenantId);

            db.ServicingItems.Should().ContainSingle(item =>
                item.LienId == lienId &&
                item.CaseId == SeedHelper.CaseId &&
                item.TaskType == "LegacyMedicalFacilityInfo" &&
                item.Notes!.Contains($"facilityId={managementFacilityId}") &&
                item.Notes.Contains("facilityName=Management Facility") &&
                item.Notes.Contains($"medicalProviderId={managementProviderId}") &&
                item.Notes.Contains("medicalProvider=Management Provider") &&
                item.Notes.Contains($"fundingCompanyId={fundingCompany.Id}") &&
                item.Notes.Contains("fundingCompany=RL Liens1"));
        }

        var managementResponse = await _client.GetAsync($"/api/liens/cases/liens/get-facility/{lienId}");
        managementResponse.StatusCode.Should().Be(HttpStatusCode.OK, await managementResponse.Content.ReadAsStringAsync());
        using var managementJson = JsonDocument.Parse(await managementResponse.Content.ReadAsStringAsync());
        var managementFacility = managementJson.RootElement.GetProperty("data");
        managementFacility.GetProperty("facilityId").GetString().Should().Be(managementFacilityId.ToString());
        managementFacility.GetProperty("facility").GetString().Should().Be("Management Facility");
        managementFacility.GetProperty("medicalProviderId").GetString().Should().Be(managementProviderId.ToString());
        managementFacility.GetProperty("medicalProvider").GetString().Should().Be("Management Provider");

        var facilityResponse = await _client.GetAsync($"/api/liens/facilities/{managementFacilityId}");
        facilityResponse.StatusCode.Should().Be(HttpStatusCode.OK, await facilityResponse.Content.ReadAsStringAsync());
        var providerResponse = await _client.GetAsync($"/api/liens/contacts/{managementProviderId}");
        providerResponse.StatusCode.Should().Be(HttpStatusCode.OK, await providerResponse.Content.ReadAsStringAsync());

        var medicalCodesResponse = await _client.GetAsync($"/api/liens/cases/liens/get-medicalcode/{lienId}");
        medicalCodesResponse.StatusCode.Should().Be(HttpStatusCode.OK, await medicalCodesResponse.Content.ReadAsStringAsync());
        using (var medicalCodesJson = JsonDocument.Parse(await medicalCodesResponse.Content.ReadAsStringAsync()))
        {
            var medicalCode = medicalCodesJson.RootElement.GetProperty("data").EnumerateArray().Single();
            medicalCode.GetProperty("code").GetString().Should().Be("99213");
            medicalCode.GetProperty("description").GetString().Should().Be("Office visit");
            medicalCode.GetProperty("medicareCost").GetString().Should().Be("180");
            medicalCode.GetProperty("billingAmount").GetString().Should().Be("1800");
            medicalCode.GetProperty("purchaseAmount").GetString().Should().Be("1250");
        }

        var managementLienResponse = await _client.GetAsync($"/api/liens/cases/liens/get-medical/{lienId}");
        managementLienResponse.StatusCode.Should().Be(HttpStatusCode.OK, await managementLienResponse.Content.ReadAsStringAsync());
        using var managementLienJson = JsonDocument.Parse(await managementLienResponse.Content.ReadAsStringAsync());
        managementLienJson.RootElement.GetProperty("data").GetProperty("fundingCompany").GetString().Should().Be("RL Liens1");

        var secondLienId = await CreateSellingLienAsync();
        using var secondMove = new HttpRequestMessage(HttpMethod.Post, $"/api/liens/selling/liens/{secondLienId}/move-to-management")
        {
            Content = JsonContent.Create(new { reason = "Reuse tenant funding company" }),
        };
        secondMove.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString());
        (await _client.SendAsync(secondMove)).EnsureSuccessStatusCode();

        using var verificationScope = _factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<LiensDbContext>();
        var tenantFundingCompanies = await verificationDb.Companies
            .Where(item =>
                item.TenantId == SeedHelper.TenantId &&
                item.OrgId == SeedHelper.OrgId &&
                item.CompanyTypeId == CompanyDirectoryReferenceData.FundingCompanyId &&
                item.NormalizedName == Company.NormalizeName("RL Liens1"))
            .ToListAsync();
        tenantFundingCompanies.Should().ContainSingle();
        var secondLien = await verificationDb.Liens.SingleAsync(item => item.Id == secondLienId);
        secondLien.FundingCompanyCompanyId.Should().Be(tenantFundingCompanies[0].Id);
    }

    [Fact]
    public async Task Move_to_management_withdraws_a_submitted_lien_before_marking_it_internal()
    {
        var lienId = await CreateSellingLienAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var lien = await db.Liens.FindAsync(lienId);
            lien!.AttachCase(SeedHelper.CaseId, SeedHelper.UserId);
            lien.SetSellingFundingReferences(SeedHelper.FundingCompanyId, null, null, null, SeedHelper.UserId);
            lien!.ListForSale(1000m, SeedHelper.UserId);
            await db.SaveChangesAsync();
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/liens/selling/liens/{lienId}/move-to-management")
        {
            Content = JsonContent.Create(new { reason = "Retained internally" }),
        };
        request.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString());

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var verificationScope = _factory.Services.CreateScope();
        var movedLien = await verificationScope.ServiceProvider.GetRequiredService<LiensDbContext>().Liens.FindAsync(lienId);
        movedLien!.Status.Should().Be(LienStatus.Draft);
        movedLien.SellerStatus.Should().Be(SellingLienStatus.Internal);
        movedLien.WithdrawnAtUtc.Should().NotBeNull();
        movedLien.FundingCompanyId.Should().BeNull();
        movedLien.FundingCompanyCompanyId.Should().NotBeNull();
        var tenantFundingCompany = await verificationScope.ServiceProvider
            .GetRequiredService<LiensDbContext>()
            .Companies.SingleAsync(item => item.Id == movedLien.FundingCompanyCompanyId);
        tenantFundingCompany.Name.Should().Be("RL Liens1");
    }

    [Fact]
    public async Task Move_to_management_creates_a_case_from_lien_information_when_the_lien_has_none()
    {
        var lienId = await CreateSellingLienAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var lien = await db.Liens.FindAsync(lienId);
            lien!.Update(
                lien.LienType,
                lien.OriginalAmount,
                SeedHelper.UserId,
                externalReference: "SELLING-CASE-42",
                subjectFirstName: "Maya",
                subjectLastName: "Santos",
                incidentDate: new DateOnly(2026, 7, 19),
                description: "Retained lien case");
            await db.SaveChangesAsync();
        }

        using var move = new HttpRequestMessage(HttpMethod.Post, $"/api/liens/selling/liens/{lienId}/move-to-management")
        {
            Content = JsonContent.Create(new { reason = "Create a management case" }),
        };
        move.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString());
        (await _client.SendAsync(move)).StatusCode.Should().Be(HttpStatusCode.OK);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var lien = await db.Liens.FindAsync(lienId);
            lien!.CaseId.Should().NotBeNull();
            lien.SellingCaseId.Should().Be(lien.CaseId);
            lien.MovedToManagementAtUtc.Should().NotBeNull();
            var managementCase = await db.Cases.FindAsync(lien.CaseId);
            managementCase!.ClientFirstName.Should().Be("Maya");
            managementCase.ClientLastName.Should().Be("Santos");
            managementCase.ExternalReference.Should().Be("SELLING-CASE-42");
            managementCase.Description.Should().Be("Retained lien case");
        }
    }

    [Fact]
    public async Task Move_to_management_uses_generic_case_name_when_lien_has_no_plaintiff_name()
    {
        var lienId = await CreateSellingLienAsync();

        using var move = new HttpRequestMessage(HttpMethod.Post, $"/api/liens/selling/liens/{lienId}/move-to-management")
        {
            Content = JsonContent.Create(new { reason = "Create generic management case" }),
        };
        move.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString());
        var response = await _client.SendAsync(move);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
        var lien = await db.Liens.FindAsync(lienId);
        var managementCase = await db.Cases.FindAsync(lien!.CaseId);
        managementCase!.ClientFirstName.Should().Be("Jane");
        managementCase.ClientLastName.Should().Be("Doe");
    }

    [Theory]
    [InlineData(SellingLienStatus.Approval)]
    [InlineData(SellingLienStatus.PreparedForSale)]
    public async Task Move_to_management_allows_draft_liens_shown_on_the_pending_tab(string sellerStatus)
    {
        var lienId = await CreateSellingLienAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var lien = await db.Liens.FindAsync(lienId);
            lien!.AttachCase(SeedHelper.CaseId, SeedHelper.UserId);
            lien!.UpdateSellingAnalyticsFields(SeedHelper.UserId, sellerStatus: sellerStatus);
            await db.SaveChangesAsync();
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/liens/selling/liens/{lienId}/move-to-management")
        {
            Content = JsonContent.Create(new { reason = "Retained internally" }),
        };
        request.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString());

        (await _client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Move_to_management_replays_the_same_idempotent_request()
    {
        var lienId = await CreateSellingLienAsync();
        var key = Guid.CreateVersion7().ToString();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var lien = await db.Liens.FindAsync(lienId);
            lien!.AttachCase(SeedHelper.CaseId, SeedHelper.UserId);
            await db.SaveChangesAsync();
        }
        var payload = new { reason = "Retained internally" };

        foreach (var _ in Enumerable.Range(0, 2))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/liens/selling/liens/{lienId}/move-to-management")
            {
                Content = JsonContent.Create(payload),
            };
            request.Headers.Add("Idempotency-Key", key);
            (await _client.SendAsync(request)).EnsureSuccessStatusCode();
        }

        using var verificationScope = _factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<LiensDbContext>();
        var reloadedLien = await verificationDb.Liens.FindAsync(lienId);
        reloadedLien!.CaseId.Should().Be(SeedHelper.CaseId);
        reloadedLien.SellingCaseId.Should().Be(SeedHelper.CaseId);
        verificationDb.LienStatusHistories.Count(item => item.LienId == lienId && item.Description!.Contains("moved to management", StringComparison.OrdinalIgnoreCase)).Should().Be(1);
    }

    [Fact]
    public async Task Move_to_management_rejects_a_lien_case_owned_by_another_organization()
    {
        var lienId = await CreateSellingLienAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var externalCase = Case.Create(
                SeedHelper.TenantId, Guid.CreateVersion7(), "OTHER-1001", "Other", "Client", SeedHelper.UserId);
            db.Cases.Add(externalCase);
            var lien = await db.Liens.FindAsync(lienId);
            lien!.AttachCase(externalCase.Id, SeedHelper.UserId);
            await db.SaveChangesAsync();
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/liens/selling/liens/{lienId}/move-to-management")
        {
            Content = JsonContent.Create(new { reason = "Invalid case organization" }),
        };
        request.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString());

        (await _client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Move_to_management_v2_transfers_complete_case_and_lien_information_to_management()
    {
        var lienId = await CreateSellingLienAsync();
        Company facility;
        Company medicalProvider;
        Company fundingCompany;
        Guid compatibilityItemId;
        using (var setupScope = _factory.Services.CreateScope())
        {
            var setupDb = setupScope.ServiceProvider.GetRequiredService<LiensDbContext>();
            facility = Company.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                CompanyDirectoryReferenceData.MedicalFacilityId,
                "Complete Care Facility",
                SeedHelper.UserId,
                email: "facility@complete-care.test",
                phone: "555-0100");
            medicalProvider = Company.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                CompanyDirectoryReferenceData.MedicalProviderId,
                "Complete Care Provider",
                SeedHelper.UserId);
            fundingCompany = Company.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                CompanyDirectoryReferenceData.FundingCompanyId,
                "Complete Capital",
                SeedHelper.UserId);
            setupDb.Companies.AddRange(facility, medicalProvider, fundingCompany);

            var existingCase = await setupDb.Cases.SingleAsync(item => item.Id == SeedHelper.CaseId);
            existingCase.Update(
                existingCase.ClientFirstName,
                existingCase.ClientLastName,
                SeedHelper.UserId,
                title: existingCase.Title,
                externalReference: "CASE-EXT-100",
                clientDob: existingCase.ClientDob,
                clientPhone: "555-0199",
                clientEmail: "maria.santos@test.local",
                clientAddress: existingCase.ClientAddress,
                dateOfIncident: existingCase.DateOfIncident,
                insuranceCarrier: "Complete Insurance",
                policyNumber: "POL-100",
                claimNumber: "CLM-100",
                description: "Existing case description",
                notes: $"Original case notes{Environment.NewLine}{Environment.NewLine}[legacy-meta]{Environment.NewLine}gender=Female");

            var setupLien = await setupDb.Liens.SingleAsync(item => item.Id == lienId);
            setupLien.SetPurchaseDate(new DateOnly(2026, 6, 20), SeedHelper.UserId);
            var seededCompatibilityItem = ServicingItem.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                $"LMFI-{Guid.CreateVersion7():N}",
                "LegacyMedicalFacilityInfo",
                "Legacy medical facility information",
                "system",
                SeedHelper.UserId,
                caseId: SeedHelper.CaseId,
                lienId: lienId,
                notes: "customField=preserve-me; fundingCompany=Stale funding company");
            compatibilityItemId = seededCompatibilityItem.Id;
            setupDb.ServicingItems.Add(seededCompatibilityItem);
            await setupDb.SaveChangesAsync();
        }

        (await _client.PutAsJsonAsync($"/api/liens/selling/liens/{lienId}/lien-information", new
        {
            sellerStatus = "Pending",
            listingVisibility = "Private",
            initialServiceDate = "2026-07-01",
            endServiceDate = "2026-07-31",
            receivableDueDate = "2026-08-15",
            notes = "Complete lien notes",
        })).EnsureSuccessStatusCode();
        (await _client.PutAsJsonAsync($"/api/liens/selling/liens/{lienId}/case-information", new
        {
            facilityId = facility.Id,
            medicalProviderId = medicalProvider.Id,
            fundingCompanyId = fundingCompany.Id,
        })).EnsureSuccessStatusCode();
        (await _client.PutAsJsonAsync($"/api/liens/selling/liens/{lienId}/medical-pricing", new
        {
            askAmount = 1250m,
            billingAmount = 1800m,
            rows = new[] { new { medicalCode = "99213", description = "Office visit", billingAmount = 1800m, medicareCost = 180m, targetSaleAmount = 1250m } },
        })).EnsureSuccessStatusCode();
        var documentId = Guid.CreateVersion7();
        (await _client.PutAsJsonAsync($"/api/liens/selling/liens/{lienId}/documents", new
        {
            documents = new[] { new { documentId, documentType = "MedicalBill", displayName = "complete-bill.pdf" } },
        })).EnsureSuccessStatusCode();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/liens/selling/liens/{lienId}/move-to-management-v2")
        {
            Content = JsonContent.Create(new
            {
                reason = "Keep internally",
                caseInfo = new
                {
                    clientFirstName = "Maria",
                    clientLastName = "Santos",
                    clientDob = "1990-01-15",
                    clientAddress = "123 Main St",
                    clientCity = "Los Angeles",
                    clientState = "CA",
                    clientZipCode = "90001",
                    isServicing = true,
                    statusLabel = "Pre-demand",
                    accidentTypeId = "MVA",
                    stateOfIncident = "CA",
                    dateOfIncident = "2026-08-01",
                    lawFirmId = SeedHelper.LawFirmId.ToString(),
                    caseManagerId = SeedHelper.LeadContactId.ToString(),
                    notes = "Brief case notes",
                },
            }),
        };
        request.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString());

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        payload.RootElement.GetProperty("caseCreated").GetBoolean().Should().BeFalse();
        var caseId = payload.RootElement.GetProperty("caseId").GetGuid();
        caseId.Should().Be(SeedHelper.CaseId);
        payload.RootElement.GetProperty("sellingCaseId").GetGuid().Should().Be(caseId);
        payload.RootElement.GetProperty("sellerStatus").GetString().Should().Be(SellingLienStatus.Internal);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
        var lien = await db.Liens.FindAsync(lienId);
        var managementCase = await db.Cases.FindAsync(caseId);
        lien!.CaseId.Should().Be(caseId);
        lien.SellingCaseId.Should().Be(caseId);
        lien.MovedToManagementAtUtc.Should().NotBeNull();
        lien.SellerStatus.Should().Be(SellingLienStatus.Internal);
        lien.Status.Should().Be(LienStatus.Draft);
        lien.IsServicing.Should().Be("true");
        lien.PurchaseDate.Should().Be(new DateOnly(2026, 6, 20));
        lien.InitialServiceDate.Should().Be(new DateOnly(2026, 7, 1));
        lien.EndServiceDate.Should().Be(new DateOnly(2026, 7, 31));
        lien.ReceivableDueDate.Should().Be(new DateOnly(2026, 8, 15));
        lien.Notes.Should().Be("Complete lien notes");
        lien.MedicalFacilityCompanyId.Should().Be(facility.Id);
        lien.MedicalProviderCompanyId.Should().Be(medicalProvider.Id);
        lien.FundingCompanyCompanyId.Should().Be(fundingCompany.Id);
        lien.FacilityId.Should().NotBeNull();
        var managementFacilityId = lien.FacilityId!.Value;
        var managementProviderId = (await db.Contacts.SingleAsync(item =>
            item.TenantId == SeedHelper.TenantId &&
            item.OrgId == SeedHelper.OrgId &&
            item.ContactType == ContactType.Provider &&
            item.Notes == $"SellingCompanyId={medicalProvider.Id}")).Id;
        managementCase!.ClientFirstName.Should().Be("Maria");
        managementCase.ClientLastName.Should().Be("Santos");
        managementCase.ClientDob.Should().Be(new DateOnly(1990, 1, 15));
        managementCase.ClientPhone.Should().Be("555-0199");
        managementCase.ClientEmail.Should().Be("maria.santos@test.local");
        managementCase.ClientAddress.Should().Be("123 Main St, Los Angeles, CA, 90001");
        managementCase.ClientAddressLine1.Should().Be("123 Main St");
        managementCase.ClientCity.Should().Be("Los Angeles");
        managementCase.ClientState.Should().Be("CA");
        managementCase.ClientPostalCode.Should().Be("90001");
        managementCase.DateOfIncident.Should().Be(new DateOnly(2026, 8, 1));
        managementCase.IncidentState.Should().Be("CA");
        managementCase.InsuranceCarrier.Should().Be("Complete Insurance");
        managementCase.PolicyNumber.Should().Be("POL-100");
        managementCase.ClaimNumber.Should().Be("CLM-100");
        managementCase.Notes.Should().Contain("Brief case notes");
        managementCase.Notes.Should().Contain("gender=Female");
        managementCase.Notes.Should().Contain("accidentState=CA");
        db.ServicingItems.Should().Contain(item =>
            item.LienId == lienId &&
            item.CaseId == caseId &&
            item.TaskType == "LegacyMedicalCode" &&
            item.Notes!.Contains("billingAmount=1800") &&
            item.Notes.Contains("purchaseAmount=1250"));
        db.ServicingItems.Should().Contain(item =>
            item.LienId == lienId &&
            item.CaseId == caseId &&
            item.TaskType == "SellingDocumentReference" &&
            item.Notes!.Contains(documentId.ToString()));
        db.ServicingItems.Should().ContainSingle(item =>
            item.LienId == lienId &&
            item.CaseId == caseId &&
            item.TaskType == "LegacyMedicalFacilityInfo" &&
            item.Notes!.Contains($"facilityId={managementFacilityId}") &&
            item.Notes.Contains("facilityName=Complete Care Facility") &&
            item.Notes.Contains($"medicalProviderId={managementProviderId}") &&
            item.Notes.Contains("medicalProvider=Complete Care Provider") &&
            item.Notes.Contains($"fundingCompanyId={fundingCompany.Id}") &&
            item.Notes.Contains("fundingCompany=Complete Capital") &&
            item.Notes.Contains("receivableDueDate=2026-08-15"));
        var compatibilityItem = await db.ServicingItems.SingleAsync(item =>
            item.LienId == lienId && item.TaskType == "LegacyMedicalFacilityInfo");
        compatibilityItem.Id.Should().Be(compatibilityItemId);
        compatibilityItem.Notes.Should().Contain("customField=preserve-me");
        compatibilityItem.Notes.Should().NotContain("fundingCompany=Stale funding company");

        var managementCaseResponse = await _client.GetAsync($"/api/liens/cases/{caseId}");
        managementCaseResponse.StatusCode.Should().Be(HttpStatusCode.OK, await managementCaseResponse.Content.ReadAsStringAsync());
        using var managementCaseJson = JsonDocument.Parse(await managementCaseResponse.Content.ReadAsStringAsync());
        managementCaseJson.RootElement.GetProperty("clientCity").GetString().Should().Be("Los Angeles");
        managementCaseJson.RootElement.GetProperty("clientState").GetString().Should().Be("CA");
        managementCaseJson.RootElement.GetProperty("clientZipcode").GetString().Should().Be("90001");
        managementCaseJson.RootElement.GetProperty("statusLabel").GetString().Should().Be("Pre-demand");
        managementCaseJson.RootElement.GetProperty("stateOfIncident").GetString().Should().Be("CA");
        managementCaseJson.RootElement.GetProperty("lawFirmId").GetString().Should().Be(SeedHelper.LawFirmId.ToString());
        managementCaseJson.RootElement.GetProperty("accidentTypeId").GetString().Should().Be("MVA");

        var managementLienResponse = await _client.GetAsync($"/api/liens/cases/liens/get-medical/{lienId}");
        managementLienResponse.StatusCode.Should().Be(HttpStatusCode.OK, await managementLienResponse.Content.ReadAsStringAsync());
        using var managementLienJson = JsonDocument.Parse(await managementLienResponse.Content.ReadAsStringAsync());
        var managementLien = managementLienJson.RootElement.GetProperty("data");
        managementLien.GetProperty("note").GetString().Should().Be("Complete lien notes");
        managementLien.GetProperty("fundingCompanyId").GetString().Should().Be(fundingCompany.Id.ToString());
        managementLien.GetProperty("fundingCompany").GetString().Should().Be("Complete Capital");

        var managementFacilityResponse = await _client.GetAsync($"/api/liens/cases/liens/get-facility/{lienId}");
        managementFacilityResponse.StatusCode.Should().Be(HttpStatusCode.OK, await managementFacilityResponse.Content.ReadAsStringAsync());
        using var managementFacilityJson = JsonDocument.Parse(await managementFacilityResponse.Content.ReadAsStringAsync());
        var managementFacility = managementFacilityJson.RootElement.GetProperty("data");
        managementFacility.GetProperty("facilityId").GetString().Should().Be(managementFacilityId.ToString());
        managementFacility.GetProperty("facility").GetString().Should().Be("Complete Care Facility");
        managementFacility.GetProperty("medicalProviderId").GetString().Should().Be(managementProviderId.ToString());
        managementFacility.GetProperty("medicalProvider").GetString().Should().Be("Complete Care Provider");

        (await _client.GetAsync($"/api/liens/facilities/{managementFacilityId}"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await _client.GetAsync($"/api/liens/contacts/{managementProviderId}"))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var medicalCodesResponse = await _client.GetAsync($"/api/liens/cases/liens/get-medicalcode/{lienId}");
        medicalCodesResponse.StatusCode.Should().Be(HttpStatusCode.OK, await medicalCodesResponse.Content.ReadAsStringAsync());
        using var medicalCodesJson = JsonDocument.Parse(await medicalCodesResponse.Content.ReadAsStringAsync());
        var medicalCode = medicalCodesJson.RootElement.GetProperty("data").EnumerateArray().Single();
        medicalCode.GetProperty("code").GetString().Should().Be("99213");
        medicalCode.GetProperty("description").GetString().Should().Be("Office visit");
        medicalCode.GetProperty("medicareCost").GetString().Should().Be("180");
        medicalCode.GetProperty("billingAmount").GetString().Should().Be("1800");
        medicalCode.GetProperty("purchaseAmount").GetString().Should().Be("1250");
    }

    [Fact]
    public async Task Move_to_management_v2_reuses_duplicate_case_and_still_processes_lien()
    {
        var lienId = await CreateSellingLienAsync();
        Guid documentReferenceId;
        var existingCase = Case.Create(
            SeedHelper.TenantId,
            SeedHelper.OrgId,
            $"CASE-{Guid.CreateVersion7():N}"[..15].ToUpperInvariant(),
            "Maria",
            "Santos",
            SeedHelper.UserId,
            clientDob: new DateOnly(1990, 1, 15),
            dateOfIncident: new DateOnly(2026, 8, 1));

        using (var setupScope = _factory.Services.CreateScope())
        {
            var setupDb = setupScope.ServiceProvider.GetRequiredService<LiensDbContext>();
            setupDb.Cases.Add(existingCase);
            var detachedLien = await setupDb.Liens.SingleAsync(item => item.Id == lienId);
            var seededDocumentReference = ServicingItem.Create(
                SeedHelper.TenantId,
                SeedHelper.OrgId,
                $"SDR-{Guid.CreateVersion7():N}",
                "SellingDocumentReference",
                "duplicate-case-document.pdf",
                "Selling",
                SeedHelper.UserId,
                caseId: detachedLien.CaseId,
                lienId: lienId,
                notes: $"documentId={Guid.CreateVersion7()}");
            documentReferenceId = seededDocumentReference.Id;
            setupDb.ServicingItems.Add(seededDocumentReference);
            detachedLien.DetachCase(SeedHelper.UserId);
            await setupDb.SaveChangesAsync();
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/liens/selling/liens/{lienId}/move-to-management-v2")
        {
            Content = JsonContent.Create(new
            {
                caseInfo = new
                {
                    clientFirstName = "maria",
                    clientLastName = "santos",
                    clientDob = "1990-01-15",
                    dateOfIncident = "2026-08-01",
                    statusLabel = "Pre-demand",
                    accidentTypeId = "MVA",
                    stateOfIncident = "CA",
                    lawFirmId = SeedHelper.LawFirmId.ToString(),
                },
            }),
        };
        request.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString());

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        payload.RootElement.GetProperty("caseCreated").GetBoolean().Should().BeFalse();
        payload.RootElement.GetProperty("caseId").GetGuid().Should().Be(existingCase.Id);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
        var lien = await db.Liens.FindAsync(lienId);
        lien!.CaseId.Should().Be(existingCase.Id);
        lien.SellingCaseId.Should().Be(existingCase.Id);
        lien.MovedToManagementAtUtc.Should().NotBeNull();
        lien.SellerStatus.Should().Be(SellingLienStatus.Internal);
        var documentReference = await db.ServicingItems.FindAsync(documentReferenceId);
        documentReference!.CaseId.Should().Be(existingCase.Id);
        var duplicateCount = await db.Cases.CountAsync(c =>
            c.TenantId == SeedHelper.TenantId &&
            c.OrgId == SeedHelper.OrgId &&
            c.ClientDob == new DateOnly(1990, 1, 15) &&
            c.DateOfIncident == new DateOnly(2026, 8, 1) &&
            c.ClientFirstName.ToLower() == "maria" &&
            c.ClientLastName.ToLower() == "santos");
        duplicateCount.Should().Be(1);
    }

    [Fact]
    public async Task Move_to_management_v2_requires_case_info_required_fields_when_case_info_is_present()
    {
        var lienId = await CreateSellingLienAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/liens/selling/liens/{lienId}/move-to-management-v2")
        {
            Content = JsonContent.Create(new
            {
                caseInfo = new
                {
                    clientFirstName = "Maria",
                    clientLastName = "Santos",
                },
            }),
        };
        request.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString());

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
        var lien = await db.Liens.FindAsync(lienId);
        lien!.CaseId.Should().Be(SeedHelper.CaseId);
        lien.SellerStatus.Should().Be(SellingLienStatus.Pending);
    }

    private async Task<Guid> PrepareSellingLienAsync(Guid buyerCompanyId, Guid buyerContactId, string? messageToBuyer = null)
    {
        var lienId = await CreateSellingLienAsync();
        (await _client.PutAsJsonAsync($"/api/liens/selling/liens/{lienId}/lien-information", new
        {
            sellerStatus = "Pending", initialServiceDate = "2026-07-19", listingVisibility = "Private",
        })).EnsureSuccessStatusCode();
        (await _client.PutAsJsonAsync($"/api/liens/selling/liens/{lienId}/case-information", new
        {
            fundingCompanyId = SeedHelper.FundingCompanyId,
            fundingCompanyContactId = SeedHelper.FundingCompanyId,
            caseId = SeedHelper.CaseId,
        })).EnsureSuccessStatusCode();
        (await _client.PutAsJsonAsync($"/api/liens/selling/liens/{lienId}/medical-pricing", new
        {
            askAmount = 1250m, billingAmount = 1800m,
            rows = new[] { new { medicalCode = "99213", billingAmount = 600m, medicareCost = 180m, targetSaleAmount = 350m } },
        })).EnsureSuccessStatusCode();
        (await _client.PutAsJsonAsync($"/api/liens/selling/liens/{lienId}/documents", new
        {
            documents = new[] { new { documentId = Guid.CreateVersion7(), documentType = "MedicalBill", displayName = "bill.pdf" } },
        })).EnsureSuccessStatusCode();
        using var prepare = new HttpRequestMessage(HttpMethod.Post, $"/api/liens/selling/liens/{lienId}/prepare-sale")
        {
            Content = JsonContent.Create(new { buyerFundingCompanyId = buyerCompanyId, buyerContactId, askAmount = 1250m, listingVisibility = "Private", messageToBuyer }),
        };
        prepare.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString());
        (await _client.SendAsync(prepare)).EnsureSuccessStatusCode();
        return lienId;
    }

    private async Task<(Guid BuyerCompanyId, Guid BuyerContactId)> SeedConfirmSaleContactsAsync(
        string buyerEmail,
        string sellerEmail)
    {
        var buyerOrgId = Guid.CreateVersion7();
        var buyerCompanyId = Guid.CreateVersion7();
        var buyerContactId = Guid.CreateVersion7();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();

        var buyerCompany = Contact.Create(
            SeedHelper.TenantId,
            buyerOrgId,
            ContactType.FundingCompany,
            "Buyer",
            "Capital",
            SeedHelper.UserId,
            organization: "Buyer Capital LLC");
        SetId(buyerCompany, buyerCompanyId);

        var buyerContact = Contact.Create(
            SeedHelper.TenantId,
            buyerOrgId,
            ContactType.Lead,
            "Buyer",
            "Reviewer",
            SeedHelper.UserId,
            organization: "Buyer Capital LLC",
            email: buyerEmail);
        SetId(buyerContact, buyerContactId);

        var sellerContact = Contact.Create(
            SeedHelper.TenantId,
            SeedHelper.OrgId,
            ContactType.LawFirm,
            "Seller",
            "Representative",
            SeedHelper.UserId,
            organization: "Seller Law LLP",
            email: sellerEmail);

        db.Contacts.AddRange(buyerCompany, buyerContact, sellerContact);
        await db.SaveChangesAsync();

        return (buyerCompanyId, buyerContactId);
    }

    private async Task<(string Token, Guid LienId)> SeedPublicAccessLinkAsync(string? externalReference = null)
    {
        var token = Convert.ToHexString(Guid.NewGuid().ToByteArray());
        var buyerOrgId = Guid.CreateVersion7();
        var buyerContactId = Guid.CreateVersion7();
        var lienId = Guid.CreateVersion7();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
        var buyer = Contact.Create(SeedHelper.TenantId, buyerOrgId, ContactType.FundingCompany, "Public", "Buyer", SeedHelper.UserId, email: "public.buyer@test.local");
        SetId(buyer, buyerContactId);
        var lien = Lien.Create(
            SeedHelper.TenantId,
            SeedHelper.OrgId,
            $"PUB-{Guid.CreateVersion7():N}"[..15],
            LienType.MedicalLien,
            900m,
            SeedHelper.UserId,
            externalReference: externalReference);
        SetId(lien, lienId);
        lien.ListForSale(450m, SeedHelper.UserId);
        var accessLink = SellingBuyerAccessLink.Create(
            SeedHelper.TenantId, lienId, SeedHelper.OrgId, buyerOrgId, buyerContactId, token,
            SellingAccessLinkPurposes.ConfirmSaleBuyerResponse, "/api/liens/selling/public/{token}", Guid.CreateVersion7().ToString(), DateTime.UtcNow.AddDays(1), SeedHelper.UserId);
        db.Contacts.Add(buyer);
        db.Liens.Add(lien);
        db.SellingBuyerAccessLinks.Add(accessLink);
        await db.SaveChangesAsync();
        return (token, lienId);
    }

    private async Task<HttpResponseMessage> PostPublicAsync(string token, string action, string idempotencyKey, object request)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"/api/liens/selling/public/{token}/{action}")
        {
            Content = JsonContent.Create(request),
        };
        message.Headers.Add("Idempotency-Key", idempotencyKey);
        return await _client.SendAsync(message);
    }

    private static void SetId<T>(T entity, Guid id) where T : class
        => typeof(T).GetProperty("Id", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!.SetValue(entity, id);

    private static void AssertSellingCaseLookupNames(
        JsonElement item,
        Guid accidentTypeId,
        Guid lawFirmId,
        Guid caseManagerId)
    {
        item.GetProperty("accidentTypeId").GetString().Should().Be(accidentTypeId.ToString());
        item.GetProperty("accidentTypeName").GetString().Should().Be("Motor Vehicle Accident");
        item.GetProperty("handlingLawFirmId").GetGuid().Should().Be(lawFirmId);
        item.GetProperty("handlingLawFirmName").GetString().Should().Be("Selling Case Law LLP");
        item.GetProperty("caseManagerId").GetGuid().Should().Be(caseManagerId);
        item.GetProperty("caseManagerName").GetString().Should().Be("Casey Manager");
    }

    private async Task<Guid> FinalizeSellingCaseAsync(string firstName, string lastName)
    {
        var draftResponse = await _client.PostAsJsonAsync("/api/liens/selling/case-drafts", new { });
        draftResponse.EnsureSuccessStatusCode();
        using var draftJson = JsonDocument.Parse(await draftResponse.Content.ReadAsStringAsync());
        var draftId = draftJson.RootElement.GetProperty("draftId").GetGuid();

        var finalizeResponse = await _client.PostAsJsonAsync(
            $"/api/liens/selling/case-drafts/{draftId}/plaintiff",
            new { firstName, lastName });
        finalizeResponse.EnsureSuccessStatusCode();
        using var finalizeJson = JsonDocument.Parse(await finalizeResponse.Content.ReadAsStringAsync());
        return finalizeJson.RootElement.GetProperty("caseId").GetGuid();
    }

    private async Task<Guid> CreateSellingLienAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/liens/selling/liens")
        {
            Content = JsonContent.Create(new { caseId = SeedHelper.CaseId, sellerStatus = "Pending", source = "Single" }),
        };
        request.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString());
        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("lienId").GetGuid();
    }
}
