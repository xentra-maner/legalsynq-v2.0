namespace CareConnect.Application.DTOs;

// CC2-INT-B06 — request/response DTOs for provider network management

// ── List / Summary ────────────────────────────────────────────────────────────

public sealed record NetworkSummaryResponse(
    Guid   Id,
    string Name,
    string Description,
    int    ProviderCount,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    Guid?  OwningOrganizationId = null);

// ── Detail ────────────────────────────────────────────────────────────────────

public sealed record NetworkDetailResponse(
    Guid   Id,
    string Name,
    string Description,
    List<NetworkProviderItem> Providers,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    Guid?  OwningOrganizationId = null);

public sealed record NetworkProviderItem(
    Guid   Id,
    Guid   NetworkProviderId,
    Guid   ProviderId,
    Guid   FacilityId,
    string Name,
    string? Title,
    string? OrganizationName,
    string FacilityName,
    string Email,
    string Phone,
    string City,
    string State,
    string AddressLine1,
    string? PostalCode,
    bool   IsActive,
    bool   AcceptingReferrals,
    Guid?  OwningOrganizationId,
    string Visibility,
    string AccessStage,
    List<SpecialtyResponse> Specialties,
    Guid? PrimarySpecialtyId,
    string? PrimarySpecialty,
    /// <summary>
    /// Whether the underlying cc_Facilities row is active. Distinct from IsActive above
    /// (the NetworkProvider membership's own active/inactive toggle — an existing,
    /// independent feature): FacilityIsActive is false only when the location itself was
    /// soft-deleted (RemoveProviderAsync cascades to Facility.Deactivate when it was the
    /// last active membership referencing it). A membership can be manually toggled
    /// IsActive=false while FacilityIsActive stays true, and vice versa is not possible.
    /// </summary>
    bool   FacilityIsActive = true,
    bool    IsMobile = false,
    double? ServiceRadiusMiles = null,
    string? ServiceAreaLabel = null);

// ── Map markers ───────────────────────────────────────────────────────────────

public sealed record NetworkProviderMarker(
    Guid   Id,
    Guid   NetworkProviderId,
    Guid   ProviderId,
    Guid   FacilityId,
    string Name,
    string? Title,
    string? OrganizationName,
    string FacilityName,
    string City,
    string State,
    string AddressLine1,
    string? PostalCode,
    string Email,
    string Phone,
    bool   AcceptingReferrals,
    bool   IsActive,
    Guid?  OwningOrganizationId,
    string Visibility,
    double Latitude,
    double Longitude,
    string? GeoPointSource,
    List<SpecialtyResponse> Specialties,
    Guid? PrimarySpecialtyId,
    string? PrimarySpecialty,
    bool    IsMobile = false,
    double? ServiceRadiusMiles = null,
    string? ServiceAreaLabel = null);

// ── Shared provider search ────────────────────────────────────────────────────

/// <summary>Returned by GET /api/networks/{id}/providers/search</summary>
public sealed record ProviderSearchResult(
    Guid    Id,
    Guid?   FacilityId,
    string? FacilityName,
    string  Name,
    string? Title,
    string? OrganizationName,
    string  Email,
    string  Phone,
    string  City,
    string  State,
    string  AddressLine1,
    string? PostalCode,
    string? Npi,
    bool    IsActive,
    bool    AcceptingReferrals,
    string  AccessStage,
    List<SpecialtyResponse> Specialties,
    Guid? PrimarySpecialtyId,
    string? PrimarySpecialty,
    bool    IsMobile = false,
    double? ServiceRadiusMiles = null,
    string? ServiceAreaLabel = null);

// ── Mutations ─────────────────────────────────────────────────────────────────

public sealed record CreateNetworkRequest(
    string Name,
    string Description);

public sealed record UpdateNetworkRequest(
    string Name,
    string Description);

/// <summary>
/// CC2-INT-B06-01: Add a provider to a network.
/// - ExistingProviderId + ExistingFacilityId → associate an existing shared provider location.
/// - ExistingProviderId + NewProvider        → create a new location for an existing shared provider.
/// - NewProvider only                        → create a new provider identity and first location.
/// </summary>
public sealed record AddProviderToNetworkRequest(
    Guid?                  ExistingProviderId,
    Guid?                  ExistingFacilityId,
    NewProviderData?       NewProvider);

public sealed record NewProviderData(
    string  FirstName,
    string  LastName,
    string? OrganizationName,
    string  Email,
    string  Phone,
    string  AddressLine1,
    string  City,
    string  State,
    string? PostalCode,
    bool    IsActive,
    bool    AcceptingReferrals,
    string? Npi,
    /// <summary>Category codes (e.g. "IMG", "PAIN"). The primary category code should be first.</summary>
    List<string>? CategoryCodes = null,
    /// <summary>The code of the default/primary provider type (must be present in CategoryCodes).</summary>
    string? PrimaryCategoryCode = null,
    /// <summary>Specialty codes (e.g. "PHYSICAL_THERAPY"). The primary specialty code should be first.</summary>
    List<string>? SpecialtyCodes = null,
    /// <summary>The code of the default/primary specialty (must be present in SpecialtyCodes).</summary>
    string? PrimarySpecialtyCode = null,
    double? Latitude = null,
    double? Longitude = null,
    string? GeoPointSource = null,
    string? Title = null,
    /// <summary>True for a roaming provider with no fixed address (AddressLine1 holds a service-area label instead).</summary>
    bool    IsMobile = false,
    /// <summary>Coverage radius in miles from the geocoded city centroid. Required when IsMobile is true.</summary>
    double? ServiceRadiusMiles = null,
    string? Visibility = null);

public sealed record UpdateNetworkProviderRequest(
    string  FirstName,
    string  LastName,
    string? OrganizationName,
    string? FacilityName,
    string  Email,
    string  Phone,
    string  AddressLine1,
    string  City,
    string  State,
    string? PostalCode,
    bool    IsActive,
    bool    AcceptingReferrals,
    List<Guid> SpecialtyIds,
    double? Latitude = null,
    double? Longitude = null,
    string? GeoPointSource = null,
    string? Title = null,
    bool    IsMobile = false,
    double? ServiceRadiusMiles = null,
    string? Visibility = null);

// ── Provider import ──────────────────────────────────────────────────────────

public sealed record ProviderImportParsedRow(
    int     RowNumber,
    string  SourceKey,
    string? TenantId,
    string? Title,
    string? ProviderName,
    string? FacilityName,
    string? FirstName,
    string? LastName,
    string? OrganizationName,
    string? Npi,
    string? Email,
    string? Phone,
    string? AddressLine1,
    string? City,
    string? State,
    string? PostalCode,
    string? IsActiveRaw,
    string? AcceptingReferralsRaw,
    string? CategoryCodesRaw = null,
    string? PrimaryCategoryCode = null,
    string? SpecialtyCodesRaw = null,
    string? PrimarySpecialtyCode = null,
    string? LatitudeRaw = null,
    string? LongitudeRaw = null,
    string? GeoPointSource = null,
    /// <summary>"Y"/"true"/"1" flags a mobile/roaming provider with no fixed address.</summary>
    string? IsMobileRaw = null,
    /// <summary>Coverage radius in miles; required when IsMobileRaw is truthy.</summary>
    string? ServiceRadiusMilesRaw = null);

public sealed record ProviderImportNormalizedRow(
    Guid    TenantId,
    string? Title,
    string  FirstName,
    string  LastName,
    string? OrganizationName,
    string  FacilityName,
    string? Npi,
    string  Email,
    string  Phone,
    string  AddressLine1,
    string  City,
    string  State,
    string? PostalCode,
    bool    IsActive,
    bool    AcceptingReferrals,
    List<string> CategoryCodes,
    string? PrimaryCategoryCode,
    List<string> SpecialtyCodes,
    string? PrimarySpecialtyCode,
    double? Latitude,
    double? Longitude,
    string? GeoPointSource,
    bool    IsMobile = false,
    double? ServiceRadiusMiles = null);

public sealed record ProviderImportParseResult(
    string FileName,
    int TotalRows,
    List<ProviderImportParsedRow> Rows);

public sealed record ProviderImportRowResult(
    int                          RowNumber,
    string                       SourceKey,
    string                       Status,
    Guid?                        ProviderId,
    Guid?                        FacilityId,
    Guid?                        NetworkProviderId,
    string                       Message,
    ProviderImportNormalizedRow? NormalizedProvider,
    List<string>                 Errors);

public sealed record ProviderImportSummaryResponse(
    Guid                          TenantId,
    Guid                          NetworkId,
    string                        FileName,
    bool                          DryRun,
    int                           TotalRows,
    int                           ValidRows,
    int                           ProcessedRows,
    int                           CreatedProviders,
    int                           CreatedFacilities,
    int                           ReusedByNpi,
    int                           ReusedByEmail,
    int                           LinkedLocations,
    int                           AlreadyInNetwork,
    int                           FailedRows,
    List<ProviderImportRowResult> Rows);
