using BuildingBlocks.Exceptions;
using CareConnect.Application.DTOs;
using CareConnect.Application.Interfaces;
using CareConnect.Application.Repositories;
using CareConnect.Application.Services;
using CareConnect.Domain;
using LegalSynq.AuditClient;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CareConnect.Tests.Application;

/// <summary>
/// Covers referral creation with/without/invalid Referral Origination. Origination is set
/// only at law firm submission time and is immutable afterward — there is no admin edit
/// path (deliberately; see ReferralEndpoints.cs's note where that endpoint used to live).
/// </summary>
public class ReferralServiceAttributionTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid OtherTenantId = Guid.CreateVersion7();

    private static ReferralService BuildService(
        Mock<IReferralRepository> referrals,
        Mock<IProviderRepository> providers,
        Mock<IReferralAttributionRepository> attributions)
    {
        return new ReferralService(
            referrals.Object,
            providers.Object,
            new Mock<ITenantServiceClient>().Object,
            new Mock<INotificationService>().Object,
            new Mock<INotificationRepository>().Object,
            new Mock<IReferralEmailService>().Object,
            new Mock<IIdentityOrganizationService>().Object,
            new Mock<IServiceScopeFactory>().Object,
            new Mock<IOrganizationRelationshipResolver>().Object,
            new Mock<IAuditEventClient>().Object,
            NullLogger<ReferralService>.Instance,
            new Mock<IHttpContextAccessor>().Object,
            new Mock<IReferralAttachmentRepository>().Object,
            attributions.Object,
            activationRequests: null);
    }

    private static Provider BuildProvider(Guid tenantId) => Provider.Create(
        tenantId: tenantId, name: "Dr. Test", organizationName: "Org", email: "p@example.com",
        phone: "555-0100", addressLine1: "1 Main St", city: "Chicago", state: "IL", postalCode: "60601",
        isActive: true, acceptingReferrals: true, createdByUserId: null);

    private static CreateReferralRequest BuildCreateRequest(Guid providerId, Guid? attributionId) => new()
    {
        ProviderId = providerId,
        ClientFirstName = "Jane",
        ClientLastName = "Doe",
        ClientPhone = "555-0200",
        ClientEmail = "jane@example.com",
        Urgency = Referral.ValidUrgencies.Normal,
        ReferralAttributionId = attributionId,
    };

    // ── CreateAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_ValidActiveAttribution_SetsReferralAttributionId()
    {
        var provider = BuildProvider(TenantId);
        var attribution = ReferralAttribution.Create(TenantId, "Cam", "Perry", "CAM_PERRY", null, true, 1, null);

        var providers = new Mock<IProviderRepository>();
        providers.Setup(p => p.GetByIdCrossAsync(provider.Id, It.IsAny<CancellationToken>())).ReturnsAsync(provider);

        var attributions = new Mock<IReferralAttributionRepository>();
        attributions.Setup(a => a.GetByIdAsync(TenantId, attribution.Id, It.IsAny<CancellationToken>())).ReturnsAsync(attribution);

        Referral? captured = null;
        var referrals = new Mock<IReferralRepository>();
        referrals.Setup(r => r.AddAsync(It.IsAny<Referral>(), It.IsAny<CancellationToken>()))
            .Callback<Referral, CancellationToken>((r, _) => captured = r)
            .Returns(Task.CompletedTask);
        referrals.Setup(r => r.GetByIdAsync(TenantId, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => captured);

        var service = BuildService(referrals, providers, attributions);
        var result = await service.CreateAsync(TenantId, Guid.CreateVersion7(), BuildCreateRequest(provider.Id, attribution.Id));

        Assert.Equal(attribution.Id, captured!.ReferralAttributionId);
    }

    [Fact]
    public async Task CreateAsync_NoAttributionSelected_LeavesReferralAttributionIdNull()
    {
        var provider = BuildProvider(TenantId);
        var providers = new Mock<IProviderRepository>();
        providers.Setup(p => p.GetByIdCrossAsync(provider.Id, It.IsAny<CancellationToken>())).ReturnsAsync(provider);

        Referral? captured = null;
        var referrals = new Mock<IReferralRepository>();
        referrals.Setup(r => r.AddAsync(It.IsAny<Referral>(), It.IsAny<CancellationToken>()))
            .Callback<Referral, CancellationToken>((r, _) => captured = r)
            .Returns(Task.CompletedTask);
        referrals.Setup(r => r.GetByIdAsync(TenantId, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => captured);

        var service = BuildService(referrals, providers, new Mock<IReferralAttributionRepository>());
        await service.CreateAsync(TenantId, Guid.CreateVersion7(), BuildCreateRequest(provider.Id, attributionId: null));

        Assert.Null(captured!.ReferralAttributionId);
    }

    [Fact]
    public async Task CreateAsync_InactiveAttribution_Rejected()
    {
        var provider = BuildProvider(TenantId);
        var providers = new Mock<IProviderRepository>();
        providers.Setup(p => p.GetByIdCrossAsync(provider.Id, It.IsAny<CancellationToken>())).ReturnsAsync(provider);

        var inactiveAttribution = ReferralAttribution.Create(TenantId, "Retired", "Source", "RETIRED", null, false, null, null);
        var attributions = new Mock<IReferralAttributionRepository>();
        attributions.Setup(a => a.GetByIdAsync(TenantId, inactiveAttribution.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(inactiveAttribution);

        var referrals = new Mock<IReferralRepository>();
        var service = BuildService(referrals, providers, attributions);

        await Assert.ThrowsAsync<ValidationException>(() =>
            service.CreateAsync(TenantId, null, BuildCreateRequest(provider.Id, inactiveAttribution.Id)));

        referrals.Verify(r => r.AddAsync(It.IsAny<Referral>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateAsync_CrossTenantAttribution_Rejected()
    {
        // Attribution belongs to OtherTenantId; the tenant-scoped repository lookup for
        // TenantId correctly returns null even though the ID itself is real.
        var provider = BuildProvider(TenantId);
        var providers = new Mock<IProviderRepository>();
        providers.Setup(p => p.GetByIdCrossAsync(provider.Id, It.IsAny<CancellationToken>())).ReturnsAsync(provider);

        var foreignAttribution = ReferralAttribution.Create(OtherTenantId, "Cam", "Perry", "CAM_PERRY", null, true, null, null);
        var attributions = new Mock<IReferralAttributionRepository>();
        attributions.Setup(a => a.GetByIdAsync(TenantId, foreignAttribution.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ReferralAttribution?)null);

        var referrals = new Mock<IReferralRepository>();
        var service = BuildService(referrals, providers, attributions);

        await Assert.ThrowsAsync<ValidationException>(() =>
            service.CreateAsync(TenantId, null, BuildCreateRequest(provider.Id, foreignAttribution.Id)));

        referrals.Verify(r => r.AddAsync(It.IsAny<Referral>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
