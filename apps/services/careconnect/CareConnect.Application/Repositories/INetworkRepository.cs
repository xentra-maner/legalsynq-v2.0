using CareConnect.Domain;

namespace CareConnect.Application.Repositories;

// CC2-INT-B06 / CC2-INT-B06-01
public interface INetworkRepository
{
    Task<List<ProviderNetwork>> GetAllByTenantAsync(Guid tenantId, CancellationToken ct = default);

    // BLK-PERF-01: Single-query alternative to GetAllByTenantAsync + N×GetWithProvidersAsync.
    // Returns each network with its provider count without loading full provider entities.
    // organizationId, when provided, scopes the result to tenant-owned networks
    // (OwningOrganizationId == null) plus the given organization's own network(s).
    // When omitted, only tenant-owned networks are returned (law-firm-owned networks
    // are excluded, not merely unscoped) — used by the public referral portal so a
    // law firm's private network is never exposed before/without that firm being
    // selected as the referral's law firm.
    Task<List<(Guid Id, string Name, string? Description, int ProviderCount, Guid? OwningOrganizationId)>> GetAllWithProviderCountAsync(Guid tenantId, Guid? organizationId = null, CancellationToken ct = default);

    Task<ProviderNetwork?> GetByIdGlobalAsync(Guid id, CancellationToken ct = default);
    Task<ProviderNetwork?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken ct = default);
    Task<ProviderNetwork?> GetWithProvidersAsync(Guid tenantId, Guid id, CancellationToken ct = default);
    Task<bool> NameExistsAsync(Guid tenantId, string name, Guid? excludeId = null, CancellationToken ct = default);
    Task AddAsync(ProviderNetwork network, CancellationToken ct = default);
    Task AddProviderAsync(NetworkProvider entry, CancellationToken ct = default);
    Task<NetworkProvider?> GetMembershipAsync(Guid networkId, Guid providerId, CancellationToken ct = default);
    Task<NetworkProvider?> GetMembershipAsync(Guid networkId, Guid providerId, Guid facilityId, CancellationToken ct = default);
    Task<NetworkProvider?> GetMembershipByIdOrProviderAsync(Guid networkId, Guid idOrProviderId, CancellationToken ct = default);
    Task RemoveProviderAsync(NetworkProvider entry, CancellationToken ct = default);
    Task<List<Provider>> GetNetworkProvidersAsync(Guid tenantId, Guid networkId, CancellationToken ct = default);
    Task<List<NetworkProvider>> GetNetworkProviderMembershipsAsync(Guid tenantId, Guid networkId, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);

    // CC2-INT-B06-01: Shared provider registry — global (cross-tenant) lookups
    Task<List<Provider>> SearchProvidersGlobalAsync(string? name, string? phone, string? npi, string? city, int limit = 20, CancellationToken ct = default);
    Task<Provider?> GetProviderByIdGlobalAsync(Guid id, CancellationToken ct = default);
    Task<Provider?> GetProviderByNpiAsync(string npi, CancellationToken ct = default);
    Task<Provider?> GetProviderByTenantEmailAsync(Guid tenantId, string email, CancellationToken ct = default);
    Task AddProviderToRegistryAsync(Provider provider, CancellationToken ct = default);
    Task UpdateProviderInRegistryAsync(Provider provider, CancellationToken ct = default);
    Task<Dictionary<string, Provider>> GetProvidersByNpisAsync(IEnumerable<string> npis, CancellationToken ct = default);
    Task<Dictionary<string, Provider>> GetProvidersByTenantEmailsAsync(Guid tenantId, IEnumerable<string> emails, CancellationToken ct = default);
    Task<HashSet<Guid>> GetNetworkProviderIdsAsync(Guid tenantId, Guid networkId, CancellationToken ct = default);
    Task<HashSet<string>> GetNetworkProviderLocationKeysAsync(Guid tenantId, Guid networkId, CancellationToken ct = default);
    Task<Facility?> GetFacilityByIdAsync(Guid tenantId, Guid facilityId, CancellationToken ct = default);
    Task<Facility?> GetFacilityByIdGlobalAsync(Guid facilityId, CancellationToken ct = default);
    Task<Facility?> FindFacilityAsync(Guid tenantId, string name, string addressLine1, string city, string state, string? postalCode, CancellationToken ct = default);
    Task AddFacilityAsync(Facility facility, CancellationToken ct = default);
    Task UpdateFacilityAsync(Facility facility, CancellationToken ct = default);

    /// <summary>
    /// True when a Facility is still referenced by another active NetworkProvider membership
    /// (any network, this tenant), other than the one being excluded. A Facility row can be shared
    /// across multiple provider memberships (deduplicated by tenant+name+address in EnsureFacilityAsync),
    /// so this must be checked before cascading a soft-delete's inactive flag onto the Facility itself.
    /// </summary>
    Task<bool> HasOtherActiveNetworkProviderForFacilityAsync(Guid tenantId, Guid facilityId, Guid excludeNetworkProviderId, CancellationToken ct = default);
    Task<ProviderFacility?> GetProviderFacilityAsync(Guid providerId, Guid facilityId, CancellationToken ct = default);
    Task<ProviderFacility?> GetPrimaryProviderFacilityAsync(Guid providerId, CancellationToken ct = default);
    Task AddProviderFacilityAsync(ProviderFacility providerFacility, CancellationToken ct = default);

    /// <summary>
    /// Replaces all category associations for a provider with the supplied list.
    /// Order is preserved: the first ID is treated as primary by convention.
    /// Does NOT call SaveChanges — caller is responsible.
    /// </summary>
    Task SyncProviderCategoriesAsync(Guid providerId, List<Guid> categoryIds, CancellationToken ct = default);
    Task SyncProviderSpecialtiesAsync(Guid providerId, List<Guid> specialtyIds, CancellationToken ct = default);

    /// <summary>
    /// Returns true when the provider is a member of at least one network that belongs to the given tenant.
    /// Used to enforce public referral binding — prevents cross-tenant provider injection on the
    /// anonymous POST /api/public/referrals endpoint.
    /// </summary>
    Task<bool> IsProviderInTenantNetworkAsync(Guid tenantId, Guid providerId, CancellationToken ct = default);
    Task<NetworkProvider?> GetTenantNetworkMembershipAsync(Guid tenantId, Guid networkProviderId, CancellationToken ct = default);
    void ClearTracking();
}
