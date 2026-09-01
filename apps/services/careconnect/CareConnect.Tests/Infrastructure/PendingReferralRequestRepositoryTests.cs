using CareConnect.Domain;
using CareConnect.Infrastructure.Data;
using CareConnect.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CareConnect.Tests.Infrastructure;

public sealed class PendingReferralRequestRepositoryTests
{
    [Fact]
    public async Task SearchAsync_NoStatusFilter_ReturnsPendingConvertedAndCancelledForLawFirm()
    {
        await using var db = CreateDbContext();
        var tenantId = Guid.CreateVersion7();
        var lawFirmOrgId = Guid.CreateVersion7();
        var attribution = BuildAttribution(tenantId);
        var attributionId = attribution.Id;

        var pending = BuildRequest(tenantId, lawFirmOrgId, attributionId, "Pending");
        var converted = BuildRequest(tenantId, lawFirmOrgId, attributionId, "Converted");
        converted.MarkConverted(Guid.CreateVersion7(), convertedByUserId: null);
        var cancelled = BuildRequest(tenantId, lawFirmOrgId, attributionId, "Cancelled");
        cancelled.MarkCancelled(cancelledByUserId: null);

        db.ReferralAttributions.Add(attribution);
        db.PendingReferralRequests.AddRange(pending, converted, cancelled);
        await db.SaveChangesAsync();

        var repository = new PendingReferralRequestRepository(db);

        var result = await repository.SearchAsync(tenantId, lawFirmOrgId, status: null, page: 1, pageSize: 20);

        Assert.Equal(3, result.TotalCount);
        Assert.Contains(result.Items, r => r.Status == PendingReferralRequest.Statuses.PendingReview);
        Assert.Contains(result.Items, r => r.Status == PendingReferralRequest.Statuses.Converted);
        Assert.Contains(result.Items, r => r.Status == PendingReferralRequest.Statuses.Cancelled);
    }

    [Fact]
    public async Task SearchForAttributionAsync_CancelledStatusFilter_ReturnsDeclinedRequestForRepresentative()
    {
        await using var db = CreateDbContext();
        var tenantId = Guid.CreateVersion7();
        var lawFirmOrgId = Guid.CreateVersion7();
        var attribution = BuildAttribution(tenantId);
        var attributionId = attribution.Id;

        var pending = BuildRequest(tenantId, lawFirmOrgId, attributionId, "Pending");
        var cancelled = BuildRequest(tenantId, lawFirmOrgId, attributionId, "Cancelled");
        cancelled.MarkCancelled(cancelledByUserId: null);

        db.ReferralAttributions.Add(attribution);
        db.PendingReferralRequests.AddRange(pending, cancelled);
        await db.SaveChangesAsync();

        var repository = new PendingReferralRequestRepository(db);

        var result = await repository.SearchForAttributionAsync(
            tenantId,
            attributionId,
            PendingReferralRequest.Statuses.Cancelled,
            createdFrom: null,
            createdTo: null,
            page: 1,
            pageSize: 20);

        var item = Assert.Single(result.Items);
        Assert.Equal(PendingReferralRequest.Statuses.Cancelled, item.Status);
        Assert.Equal(1, result.TotalCount);
    }

    [Fact]
    public async Task SearchForAttributionAsync_DateOnlyCreatedTo_IncludesEntireDay()
    {
        await using var db = CreateDbContext();
        var tenantId = Guid.CreateVersion7();
        var lawFirmOrgId = Guid.CreateVersion7();
        var attribution = BuildAttribution(tenantId);
        var attributionId = attribution.Id;

        var inRange = BuildRequest(tenantId, lawFirmOrgId, attributionId, "Aug31");
        SetCreatedAtUtc(inRange, new DateTime(2026, 8, 31, 17, 30, 0, DateTimeKind.Utc));
        var outOfRange = BuildRequest(tenantId, lawFirmOrgId, attributionId, "Sep1");
        SetCreatedAtUtc(outOfRange, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));

        db.ReferralAttributions.Add(attribution);
        db.PendingReferralRequests.AddRange(inRange, outOfRange);
        await db.SaveChangesAsync();

        var repository = new PendingReferralRequestRepository(db);

        var result = await repository.SearchForAttributionAsync(
            tenantId,
            attributionId,
            status: null,
            createdFrom: new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            createdTo: new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc),
            page: 1,
            pageSize: 20);

        var item = Assert.Single(result.Items);
        Assert.Equal("JaneAug31", item.ClientFirstName);
        Assert.Equal(1, result.TotalCount);
    }

    private static PendingReferralRequest BuildRequest(
        Guid tenantId,
        Guid lawFirmOrgId,
        Guid attributionId,
        string suffix)
        => PendingReferralRequest.Create(
            tenantId: tenantId,
            lawFirmOrganizationId: lawFirmOrgId,
            referralAttributionId: attributionId,
            clientFirstName: $"Jane{suffix}",
            clientLastName: "Doe",
            clientDob: null,
            clientPhone: "555-0100",
            clientEmail: "jane@example.com",
            caseNumber: $"CASE-{suffix}",
            requestedService: "Physical Therapy",
            urgency: Referral.ValidUrgencies.Normal,
            treatmentTypeId: null,
            dateOfAccident: null,
            notes: null,
            lienCompanyName: null,
            lienCompanyEmail: null);

    private static ReferralAttribution BuildAttribution(Guid tenantId)
        => ReferralAttribution.Create(
            tenantId,
            "Referral",
            "Associate",
            "REF-ASSOC",
            description: null,
            isActive: true,
            displayOrder: null,
            createdByUserId: null);

    private static CareConnectDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<CareConnectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new CareConnectDbContext(options);
    }

    private static void SetCreatedAtUtc(PendingReferralRequest request, DateTime value) =>
        typeof(PendingReferralRequest)
            .GetProperty(nameof(PendingReferralRequest.CreatedAtUtc))!
            .SetValue(request, value);
}
