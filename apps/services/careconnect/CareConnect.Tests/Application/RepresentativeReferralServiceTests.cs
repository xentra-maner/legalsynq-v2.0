using CareConnect.Application.DTOs;
using CareConnect.Application.Interfaces;
using CareConnect.Application.Repositories;
using CareConnect.Application.Services;
using CareConnect.Domain;
using Moq;
using Xunit;

namespace CareConnect.Tests.Application;

public sealed class RepresentativeReferralServiceTests
{
    [Fact]
    public async Task GetMetricsAsync_SeparatesPendingAcceptedAndDeclinedRequestOutcomes()
    {
        var tenantId = Guid.CreateVersion7();
        var attributionId = Guid.CreateVersion7();
        var providerId = Guid.CreateVersion7();
        var from = DateTime.UtcNow.AddDays(-30);
        var to = DateTime.UtcNow.AddDays(1);

        var pendingReferral = BuildReferral(tenantId, attributionId, providerId);
        var acceptedReferral = BuildReferral(tenantId, attributionId, providerId);
        acceptedReferral.Accept(updatedByUserId: null);

        var referrals = new Mock<IReferralRepository>();
        referrals
            .Setup(r => r.SearchAsync(
                tenantId,
                It.Is<GetReferralsQuery>(q =>
                    q.RestrictedToAttributionIds != null &&
                    q.RestrictedToAttributionIds.SequenceEqual(new[] { attributionId })),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((new List<Referral> { pendingReferral, acceptedReferral }, 2));

        var pendingRequests = new Mock<IPendingReferralRequestRepository>();
        pendingRequests
            .Setup(r => r.CountForAttributionAsync(
                tenantId,
                attributionId,
                It.IsAny<string?>(),
                from,
                to,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                Guid requestedTenantId,
                Guid requestedAttributionId,
                string? status,
                DateTime? requestedFrom,
                DateTime? requestedTo,
                CancellationToken cancellationToken) => status switch
            {
                PendingReferralRequest.Statuses.PendingReview => 2,
                PendingReferralRequest.Statuses.Converted => 3,
                PendingReferralRequest.Statuses.Cancelled => 1,
                _ => 0,
            });

        var service = new RepresentativeReferralService(
            referrals.Object,
            pendingRequests.Object,
            Mock.Of<IIdentityOrganizationService>());

        var result = await service.GetMetricsAsync(tenantId, attributionId, from, to, CancellationToken.None);

        Assert.Equal(2, result.PendingRequestReferrals);
        Assert.Equal(3, result.AcceptedRequestReferrals);
        Assert.Equal(1, result.DeclinedRequestReferrals);
        Assert.Equal(1, result.PendingReferrals);
        Assert.Equal(1, result.AcceptedReferrals);
    }

    private static Referral BuildReferral(Guid tenantId, Guid attributionId, Guid providerId)
        => Referral.Create(
            tenantId: tenantId,
            referringOrganizationId: Guid.CreateVersion7(),
            receivingOrganizationId: Guid.CreateVersion7(),
            providerId: providerId,
            subjectPartyId: null,
            subjectNameSnapshot: null,
            subjectDobSnapshot: null,
            clientFirstName: "Jane",
            clientLastName: "Doe",
            clientDob: null,
            clientPhone: "555-0100",
            clientEmail: "jane@example.com",
            caseNumber: null,
            requestedService: "Physical Therapy",
            urgency: Referral.ValidUrgencies.Normal,
            notes: null,
            createdByUserId: null,
            referralAttributionId: attributionId,
            origin: ReferralOrigin.ReferralAssociate);
}
