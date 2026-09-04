using System.Text.RegularExpressions;
using BuildingBlocks.Exceptions;
using CareConnect.Application.DTOs;
using CareConnect.Application.Interfaces;
using CareConnect.Application.Repositories;
using CareConnect.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CareConnect.Application.Services;

public sealed class PendingReferralRequestService : IPendingReferralRequestService
{
    private readonly IPendingReferralRequestRepository _pending;
    private readonly IReferralAttributionRepository _attributions;
    private readonly IProviderRepository _providers;
    private readonly INetworkRepository _networks;
    private readonly IIdentityOrganizationService _identityOrganizations;
    private readonly IOrganizationRelationshipResolver _relationshipResolver;
    private readonly IReferralRepository _referrals;
    private readonly IPendingReferralAttachmentRepository _pendingAttachments;
    private readonly IReferralAttachmentRepository _referralAttachments;
    private readonly IDocumentServiceClient _documents;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PendingReferralRequestService> _logger;

    public PendingReferralRequestService(
        IPendingReferralRequestRepository pending,
        IReferralAttributionRepository attributions,
        IProviderRepository providers,
        INetworkRepository networks,
        IIdentityOrganizationService identityOrganizations,
        IOrganizationRelationshipResolver relationshipResolver,
        IReferralRepository referrals,
        IPendingReferralAttachmentRepository pendingAttachments,
        IReferralAttachmentRepository referralAttachments,
        IDocumentServiceClient documents,
        IServiceScopeFactory scopeFactory,
        ILogger<PendingReferralRequestService> logger)
    {
        _pending = pending;
        _attributions = attributions;
        _providers = providers;
        _networks = networks;
        _identityOrganizations = identityOrganizations;
        _relationshipResolver = relationshipResolver;
        _referrals = referrals;
        _pendingAttachments = pendingAttachments;
        _referralAttachments = referralAttachments;
        _documents = documents;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<List<LawFirmOptionResponse>> ListLawFirmOptionsAsync(Guid tenantId, CancellationToken ct = default)
    {
        var lawFirms = await _identityOrganizations.ListLawFirmOrganizationsAsync(tenantId, ct);
        return lawFirms
            .OrderBy(o => o.Name)
            .Select(o => new LawFirmOptionResponse { Id = o.Id, Name = o.Name })
            .ToList();
    }

    public async Task<PendingReferralRequestResponse> CreateAsync(
        Guid tenantId, Guid referralAttributionId, CreatePendingReferralRequest request, CancellationToken ct = default)
    {
        ValidateCreate(request);

        var attribution = await _attributions.GetByIdAsync(tenantId, referralAttributionId, ct);
        if (attribution is null || !attribution.IsActive)
            throw new ValidationException("One or more validation errors occurred.",
                new() { ["code"] = ["The access code no longer grants access."] });
        await ValidateLawFirmAsync(tenantId, request.LawFirmOrganizationId, ct);

        var preferences = await ResolvePreferencesAsync(tenantId, request, ct);

        var pending = PendingReferralRequest.Create(
            tenantId,
            request.LawFirmOrganizationId,
            referralAttributionId,
            request.ClientFirstName,
            request.ClientLastName,
            request.ClientDob,
            request.ClientPhone,
            request.ClientEmail ?? string.Empty,
            request.CaseNumber,
            request.RequestedService,
            request.Urgency,
            request.TreatmentTypeId,
            request.DateOfAccident,
            request.Notes,
            request.LienCompanyName,
            request.LienCompanyEmail);

        for (var i = 0; i < preferences.Count; i++)
        {
            var preference = preferences[i];
            pending.AddProviderPreference(
                preference.ProviderId,
                preference.FacilityId,
                preference.ProviderName,
                preference.FacilityName,
                i);
        }

        await _pending.AddAsync(pending, ct);
        return await EnrichLawFirmNameAsync(ToResponse(pending), ct);
    }

    public async Task<PagedResponse<PendingReferralRequestResponse>> SearchForLawFirmAsync(
        Guid tenantId, Guid lawFirmOrganizationId, string? status, int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var (items, total) = await _pending.SearchAsync(tenantId, lawFirmOrganizationId, status, page, pageSize, ct);
        var responses = new List<PendingReferralRequestResponse>(items.Count);
        foreach (var item in items)
            responses.Add(await EnrichLawFirmNameAsync(ToResponse(item), ct));

        return new PagedResponse<PendingReferralRequestResponse>
        {
            Items = responses,
            Page = page,
            PageSize = pageSize,
            TotalCount = total,
        };
    }

    public async Task<PagedResponse<PendingReferralRequestResponse>> SearchForAttributionAsync(
        Guid tenantId,
        Guid referralAttributionId,
        string? status,
        DateTime? createdFrom,
        DateTime? createdTo,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var (items, total) = await _pending.SearchForAttributionAsync(
            tenantId,
            referralAttributionId,
            status,
            createdFrom,
            createdTo,
            page,
            pageSize,
            ct);

        var responses = new List<PendingReferralRequestResponse>(items.Count);
        foreach (var item in items)
            responses.Add(await EnrichLawFirmNameAsync(ToResponse(item), ct));

        return new PagedResponse<PendingReferralRequestResponse>
        {
            Items = responses,
            Page = page,
            PageSize = pageSize,
            TotalCount = total,
        };
    }

    public async Task<PendingReferralRequestResponse?> GetForAttributionAsync(
        Guid tenantId, Guid referralAttributionId, Guid id, CancellationToken ct = default)
    {
        var item = await _pending.GetForAttributionAsync(tenantId, referralAttributionId, id, ct);
        return item is null ? null : await EnrichLawFirmNameAsync(ToResponse(item), ct);
    }

    public async Task<PendingReferralRequestResponse?> GetForLawFirmAsync(
        Guid tenantId, Guid lawFirmOrganizationId, Guid id, CancellationToken ct = default)
    {
        var item = await _pending.GetByIdAsync(tenantId, id, ct);
        if (item is null || item.LawFirmOrganizationId != lawFirmOrganizationId)
            return null;

        return await EnrichLawFirmNameAsync(ToResponse(item), ct);
    }

    public async Task<PendingReferralRequestResponse> UpdateForLawFirmAsync(
        Guid tenantId, Guid lawFirmOrganizationId, Guid id, Guid? userId, UpdatePendingReferralRequest request, CancellationToken ct = default)
    {
        ValidateUpdate(request);

        var item = await _pending.GetByIdAsync(tenantId, id, ct)
            ?? throw new NotFoundException($"Pending referral request '{id}' was not found.");
        if (item.LawFirmOrganizationId != lawFirmOrganizationId)
            throw new NotFoundException($"Pending referral request '{id}' was not found.");
        if (item.Status != PendingReferralRequest.Statuses.PendingReview)
            throw new ConflictException("PENDING_REFERRAL_NOT_EDITABLE");

        item.UpdateReviewDetails(
            request.ClientFirstName,
            request.ClientLastName,
            request.ClientDob,
            request.ClientPhone,
            request.ClientEmail,
            request.CaseNumber,
            request.RequestedService,
            request.Urgency,
            request.TreatmentTypeId,
            request.DateOfAccident,
            request.Notes,
            request.LienCompanyName,
            request.LienCompanyEmail,
            userId);

        await _pending.UpdateAsync(item, ct);
        return await EnrichLawFirmNameAsync(ToResponse(item), ct);
    }

    public async Task<PendingReferralRequestResponse> CancelForLawFirmAsync(
        Guid tenantId, Guid lawFirmOrganizationId, Guid id, Guid? userId, CancellationToken ct = default)
    {
        var item = await _pending.GetByIdAsync(tenantId, id, ct)
            ?? throw new NotFoundException($"Pending referral request '{id}' was not found.");
        if (item.LawFirmOrganizationId != lawFirmOrganizationId)
            throw new NotFoundException($"Pending referral request '{id}' was not found.");
        if (item.Status != PendingReferralRequest.Statuses.PendingReview)
            throw new ConflictException("PENDING_REFERRAL_NOT_DECLINABLE");

        item.MarkCancelled(userId);
        await _pending.UpdateAsync(item, ct);
        return await EnrichLawFirmNameAsync(ToResponse(item), ct);
    }

    public async Task<AttachmentMetadataResponse> UploadAttachmentForAttributionAsync(
        Guid tenantId,
        Guid referralAttributionId,
        Guid id,
        Stream fileContent,
        string fileName,
        string contentType,
        long fileSizeBytes,
        CancellationToken ct = default)
    {
        var item = await _pending.GetForAttributionAsync(tenantId, referralAttributionId, id, ct);
        if (item is null || item.Status != PendingReferralRequest.Statuses.PendingReview)
            throw new NotFoundException($"Pending referral request '{id}' was not found.");

        var uploadResult = await _documents.UploadAsync(
            fileContent,
            fileName,
            contentType,
            fileSizeBytes,
            tenantId,
            title: fileName,
            referenceId: id.ToString(),
            referenceType: "pending-referral-request",
            ct: ct);

        if (!uploadResult.Success || string.IsNullOrWhiteSpace(uploadResult.DocumentId))
            throw new InvalidOperationException(
                $"Document upload failed: {uploadResult.Error ?? "unknown error"}");

        var attachment = PendingReferralAttachment.Create(
            tenantId,
            id,
            fileName,
            contentType,
            fileSizeBytes,
            externalDocumentId: uploadResult.DocumentId,
            externalStorageProvider: AttachmentScope.Shared,
            status: "Uploaded",
            notes: null,
            createdByUserId: null);

        await _pendingAttachments.AddAsync(attachment, ct);
        return ToAttachmentResponse(attachment);
    }

    public async Task<AttachmentMetadataResponse> UploadAttachmentForLawFirmAsync(
        Guid tenantId,
        Guid lawFirmOrganizationId,
        Guid id,
        Guid? userId,
        Stream fileContent,
        string fileName,
        string contentType,
        long fileSizeBytes,
        CancellationToken ct = default)
    {
        var item = await _pending.GetByIdAsync(tenantId, id, ct);
        if (item is null || item.LawFirmOrganizationId != lawFirmOrganizationId || item.Status != PendingReferralRequest.Statuses.PendingReview)
            throw new NotFoundException($"Pending referral request '{id}' was not found.");

        var uploadResult = await _documents.UploadAsync(
            fileContent,
            fileName,
            contentType,
            fileSizeBytes,
            tenantId,
            title: fileName,
            referenceId: id.ToString(),
            referenceType: "pending-referral-request",
            ct: ct);

        if (!uploadResult.Success || string.IsNullOrWhiteSpace(uploadResult.DocumentId))
            throw new InvalidOperationException(
                $"Document upload failed: {uploadResult.Error ?? "unknown error"}");

        var attachment = PendingReferralAttachment.Create(
            tenantId,
            id,
            fileName,
            contentType,
            fileSizeBytes,
            externalDocumentId: uploadResult.DocumentId,
            externalStorageProvider: AttachmentScope.Shared,
            status: "Uploaded",
            notes: null,
            createdByUserId: userId);

        await _pendingAttachments.AddAsync(attachment, ct);
        return ToAttachmentResponse(attachment);
    }

    public async Task<SignedUrlResponse?> GetAttachmentSignedUrlForLawFirmAsync(
        Guid tenantId,
        Guid lawFirmOrganizationId,
        Guid id,
        Guid attachmentId,
        bool isDownload,
        CancellationToken ct = default)
    {
        var item = await _pending.GetByIdAsync(tenantId, id, ct);
        if (item is null || item.LawFirmOrganizationId != lawFirmOrganizationId)
            throw new NotFoundException($"Pending referral request '{id}' was not found.");

        var attachment = item.Attachments.FirstOrDefault(a => a.Id == attachmentId)
            ?? throw new NotFoundException($"Attachment '{attachmentId}' was not found.");

        if (string.IsNullOrWhiteSpace(attachment.ExternalDocumentId))
            throw new InvalidOperationException("Attachment has no associated document in the Documents service.");

        var result = await _documents.GetSignedUrlAsync(tenantId, attachment.ExternalDocumentId, isDownload, ct);
        if (result is null) return null;

        return new SignedUrlResponse
        {
            Url = result.RedeemUrl,
            ExpiresInSeconds = result.ExpiresInSeconds,
        };
    }

    public async Task<ReferralResponse> ConvertAsync(
        Guid tenantId,
        Guid lawFirmOrganizationId,
        Guid id,
        Guid? userId,
        string? userEmail,
        string? userName,
        ConvertPendingReferralRequest request,
        CancellationToken ct = default)
    {
        var item = await _pending.GetByIdAsync(tenantId, id, ct)
            ?? throw new NotFoundException($"Pending referral request '{id}' was not found.");
        if (item.LawFirmOrganizationId != lawFirmOrganizationId)
            throw new NotFoundException($"Pending referral request '{id}' was not found.");
        if (item.Status != PendingReferralRequest.Statuses.PendingReview)
            throw new ConflictException("PENDING_REFERRAL_ALREADY_CONVERTED");

        var lawFirmName = await _identityOrganizations.GetOrganizationNameAsync(lawFirmOrganizationId, ct);
        var targets = await ResolveConversionTargetsAsync(tenantId, item, request, ct);
        var referrals = new List<Referral>(targets.Count);

        foreach (var target in targets)
        {
            Guid? orgRelationshipId = null;
            if (target.ReceivingOrganizationId.HasValue)
            {
                orgRelationshipId = await _relationshipResolver.FindActiveRelationshipAsync(
                    lawFirmOrganizationId,
                    target.ReceivingOrganizationId.Value,
                    ct);
            }

            referrals.Add(Referral.Create(
                tenantId,
                referringOrganizationId: lawFirmOrganizationId,
                receivingOrganizationId: target.ReceivingOrganizationId,
                providerId: target.Provider.Id,
                subjectPartyId: null,
                subjectNameSnapshot: null,
                subjectDobSnapshot: null,
                clientFirstName: item.ClientFirstName,
                clientLastName: item.ClientLastName,
                clientDob: item.ClientDob,
                clientPhone: item.ClientPhone,
                clientEmail: item.ClientEmail,
                caseNumber: item.CaseNumber,
                requestedService: item.RequestedService,
                urgency: item.Urgency,
                notes: item.Notes,
                createdByUserId: userId,
                organizationRelationshipId: orgRelationshipId,
                referrerEmail: userEmail,
                referrerName: userName,
                referrerFirmName: lawFirmName,
                treatmentTypeId: item.TreatmentTypeId,
                dateOfAccident: item.DateOfAccident,
                facilityId: target.FacilityId,
                referralAttributionId: item.ReferralAttributionId,
                origin: ReferralOrigin.ReferralAssociate,
                lienCompanyName: item.LienCompanyName,
                lienCompanyEmail: item.LienCompanyEmail));
        }

        var primaryReferral = referrals[0];
        item.MarkConverted(primaryReferral.Id, userId);
        await _pending.UpdateAsync(item, referrals, ct);

        foreach (var referral in referrals)
            await CopyPendingAttachmentsToReferralAsync(tenantId, item.Id, referral.Id, userId, ct);

        var treatmentTypeName = item.TreatmentTypeId.HasValue
            ? await _referrals.GetTreatmentTypeNameAsync(item.TreatmentTypeId.Value, ct)
            : null;

        foreach (var referral in referrals)
            FireProviderNotification(referral.TenantId, referral.Id, referral.ProviderId, treatmentTypeName);

        var loaded = await _referrals.GetByIdAsync(tenantId, primaryReferral.Id, ct)
            ?? throw new NotFoundException($"Referral '{primaryReferral.Id}' was not found after conversion.");
        return ToReferralResponse(loaded, treatmentTypeName);
    }

    private async Task<List<ConversionTarget>> ResolveConversionTargetsAsync(
        Guid tenantId,
        PendingReferralRequest item,
        ConvertPendingReferralRequest request,
        CancellationToken ct)
    {
        var selections = BuildConversionSelections(item, request);
        var targets = new List<ConversionTarget>(selections.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var selection in selections)
        {
            var target = await ResolveConversionTargetAsync(tenantId, selection, ct);
            var key = $"{target.Provider.Id:N}:{target.FacilityId?.ToString("N") ?? string.Empty}";
            if (seen.Add(key))
                targets.Add(target);
        }

        if (targets.Count == 0)
        {
            throw new ValidationException("One or more validation errors occurred.",
                new() { ["providerId"] = ["ProviderId or NetworkProviderId is required."] });
        }

        return targets;
    }

    private static List<PendingReferralProviderSelectionRequest> BuildConversionSelections(
        PendingReferralRequest item,
        ConvertPendingReferralRequest request)
    {
        if (request.ProviderSelections is { Count: > 0 })
            return request.ProviderSelections;

        if (request.NetworkProviderId.HasValue || (request.ProviderId.HasValue && request.ProviderId.Value != Guid.Empty))
        {
            return
            [
                new PendingReferralProviderSelectionRequest
                {
                    ProviderId = request.ProviderId,
                    NetworkProviderId = request.NetworkProviderId,
                    FacilityId = request.FacilityId,
                },
            ];
        }

        var preferences = item.ProviderPreferences
            .OrderBy(p => p.DisplayOrder)
            .Select(p => new PendingReferralProviderSelectionRequest
            {
                ProviderId = p.ProviderId,
                FacilityId = p.FacilityId,
            })
            .ToList();

        if (preferences.Count > 0)
            return preferences;

        return item.RecommendedProviderId.HasValue
            ?
            [
                new PendingReferralProviderSelectionRequest
                {
                    ProviderId = item.RecommendedProviderId,
                    FacilityId = item.RecommendedFacilityId,
                },
            ]
            : [];
    }

    private async Task<ConversionTarget> ResolveConversionTargetAsync(
        Guid tenantId,
        PendingReferralProviderSelectionRequest selection,
        CancellationToken ct)
    {
        if (selection.NetworkProviderId.HasValue)
        {
            var membership = await _networks.GetTenantNetworkMembershipAsync(tenantId, selection.NetworkProviderId.Value, ct)
                ?? throw new NotFoundException($"Network provider '{selection.NetworkProviderId.Value}' was not found.");
            if (!membership.IsActive || !membership.AcceptingReferrals)
                throw new ValidationException("One or more validation errors occurred.",
                    new() { ["networkProviderId"] = ["Selected provider location is not accepting referrals."] });

            if (selection.ProviderId.HasValue && selection.ProviderId.Value != Guid.Empty && selection.ProviderId.Value != membership.ProviderId)
                throw new NotFoundException($"Network provider '{selection.NetworkProviderId.Value}' was not found.");

            return new ConversionTarget(membership.Provider, membership.FacilityId, membership.Provider.OrganizationId);
        }

        if (selection.ProviderId.HasValue && selection.ProviderId.Value != Guid.Empty)
        {
            var provider = await _providers.GetByIdCrossAsync(selection.ProviderId.Value, ct)
                ?? throw new NotFoundException($"Provider '{selection.ProviderId.Value}' was not found.");
            var facilityId = ResolveProviderFacilityId(provider, selection.FacilityId, requireActive: true, fieldName: "facilityId");
            return new ConversionTarget(provider, facilityId, provider.OrganizationId);
        }

        throw new ValidationException("One or more validation errors occurred.",
            new() { ["providerId"] = ["ProviderId or NetworkProviderId is required."] });
    }

    private void FireProviderNotification(Guid tenantId, Guid referralId, Guid providerId, string? treatmentTypeName)
    {
        var scopeFactory = _scopeFactory;
        var logger = _logger;
        _ = Task.Run(async () =>
        {
            using var scope = scopeFactory.CreateScope();
            try
            {
                var referrals = scope.ServiceProvider.GetRequiredService<IReferralRepository>();
                var providers = scope.ServiceProvider.GetRequiredService<IProviderRepository>();
                var emailSvc = scope.ServiceProvider.GetRequiredService<IReferralEmailService>();
                var referral = await referrals.GetByIdAsync(tenantId, referralId, CancellationToken.None);
                if (referral is null) return;

                var provider = await providers.GetByIdCrossAsync(providerId, CancellationToken.None);
                if (provider is null) return;

                await emailSvc.SendNewReferralNotificationAsync(referral, provider, treatmentTypeName, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Background referral notification failed after pending conversion for referral {ReferralId}.", referralId);
            }
        });
    }

    private async Task CopyPendingAttachmentsToReferralAsync(
        Guid tenantId,
        Guid pendingReferralRequestId,
        Guid referralId,
        Guid? userId,
        CancellationToken ct)
    {
        var attachments = await _pendingAttachments.GetByRequestAsync(tenantId, pendingReferralRequestId, ct);
        foreach (var pendingAttachment in attachments)
        {
            var referralAttachment = ReferralAttachment.Create(
                tenantId,
                referralId,
                pendingAttachment.FileName,
                pendingAttachment.ContentType,
                pendingAttachment.FileSizeBytes,
                pendingAttachment.ExternalDocumentId,
                pendingAttachment.ExternalStorageProvider ?? AttachmentScope.Shared,
                pendingAttachment.Status,
                pendingAttachment.Notes,
                userId);

            await _referralAttachments.AddAsync(referralAttachment, ct);
        }
    }

    private async Task<PendingReferralRequestResponse> EnrichLawFirmNameAsync(PendingReferralRequestResponse response, CancellationToken ct)
    {
        response.LawFirmName = await _identityOrganizations.GetOrganizationNameAsync(response.LawFirmOrganizationId, ct);
        return response;
    }

    private static void ValidateCreate(CreatePendingReferralRequest r)
    {
        r.PreferredProviders ??= new();
        var errors = new Dictionary<string, string[]>();
        if (r.LawFirmOrganizationId == Guid.Empty) errors["lawFirmOrganizationId"] = ["LawFirmOrganizationId is required."];
        if (string.IsNullOrWhiteSpace(r.ClientFirstName)) errors["clientFirstName"] = ["ClientFirstName is required."];
        if (string.IsNullOrWhiteSpace(r.ClientLastName)) errors["clientLastName"] = ["ClientLastName is required."];
        if (!r.ClientDob.HasValue) errors["clientDob"] = ["ClientDob is required."];
        if (string.IsNullOrWhiteSpace(r.ClientPhone)) errors["clientPhone"] = ["ClientPhone is required."];
        if (!r.DateOfAccident.HasValue) errors["dateOfAccident"] = ["DateOfAccident is required."];
        if (!Referral.ValidUrgencies.All.Contains(r.Urgency)) errors["urgency"] = [$"Urgency must be one of: {string.Join(", ", Referral.ValidUrgencies.All)}."];
        if (!string.IsNullOrWhiteSpace(r.ClientEmail) && !Regex.IsMatch(r.ClientEmail.Trim(), @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
            errors["clientEmail"] = ["ClientEmail format is invalid."];
        if (!string.IsNullOrWhiteSpace(r.LienCompanyEmail) && !Regex.IsMatch(r.LienCompanyEmail.Trim(), @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
            errors["lienCompanyEmail"] = ["LienCompanyEmail format is invalid."];
        if (r.RecommendedProviderId.HasValue && r.RecommendedProviderId.Value == Guid.Empty)
            errors["recommendedProviderId"] = ["RecommendedProviderId is invalid."];
        if (r.RecommendedFacilityId.HasValue && r.RecommendedFacilityId.Value == Guid.Empty)
            errors["recommendedFacilityId"] = ["RecommendedFacilityId is invalid."];
        if (r.PreferredProviders.Count > 10)
            errors["preferredProviders"] = ["Up to 10 preferred providers can be selected."];
        for (var i = 0; i < r.PreferredProviders.Count; i++)
        {
            if (r.PreferredProviders[i].ProviderId == Guid.Empty)
                errors[$"preferredProviders[{i}].providerId"] = ["ProviderId is required."];
            if (r.PreferredProviders[i].FacilityId == Guid.Empty)
                errors[$"preferredProviders[{i}].facilityId"] = ["FacilityId is invalid."];
        }
        if (errors.Count > 0)
            throw new ValidationException("One or more validation errors occurred.", errors);
    }

    private static void ValidateUpdate(UpdatePendingReferralRequest r)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(r.ClientFirstName)) errors["clientFirstName"] = ["ClientFirstName is required."];
        if (string.IsNullOrWhiteSpace(r.ClientLastName)) errors["clientLastName"] = ["ClientLastName is required."];
        if (!r.ClientDob.HasValue) errors["clientDob"] = ["ClientDob is required."];
        if (string.IsNullOrWhiteSpace(r.ClientPhone)) errors["clientPhone"] = ["ClientPhone is required."];
        if (!r.DateOfAccident.HasValue) errors["dateOfAccident"] = ["DateOfAccident is required."];
        if (!Referral.ValidUrgencies.All.Contains(r.Urgency)) errors["urgency"] = [$"Urgency must be one of: {string.Join(", ", Referral.ValidUrgencies.All)}."];
        if (!string.IsNullOrWhiteSpace(r.ClientEmail) && !Regex.IsMatch(r.ClientEmail.Trim(), @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
            errors["clientEmail"] = ["ClientEmail format is invalid."];
        if (!string.IsNullOrWhiteSpace(r.LienCompanyEmail) && !Regex.IsMatch(r.LienCompanyEmail.Trim(), @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
            errors["lienCompanyEmail"] = ["LienCompanyEmail format is invalid."];
        if (errors.Count > 0)
            throw new ValidationException("One or more validation errors occurred.", errors);
    }

    private async Task<List<ProviderRecommendation>> ResolvePreferencesAsync(
        Guid tenantId,
        CreatePendingReferralRequest request,
        CancellationToken ct)
    {
        var requested = (request.PreferredProviders ?? new List<PendingReferralProviderPreferenceRequest>())
            .Select(p => new PendingReferralProviderPreferenceRequest
            {
                ProviderId = p.ProviderId,
                FacilityId = p.FacilityId,
            })
            .ToList();

        if (requested.Count == 0 && request.RecommendedProviderId.HasValue)
        {
            requested.Add(new PendingReferralProviderPreferenceRequest
            {
                ProviderId = request.RecommendedProviderId.Value,
                FacilityId = request.RecommendedFacilityId,
            });
        }

        if (requested.Count == 0)
            return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolved = new List<ProviderRecommendation>(requested.Count);

        foreach (var preference in requested)
        {
            var key = $"{preference.ProviderId:N}:{preference.FacilityId?.ToString("N") ?? string.Empty}";
            if (!seen.Add(key)) continue;

            resolved.Add(await ResolvePreferenceAsync(tenantId, preference, ct));
        }

        return resolved;
    }

    private async Task<ProviderRecommendation> ResolvePreferenceAsync(
        Guid tenantId,
        PendingReferralProviderPreferenceRequest request,
        CancellationToken ct)
    {
        var provider = await _providers.GetByIdCrossAsync(request.ProviderId, ct)
            ?? throw new ValidationException("One or more validation errors occurred.",
                new() { ["preferredProviders"] = ["Preferred provider was not found."] });
        if (!provider.IsActive)
            throw new ValidationException("One or more validation errors occurred.",
                new() { ["preferredProviders"] = ["Preferred provider is not active."] });

        string? facilityName = null;
        if (request.FacilityId.HasValue)
        {
            var facility = ResolveProviderFacility(provider, request.FacilityId.Value, requireActive: true)
                ?? await _networks.GetFacilityByIdGlobalAsync(request.FacilityId.Value, ct);
            if (facility is null || !facility.IsActive)
                throw new ValidationException("One or more validation errors occurred.",
                    new() { ["preferredProviders"] = ["Provider location was not found."] });
            facilityName = facility.Name;
        }

        return new ProviderRecommendation(
            provider.Id,
            request.FacilityId,
            provider.OrganizationName ?? provider.Name,
            facilityName);
    }

    private async Task ValidateLawFirmAsync(Guid tenantId, Guid lawFirmOrganizationId, CancellationToken ct)
    {
        var lawFirms = await _identityOrganizations.ListLawFirmOrganizationsAsync(tenantId, ct);
        if (!lawFirms.Any(o => o.Id == lawFirmOrganizationId))
            throw new ValidationException("One or more validation errors occurred.",
                new() { ["lawFirmOrganizationId"] = ["Selected law firm was not found."] });
    }

    private static Guid? ResolveProviderFacilityId(Provider provider, Guid? facilityId, bool requireActive, string fieldName)
    {
        if (!facilityId.HasValue)
            return null;

        var facility = ResolveProviderFacility(provider, facilityId.Value, requireActive);
        if (facility is null)
            throw new ValidationException("One or more validation errors occurred.",
                new() { [fieldName] = ["Provider location was not found."] });

        return facility.Id;
    }

    private static Facility? ResolveProviderFacility(Provider provider, Guid facilityId, bool requireActive)
    {
        var facility = provider.ProviderFacilities
            .Where(pf => pf.FacilityId == facilityId)
            .Select(pf => pf.Facility)
            .FirstOrDefault();

        return facility is null || (requireActive && !facility.IsActive)
            ? null
            : facility;
    }

    private static PendingReferralRequestResponse ToResponse(PendingReferralRequest r) => new()
    {
        Id = r.Id,
        TenantId = r.TenantId,
        LawFirmOrganizationId = r.LawFirmOrganizationId,
        ReferralAttributionId = r.ReferralAttributionId,
        ReferralAttribution = r.ReferralAttribution is null ? null : new ReferralAttributionSummary
        {
            Id = r.ReferralAttribution.Id,
            FirstName = r.ReferralAttribution.FirstName,
            LastName = r.ReferralAttribution.LastName,
            IsActive = r.ReferralAttribution.IsActive,
        },
        Origin = r.Origin,
        ClientFirstName = r.ClientFirstName,
        ClientLastName = r.ClientLastName,
        ClientDob = r.ClientDob?.ToString("yyyy-MM-dd"),
        ClientPhone = r.ClientPhone,
        ClientEmail = r.ClientEmail,
        CaseNumber = r.CaseNumber,
        RequestedService = r.RequestedService,
        Urgency = r.Urgency,
        TreatmentTypeId = r.TreatmentTypeId,
        DateOfAccident = r.DateOfAccident?.ToString("yyyy-MM-dd"),
        RecommendedProviderId = r.RecommendedProviderId,
        RecommendedFacilityId = r.RecommendedFacilityId,
        RecommendedProviderName = r.RecommendedProviderName,
        RecommendedFacilityName = r.RecommendedFacilityName,
        PreferredProviders = r.ProviderPreferences
            .OrderBy(p => p.DisplayOrder)
            .Select(p => new PendingReferralProviderPreferenceResponse
            {
                Id = p.Id,
                ProviderId = p.ProviderId,
                FacilityId = p.FacilityId,
                ProviderName = p.ProviderName,
                FacilityName = p.FacilityName,
                DisplayOrder = p.DisplayOrder,
            })
            .ToList(),
        Attachments = r.Attachments
            .OrderByDescending(a => a.CreatedAtUtc)
            .Select(ToAttachmentResponse)
            .ToList(),
        Notes = r.Notes,
        LienCompanyName = r.LienCompanyName,
        LienCompanyEmail = r.LienCompanyEmail,
        Status = r.Status,
        ConvertedReferralId = r.ConvertedReferralId,
        ConvertedAtUtc = r.ConvertedAtUtc,
        CreatedAtUtc = r.CreatedAtUtc,
        UpdatedAtUtc = r.UpdatedAtUtc,
    };

    private static AttachmentMetadataResponse ToAttachmentResponse(PendingReferralAttachment a) => new()
    {
        Id = a.Id,
        FileName = a.FileName,
        ContentType = a.ContentType,
        FileSizeBytes = a.FileSizeBytes,
        ExternalDocumentId = a.ExternalDocumentId,
        ExternalStorageProvider = a.ExternalStorageProvider,
        Status = a.Status,
        Notes = a.Notes,
        CreatedAtUtc = a.CreatedAtUtc,
        CreatedByUserId = a.CreatedByUserId,
    };

    private static ReferralResponse ToReferralResponse(Referral r, string? treatmentTypeName) => new()
    {
        Id = r.Id,
        TenantId = r.TenantId,
        ProviderId = r.ProviderId,
        FacilityId = r.FacilityId,
        ProviderName = r.Provider?.OrganizationName ?? r.Provider?.Name ?? string.Empty,
        ClientFirstName = r.ClientFirstName,
        ClientLastName = r.ClientLastName,
        ClientDob = r.ClientDob?.ToString("yyyy-MM-dd"),
        ClientPhone = r.ClientPhone,
        ClientEmail = r.ClientEmail,
        CaseNumber = r.CaseNumber,
        RequestedService = r.RequestedService,
        Urgency = r.Urgency,
        Status = r.Status,
        Notes = r.Notes,
        Origin = r.Origin,
        LienCompanyName = r.LienCompanyName,
        LienCompanyEmail = r.LienCompanyEmail,
        CreatedAtUtc = r.CreatedAtUtc,
        UpdatedAtUtc = r.UpdatedAtUtc,
        ReferringOrganizationId = r.ReferringOrganizationId,
        ReceivingOrganizationId = r.ReceivingOrganizationId,
        OrganizationRelationshipId = r.OrganizationRelationshipId,
        ReferrerEmail = r.ReferrerEmail,
        ReferrerName = r.ReferrerName,
        TokenVersion = r.TokenVersion,
        DateOfAccident = r.DateOfAccident?.ToString("yyyy-MM-dd"),
        TreatmentTypeId = r.TreatmentTypeId,
        TreatmentTypeName = treatmentTypeName,
        ReferralAttribution = r.ReferralAttribution is null ? null : new ReferralAttributionSummary
        {
            Id = r.ReferralAttribution.Id,
            FirstName = r.ReferralAttribution.FirstName,
            LastName = r.ReferralAttribution.LastName,
            IsActive = r.ReferralAttribution.IsActive,
        },
    };

    private sealed record ProviderRecommendation(
        Guid ProviderId,
        Guid? FacilityId,
        string ProviderName,
        string? FacilityName)
    ;

    private sealed record ConversionTarget(
        Provider Provider,
        Guid? FacilityId,
        Guid? ReceivingOrganizationId);
}
