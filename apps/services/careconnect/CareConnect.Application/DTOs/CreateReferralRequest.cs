namespace CareConnect.Application.DTOs;

public class CreateReferralRequest
{
    /// <summary>
    /// Optional authenticated-flow tenant override for users who can act in more than one tenant.
    /// When omitted, the API uses the caller's active JWT tenant.
    /// </summary>
    public Guid? TenantId { get; set; }

    public Guid ProviderId { get; set; }
    public Guid? FacilityId { get; set; }
    public Guid? NetworkProviderId { get; set; }
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

    /// <summary>
    /// Optional Referral Attribution — who or what originated this referral. Blank/null by
    /// default; never auto-selected. Must resolve to an active attribution in the caller's
    /// own tenant, or the request is rejected — see ReferralService.ResolveAttributionAsync.
    /// </summary>
    public Guid? ReferralAttributionId { get; set; }

    // Phase C: optional multi-org context.
    // When both are supplied, the service attempts to resolve the active
    // OrganizationRelationship in Identity and set it on the created referral.
    public Guid? ReferringOrganizationId { get; set; }
    public Guid? ReceivingOrganizationId { get; set; }

    // LSCC-005: referrer contact stored for email notifications.
    // Pre-filled from session on the frontend; optional for backward compatibility.
    public string? ReferrerEmail { get; set; }
    public string? ReferrerName  { get; set; }
    public string? ReferrerFirstName { get; set; }
    public string? ReferrerLastName  { get; set; }
    public string? ReferrerFirmName  { get; set; }
    public string? ReferrerPhone    { get; set; }

    /// <summary>
    /// HMAC signature proving the authenticated user selected the supplied
    /// tenant/org scope from the trusted browse-network flow.
    /// </summary>
    public string? ReferrerScopeSignature { get; set; }
}
