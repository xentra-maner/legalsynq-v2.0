using CareConnect.Application.DTOs;
using CareConnect.Application.Interfaces;
using CareConnect.Application.Repositories;
using CareConnect.Application.Authorization;
using CareConnect.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CareConnect.Application.Services;

public class ReferralThreadService : IReferralThreadService
{
    private readonly IReferralRepository _referrals;
    private readonly IReferralCommentRepository _comments;
    private readonly IReferralAttachmentRepository _attachments;
    private readonly IDocumentServiceClient _documents;
    private readonly IReferralEmailService _emailService;
    private readonly IIdentityOrganizationService _identityOrgService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ReferralThreadService> _logger;

    public ReferralThreadService(
        IReferralRepository referrals,
        IReferralCommentRepository comments,
        IReferralAttachmentRepository attachments,
        IDocumentServiceClient documents,
        IReferralEmailService emailService,
        IIdentityOrganizationService identityOrgService,
        IServiceScopeFactory scopeFactory,
        ILogger<ReferralThreadService> logger)
    {
        _referrals = referrals;
        _comments = comments;
        _attachments = attachments;
        _documents = documents;
        _emailService = emailService;
        _identityOrgService = identityOrgService;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<PublicReferralAccessResult<PublicReferralThreadResponse>> GetPublicThreadAccessAsync(string token, CancellationToken ct = default)
    {
        var access = await GetReferralByTokenAsync(token, ct);
        if (access.Referral is null)
            return PublicReferralAccessResult<PublicReferralThreadResponse>.Failure(
                access.FailureReason ?? ReferralTokenFailureReasons.Malformed);

        var referral = access.Referral;

        var comments = await _comments.GetByReferralAsync(referral.TenantId, referral.Id, ct);
        var attachmentsByComment = await LoadCommentAttachmentsAsync(referral.TenantId, referral.Id, comments, ct);
        var attachments = await _attachments.GetByReferralAsync(referral.TenantId, referral.Id, ct);
        var treatmentTypeName = referral.TreatmentTypeId.HasValue
            ? await _referrals.GetTreatmentTypeNameAsync(referral.TreatmentTypeId.Value, ct)
            : null;

        var providerName = BuildProviderName(referral);
        var referrerFirmName = await ResolveReferrerFirmNameAsync(referral, ct);
        var location = ReferralLocationResolver.Resolve(referral);
        return PublicReferralAccessResult<PublicReferralThreadResponse>.Success(new PublicReferralThreadResponse
        {
            ReferralId = referral.Id,
            TenantId = referral.TenantId,
            ProviderId = referral.Provider?.Id ?? referral.ProviderId,
            Status = referral.Status,
            ClientName = $"{referral.ClientFirstName} {referral.ClientLastName}".Trim(),
            ClientPhone = referral.ClientPhone,
            ClientEmail = referral.ClientEmail,
            ClientDob = referral.ClientDob.HasValue
                ? referral.ClientDob.Value.ToString("MM/dd/yyyy")
                : null,
            CaseNumber = referral.CaseNumber,
            Service = referral.RequestedService ?? string.Empty,
            Urgency = referral.Urgency,
            Notes = referral.Notes,
            DateOfAccident = referral.DateOfAccident?.ToString("yyyy-MM-dd"),
            ProviderName = providerName,
            ProviderTitle = referral.Provider?.Title,
            ProviderFirstName = referral.Provider?.FirstName,
            ProviderLastName = referral.Provider?.LastName,
            ProviderEmail = referral.Provider?.Email ?? string.Empty,
            ProviderPhone = referral.Provider?.Phone ?? string.Empty,
            FacilityName = location.FacilityName,
            LocationAddressLine1 = location.AddressLine1,
            LocationCity = location.City,
            LocationState = location.State,
            LocationPostalCode = location.PostalCode,
            LocationIsMobile = location.IsMobile,
            ReferrerFirmName = referrerFirmName,
            ReferrerPhone = referral.ReferrerPhone,
            ReferrerName = referral.ReferrerName,
            ReferrerFirstName = referral.ReferrerFirstName,
            ReferrerLastName = referral.ReferrerLastName,
            ReferrerEmail = referral.ReferrerEmail,
            CreatedAtUtc = referral.CreatedAtUtc,
            TreatmentTypeId   = referral.TreatmentTypeId,
            TreatmentTypeName = treatmentTypeName,
            LienCompanyName = referral.LienCompanyName,
            LienCompanyEmail = referral.LienCompanyEmail,
            ProviderHasAccount = ProviderHasPortalAccount(referral.Provider),
            Comments = comments
                .Select(comment => MapComment(
                    comment,
                    attachmentsByComment.TryGetValue(comment.Id, out var commentAttachments)
                        ? commentAttachments
                        : []))
                .ToList(),
            Attachments = attachments
                .OrderBy(a => a.CreatedAtUtc)
                .Select(a => new ReferralThreadAttachmentResponse
                {
                    Id = a.Id,
                    FileName = a.FileName,
                    ContentType = a.ContentType,
                    FileSizeBytes = a.FileSizeBytes,
                })
                .ToList(),
        });
    }

    public async Task<PublicReferralThreadResponse?> GetPublicThreadAsync(string token, CancellationToken ct = default)
    {
        var result = await GetPublicThreadAccessAsync(token, ct);
        return result.Data;
    }

    public async Task<ReferralCommentResponse?> PostPublicCommentAsync(
        string token,
        string senderType,
        string message,
        CancellationToken ct = default)
        => await PostPublicCommentWithAttachmentsAsync(token, senderType, message, [], ct);

    public async Task<ReferralCommentResponse?> PostPublicCommentWithAttachmentsAsync(
        string token,
        string senderType,
        string message,
        IReadOnlyList<ReferralMessageAttachmentUpload> attachments,
        CancellationToken ct = default)
    {
        var access = await GetReferralByTokenAsync(token, ct);
        if (access.Referral is null)
            return null;
        var referral = access.Referral;

        var resolvedSenderName = senderType == "provider"
            ? referral.Provider?.Name ?? "Provider"
            : referral.ReferrerName ?? "Referrer";

        var comment = new ReferralComment
        {
            Id = Guid.CreateVersion7(),
            TenantId = referral.TenantId,
            ReferralId = referral.Id,
            SenderType = senderType.Trim(),
            SenderName = resolvedSenderName,
            Message = message.Trim(),
            CreatedAt = DateTime.UtcNow,
        };

        var savedAttachments = await UploadMessageAttachmentsAsync(
            referral,
            comment.Id,
            userId: null,
            attachments,
            ct);
        comment.Attachments = savedAttachments;

        if (savedAttachments.Count == 0)
            await _comments.AddAsync(comment, ct);
        else
            await _comments.AddWithAttachmentsAsync(comment, savedAttachments, ct);

        QueueCommentNotification(referral, comment);
        return MapComment(comment, savedAttachments);
    }

    public async Task<IReadOnlyList<ReferralCommentResponse>?> GetAuthenticatedCommentsAsync(
        Guid tenantId,
        Guid referralId,
        Guid? callerOrganizationId,
        string? callerEmail,
        bool useGlobalLookup,
        bool bypassParticipantCheck,
        CancellationToken ct = default)
    {
        var referral = await LoadAuthenticatedReferralAsync(
            tenantId,
            referralId,
            callerOrganizationId,
            callerEmail,
            useGlobalLookup,
            bypassParticipantCheck,
            ct);
        if (referral is null)
            return null;

        var comments = await _comments.GetByReferralAsync(referral.TenantId, referral.Id, ct);
        var attachmentsByComment = await LoadCommentAttachmentsAsync(referral.TenantId, referral.Id, comments, ct);
        return comments
            .Select(comment => MapComment(
                comment,
                attachmentsByComment.TryGetValue(comment.Id, out var commentAttachments)
                    ? commentAttachments
                    : []))
            .ToList();
    }

    public async Task<ReferralCommentResponse?> PostAuthenticatedCommentAsync(
        Guid tenantId,
        Guid referralId,
        Guid? callerOrganizationId,
        string? callerEmail,
        string senderName,
        string message,
        bool useGlobalLookup,
        CancellationToken ct = default)
        => await PostAuthenticatedCommentWithAttachmentsAsync(
            tenantId,
            referralId,
            callerOrganizationId,
            callerEmail,
            senderName,
            message,
            useGlobalLookup,
            [],
            ct: ct);

    public async Task<ReferralCommentResponse?> PostAuthenticatedCommentWithAttachmentsAsync(
        Guid tenantId,
        Guid referralId,
        Guid? callerOrganizationId,
        string? callerEmail,
        string senderName,
        string message,
        bool useGlobalLookup,
        IReadOnlyList<ReferralMessageAttachmentUpload> attachments,
        Guid? createdByUserId = null,
        CancellationToken ct = default)
    {
        var participant = await LoadAuthenticatedCommentParticipantAsync(
            tenantId,
            referralId,
            callerOrganizationId,
            callerEmail,
            useGlobalLookup,
            ct);
        if (participant is null)
            return null;

        var comment = new ReferralComment
        {
            Id = Guid.CreateVersion7(),
            TenantId = participant.Referral.TenantId,
            ReferralId = participant.Referral.Id,
            SenderType = participant.SenderType,
            SenderName = senderName.Trim(),
            Message = message.Trim(),
            CreatedAt = DateTime.UtcNow,
        };

        var savedAttachments = await UploadMessageAttachmentsAsync(
            participant.Referral,
            comment.Id,
            userId: createdByUserId,
            attachments,
            ct);
        comment.Attachments = savedAttachments;

        if (savedAttachments.Count == 0)
            await _comments.AddAsync(comment, ct);
        else
            await _comments.AddWithAttachmentsAsync(comment, savedAttachments, ct);

        QueueCommentNotification(participant.Referral, comment);
        return MapComment(comment, savedAttachments);
    }

    private async Task<TokenScopedReferralResult> GetReferralByTokenAsync(string token, CancellationToken ct)
    {
        var tokenValidation = _emailService.ValidateViewTokenDetailed(token);
        if (!tokenValidation.IsValid)
        {
            LogPublicTokenFailure("thread", tokenValidation, null);
            return new TokenScopedReferralResult(null, tokenValidation.FailureReason);
        }

        var referral = await _referrals.GetByIdGlobalAsync(tokenValidation.ReferralId!.Value, ct);
        if (referral is null)
        {
            LogPublicTokenFailure(
                "thread",
                tokenValidation,
                tokenValidation.ReferralId,
                failureReasonOverride: ReferralTokenFailureReasons.ReferralNotFound);
            return new TokenScopedReferralResult(null, ReferralTokenFailureReasons.ReferralNotFound);
        }

        if (referral.TokenVersion != tokenValidation.TokenVersion)
        {
            LogPublicTokenFailure(
                "thread",
                tokenValidation,
                referral.Id,
                currentReferralTokenVersion: referral.TokenVersion,
                failureReasonOverride: ReferralTokenFailureReasons.Revoked);
            return new TokenScopedReferralResult(null, ReferralTokenFailureReasons.Revoked);
        }

        return new TokenScopedReferralResult(referral, null);
    }

    private void LogPublicTokenFailure(
        string surface,
        ReferralTokenValidationOutcome tokenValidation,
        Guid? requestedReferralId,
        int? currentReferralTokenVersion = null,
        string? failureReasonOverride = null)
    {
        var failureReason = failureReasonOverride ?? tokenValidation.FailureReason ?? ReferralTokenFailureReasons.Malformed;
        _logger.LogWarning(
            "Public referral token rejected on surface {Surface}. FailureReason={FailureReason} RequestedReferralId={RequestedReferralId} TokenReferralId={TokenReferralId} TokenVersion={TokenVersion} CurrentReferralTokenVersion={CurrentReferralTokenVersion}",
            surface,
            failureReason,
            requestedReferralId,
            tokenValidation.ReferralId,
            tokenValidation.TokenVersion,
            currentReferralTokenVersion);
    }

    private async Task<Referral?> LoadAuthenticatedReferralAsync(
        Guid tenantId,
        Guid referralId,
        Guid? callerOrganizationId,
        string? callerEmail,
        bool useGlobalLookup,
        bool bypassParticipantCheck,
        CancellationToken ct)
    {
        var referral = useGlobalLookup
            ? await _referrals.GetByIdGlobalAsync(referralId, ct)
            : await _referrals.GetByIdAsync(tenantId, referralId, ct);
        if (referral is null)
            return null;

        if (bypassParticipantCheck)
            return referral;

        return IsAuthenticatedParticipant(referral, callerOrganizationId, callerEmail)
            ? referral
            : null;
    }

    private async Task<AuthenticatedCommentParticipant?> LoadAuthenticatedCommentParticipantAsync(
        Guid tenantId,
        Guid referralId,
        Guid? callerOrganizationId,
        string? callerEmail,
        bool useGlobalLookup,
        CancellationToken ct)
    {
        var referral = await LoadAuthenticatedReferralAsync(
            tenantId,
            referralId,
            callerOrganizationId,
            callerEmail,
            useGlobalLookup,
            bypassParticipantCheck: false,
            ct);
        if (referral is null)
            return null;

        var senderType = ResolveSenderType(referral, callerOrganizationId, callerEmail);
        return senderType is null
            ? null
            : new AuthenticatedCommentParticipant(referral, senderType);
    }

    private void QueueCommentNotification(Referral referral, ReferralComment comment)
    {
        var referralId = referral.Id;
        _logger.LogInformation(
            "ReferralThread: comment posted on referral {ReferralId} by {SenderType} '{SenderName}'.",
            referralId,
            comment.SenderType,
            comment.SenderName);

        _ = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var emailService = scope.ServiceProvider.GetRequiredService<IReferralEmailService>();
            try
            {
                await emailService.SendCommentNotificationAsync(referral, comment, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "ReferralThread: failed to send comment notification for referral {ReferralId}.",
                    referralId);
            }
        }, CancellationToken.None);
    }

    private async Task<Dictionary<Guid, List<ReferralAttachment>>> LoadCommentAttachmentsAsync(
        Guid tenantId,
        Guid referralId,
        IReadOnlyList<ReferralComment> comments,
        CancellationToken ct)
    {
        var commentIds = comments
            .Select(c => c.Id)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        if (commentIds.Count == 0)
            return [];

        var rows = await _attachments.GetByReferralCommentIdsAsync(tenantId, referralId, commentIds, ct);
        return rows
            .Where(a => a.ReferralCommentId.HasValue)
            .GroupBy(a => a.ReferralCommentId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(a => a.CreatedAtUtc).ToList());
    }

    private async Task<List<ReferralAttachment>> UploadMessageAttachmentsAsync(
        Referral referral,
        Guid commentId,
        Guid? userId,
        IReadOnlyList<ReferralMessageAttachmentUpload> uploads,
        CancellationToken ct)
    {
        if (uploads.Count == 0)
            return [];

        var attachments = new List<ReferralAttachment>(uploads.Count);
        foreach (var upload in uploads)
        {
            var uploadResult = await _documents.UploadAsync(
                upload.FileContent,
                upload.FileName,
                upload.ContentType,
                upload.FileSizeBytes,
                referral.TenantId,
                title:         upload.FileName,
                referenceId:   commentId.ToString(),
                referenceType: "referral-comment",
                ct:            ct);

            if (!uploadResult.Success || string.IsNullOrWhiteSpace(uploadResult.DocumentId))
            {
                throw new InvalidOperationException(
                    $"Document upload failed: {uploadResult.Error ?? "unknown error"}");
            }

            attachments.Add(ReferralAttachment.Create(
                referral.TenantId,
                referral.Id,
                upload.FileName,
                upload.ContentType,
                upload.FileSizeBytes,
                externalDocumentId:      uploadResult.DocumentId,
                externalStorageProvider: AttachmentScope.Shared,
                status:                  "Uploaded",
                notes:                   null,
                createdByUserId:         userId,
                referralCommentId:       commentId));
        }

        return attachments;
    }

    private static ReferralCommentResponse MapComment(
        ReferralComment comment,
        IReadOnlyList<ReferralAttachment>? attachments = null) => new()
    {
        Id = comment.Id,
        SenderType = comment.SenderType,
        SenderName = comment.SenderName,
        Message = comment.Message,
        CreatedAtUtc = NormalizeUtc(comment.CreatedAt),
        Attachments = (attachments ?? comment.Attachments)
            .OrderBy(a => a.CreatedAtUtc)
            .Select(MapMessageAttachment)
            .ToList(),
    };

    private static ReferralMessageAttachmentResponse MapMessageAttachment(ReferralAttachment attachment) => new()
    {
        Id = attachment.Id,
        FileName = attachment.FileName,
        ContentType = attachment.ContentType,
        FileSizeBytes = attachment.FileSizeBytes,
        CreatedAtUtc = attachment.CreatedAtUtc,
    };

    private static DateTime NormalizeUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc
            ? value
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private async Task<string?> ResolveReferrerFirmNameAsync(Referral referral, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(referral.ReferrerFirmName))
            return referral.ReferrerFirmName;

        if (referral.ReferringOrganizationId.HasValue)
        {
            try
            {
                var orgName = await _identityOrgService.GetOrganizationNameAsync(referral.ReferringOrganizationId.Value, ct);
                if (!string.IsNullOrWhiteSpace(orgName))
                    return orgName;
            }
            catch { /* non-fatal */ }
        }

        return null;
    }

    private static string BuildProviderName(Referral referral)
    {
        if (referral.Provider is null)
            return "Provider";

        return string.IsNullOrWhiteSpace(referral.Provider.OrganizationName)
            ? referral.Provider.Name
            : referral.Provider.OrganizationName;
    }

    private static bool ProviderHasPortalAccount(Provider? provider) =>
        provider is not null && (
            ProviderAccessStage.IsAtLeast(provider.AccessStage, ProviderAccessStage.CommonPortal) ||
            provider.OrganizationId.HasValue ||
            provider.IdentityUserId.HasValue);

    private static bool IsAuthenticatedParticipant(Referral referral, Guid? callerOrganizationId, string? callerEmail)
    {
        if (CareConnectParticipantHelper.IsReferralParticipant(referral, callerOrganizationId))
            return true;

        return referral.ReferringOrganizationId is null
            && !string.IsNullOrWhiteSpace(callerEmail)
            && string.Equals(referral.ReferrerEmail, callerEmail, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveSenderType(Referral referral, Guid? callerOrganizationId, string? callerEmail)
    {
        if (callerOrganizationId.HasValue && referral.ReceivingOrganizationId == callerOrganizationId)
            return "provider";

        if (callerOrganizationId.HasValue && referral.ReferringOrganizationId == callerOrganizationId)
            return "referrer";

        if (referral.ReferringOrganizationId is null
            && !string.IsNullOrWhiteSpace(callerEmail)
            && string.Equals(referral.ReferrerEmail, callerEmail, StringComparison.OrdinalIgnoreCase))
        {
            return "referrer";
        }

        return null;
    }

    private sealed record AuthenticatedCommentParticipant(Referral Referral, string SenderType);
    private sealed record TokenScopedReferralResult(Referral? Referral, string? FailureReason);
}
