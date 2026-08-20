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

    public async Task<PendingReferralRequestResponse?> GetForLawFirmAsync(
        Guid tenantId, Guid lawFirmOrganizationId, Guid id, CancellationToken ct = default)
    {
        var item = await _pending.GetByIdAsync(tenantId, id, ct);
        if (item is null || item.LawFirmOrganizationId != lawFirmOrganizationId)
            return null;

        return await EnrichLawFirmNameAsync(ToResponse(item), ct);
    }

    public async Task<ReferralResponse> ConvertAsync(
        Guid tenantId, Guid lawFirmOrganizationId, Guid id, Guid? userId, ConvertPendingReferralRequest request, CancellationToken ct = default)
    {
        var item = await _pending.GetByIdAsync(tenantId, id, ct)
            ?? throw new NotFoundException($"Pending referral request '{id}' was not found.");
        if (item.LawFirmOrganizationId != lawFirmOrganizationId)
            throw new NotFoundException($"Pending referral request '{id}' was not found.");
        if (item.Status != PendingReferralRequest.Statuses.PendingReview)
            throw new ConflictException("PENDING_REFERRAL_ALREADY_CONVERTED");

        Provider provider;
        Guid? facilityId = null;
        Guid? receivingOrganizationId;
        var selectedNetworkProviderId = request.NetworkProviderId;
        var selectedProviderId = request.ProviderId.HasValue && request.ProviderId.Value != Guid.Empty
            ? request.ProviderId
            : item.ProviderPreferences.OrderBy(p => p.DisplayOrder).FirstOrDefault()?.ProviderId ?? item.RecommendedProviderId;
        var selectedFacilityId = request.ProviderId.HasValue && request.ProviderId.Value != Guid.Empty
            ? request.FacilityId
            : item.ProviderPreferences.OrderBy(p => p.DisplayOrder).FirstOrDefault()?.FacilityId ?? item.RecommendedFacilityId;

        if (selectedNetworkProviderId.HasValue)
        {
            var membership = await _networks.GetTenantNetworkMembershipAsync(tenantId, selectedNetworkProviderId.Value, ct)
                ?? throw new NotFoundException($"Network provider '{selectedNetworkProviderId.Value}' was not found.");
            if (!membership.IsActive || !membership.AcceptingReferrals)
                throw new ValidationException("One or more validation errors occurred.",
                    new() { ["networkProviderId"] = ["Selected provider location is not accepting referrals."] });

            provider = membership.Provider;
            facilityId = membership.FacilityId;
            receivingOrganizationId = provider.OrganizationId;
        }
        else if (selectedProviderId.HasValue && selectedProviderId.Value != Guid.Empty)
        {
            provider = await _providers.GetByIdCrossAsync(selectedProviderId.Value, ct)
                ?? throw new NotFoundException($"Provider '{selectedProviderId.Value}' was not found.");
            facilityId = ResolveProviderFacilityId(provider, selectedFacilityId, requireActive: true, fieldName: "facilityId");
            receivingOrganizationId = provider.OrganizationId;
        }
        else
        {
            throw new ValidationException("One or more validation errors occurred.",
                new() { ["providerId"] = ["ProviderId or NetworkProviderId is required."] });
        }

        Guid? orgRelationshipId = null;
        if (receivingOrganizationId.HasValue)
        {
            orgRelationshipId = await _relationshipResolver.FindActiveRelationshipAsync(
                lawFirmOrganizationId,
                receivingOrganizationId.Value,
                ct);
        }

        var referral = Referral.Create(
            tenantId,
            referringOrganizationId: lawFirmOrganizationId,
            receivingOrganizationId: receivingOrganizationId,
            providerId: provider.Id,
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
            treatmentTypeId: item.TreatmentTypeId,
            dateOfAccident: item.DateOfAccident,
            facilityId: facilityId,
            referralAttributionId: item.ReferralAttributionId,
            origin: ReferralOrigin.ReferralAssociate,
            lienCompanyName: item.LienCompanyName,
            lienCompanyEmail: item.LienCompanyEmail);

        item.MarkConverted(referral.Id, userId);
        await _pending.UpdateAsync(item, referral, ct);

        var treatmentTypeName = item.TreatmentTypeId.HasValue
            ? await _referrals.GetTreatmentTypeNameAsync(item.TreatmentTypeId.Value, ct)
            : null;
        FireProviderNotification(referral.TenantId, referral.Id, provider.Id, treatmentTypeName);

        var loaded = await _referrals.GetByIdAsync(tenantId, referral.Id, ct)
            ?? throw new NotFoundException($"Referral '{referral.Id}' was not found after conversion.");
        return ToReferralResponse(loaded, treatmentTypeName);
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
                var provider = await providers.GetByIdCrossAsync(providerId, CancellationToken.None);
                if (referral is not null && provider is not null)
                    await emailSvc.SendNewReferralNotificationAsync(referral, provider, treatmentTypeName, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Background referral notification failed after pending conversion for referral {ReferralId}.", referralId);
            }
        });
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
        if (string.IsNullOrWhiteSpace(r.ClientPhone)) errors["clientPhone"] = ["ClientPhone is required."];
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
        Notes = r.Notes,
        LienCompanyName = r.LienCompanyName,
        LienCompanyEmail = r.LienCompanyEmail,
        Status = r.Status,
        ConvertedReferralId = r.ConvertedReferralId,
        ConvertedAtUtc = r.ConvertedAtUtc,
        CreatedAtUtc = r.CreatedAtUtc,
        UpdatedAtUtc = r.UpdatedAtUtc,
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
}
