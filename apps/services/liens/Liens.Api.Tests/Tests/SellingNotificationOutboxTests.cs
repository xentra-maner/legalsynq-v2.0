using Liens.Domain.Entities;
using Liens.Api.Tests.Helpers;
using Liens.Domain.Enums;
using Liens.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;

namespace Liens.Api.Tests.Tests;

public sealed class SellingNotificationOutboxTests : IClassFixture<LiensApiFactory>, IAsyncLifetime
{
    private readonly LiensApiFactory _factory;
    private HttpClient _client = null!;

    public SellingNotificationOutboxTests(LiensApiFactory factory) => _factory = factory;

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
    public void Create_StoresOnlyGenericPresentationAndRecipient()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var occurredAt = DateTime.UtcNow;

        var item = SellingNotificationOutboxItem.Create(
            tenantId,
            userId,
            "lien.offer.message.created",
            "message",
            "New Message",
            "A buyer sent a new message regarding lien LN-100.",
            "Buyer",
            "B",
            occurredAt,
            $"selling:message:{Guid.NewGuid():N}:{userId:N}");

        Assert.Equal(tenantId, item.TenantId);
        Assert.Equal(userId, item.RecipientUserId);
        Assert.Equal("message", item.Category);
        Assert.DoesNotContain("medical", item.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecordFailure_DeadLettersAfterMaximumAttempts()
    {
        var item = SellingNotificationOutboxItem.Create(
            Guid.NewGuid(), Guid.NewGuid(), "lien.offer.submitted", "lien",
            "Offer Submitted", "Your offer was submitted.", "Synq Selling", "SS",
            DateTime.UtcNow, Guid.NewGuid().ToString("N"));

        for (var attempt = 0; attempt < SellingNotificationOutboxItem.MaximumAttempts; attempt++)
            item.RecordFailure("temporary", retryable: true, DateTime.UtcNow);

        Assert.Equal(SellingNotificationOutboxItem.MaximumAttempts, item.AttemptCount);
        Assert.NotNull(item.DeadLetteredAtUtc);
    }

    [Fact]
    public void LienOffer_TracksPlatformSubmitterSeparately()
    {
        var contactId = Guid.NewGuid();
        var platformUserId = Guid.NewGuid();

        var offer = LienOffer.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            100m, contactId, submittedByPlatformUserId: platformUserId);

        Assert.Equal(contactId, offer.CreatedByUserId);
        Assert.Equal(platformUserId, offer.SubmittedByPlatformUserId);
    }

    [Fact]
    public async Task Accepting_offer_enqueues_inbox_events_for_accepted_and_competing_platform_users()
    {
        var acceptedRecipient = Guid.NewGuid();
        var rejectedRecipient = Guid.NewGuid();
        Guid acceptedOfferId;
        Guid rejectedOfferId;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LiensDbContext>();
            var lien = await db.Liens.SingleAsync(item => item.Id == SeedHelper.LienId);
            lien.TransitionStatus(LienStatus.Offered, SeedHelper.UserId);

            var acceptedOffer = LienOffer.Create(
                SeedHelper.TenantId, lien.Id, Guid.NewGuid(), SeedHelper.OrgId,
                600m, acceptedRecipient, submittedByPlatformUserId: acceptedRecipient);
            var competingOffer = LienOffer.Create(
                SeedHelper.TenantId, lien.Id, Guid.NewGuid(), SeedHelper.OrgId,
                550m, rejectedRecipient, submittedByPlatformUserId: rejectedRecipient);
            acceptedOfferId = acceptedOffer.Id;
            rejectedOfferId = competingOffer.Id;
            db.LienOffers.AddRange(acceptedOffer, competingOffer);
            await db.SaveChangesAsync();
        }

        var response = await _client.PostAsync($"/api/liens/offers/{acceptedOfferId}/accept", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var verificationScope = _factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<LiensDbContext>();
        var outboxItems = await verificationDb.SellingNotificationOutboxItems
            .Where(item => item.EventKey == "lien.offer.accepted" || item.EventKey == "lien.offer.rejected")
            .ToListAsync();

        var accepted = Assert.Single(outboxItems, item => item.EventKey == "lien.offer.accepted");
        Assert.Equal(acceptedRecipient, accepted.RecipientUserId);
        Assert.Contains($"offer:{acceptedOfferId:N}:accepted:{acceptedRecipient:N}", accepted.IdempotencyKey);

        var rejected = Assert.Single(outboxItems, item => item.EventKey == "lien.offer.rejected");
        Assert.Equal(rejectedRecipient, rejected.RecipientUserId);
        Assert.Contains($"offer:{rejectedOfferId:N}:rejected:{rejectedRecipient:N}", rejected.IdempotencyKey);
        Assert.DoesNotContain("medical", rejected.Description, StringComparison.OrdinalIgnoreCase);
    }
}
