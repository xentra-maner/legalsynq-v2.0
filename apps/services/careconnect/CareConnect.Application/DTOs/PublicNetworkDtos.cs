namespace CareConnect.Application.DTOs;

// CC2-INT-B07 — Public network surface DTOs.
// These are safe to expose without authentication.
// Tenant ID is NEVER included in responses (the caller already knows which tenant they're on).

/// <summary>
/// Public-facing network summary.
/// Returned by GET /api/public/network — accessible without authentication.
/// </summary>
public sealed record PublicNetworkSummary(
    Guid   Id,
    string Name,
    string Description,
    int    ProviderCount,
    Guid?  OwningOrganizationId = null);

/// <summary>
/// Public-facing provider item within a network.
/// Omits sensitive internal IDs; safe to return to unauthenticated callers.
/// </summary>
public sealed record PublicProviderItem(
    Guid    Id,
    Guid    NetworkProviderId,
    Guid    ProviderId,
    Guid    FacilityId,
    string  Name,
    string? Title,
    string? OrganizationName,
    string  FacilityName,
    string  AddressLine1,
    string  Phone,
    string? Email,
    string  City,
    string  State,
    string? PostalCode,
    bool    IsActive,
    bool    AcceptingReferrals,
    string  AccessStage,
    string? PrimaryCategory,
    List<SpecialtyResponse> Specialties,
    Guid? PrimarySpecialtyId,
    string? PrimarySpecialty,
    double? DistanceMiles = null,
    bool    IsMobile = false,
    double? ServiceRadiusMiles = null,
    string? ServiceAreaLabel = null);

/// <summary>
/// Public-facing map marker for a provider in a network.
/// Latitude/Longitude included only when the provider has geo data.
/// </summary>
public sealed record PublicProviderMarker(
    Guid    Id,
    Guid    NetworkProviderId,
    Guid    ProviderId,
    Guid    FacilityId,
    string  Name,
    string? Title,
    string? OrganizationName,
    string  FacilityName,
    string  City,
    string  State,
    bool    AcceptingReferrals,
    double  Latitude,
    double  Longitude,
    List<SpecialtyResponse> Specialties,
    Guid? PrimarySpecialtyId,
    string? PrimarySpecialty,
    double? DistanceMiles = null,
    bool    IsMobile = false,
    double? ServiceRadiusMiles = null,
    string? ServiceAreaLabel = null);

/// <summary>
/// Resolved public network surface returned when the tenant has a single network.
/// Bundles the network + its providers for a single API round-trip.
/// </summary>
public sealed record PublicNetworkDetail(
    Guid   NetworkId,
    string NetworkName,
    string NetworkDescription,
    List<PublicProviderItem>   Providers,
    List<PublicProviderMarker> Markers,
    List<SpecialtyResponse> Specialties);

/// <summary>
/// Stage-based redirect instruction returned when the network surface detects
/// a provider/user should be redirected to a more advanced portal.
/// CC2-INT-B06-02 stage enforcement for the public surface.
/// </summary>
public sealed record StageRedirectInfo(
    string Stage,
    string TargetUrl);

// ── CC2-INT-B08: Public referral initiation ──────────────────────────────────

/// <summary>
/// Input for POST /api/public/referrals.
/// Submitted from the public network directory without authentication.
/// Fields map to CreateReferralRequest which drives the existing referral pipeline.
/// </summary>
public sealed class PublicReferralRequest
{
    /// <summary>Target provider (from the public directory card).</summary>
    public Guid ProviderId { get; set; }

    /// <summary>Selected provider-location network membership from the public directory card.</summary>
    public Guid? NetworkProviderId { get; set; }

    /// <summary>First name of the person submitting the referral (law firm staff).</summary>
    public string SenderFirstName { get; set; } = string.Empty;

    /// <summary>Last name of the person submitting the referral (optional).</summary>
    public string? SenderLastName { get; set; }

    /// <summary>Email of the person submitting (used for confirmation).</summary>
    public string SenderEmail { get; set; } = string.Empty;

    /// <summary>Law firm / organization name (optional — stored for enrollment pre-fill).</summary>
    public string? SenderFirmName { get; set; }

    /// <summary>Referrer phone number (optional — stored for enrollment pre-fill).</summary>
    public string? SenderPhone { get; set; }

    /// <summary>Patient first name.</summary>
    public string PatientFirstName { get; set; } = string.Empty;

    /// <summary>Patient last name.</summary>
    public string PatientLastName { get; set; } = string.Empty;

    /// <summary>Patient phone number.</summary>
    public string PatientPhone { get; set; } = string.Empty;

    /// <summary>Patient email (optional).</summary>
    public string? PatientEmail { get; set; }

    /// <summary>Patient date of birth (required for referral intake).</summary>
    public DateOnly? PatientDateOfBirth { get; set; }

    /// <summary>Date of accident / incident (required for personal-injury referrals).</summary>
    public DateOnly? PatientDateOfAccident { get; set; }

    /// <summary>Patient address — free-text, optional.</summary>
    public string? PatientAddress { get; set; }

    /// <summary>
    /// Type of service requested (optional free text).
    /// Defaults to "General Referral" when omitted.
    /// </summary>
    public string? ServiceType { get; set; }

    /// <summary>Additional case notes (optional).</summary>
    public string? Notes { get; set; }

    public string? LienCompanyName { get; set; }
    public string? LienCompanyEmail { get; set; }

    /// <summary>
    /// Urgency level (optional). Must be one of <c>Referral.ValidUrgencies.All</c>
    /// (Low, Normal, Urgent, Emergency). Falls back to "Normal" when omitted or invalid.
    /// </summary>
    public string? Urgency { get; set; }

    /// <summary>Treatment type ID selected from the treatment types list (optional).</summary>
    public Guid? TreatmentTypeId { get; set; }

    /// <summary>
    /// Referral Attribution selected from the tenant's active options (optional). Validated
    /// server-side (must belong to this tenant and be active) by
    /// ReferralService.CreateAsync — never trusted at face value.
    /// </summary>
    public Guid? ReferralAttributionId { get; set; }
}

/// <summary>
/// Success response for POST /api/public/referrals.
/// Returns the minimum necessary to confirm submission — no PII echoed back.
/// </summary>
public sealed record PublicReferralResponse(
    Guid   ReferralId,
    Guid   ProviderId,
    Guid?  FacilityId,
    Guid?  NetworkProviderId,
    string ProviderName,
    string ProviderStage,
    string Message);
