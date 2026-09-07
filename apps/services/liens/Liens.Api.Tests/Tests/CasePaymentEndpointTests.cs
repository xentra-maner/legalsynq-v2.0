using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Liens.Api.Tests.Helpers;
using Liens.Domain;
using Liens.Domain.Entities;
using Liens.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Liens.Api.Tests.Tests;

public sealed class CasePaymentEndpointTests : IClassFixture<LiensApiFactory>, IAsyncLifetime
{
    private readonly LiensApiFactory _factory;
    private HttpClient _client = null!;

    public CasePaymentEndpointTests(LiensApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        await SeedHelper.SeedAsync(scope.ServiceProvider);
        scope.ServiceProvider.GetRequiredService<CapturingAuditPublisher>().Clear();

        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", JwtTokenHelper.CreateFullAccessToken(SeedHelper.TenantId, SeedHelper.UserId));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task RecordPayment_is_atomic_idempotent_and_visible_in_case_ledger()
    {
        const string idempotencyKey = "case-payment-create-1";
        var request = CreateRequest(400m);

        var first = await PostPaymentAsync(request, idempotencyKey);
        first.StatusCode.Should().Be(HttpStatusCode.Created, await first.Content.ReadAsStringAsync());
        var firstBody = await first.Content.ReadFromJsonAsync<JsonDocument>();
        var receiptId = firstBody!.RootElement.GetProperty("receiptId").GetGuid();

        var replay = await PostPaymentAsync(request, idempotencyKey);
        replay.StatusCode.Should().Be(HttpStatusCode.Created, await replay.Content.ReadAsStringAsync());
        var replayBody = await replay.Content.ReadFromJsonAsync<JsonDocument>();
        replayBody!.RootElement.GetProperty("receiptId").GetGuid().Should().Be(receiptId);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            (await db.SettlementPaymentDetails.CountAsync()).Should().Be(2);
            var created = await db.SettlementPaymentDetails.SingleAsync(item => item.ReceiptId == receiptId);
            created.PaymentMethod.Should().Be("ACH");
            created.DetailsContext.Should().Be("Attorney trust account");
            created.PostingStatus.Should().Be(SettlementPaymentDetail.PostedStatus);
        }

        var list = await _client.GetAsync($"/api/liens/cases/{SeedHelper.CaseId}/payments?page=1&pageSize=10");
        list.StatusCode.Should().Be(HttpStatusCode.OK, await list.Content.ReadAsStringAsync());
        var listBody = await list.Content.ReadFromJsonAsync<JsonDocument>();
        listBody!.RootElement.GetProperty("summary").GetProperty("totalPaid").GetDecimal().Should().Be(4_900m);
        listBody.RootElement.GetProperty("summary").GetProperty("remainingBalance").GetDecimal().Should().Be(100m);
        listBody.RootElement.GetProperty("totalCount").GetInt32().Should().Be(2);

        _factory.Services.GetRequiredService<CapturingAuditPublisher>().Events
            .Should().ContainSingle(item => item.EventType == "lien.payment.recorded");
    }

    [Fact]
    public async Task VoidPayment_voids_receipt_and_excludes_it_from_posted_total()
    {
        var create = await PostPaymentAsync(CreateRequest(400m), "case-payment-create-for-void");
        var body = await create.Content.ReadFromJsonAsync<JsonDocument>();
        var paymentId = body!.RootElement.GetProperty("allocations")[0].GetProperty("id").GetGuid();

        using var voidRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/liens/cases/{SeedHelper.CaseId}/payments/{paymentId}/void")
        {
            Content = JsonContent.Create(new { reason = "Duplicate bank import" }),
        };
        voidRequest.Headers.Add("Idempotency-Key", "case-payment-void-1");
        var voidResponse = await _client.SendAsync(voidRequest);
        voidResponse.StatusCode.Should().Be(HttpStatusCode.OK, await voidResponse.Content.ReadAsStringAsync());

        var list = await _client.GetFromJsonAsync<JsonDocument>(
            $"/api/liens/cases/{SeedHelper.CaseId}/payments?page=1&pageSize=10");
        list!.RootElement.GetProperty("summary").GetProperty("totalPaid").GetDecimal().Should().Be(4_500m);
        list.RootElement.GetProperty("items").EnumerateArray()
            .Should().Contain(item =>
                item.GetProperty("id").GetGuid() == paymentId &&
                item.GetProperty("postingStatus").GetString() == SettlementPaymentDetail.VoidedStatus);

        var legacyPayments = await _client.GetFromJsonAsync<JsonDocument>(
            $"/api/liens/settlement/payments/case/{SeedHelper.CaseId}");
        legacyPayments!.RootElement.GetArrayLength().Should().Be(1);
        legacyPayments.RootElement[0].GetProperty("id").GetGuid().Should().Be(SeedHelper.PaymentId);
    }

    [Fact]
    public async Task RecordPayment_rejects_overpayment_without_writing_rows()
    {
        var response = await PostPaymentAsync(CreateRequest(501m), "case-payment-overpayment");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
        body!.RootElement.GetProperty("error").GetProperty("message").GetString()
            .Should().Be("Payment amount cannot exceed the available balance of $500.00.");
        body!.RootElement.GetProperty("error").GetProperty("fields").GetProperty("amount")[0].GetString()
            .Should().Be("Payment amount cannot exceed the available balance of $500.00.");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
        (await db.SettlementPaymentDetails.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task RecordPayment_rejects_idempotency_key_reuse_with_different_body()
    {
        const string key = "case-payment-reused-key";
        var first = await PostPaymentAsync(CreateRequest(100m), key);
        first.StatusCode.Should().Be(HttpStatusCode.Created, await first.Content.ReadAsStringAsync());

        var conflicting = await PostPaymentAsync(CreateRequest(101m), key);
        conflicting.StatusCode.Should().Be(HttpStatusCode.Conflict, await conflicting.Content.ReadAsStringAsync());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
        (await db.SettlementPaymentDetails.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task RecordPayment_requires_lien_settle_permission()
    {
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            JwtTokenHelper.CreateToken(
                SeedHelper.TenantId,
                SeedHelper.UserId,
                [LiensPermissions.LienRead]));

        var response = await PostPaymentAsync(CreateRequest(400m), "case-payment-permission");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private async Task<HttpResponseMessage> PostPaymentAsync(object request, string idempotencyKey)
    {
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/liens/cases/{SeedHelper.CaseId}/payments")
        {
            Content = JsonContent.Create(request),
        };
        message.Headers.Add("Idempotency-Key", idempotencyKey);
        return await _client.SendAsync(message);
    }

    private static object CreateRequest(decimal amount) => new
    {
        amount,
        paymentDate = "2026-08-24",
        paymentMethod = "ACH",
        referenceNumber = "ACH-9001",
        detailsContext = "Attorney trust account",
        notes = "Case payment test",
        settlementType = "by_attorney",
        settlementStatus = "partial_payment",
        lienStatus = "Active",
        allocations = new[] { new { lienId = SeedHelper.LienId, amount } },
    };
}
