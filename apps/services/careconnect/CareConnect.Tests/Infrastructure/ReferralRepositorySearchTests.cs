using CareConnect.Application.DTOs;
using CareConnect.Domain;
using CareConnect.Infrastructure.Data;
using CareConnect.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CareConnect.Tests.Infrastructure;

public sealed class ReferralRepositorySearchTests
{
    [Fact]
    public async Task SearchAsync_SearchTextMatchesNaturalLanguageAcrossClientProviderAndLawFirm()
    {
        await using var db = CreateDbContext();
        var tenantId = Guid.CreateVersion7();

        var matchingProvider = Provider.Create(
            tenantId,
            "Atlas Medical",
            "Atlas Health",
            "atlas@example.com",
            "555-1000",
            "123 Main St",
            "Phoenix",
            "AZ",
            "85001",
            isActive: true,
            acceptingReferrals: true,
            createdByUserId: null);
        var otherProvider = Provider.Create(
            tenantId,
            "North Clinic",
            "North Health",
            "north@example.com",
            "555-2000",
            "456 Broad St",
            "Phoenix",
            "AZ",
            "85002",
            isActive: true,
            acceptingReferrals: true,
            createdByUserId: null);

        db.Providers.AddRange(matchingProvider, otherProvider);

        var matchingReferral = Referral.Create(
            tenantId,
            referringOrganizationId: null,
            receivingOrganizationId: null,
            providerId: matchingProvider.Id,
            subjectPartyId: null,
            subjectNameSnapshot: "Jane Doe",
            subjectDobSnapshot: null,
            clientFirstName: "Jane",
            clientLastName: "Doe",
            clientDob: null,
            clientPhone: "555-3000",
            clientEmail: "jane@example.com",
            caseNumber: "CASE-100",
            requestedService: "Physical Therapy",
            urgency: Referral.ValidUrgencies.Urgent,
            notes: null,
            createdByUserId: null,
            referrerEmail: "pat@acmelaw.com",
            referrerName: "Pat Referrer",
            referrerFirmName: "Acme Law");
        var otherReferral = Referral.Create(
            tenantId,
            referringOrganizationId: null,
            receivingOrganizationId: null,
            providerId: otherProvider.Id,
            subjectPartyId: null,
            subjectNameSnapshot: "John Smith",
            subjectDobSnapshot: null,
            clientFirstName: "John",
            clientLastName: "Smith",
            clientDob: null,
            clientPhone: "555-4000",
            clientEmail: "john@example.com",
            caseNumber: "CASE-200",
            requestedService: "Imaging",
            urgency: Referral.ValidUrgencies.Normal,
            notes: null,
            createdByUserId: null,
            referrerEmail: "amy@northfirm.com",
            referrerName: "Amy Advocate",
            referrerFirmName: "North Firm");

        db.Referrals.AddRange(matchingReferral, otherReferral);
        await db.SaveChangesAsync();

        var repository = new ReferralRepository(db);

        var result = await repository.SearchAsync(tenantId, new GetReferralsQuery
        {
            SearchText = "Find the referral for Jane Doe at Atlas Health from Acme Law",
        });

        var referral = Assert.Single(result.Items);
        Assert.Equal(matchingReferral.Id, referral.Id);
    }

    [Fact]
    public async Task SearchAsync_ProviderNameMatchesProviderOrganizationName()
    {
        await using var db = CreateDbContext();
        var tenantId = Guid.CreateVersion7();

        var provider = Provider.Create(
            tenantId,
            "Atlas Medical",
            "Atlas Health",
            "atlas@example.com",
            "555-1000",
            "123 Main St",
            "Phoenix",
            "AZ",
            "85001",
            isActive: true,
            acceptingReferrals: true,
            createdByUserId: null);

        db.Providers.Add(provider);
        db.Referrals.Add(Referral.Create(
            tenantId,
            referringOrganizationId: null,
            receivingOrganizationId: null,
            providerId: provider.Id,
            subjectPartyId: null,
            subjectNameSnapshot: "Jane Doe",
            subjectDobSnapshot: null,
            clientFirstName: "Jane",
            clientLastName: "Doe",
            clientDob: null,
            clientPhone: "555-3000",
            clientEmail: "jane@example.com",
            caseNumber: "CASE-100",
            requestedService: "Physical Therapy",
            urgency: Referral.ValidUrgencies.Urgent,
            notes: null,
            createdByUserId: null,
            referrerEmail: "pat@acmelaw.com",
            referrerName: "Pat Referrer",
            referrerFirmName: "Acme Law"));
        await db.SaveChangesAsync();

        var repository = new ReferralRepository(db);

        var result = await repository.SearchAsync(tenantId, new GetReferralsQuery
        {
            ProviderName = "Atlas Health",
        });

        Assert.Single(result.Items);
    }

    [Fact]
    public async Task SearchAsync_ProviderNameMatchesOrganizationNameWhenInitialsUsePunctuation()
    {
        await using var db = CreateDbContext();
        var tenantId = Guid.CreateVersion7();

        var provider = Provider.Create(
            tenantId,
            "Dr. Ralph Lopez",
            "R.L. Medical Group",
            "atlas@example.com",
            "555-1000",
            "123 Main St",
            "Phoenix",
            "AZ",
            "85001",
            isActive: true,
            acceptingReferrals: true,
            createdByUserId: null);

        db.Providers.Add(provider);
        db.Referrals.Add(Referral.Create(
            tenantId,
            referringOrganizationId: null,
            receivingOrganizationId: null,
            providerId: provider.Id,
            subjectPartyId: null,
            subjectNameSnapshot: "Jane Doe",
            subjectDobSnapshot: null,
            clientFirstName: "Jane",
            clientLastName: "Doe",
            clientDob: null,
            clientPhone: "555-3000",
            clientEmail: "jane@example.com",
            caseNumber: "CASE-100",
            requestedService: "Physical Therapy",
            urgency: Referral.ValidUrgencies.Urgent,
            notes: null,
            createdByUserId: null,
            referrerEmail: "pat@acmelaw.com",
            referrerName: "Pat Referrer",
            referrerFirmName: "Acme Law"));
        await db.SaveChangesAsync();

        var repository = new ReferralRepository(db);

        var result = await repository.SearchAsync(tenantId, new GetReferralsQuery
        {
            ProviderName = "RL Medical Group",
        });

        Assert.Single(result.Items);
    }

    [Fact]
    public async Task SearchAsync_SearchTextIgnoresDirectiveWordsForProviderLookup()
    {
        await using var db = CreateDbContext();
        var tenantId = Guid.CreateVersion7();

        var provider = Provider.Create(
            tenantId,
            "Dr. Ralph Lopez",
            "RL Medical Group",
            "atlas@example.com",
            "555-1000",
            "123 Main St",
            "Phoenix",
            "AZ",
            "85001",
            isActive: true,
            acceptingReferrals: true,
            createdByUserId: null);

        db.Providers.Add(provider);
        db.Referrals.Add(Referral.Create(
            tenantId,
            referringOrganizationId: null,
            receivingOrganizationId: null,
            providerId: provider.Id,
            subjectPartyId: null,
            subjectNameSnapshot: "Jane Doe",
            subjectDobSnapshot: null,
            clientFirstName: "Jane",
            clientLastName: "Doe",
            clientDob: null,
            clientPhone: "555-3000",
            clientEmail: "jane@example.com",
            caseNumber: "CASE-100",
            requestedService: "Physical Therapy",
            urgency: Referral.ValidUrgencies.Urgent,
            notes: null,
            createdByUserId: null,
            referrerEmail: "pat@acmelaw.com",
            referrerName: "Pat Referrer",
            referrerFirmName: "Acme Law"));
        await db.SaveChangesAsync();

        var repository = new ReferralRepository(db);

        var result = await repository.SearchAsync(tenantId, new GetReferralsQuery
        {
            SearchText = "look for the latest referral sent to RL Medical Group",
        });

        Assert.Single(result.Items);
    }

    [Fact]
    public async Task SearchAsync_CrossTenantReceiverFallsBackToProviderOrganizationLink()
    {
        await using var db = CreateDbContext();
        var tenantId = Guid.CreateVersion7();
        var receivingOrgId = Guid.CreateVersion7();

        var provider = Provider.Create(
            tenantId,
            "Dr. Ralph Lopez",
            "RL Medical Group",
            "atlas@example.com",
            "555-1000",
            "123 Main St",
            "Phoenix",
            "AZ",
            "85001",
            isActive: true,
            acceptingReferrals: true,
            createdByUserId: null);
        provider.LinkOrganization(receivingOrgId);

        db.Providers.Add(provider);
        db.Referrals.Add(Referral.Create(
            tenantId,
            referringOrganizationId: null,
            receivingOrganizationId: null,
            providerId: provider.Id,
            subjectPartyId: null,
            subjectNameSnapshot: "Jane Doe",
            subjectDobSnapshot: null,
            clientFirstName: "Jane",
            clientLastName: "Doe",
            clientDob: null,
            clientPhone: "555-3000",
            clientEmail: "jane@example.com",
            caseNumber: "CASE-100",
            requestedService: "Physical Therapy",
            urgency: Referral.ValidUrgencies.Urgent,
            notes: null,
            createdByUserId: null,
            referrerEmail: "pat@acmelaw.com",
            referrerName: "Pat Referrer",
            referrerFirmName: "Acme Law"));
        await db.SaveChangesAsync();

        var repository = new ReferralRepository(db);

        var result = await repository.SearchAsync(tenantId, new GetReferralsQuery
        {
            CrossTenantReceiver = true,
            ReceivingOrgId = receivingOrgId,
        });

        Assert.Single(result.Items);
    }

    [Fact]
    public async Task SearchAsync_ReferrerNameMatchesLawFirmName()
    {
        await using var db = CreateDbContext();
        var tenantId = Guid.CreateVersion7();

        var provider = Provider.Create(
            tenantId,
            "Atlas Medical",
            "Atlas Health",
            "atlas@example.com",
            "555-1000",
            "123 Main St",
            "Phoenix",
            "AZ",
            "85001",
            isActive: true,
            acceptingReferrals: true,
            createdByUserId: null);

        db.Providers.Add(provider);
        db.Referrals.Add(Referral.Create(
            tenantId,
            referringOrganizationId: null,
            receivingOrganizationId: null,
            providerId: provider.Id,
            subjectPartyId: null,
            subjectNameSnapshot: "Jane Doe",
            subjectDobSnapshot: null,
            clientFirstName: "Jane",
            clientLastName: "Doe",
            clientDob: null,
            clientPhone: "555-3000",
            clientEmail: "jane@example.com",
            caseNumber: "CASE-100",
            requestedService: "Physical Therapy",
            urgency: Referral.ValidUrgencies.Urgent,
            notes: null,
            createdByUserId: null,
            referrerEmail: "pat@acmelaw.com",
            referrerName: "Pat Referrer",
            referrerFirmName: "Acme Law"));
        await db.SaveChangesAsync();

        var repository = new ReferralRepository(db);

        var result = await repository.SearchAsync(tenantId, new GetReferralsQuery
        {
            ReferrerName = "Acme Law",
        });

        Assert.Single(result.Items);
    }

    [Fact]
    public async Task SearchAsync_DateOnlyCreatedToIncludesEntireSelectedDay()
    {
        await using var db = CreateDbContext();
        var tenantId = Guid.CreateVersion7();
        var provider = CreateProvider(tenantId);

        var august31Referral = CreateReferral(tenantId, provider.Id, "August", "Client");
        var september1Referral = CreateReferral(tenantId, provider.Id, "September", "Client");
        SetCreatedAtUtc(august31Referral, new DateTime(2026, 8, 31, 12, 30, 0, DateTimeKind.Utc));
        SetCreatedAtUtc(september1Referral, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));

        db.Providers.Add(provider);
        db.Referrals.AddRange(august31Referral, september1Referral);
        await db.SaveChangesAsync();

        var repository = new ReferralRepository(db);

        var result = await repository.SearchAsync(tenantId, new GetReferralsQuery
        {
            CreatedFrom = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            CreatedTo = new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc),
        });

        var referral = Assert.Single(result.Items);
        Assert.Equal(august31Referral.Id, referral.Id);
    }

    [Fact]
    public async Task SearchAsync_TimestampCreatedToKeepsExactUpperBound()
    {
        await using var db = CreateDbContext();
        var tenantId = Guid.CreateVersion7();
        var provider = CreateProvider(tenantId);

        var beforeUpperBoundReferral = CreateReferral(tenantId, provider.Id, "Before", "Client");
        var afterUpperBoundReferral = CreateReferral(tenantId, provider.Id, "After", "Client");
        SetCreatedAtUtc(beforeUpperBoundReferral, new DateTime(2026, 8, 31, 12, 30, 0, DateTimeKind.Utc));
        SetCreatedAtUtc(afterUpperBoundReferral, new DateTime(2026, 8, 31, 12, 30, 1, DateTimeKind.Utc));

        db.Providers.Add(provider);
        db.Referrals.AddRange(beforeUpperBoundReferral, afterUpperBoundReferral);
        await db.SaveChangesAsync();

        var repository = new ReferralRepository(db);

        var result = await repository.SearchAsync(tenantId, new GetReferralsQuery
        {
            CreatedTo = new DateTime(2026, 8, 31, 12, 30, 0, DateTimeKind.Utc),
        });

        var referral = Assert.Single(result.Items);
        Assert.Equal(beforeUpperBoundReferral.Id, referral.Id);
    }

    private static CareConnectDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<CareConnectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new CareConnectDbContext(options);
    }

    private static Provider CreateProvider(Guid tenantId) =>
        Provider.Create(
            tenantId,
            "Atlas Medical",
            "Atlas Health",
            "atlas@example.com",
            "555-1000",
            "123 Main St",
            "Phoenix",
            "AZ",
            "85001",
            isActive: true,
            acceptingReferrals: true,
            createdByUserId: null);

    private static Referral CreateReferral(Guid tenantId, Guid providerId, string firstName, string lastName) =>
        Referral.Create(
            tenantId,
            referringOrganizationId: null,
            receivingOrganizationId: null,
            providerId: providerId,
            subjectPartyId: null,
            subjectNameSnapshot: $"{firstName} {lastName}",
            subjectDobSnapshot: null,
            clientFirstName: firstName,
            clientLastName: lastName,
            clientDob: null,
            clientPhone: "555-3000",
            clientEmail: $"{firstName.ToLowerInvariant()}@example.com",
            caseNumber: null,
            requestedService: "Physical Therapy",
            urgency: Referral.ValidUrgencies.Normal,
            notes: null,
            createdByUserId: null);

    private static void SetCreatedAtUtc(Referral referral, DateTime value) =>
        typeof(Referral)
            .GetProperty(nameof(Referral.CreatedAtUtc))!
            .SetValue(referral, value);
}
