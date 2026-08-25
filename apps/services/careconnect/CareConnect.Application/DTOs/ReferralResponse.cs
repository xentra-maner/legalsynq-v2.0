namespace CareConnect.Application.DTOs;

public class ReferralResponse
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ProviderId { get; set; }
    public Guid? FacilityId { get; set; }
    public string ProviderName { get; set; } = string.Empty;

    // Referral location — the specific facility this referral was routed to, falling back
    // to the provider's own address for legacy/single-location referrals. See ReferralLocationResolver.
    public string? FacilityName { get; set; }
    public string LocationAddressLine1 { get; set; } = string.Empty;
    public string LocationCity { get; set; } = string.Empty;
    public string LocationState { get; set; } = string.Empty;
    public string LocationPostalCode { get; set; } = string.Empty;
    public string ClientFirstName { get; set; } = string.Empty;
    public string ClientLastName { get; set; } = string.Empty;
    public string? ClientDob { get; set; }
    public string ClientPhone { get; set; } = string.Empty;
    public string ClientEmail { get; set; } = string.Empty;
    public string? CaseNumber { get; set; }
    public string? RequestedService { get; set; }
    public string Urgency { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public string? DeclineNotes { get; set; }
    public string Origin { get; set; } = string.Empty;
    public string? LienCompanyName { get; set; }
    public string? LienCompanyEmail { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    // Phase C / Phase 5: org context fields.
    // Null when the referral was created without org IDs or before Phase C.
    public Guid? ReferringOrganizationId { get; set; }
    public Guid? ReceivingOrganizationId { get; set; }
    public Guid? OrganizationRelationshipId { get; set; }
    public string? ReferringOrganizationName { get; set; }

    // CC-REFERRER-EMAIL: email of the referrer (set for public referrals submitted
    // before the law firm activated their portal, where ReferringOrganizationId is null).
    public string? ReferrerEmail { get; set; }
    public string? ReferrerName { get; set; }

    // Network the provider belongs to (first network membership; null if provider not in any network).
    public string? NetworkName { get; set; }

    // LSCC-005-01: hardening fields
    public int     TokenVersion          { get; set; } = 1;
    public string? ProviderEmailStatus   { get; set; }
    public int     ProviderEmailAttempts { get; set; }
    public string? ProviderEmailFailureReason { get; set; }

    // Date of Accident — standalone field (previously embedded in Notes).
    public string? DateOfAccident { get; set; }

    // Type of Treatment — set by Referrer at creation.
    public Guid?   TreatmentTypeId   { get; set; }
    public string? TreatmentTypeName { get; set; }

    // Referral Origination — who or what originated this referral. Null = "Not specified".
    public ReferralAttributionSummary? ReferralAttribution { get; set; }
}
