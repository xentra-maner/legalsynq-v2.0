namespace CareConnect.Application.DTOs;

public sealed class LawFirmOptionResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class CreatePendingReferralRequest
{
    public Guid LawFirmOrganizationId { get; set; }
    public string ClientFirstName { get; set; } = string.Empty;
    public string ClientLastName { get; set; } = string.Empty;
    public DateTime? ClientDob { get; set; }
    public string ClientPhone { get; set; } = string.Empty;
    public string ClientEmail { get; set; } = string.Empty;
    public string? CaseNumber { get; set; }
    public string? RequestedService { get; set; }
    public string Urgency { get; set; } = string.Empty;
    public Guid? TreatmentTypeId { get; set; }
    public DateOnly? DateOfAccident { get; set; }
    public Guid? RecommendedProviderId { get; set; }
    public Guid? RecommendedFacilityId { get; set; }
    public List<PendingReferralProviderPreferenceRequest> PreferredProviders { get; set; } = new();
    public string? Notes { get; set; }
    public string? LienCompanyName { get; set; }
    public string? LienCompanyEmail { get; set; }
}

public sealed class PendingReferralProviderPreferenceRequest
{
    public Guid ProviderId { get; set; }
    public Guid? FacilityId { get; set; }
}

public sealed class PendingReferralRequestResponse
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid LawFirmOrganizationId { get; set; }
    public string? LawFirmName { get; set; }
    public Guid ReferralAttributionId { get; set; }
    public ReferralAttributionSummary? ReferralAttribution { get; set; }
    public string Origin { get; set; } = string.Empty;
    public string ClientFirstName { get; set; } = string.Empty;
    public string ClientLastName { get; set; } = string.Empty;
    public string? ClientDob { get; set; }
    public string ClientPhone { get; set; } = string.Empty;
    public string ClientEmail { get; set; } = string.Empty;
    public string? CaseNumber { get; set; }
    public string? RequestedService { get; set; }
    public string Urgency { get; set; } = string.Empty;
    public Guid? TreatmentTypeId { get; set; }
    public string? DateOfAccident { get; set; }
    public Guid? RecommendedProviderId { get; set; }
    public Guid? RecommendedFacilityId { get; set; }
    public string? RecommendedProviderName { get; set; }
    public string? RecommendedFacilityName { get; set; }
    public List<PendingReferralProviderPreferenceResponse> PreferredProviders { get; set; } = new();
    public List<AttachmentMetadataResponse> Attachments { get; set; } = new();
    public string? Notes { get; set; }
    public string? LienCompanyName { get; set; }
    public string? LienCompanyEmail { get; set; }
    public string Status { get; set; } = string.Empty;
    public Guid? ConvertedReferralId { get; set; }
    public DateTime? ConvertedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class PendingReferralProviderPreferenceResponse
{
    public Guid Id { get; set; }
    public Guid ProviderId { get; set; }
    public Guid? FacilityId { get; set; }
    public string ProviderName { get; set; } = string.Empty;
    public string? FacilityName { get; set; }
    public int DisplayOrder { get; set; }
}

public sealed class ConvertPendingReferralRequest
{
    public Guid? ProviderId { get; set; }
    public Guid? NetworkProviderId { get; set; }
    public Guid? FacilityId { get; set; }
    public List<PendingReferralProviderSelectionRequest> ProviderSelections { get; set; } = new();
}

public sealed class PendingReferralProviderSelectionRequest
{
    public Guid? ProviderId { get; set; }
    public Guid? NetworkProviderId { get; set; }
    public Guid? FacilityId { get; set; }
}

public sealed class UpdatePendingReferralRequest
{
    public string ClientFirstName { get; set; } = string.Empty;
    public string ClientLastName { get; set; } = string.Empty;
    public DateTime? ClientDob { get; set; }
    public string ClientPhone { get; set; } = string.Empty;
    public string ClientEmail { get; set; } = string.Empty;
    public string? CaseNumber { get; set; }
    public string? RequestedService { get; set; }
    public string Urgency { get; set; } = string.Empty;
    public Guid? TreatmentTypeId { get; set; }
    public DateOnly? DateOfAccident { get; set; }
    public string? Notes { get; set; }
    public string? LienCompanyName { get; set; }
    public string? LienCompanyEmail { get; set; }
}
