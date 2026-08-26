using CareConnect.Application.DTOs;
using CareConnect.Application.Interfaces;
using CareConnect.Application.Repositories;
using CareConnect.Application.Services;
using CareConnect.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CareConnect.Tests.Application;

public class PendingReferralRequestServiceTests
{
    [Fact]
    public async Task ConvertAsync_StampsLawFirmNotificationMetadata_OnCreatedReferral()
    {
        var tenantId = Guid.CreateVersion7();
        var lawFirmOrgId = Guid.CreateVersion7();
        var providerOrgId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        var attributionId = Guid.CreateVersion7();
        const string lawFirmName = "North Valley Law";
        const string userEmail = "case.manager@northvalley.example";
        const string userName = "Case Manager";

        var provider = BuildProvider(tenantId);
        provider.LinkOrganization(providerOrgId);

        var pending = PendingReferralRequest.Create(
            tenantId,
            lawFirmOrgId,
            attributionId,
            "Jane",
            "Doe",
            new DateTime(1990, 1, 2),
            "555-0100",
            "jane@example.com",
            "CASE-123",
            "Physical Therapy",
            Referral.ValidUrgencies.Normal,
            treatmentTypeId: null,
            dateOfAccident: new DateOnly(2026, 8, 1),
            notes: "Needs morning appointment.",
            lienCompanyName: null,
            lienCompanyEmail: null);
        pending.AddProviderPreference(provider.Id, facilityId: null, provider.OrganizationName!, facilityName: null, displayOrder: 0);

        Referral? createdReferral = null;

        var pendingRepo = new Mock<IPendingReferralRequestRepository>();
        pendingRepo
            .Setup(r => r.GetByIdAsync(tenantId, pending.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(pending);
        pendingRepo
            .Setup(r => r.UpdateAsync(pending, It.IsAny<Referral>(), It.IsAny<CancellationToken>()))
            .Callback<PendingReferralRequest, Referral, CancellationToken>((_, referral, _) => createdReferral = referral)
            .Returns(Task.CompletedTask);

        var referrals = new Mock<IReferralRepository>();
        referrals
            .Setup(r => r.GetByIdAsync(tenantId, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => createdReferral);

        var providers = new Mock<IProviderRepository>();
        providers
            .Setup(r => r.GetByIdCrossAsync(provider.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(provider);

        var identity = new Mock<IIdentityOrganizationService>();
        identity
            .Setup(s => s.GetOrganizationNameAsync(lawFirmOrgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(lawFirmName);

        var service = BuildService(
            pendingRepo.Object,
            providers.Object,
            referrals.Object,
            identity.Object,
            Mock.Of<IReferralEmailService>());

        await service.ConvertAsync(
            tenantId,
            lawFirmOrgId,
            pending.Id,
            userId,
            userEmail,
            userName,
            new ConvertPendingReferralRequest(),
            CancellationToken.None);

        Assert.NotNull(createdReferral);
        Assert.Equal(lawFirmOrgId, createdReferral.ReferringOrganizationId);
        Assert.Equal(userEmail, createdReferral.ReferrerEmail);
        Assert.Equal(userName, createdReferral.ReferrerName);
        Assert.Equal(lawFirmName, createdReferral.ReferrerFirmName);
    }

    private static PendingReferralRequestService BuildService(
        IPendingReferralRequestRepository pending,
        IProviderRepository providers,
        IReferralRepository referrals,
        IIdentityOrganizationService identity,
        IReferralEmailService emailService)
    {
        var serviceProvider = new ServiceCollection()
            .AddSingleton(referrals)
            .AddSingleton(providers)
            .AddSingleton(emailService)
            .BuildServiceProvider();

        return new PendingReferralRequestService(
            pending,
            Mock.Of<IReferralAttributionRepository>(),
            providers,
            Mock.Of<INetworkRepository>(),
            identity,
            Mock.Of<IOrganizationRelationshipResolver>(),
            referrals,
            Mock.Of<IPendingReferralAttachmentRepository>(r =>
                r.GetByRequestAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()) == Task.FromResult(new List<PendingReferralAttachment>())),
            Mock.Of<IReferralAttachmentRepository>(),
            Mock.Of<IDocumentServiceClient>(),
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PendingReferralRequestService>.Instance);
    }

    private static Provider BuildProvider(Guid tenantId)
        => Provider.Create(
            tenantId,
            "Desert Therapy",
            "Desert Therapy LLC",
            "intake@deserttherapy.example",
            "555-0200",
            "123 Main St",
            "Las Vegas",
            "NV",
            "89101",
            isActive: true,
            acceptingReferrals: true,
            createdByUserId: null);
}
