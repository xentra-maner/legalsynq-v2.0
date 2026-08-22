using CareConnect.Application.DTOs;
using CareConnect.Domain;

namespace CareConnect.Application.Interfaces;

// CC2-INT-B06 / CC2-INT-B06-01
public interface INetworkService
{
    Task<List<NetworkSummaryResponse>> GetAllAsync(Guid tenantId, CancellationToken ct = default, Guid? callerOrgId = null, bool isTenantAdmin = false, bool isNetworkManager = false);
    Task<NetworkDetailResponse> GetByIdAsync(Guid tenantId, Guid id, CancellationToken ct = default, Guid? callerOrgId = null, bool isTenantAdmin = false, bool isNetworkManager = false);

    /// <summary>
    /// Single-tenant-network cutover: resolves (creating if necessary) the tenant's one
    /// shared ProviderNetwork. Replaces the old public "create a network" flow — there is
    /// no longer a user-facing network name/description to set on creation.
    /// </summary>
    Task<ProviderNetwork> GetOrCreateTenantNetworkAsync(Guid tenantId, CancellationToken ct = default);

    Task<NetworkSummaryResponse> UpdateAsync(Guid tenantId, Guid id, Guid? userId, UpdateNetworkRequest request, CancellationToken ct = default, bool isTenantAdmin = false, Guid? callerOrgId = null, bool isNetworkManager = false);

    /// <summary>
    /// CC2-INT-B06-01: Match-or-create flow.
    /// If ExistingProviderId + ExistingFacilityId are set → associate that shared provider location.
    /// If ExistingProviderId + NewProvider are set → create a new location for that shared provider.
    /// If NewProvider is set alone → create a new shared provider identity and first location.
    /// </summary>
    Task<NetworkProviderItem> AddProviderAsync(Guid tenantId, Guid networkId, AddProviderToNetworkRequest request, Guid? userId, CancellationToken ct = default, Guid? owningOrganizationId = null, bool isTenantAdmin = false);

    Task RemoveProviderAsync(Guid tenantId, Guid networkId, Guid providerId, bool cascadeFacility, Guid? userId, CancellationToken ct = default, bool isTenantAdmin = false, Guid? callerOrgId = null, bool isNetworkManager = false);
    Task<List<NetworkProviderMarker>> GetMarkersAsync(Guid tenantId, Guid networkId, CancellationToken ct = default, Guid? callerOrgId = null, bool isTenantAdmin = false, bool isNetworkManager = false);
    Task<NetworkProviderItem> UpdateProviderAsync(Guid tenantId, Guid networkId, Guid providerId, UpdateNetworkProviderRequest request, Guid? userId, CancellationToken ct = default, bool isTenantAdmin = false, Guid? callerOrgId = null, bool isNetworkManager = false);

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
