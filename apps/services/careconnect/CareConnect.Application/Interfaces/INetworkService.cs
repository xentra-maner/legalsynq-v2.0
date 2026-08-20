using CareConnect.Application.DTOs;

namespace CareConnect.Application.Interfaces;

// CC2-INT-B06 / CC2-INT-B06-01
public interface INetworkService
{
    Task<List<NetworkSummaryResponse>> GetAllAsync(Guid tenantId, CancellationToken ct = default);
    Task<NetworkDetailResponse> GetByIdAsync(Guid tenantId, Guid id, CancellationToken ct = default);
    Task<NetworkSummaryResponse> CreateAsync(Guid tenantId, Guid? userId, CreateNetworkRequest request, CancellationToken ct = default);
    Task<NetworkSummaryResponse> UpdateAsync(Guid tenantId, Guid id, Guid? userId, UpdateNetworkRequest request, CancellationToken ct = default);
    Task DeleteAsync(Guid tenantId, Guid id, CancellationToken ct = default);

    /// <summary>
    /// CC2-INT-B06-01: Match-or-create flow.
    /// If ExistingProviderId + ExistingFacilityId are set → associate that shared provider location.
    /// If ExistingProviderId + NewProvider are set → create a new location for that shared provider.
    /// If NewProvider is set alone → create a new shared provider identity and first location.
    /// </summary>
    Task<NetworkProviderItem> AddProviderAsync(Guid tenantId, Guid networkId, AddProviderToNetworkRequest request, Guid? userId, CancellationToken ct = default, Guid? owningOrganizationId = null, bool isTenantAdmin = false);

    Task RemoveProviderAsync(Guid tenantId, Guid networkId, Guid providerId, bool cascadeFacility, Guid? userId, CancellationToken ct = default);
    Task<List<NetworkProviderMarker>> GetMarkersAsync(Guid tenantId, Guid networkId, CancellationToken ct = default);
    Task<NetworkProviderItem> UpdateProviderAsync(Guid tenantId, Guid networkId, Guid providerId, UpdateNetworkProviderRequest request, Guid? userId, CancellationToken ct = default, bool isTenantAdmin = false);

    /// <summary>CC2-INT-B06-01: Search the shared global provider registry.</summary>
    Task<List<ProviderSearchResult>> SearchProvidersAsync(string? name, string? phone, string? npi, string? city, CancellationToken ct = default);

    Task<ProviderImportSummaryResponse> ImportProvidersAsync(
        Guid networkId,
        Stream fileStream,
        string fileName,
        bool dryRun,
        Guid? userId,
        CancellationToken ct = default);
}
