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

public class ReferralThreadServiceTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ReferringOrgId = Guid.CreateVersion7();
    private static readonly Guid ReceivingOrgId = Guid.CreateVersion7();

    [Fact]
    public async Task GetAuthenticatedCommentsAsync_ReturnsComments_ForReceivingParticipant()
    {
        var referral = BuildReferral();
        var repo = new Mock<IReferralRepository>();
        repo.Setup(r => r.GetByIdGlobalAsync(referral.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(referral);

        var commentsRepo = new Mock<IReferralCommentRepository>();
        commentsRepo.Setup(r => r.GetByReferralAsync(referral.TenantId, referral.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new ReferralComment
                {
                    TenantId = referral.TenantId,
                    ReferralId = referral.Id,
                    SenderType = "provider",
                    SenderName = "Dr. Gray",
                    Message = "We can take this case.",
                    CreatedAt = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc),
                },
            ]);

        var sut = BuildService(repo, commentsRepo);

        var result = await sut.GetAuthenticatedCommentsAsync(
            TenantId,
            referral.Id,
            ReceivingOrgId,
            callerEmail: null,
            useGlobalLookup: true,
            bypassParticipantCheck: false);

        Assert.NotNull(result);
        Assert.Single(result!);
        Assert.Equal("provider", result[0].SenderType);
        Assert.Equal("Dr. Gray", result[0].SenderName);
    }

    [Fact]
    public async Task GetAuthenticatedCommentsAsync_NormalizesUnspecifiedCommentTimestampToUtc()
    {
        var referral = BuildReferral();
        var repo = new Mock<IReferralRepository>();
        repo.Setup(r => r.GetByIdGlobalAsync(referral.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(referral);

        var createdAt = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Unspecified);
        var commentsRepo = new Mock<IReferralCommentRepository>();
        commentsRepo.Setup(r => r.GetByReferralAsync(referral.TenantId, referral.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new ReferralComment
                {
                    TenantId = referral.TenantId,
                    ReferralId = referral.Id,
                    SenderType = "provider",
                    SenderName = "Dr. Gray",
                    Message = "We can take this case.",
                    CreatedAt = createdAt,
                },
            ]);

        var sut = BuildService(repo, commentsRepo);

        var result = await sut.GetAuthenticatedCommentsAsync(
            TenantId,
            referral.Id,
            ReceivingOrgId,
            callerEmail: null,
            useGlobalLookup: true,
            bypassParticipantCheck: false);

        Assert.NotNull(result);
        Assert.Single(result!);
        Assert.Equal(DateTimeKind.Utc, result[0].CreatedAtUtc.Kind);
        Assert.Equal(createdAt, result[0].CreatedAtUtc);
    }

    [Fact]
    public async Task GetPublicThreadAccessAsync_ReturnsProviderPrefillFields()
    {
        var referral = BuildReferral(referringOrganizationId: null);
        var provider = new ProviderStub
        {
            Name = "Dr. Gray",
            Title = "Dr.",
            FirstName = "Graham",
            LastName = "Gray",
            OrganizationName = "Gray Clinic",
            Email = "intake@gray.example",
            AccessStage = ProviderAccessStage.Url,
        }.ToDomain(null);
        SetProvider(referral, provider);

        var repo = new Mock<IReferralRepository>();
        repo.Setup(r => r.GetByIdGlobalAsync(referral.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(referral);

        var commentsRepo = new Mock<IReferralCommentRepository>();
        commentsRepo.Setup(r => r.GetByReferralAsync(referral.TenantId, referral.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var emailService = Mock.Of<IReferralEmailService>(e =>
            e.ValidateViewTokenDetailed("valid-token") ==
            CareConnect.Application.DTOs.ReferralTokenValidationOutcome.Success(referral.Id, referral.TokenVersion));

        var sut = BuildService(repo, commentsRepo, emailService);

        var result = await sut.GetPublicThreadAccessAsync("valid-token");

        Assert.NotNull(result.Data);
        Assert.Equal(provider.Id, result.Data!.ProviderId);
        Assert.Equal("Gray Clinic", result.Data.ProviderName);
        Assert.Equal("Dr.", result.Data.ProviderTitle);
        Assert.Equal("Graham", result.Data.ProviderFirstName);
        Assert.Equal("Gray", result.Data.ProviderLastName);
        Assert.Equal("intake@gray.example", result.Data.ProviderEmail);
        Assert.Equal("555-0101", result.Data.ProviderPhone);
        Assert.Null(result.Data.FacilityName);
        Assert.Equal("123 Main", result.Data.LocationAddressLine1);
        Assert.Equal("Las Vegas", result.Data.LocationCity);
        Assert.Equal("NV", result.Data.LocationState);
        Assert.Equal("89101", result.Data.LocationPostalCode);
    }

    [Fact]
    public async Task GetPublicThreadAccessAsync_ReturnsReferralOrigination()
    {
        var referral = BuildReferral(referringOrganizationId: null);
        SetReferralAttribution(referral, "Cam", "Perry");

        var repo = new Mock<IReferralRepository>();
        repo.Setup(r => r.GetByIdGlobalAsync(referral.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(referral);

        var commentsRepo = new Mock<IReferralCommentRepository>();
        commentsRepo.Setup(r => r.GetByReferralAsync(referral.TenantId, referral.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var emailService = Mock.Of<IReferralEmailService>(e =>
            e.ValidateViewTokenDetailed("valid-token") ==
            CareConnect.Application.DTOs.ReferralTokenValidationOutcome.Success(referral.Id, referral.TokenVersion));

        var sut = BuildService(repo, commentsRepo, emailService);

        var result = await sut.GetPublicThreadAccessAsync("valid-token");

        Assert.NotNull(result.Data?.ReferralAttribution);
        Assert.Equal("Cam", result.Data!.ReferralAttribution!.FirstName);
        Assert.Equal("Perry", result.Data.ReferralAttribution.LastName);
    }

    [Fact]
    public async Task GetPublicThreadAccessAsync_PrefersFacilityAddress_OverProviderDefaultAddress()
    {
        var referral = BuildReferral(referringOrganizationId: null);
        var provider = new ProviderStub
        {
            Name = "Dr. Gray",
            OrganizationName = "Gray Clinic",
            Email = "intake@gray.example",
            AccessStage = ProviderAccessStage.Url,
        }.ToDomain(null);
        SetProvider(referral, provider);

        var facility = Facility.Create(
            TenantId,
            name: "Gray Clinic - North",
            addressLine1: "456 North Ave",
            city: "Henderson",
            state: "NV",
            postalCode: "89052",
            phone: null,
            isActive: true,
            createdByUserId: null);
        typeof(Referral).GetProperty(nameof(Referral.Facility))!.SetValue(referral, facility);

        var repo = new Mock<IReferralRepository>();
        repo.Setup(r => r.GetByIdGlobalAsync(referral.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(referral);

        var commentsRepo = new Mock<IReferralCommentRepository>();
        commentsRepo.Setup(r => r.GetByReferralAsync(referral.TenantId, referral.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var emailService = Mock.Of<IReferralEmailService>(e =>
            e.ValidateViewTokenDetailed("valid-token") ==
            CareConnect.Application.DTOs.ReferralTokenValidationOutcome.Success(referral.Id, referral.TokenVersion));

        var sut = BuildService(repo, commentsRepo, emailService);

        var result = await sut.GetPublicThreadAccessAsync("valid-token");

        Assert.NotNull(result.Data);
        Assert.Equal("Gray Clinic - North", result.Data!.FacilityName);
        Assert.Equal("456 North Ave", result.Data.LocationAddressLine1);
        Assert.Equal("Henderson", result.Data.LocationCity);
        Assert.Equal("NV", result.Data.LocationState);
        Assert.Equal("89052", result.Data.LocationPostalCode);
    }

    [Fact]
    public async Task GetPublicThreadAccessAsync_MobileFacility_ReturnsServiceAreaLabelAndIsMobileFlag()
    {
        var referral = BuildReferral(referringOrganizationId: null);
        var provider = new ProviderStub
        {
            Name = "Dr. Gray",
            OrganizationName = "Gray Clinic",
            Email = "intake@gray.example",
            AccessStage = ProviderAccessStage.Url,
        }.ToDomain(null);
        SetProvider(referral, provider);

        var facility = Facility.Create(
            TenantId,
            name: "Gray Clinic - Mobile",
            addressLine1: "Greater Las Vegas Metro",
            city: "Las Vegas",
            state: "NV",
            postalCode: "89101",
            phone: null,
            isActive: true,
            createdByUserId: null,
            isMobile: true,
            serviceRadiusMiles: 25);
        typeof(Referral).GetProperty(nameof(Referral.Facility))!.SetValue(referral, facility);

        var repo = new Mock<IReferralRepository>();
        repo.Setup(r => r.GetByIdGlobalAsync(referral.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(referral);

        var commentsRepo = new Mock<IReferralCommentRepository>();
        commentsRepo.Setup(r => r.GetByReferralAsync(referral.TenantId, referral.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var emailService = Mock.Of<IReferralEmailService>(e =>
            e.ValidateViewTokenDetailed("valid-token") ==
            CareConnect.Application.DTOs.ReferralTokenValidationOutcome.Success(referral.Id, referral.TokenVersion));

        var sut = BuildService(repo, commentsRepo, emailService);

        var result = await sut.GetPublicThreadAccessAsync("valid-token");

        Assert.NotNull(result.Data);
        Assert.True(result.Data!.LocationIsMobile);
        Assert.Equal("Greater Las Vegas Metro", result.Data.LocationAddressLine1);
    }

    [Fact]
    public async Task GetPublicThreadAccessAsync_TreatsLegacyOrgLinkedProviderAsHavingAccount()
    {
        var referral = BuildReferral(referringOrganizationId: null);
        var provider = new ProviderStub
        {
            Name = "Dr. Gray",
            OrganizationName = "Gray Clinic",
            Email = "intake@gray.example",
            AccessStage = ProviderAccessStage.Url,
        }.ToDomain(Guid.CreateVersion7());
        SetProvider(referral, provider);

        var repo = new Mock<IReferralRepository>();
        repo.Setup(r => r.GetByIdGlobalAsync(referral.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(referral);

        var commentsRepo = new Mock<IReferralCommentRepository>();
        commentsRepo.Setup(r => r.GetByReferralAsync(referral.TenantId, referral.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var emailService = Mock.Of<IReferralEmailService>(e =>
            e.ValidateViewTokenDetailed("valid-token") ==
            CareConnect.Application.DTOs.ReferralTokenValidationOutcome.Success(referral.Id, referral.TokenVersion));

        var sut = BuildService(repo, commentsRepo, emailService);

        var result = await sut.GetPublicThreadAccessAsync("valid-token");

        Assert.NotNull(result.Data);
        Assert.True(result.Data!.ProviderHasAccount);
    }

    [Fact]
    public async Task GetAuthenticatedCommentsAsync_ReturnsComments_ForPublicReferrerEmailMatch()
    {
        var referral = BuildReferral(referringOrganizationId: null);
        var repo = new Mock<IReferralRepository>();
        repo.Setup(r => r.GetByIdAsync(TenantId, referral.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(referral);

        var commentsRepo = new Mock<IReferralCommentRepository>();
        commentsRepo.Setup(r => r.GetByReferralAsync(referral.TenantId, referral.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var sut = BuildService(repo, commentsRepo);

        var result = await sut.GetAuthenticatedCommentsAsync(
            TenantId,
            referral.Id,
            callerOrganizationId: null,
            callerEmail: "firm@example.com",
            useGlobalLookup: false,
            bypassParticipantCheck: false);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task GetAuthenticatedCommentsAsync_ReturnsNull_ForWrongReceivingOrg()
    {
        var referral = BuildReferral();
        var repo = new Mock<IReferralRepository>();
        repo.Setup(r => r.GetByIdGlobalAsync(referral.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(referral);

        var sut = BuildService(repo, new Mock<IReferralCommentRepository>());

        var result = await sut.GetAuthenticatedCommentsAsync(
            TenantId,
            referral.Id,
            Guid.CreateVersion7(),
            callerEmail: null,
            useGlobalLookup: true,
            bypassParticipantCheck: false);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetAuthenticatedCommentsAsync_ReturnsComments_ForPlatformAdminBypass()
    {
        var referral = BuildReferral();
        var repo = new Mock<IReferralRepository>();
        repo.Setup(r => r.GetByIdGlobalAsync(referral.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(referral);

        var commentsRepo = new Mock<IReferralCommentRepository>();
        commentsRepo.Setup(r => r.GetByReferralAsync(referral.TenantId, referral.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var sut = BuildService(repo, commentsRepo);

        var result = await sut.GetAuthenticatedCommentsAsync(
            TenantId,
            referral.Id,
            callerOrganizationId: null,
            callerEmail: null,
            useGlobalLookup: true,
            bypassParticipantCheck: true);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task GetAuthenticatedCommentsAsync_IncludesMessageAttachments()
    {
        var referral = BuildReferral();
        var commentId = Guid.CreateVersion7();
        var attachmentId = Guid.CreateVersion7();

        var repo = new Mock<IReferralRepository>();
        repo.Setup(r => r.GetByIdGlobalAsync(referral.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(referral);

        var commentsRepo = new Mock<IReferralCommentRepository>();
        commentsRepo.Setup(r => r.GetByReferralAsync(referral.TenantId, referral.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new ReferralComment
                {
                    Id = commentId,
                    TenantId = referral.TenantId,
                    ReferralId = referral.Id,
                    SenderType = "provider",
                    SenderName = "Dr. Gray",
                    Message = "Please review the attached image.",
                    CreatedAt = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc),
                },
            ]);

        var attachmentsRepo = new Mock<IReferralAttachmentRepository>();
        attachmentsRepo.Setup(r => r.GetByReferralCommentIdsAsync(
                referral.TenantId,
                referral.Id,
                It.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(commentId)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                CreateMessageAttachment(
                    attachmentId,
                    referral.TenantId,
                    referral.Id,
                    commentId,
                    "scan.png",
                    "image/png",
                    2048,
                    "doc-scan")
            ]);

        var sut = BuildService(repo, commentsRepo, attachments: attachmentsRepo);

        var result = await sut.GetAuthenticatedCommentsAsync(
            TenantId,
            referral.Id,
            ReceivingOrgId,
            callerEmail: null,
            useGlobalLookup: true,
            bypassParticipantCheck: false);

        Assert.NotNull(result);
        var attachment = Assert.Single(result![0].Attachments);
        Assert.Equal(attachmentId, attachment.Id);
        Assert.Equal("scan.png", attachment.FileName);
        Assert.Equal("image/png", attachment.ContentType);
        Assert.Equal(2048, attachment.FileSizeBytes);
    }

    [Fact]
    public async Task PostAuthenticatedCommentAsync_PersistsProviderComment_ForReceivingParticipant()
    {
        var referral = BuildReferral();
        var repo = new Mock<IReferralRepository>();
        repo.Setup(r => r.GetByIdGlobalAsync(referral.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(referral);

        ReferralComment? saved = null;
        var commentsRepo = new Mock<IReferralCommentRepository>();
        commentsRepo.Setup(r => r.AddAsync(It.IsAny<ReferralComment>(), It.IsAny<CancellationToken>()))
            .Callback<ReferralComment, CancellationToken>((comment, _) => saved = comment)
            .Returns(Task.CompletedTask);

        var sut = BuildService(repo, commentsRepo);

        var result = await sut.PostAuthenticatedCommentAsync(
            TenantId,
            referral.Id,
            ReceivingOrgId,
            callerEmail: null,
            "Dr. Gray",
            "We have availability next week.",
            useGlobalLookup: true);

        Assert.NotNull(result);
        Assert.NotNull(saved);
        Assert.Equal("provider", saved!.SenderType);
        Assert.Equal("Dr. Gray", saved.SenderName);
        Assert.Equal("We have availability next week.", saved.Message);
    }

    [Fact]
    public async Task PostAuthenticatedCommentAsync_PersistsReferrerComment_ForReferringParticipant()
    {
        var referral = BuildReferral();
        var repo = new Mock<IReferralRepository>();
        repo.Setup(r => r.GetByIdAsync(TenantId, referral.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(referral);

        ReferralComment? saved = null;
        var commentsRepo = new Mock<IReferralCommentRepository>();
        commentsRepo.Setup(r => r.AddAsync(It.IsAny<ReferralComment>(), It.IsAny<CancellationToken>()))
            .Callback<ReferralComment, CancellationToken>((comment, _) => saved = comment)
            .Returns(Task.CompletedTask);

        var sut = BuildService(repo, commentsRepo);

        var result = await sut.PostAuthenticatedCommentAsync(
            TenantId,
            referral.Id,
            ReferringOrgId,
            callerEmail: null,
            "Sarah",
            "Following up on the referral.",
            useGlobalLookup: false);

        Assert.NotNull(result);
        Assert.NotNull(saved);
        Assert.Equal("referrer", saved!.SenderType);
        Assert.Equal("Sarah", saved.SenderName);
    }

    [Fact]
    public async Task PostAuthenticatedCommentAsync_ReturnsNull_ForMissingParticipant()
    {
        var referral = BuildReferral();
        var repo = new Mock<IReferralRepository>();
        repo.Setup(r => r.GetByIdGlobalAsync(referral.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(referral);

        var sut = BuildService(repo, new Mock<IReferralCommentRepository>());

        var result = await sut.PostAuthenticatedCommentAsync(
            TenantId,
            referral.Id,
            callerOrganizationId: null,
            callerEmail: null,
            "Dr. Gray",
            "We have availability next week.",
            useGlobalLookup: true);

        Assert.Null(result);
    }

    [Fact]
    public async Task PostAuthenticatedCommentWithAttachmentsAsync_PersistsAttachments_ForReceivingParticipant()
    {
        var referral = BuildReferral();
        var repo = new Mock<IReferralRepository>();
        repo.Setup(r => r.GetByIdGlobalAsync(referral.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(referral);

        IReadOnlyCollection<ReferralAttachment>? savedAttachments = null;
        var commentsRepo = new Mock<IReferralCommentRepository>();
        commentsRepo.Setup(r => r.AddWithAttachmentsAsync(
                It.IsAny<ReferralComment>(),
                It.IsAny<IReadOnlyCollection<ReferralAttachment>>(),
                It.IsAny<CancellationToken>()))
            .Callback<ReferralComment, IReadOnlyCollection<ReferralAttachment>, CancellationToken>((_, attachments, _) =>
            {
                savedAttachments = attachments;
            })
            .Returns(Task.CompletedTask);

        var docClient = new Mock<IDocumentServiceClient>();
        docClient.Setup(d => d.UploadAsync(
                It.IsAny<Stream>(),
                "note.pdf",
                "application/pdf",
                3,
                referral.TenantId,
                "note.pdf",
                null,
                null,
                It.IsAny<string>(),
                "referral-comment",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentUploadResult(true, "doc-auth-1", null));

        var sut = BuildService(repo, commentsRepo, documents: docClient);
        await using var stream = new MemoryStream([1, 2, 3]);

        var result = await sut.PostAuthenticatedCommentWithAttachmentsAsync(
            TenantId,
            referral.Id,
            ReceivingOrgId,
            callerEmail: null,
            "Dr. Gray",
            "Attached note.",
            useGlobalLookup: true,
            [new ReferralMessageAttachmentUpload(stream, "note.pdf", "application/pdf", 3)]);

        Assert.NotNull(result);
        var saved = Assert.Single(savedAttachments!);
        Assert.NotNull(saved.ReferralCommentId);
        Assert.Equal("doc-auth-1", saved.ExternalDocumentId);
        Assert.Equal("note.pdf", result!.Attachments.Single().FileName);
    }

    [Fact]
    public async Task GetPublicThreadAccessAsync_WithTreatmentType_PopulatesNameInResponse()
    {
        var treatmentTypeId   = Guid.CreateVersion7();
        var treatmentTypeName = "Physical Therapy";

        var referral = BuildReferral(referringOrganizationId: null);
        // Inject treatment type ID directly via reflection (domain keeps the setter private)
        typeof(Referral).GetProperty(nameof(Referral.TreatmentTypeId))!
            .SetValue(referral, treatmentTypeId,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                null, null, null);

        var repo = new Mock<IReferralRepository>();
        repo.Setup(r => r.GetByIdGlobalAsync(referral.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(referral);
        repo.Setup(r => r.GetTreatmentTypeNameAsync(treatmentTypeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(treatmentTypeName);

        var commentsRepo = new Mock<IReferralCommentRepository>();
        commentsRepo.Setup(r => r.GetByReferralAsync(referral.TenantId, referral.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var emailService = Mock.Of<IReferralEmailService>(e =>
            e.ValidateViewTokenDetailed("valid-token") ==
            CareConnect.Application.DTOs.ReferralTokenValidationOutcome.Success(referral.Id, referral.TokenVersion));

        var sut = BuildService(repo, commentsRepo, emailService);

        var result = await sut.GetPublicThreadAccessAsync("valid-token");

        Assert.NotNull(result.Data);
        Assert.Equal(treatmentTypeId, result.Data!.TreatmentTypeId);
        Assert.Equal(treatmentTypeName, result.Data.TreatmentTypeName);
    }

    [Fact]
    public async Task GetPublicThreadAccessAsync_NoTreatmentType_LeavesFieldsNull()
    {
        var referral = BuildReferral(referringOrganizationId: null);

        var repo = new Mock<IReferralRepository>();
        repo.Setup(r => r.GetByIdGlobalAsync(referral.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(referral);

        var commentsRepo = new Mock<IReferralCommentRepository>();
        commentsRepo.Setup(r => r.GetByReferralAsync(referral.TenantId, referral.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var emailService = Mock.Of<IReferralEmailService>(e =>
            e.ValidateViewTokenDetailed("valid-token") ==
            CareConnect.Application.DTOs.ReferralTokenValidationOutcome.Success(referral.Id, referral.TokenVersion));

        var sut = BuildService(repo, commentsRepo, emailService);

        var result = await sut.GetPublicThreadAccessAsync("valid-token");

        Assert.NotNull(result.Data);
        Assert.Null(result.Data!.TreatmentTypeId);
        Assert.Null(result.Data.TreatmentTypeName);
    }

    [Fact]
    public async Task PostPublicCommentAsync_SendsNotification_ThroughSharedPath()
    {
        var referral = BuildReferral();
        SetProvider(referral, new ProviderStub
        {
            Name = "Dr. Gray",
            OrganizationName = "Gray Clinic",
            Email = "clinic@example.com",
            AccessStage = ProviderAccessStage.CommonPortal,
        }.ToDomain(referral.ReceivingOrganizationId));

        var emailService = new Mock<IReferralEmailService>();
        emailService.Setup(e => e.ValidateViewTokenDetailed("valid-token"))
            .Returns(CareConnect.Application.DTOs.ReferralTokenValidationOutcome.Success(referral.Id, referral.TokenVersion));
        emailService.Setup(e => e.SendCommentNotificationAsync(referral, It.IsAny<ReferralComment>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var repo = new Mock<IReferralRepository>();
        repo.Setup(r => r.GetByIdGlobalAsync(referral.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(referral);

        var commentsRepo = new Mock<IReferralCommentRepository>();
        commentsRepo.Setup(r => r.AddAsync(It.IsAny<ReferralComment>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var scopeFactory = BuildScopeFactory(emailService.Object);
        var sut = BuildService(repo, commentsRepo, emailService.Object, scopeFactory);

        var result = await sut.PostPublicCommentAsync("valid-token", "referrer", "Checking status.");
        await Task.Delay(50);

        Assert.NotNull(result);
        emailService.Verify(
            e => e.SendCommentNotificationAsync(referral, It.IsAny<ReferralComment>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PostPublicCommentWithAttachmentsAsync_UploadsAndPersistsMessageAttachments()
    {
        var referral = BuildReferral();

        var emailService = new Mock<IReferralEmailService>();
        emailService.Setup(e => e.ValidateViewTokenDetailed("valid-token"))
            .Returns(CareConnect.Application.DTOs.ReferralTokenValidationOutcome.Success(referral.Id, referral.TokenVersion));

        var repo = new Mock<IReferralRepository>();
        repo.Setup(r => r.GetByIdGlobalAsync(referral.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(referral);

        ReferralComment? savedComment = null;
        IReadOnlyCollection<ReferralAttachment>? savedAttachments = null;
        var commentsRepo = new Mock<IReferralCommentRepository>();
        commentsRepo.Setup(r => r.AddWithAttachmentsAsync(
                It.IsAny<ReferralComment>(),
                It.IsAny<IReadOnlyCollection<ReferralAttachment>>(),
                It.IsAny<CancellationToken>()))
            .Callback<ReferralComment, IReadOnlyCollection<ReferralAttachment>, CancellationToken>((comment, attachments, _) =>
            {
                savedComment = comment;
                savedAttachments = attachments;
            })
            .Returns(Task.CompletedTask);

        var docClient = new Mock<IDocumentServiceClient>();
        docClient.Setup(d => d.UploadAsync(
                It.IsAny<Stream>(),
                "scan.png",
                "image/png",
                3,
                referral.TenantId,
                "scan.png",
                null,
                null,
                It.IsAny<string>(),
                "referral-comment",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentUploadResult(true, "doc-message-1", null));

        var sut = BuildService(repo, commentsRepo, emailService.Object, documents: docClient);
        await using var stream = new MemoryStream([1, 2, 3]);

        var result = await sut.PostPublicCommentWithAttachmentsAsync(
            "valid-token",
            "provider",
            "See attached.",
            [new ReferralMessageAttachmentUpload(stream, "scan.png", "image/png", 3)]);

        Assert.NotNull(result);
        Assert.NotNull(savedComment);
        var saved = Assert.Single(savedAttachments!);
        Assert.Equal(savedComment!.Id, saved.ReferralCommentId);
        Assert.Equal("doc-message-1", saved.ExternalDocumentId);
        Assert.Equal("scan.png", result!.Attachments.Single().FileName);
        commentsRepo.Verify(r => r.AddAsync(It.IsAny<ReferralComment>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PostPublicCommentWithAttachmentsAsync_InvalidToken_DoesNotUpload()
    {
        var repo = new Mock<IReferralRepository>();
        var commentsRepo = new Mock<IReferralCommentRepository>();
        var docClient = new Mock<IDocumentServiceClient>();
        var sut = BuildService(repo, commentsRepo, documents: docClient);
        await using var stream = new MemoryStream([1, 2, 3]);

        var result = await sut.PostPublicCommentWithAttachmentsAsync(
            "invalid-token",
            "provider",
            "See attached.",
            [new ReferralMessageAttachmentUpload(stream, "scan.png", "image/png", 3)]);

        Assert.Null(result);
        docClient.Verify(d => d.UploadAsync(
                It.IsAny<Stream>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<long>(),
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        commentsRepo.Verify(r => r.AddWithAttachmentsAsync(
                It.IsAny<ReferralComment>(),
                It.IsAny<IReadOnlyCollection<ReferralAttachment>>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static Referral BuildReferral()
        => BuildReferral(ReferringOrgId);

    private static Referral BuildReferral(Guid? referringOrganizationId)
    {
        return Referral.Create(
            TenantId,
            referringOrganizationId: referringOrganizationId,
            receivingOrganizationId: ReceivingOrgId,
            providerId: Guid.CreateVersion7(),
            subjectPartyId: null,
            subjectNameSnapshot: null,
            subjectDobSnapshot: null,
            clientFirstName: "Jamie",
            clientLastName: "Stone",
            clientDob: null,
            clientPhone: "555-0100",
            clientEmail: "jamie@example.com",
            caseNumber: "CASE-1",
            requestedService: "MRI",
            urgency: Referral.ValidUrgencies.Normal,
            notes: "Needs imaging",
            createdByUserId: null,
            referrerEmail: "firm@example.com",
            referrerName: "Sarah");
    }

    private static ReferralThreadService BuildService(
        Mock<IReferralRepository> referrals,
        Mock<IReferralCommentRepository> comments,
        IReferralEmailService? emailService = null,
        IServiceScopeFactory? scopeFactory = null,
        Mock<IReferralAttachmentRepository>? attachments = null,
        Mock<IDocumentServiceClient>? documents = null)
    {
        if (attachments is null)
        {
            attachments = new Mock<IReferralAttachmentRepository>();
            attachments.Setup(r => r.GetByReferralAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            attachments.Setup(r => r.GetByReferralCommentIdsAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<Guid>(),
                    It.IsAny<IReadOnlyCollection<Guid>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
        }
        documents ??= new Mock<IDocumentServiceClient>();

        emailService ??= Mock.Of<IReferralEmailService>(e =>
            e.ValidateViewTokenDetailed(It.IsAny<string>()) == CareConnect.Application.DTOs.ReferralTokenValidationOutcome.Failure(CareConnect.Application.DTOs.ReferralTokenFailureReasons.Malformed));
        scopeFactory ??= BuildScopeFactory(emailService);

        return new ReferralThreadService(
            referrals.Object,
            comments.Object,
            attachments.Object,
            documents.Object,
            emailService,
            Mock.Of<IIdentityOrganizationService>(),
            scopeFactory,
            NullLogger<ReferralThreadService>.Instance);
    }

    private static IServiceScopeFactory BuildScopeFactory(IReferralEmailService emailService)
    {
        var provider = new Mock<IServiceProvider>();
        provider.Setup(p => p.GetService(typeof(IReferralEmailService))).Returns(emailService);

        var scope = new Mock<IServiceScope>();
        scope.SetupGet(s => s.ServiceProvider).Returns(provider.Object);

        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);
        return scopeFactory.Object;
    }

    private static void SetProvider(Referral referral, Provider provider)
    {
        typeof(Referral).GetProperty(nameof(Referral.Provider))!.SetValue(referral, provider);
    }

    private static void SetReferralAttribution(Referral referral, string firstName, string lastName)
    {
        var attribution = ReferralAttribution.Create(
            referral.TenantId,
            firstName,
            lastName,
            $"{firstName}_{lastName}".ToUpperInvariant(),
            null,
            true,
            null,
            null);

        typeof(Referral).GetProperty(nameof(Referral.ReferralAttribution))!.SetValue(referral, attribution);
    }

    private static ReferralAttachment CreateMessageAttachment(
        Guid attachmentId,
        Guid tenantId,
        Guid referralId,
        Guid commentId,
        string fileName,
        string contentType,
        long fileSizeBytes,
        string documentId)
    {
        var attachment = ReferralAttachment.Create(
            tenantId,
            referralId,
            fileName,
            contentType,
            fileSizeBytes,
            externalDocumentId:      documentId,
            externalStorageProvider: AttachmentScope.Shared,
            status:                  "Uploaded",
            notes:                   null,
            createdByUserId:         null,
            referralCommentId:       commentId);

        typeof(ReferralAttachment).GetProperty(nameof(ReferralAttachment.Id))!
            .SetValue(attachment, attachmentId);

        return attachment;
    }

    private sealed class ProviderStub
    {
        public string Name { get; init; } = string.Empty;
        public string? Title { get; init; }
        public string? FirstName { get; init; }
        public string? LastName { get; init; }
        public string OrganizationName { get; init; } = string.Empty;
        public string Email { get; init; } = string.Empty;
        public string AccessStage { get; init; } = ProviderAccessStage.CommonPortal;

        public Provider ToDomain(Guid? organizationId)
        {
            var provider = Provider.Create(
                TenantId,
                Name,
                OrganizationName,
                Email,
                "555-0101",
                "123 Main",
                "Las Vegas",
                "NV",
                "89101",
                isActive: true,
                acceptingReferrals: true,
                createdByUserId: null,
                firstName: FirstName,
                lastName: LastName,
                title: Title);

            if (organizationId.HasValue)
                provider.LinkOrganization(organizationId.Value);

            if (AccessStage == ProviderAccessStage.CommonPortal)
                provider.MarkCommonPortalActivated(null);
            else if (AccessStage == ProviderAccessStage.Tenant)
                provider.MarkTenantProvisioned(Guid.CreateVersion7());

            return provider;
        }
    }
}
