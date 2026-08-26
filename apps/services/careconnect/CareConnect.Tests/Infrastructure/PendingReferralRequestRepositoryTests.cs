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
}
