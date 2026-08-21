// BLK-PERF-01: All read-only queries use AsNoTracking() to avoid EF Core change-tracking overhead.
// GetAllWithProviderCountAsync added to eliminate N+1 in the public network list endpoint.
using CareConnect.Application.Repositories;
using CareConnect.Domain;
using CareConnect.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace CareConnect.Infrastructure.Repositories;

// CC2-INT-B06
public class NetworkRepository : INetworkRepository
{
    private readonly CareConnectDbContext _db;

    public NetworkRepository(CareConnectDbContext db)
    {
        _db = db;
    }

    public async Task<List<ProviderNetwork>> GetAllByTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        return await _db.ProviderNetworks
            .AsNoTracking()
            .Where(n => n.TenantId == tenantId && !n.IsDeleted)
            .OrderBy(n => n.Name)
            .ToListAsync(ct);
    }

    // BLK-PERF-01: Replaces the N+1 pattern in PublicNetworkEndpoints GET / where each
    // network in the list triggered a separate GetWithProvidersAsync round-trip.
    // A single query projects network fields + sub-query COUNT() of NetworkProviders.
    public async Task<List<(Guid Id, string Name, string? Description, int ProviderCount, Guid? OwningOrganizationId)>> GetAllWithProviderCountAsync(
        Guid tenantId, Guid? organizationId = null, CancellationToken ct = default)
    {
        return await _db.ProviderNetworks
            .AsNoTracking()
            .Where(n => n.TenantId == tenantId && !n.IsDeleted)
            .Where(n => organizationId.HasValue
                ? (n.OwningOrganizationId == null || n.OwningOrganizationId == organizationId)
                : n.OwningOrganizationId == null)
            .OrderBy(n => n.Name)
            .Select(n => ValueTuple.Create(
                n.Id,
                n.Name,
                (string?)n.Description,
                n.NetworkProviders.Count(np => np.IsActive && np.Provider.IsActive && np.Facility.IsActive),
                n.OwningOrganizationId))
            .ToListAsync(ct);
    }

    public async Task<ProviderNetwork?> GetByIdGlobalAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.ProviderNetworks
            .AsNoTracking()
            .FirstOrDefaultAsync(n => n.Id == id && !n.IsDeleted, ct);
    }

    public async Task<ProviderNetwork?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        return await _db.ProviderNetworks
            .AsNoTracking()
            .FirstOrDefaultAsync(n => n.TenantId == tenantId && n.Id == id && !n.IsDeleted, ct);
    }

    public async Task<ProviderNetwork?> GetWithProvidersAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        return await _db.ProviderNetworks
            .AsNoTracking()
            .Include(n => n.NetworkProviders)
                .ThenInclude(np => np.Provider)
                    .ThenInclude(p => p.ProviderSpecialties)
                        .ThenInclude(ps => ps.Specialty)
            .Include(n => n.NetworkProviders)
                .ThenInclude(np => np.Facility)
            .FirstOrDefaultAsync(n => n.TenantId == tenantId && n.Id == id && !n.IsDeleted, ct);
    }

    public async Task<bool> NameExistsAsync(Guid tenantId, string name, Guid? excludeId = null, CancellationToken ct = default)
    {
        return await _db.ProviderNetworks
            .AnyAsync(n => n.TenantId == tenantId && !n.IsDeleted &&
                           n.Name == name && (excludeId == null || n.Id != excludeId.Value), ct);
    }

    public async Task AddAsync(ProviderNetwork network, CancellationToken ct = default)
    {
        await _db.ProviderNetworks.AddAsync(network, ct);
    }

    public async Task AddProviderAsync(NetworkProvider entry, CancellationToken ct = default)
    {
        await _db.NetworkProviders.AddAsync(entry, ct);
    }

    public async Task<NetworkProvider?> GetMembershipAsync(Guid networkId, Guid providerId, CancellationToken ct = default)
    {
        return await _db.NetworkProviders
            .AsNoTracking()
            .FirstOrDefaultAsync(np => np.ProviderNetworkId == networkId && np.ProviderId == providerId, ct);
    }

    public async Task<NetworkProvider?> GetMembershipAsync(Guid networkId, Guid providerId, Guid facilityId, CancellationToken ct = default)
    {
        // Includes mirror GetMembershipByIdOrProviderAsync: without them, a Provider that's
        // already tracked in this DbContext (e.g. just created a few lines up by the AddProvider
        // flow) gets returned via EF's identity map with ProviderSpecialties either empty or
        // holding entries whose .Specialty navigation was never fixed up — MapSpecialties then
        // silently drops them, so a freshly-added provider's specialties don't show until a
        // page reload re-queries with a real join.
        return await _db.NetworkProviders
            .Include(np => np.Provider)
                .ThenInclude(p => p.ProviderSpecialties)
                    .ThenInclude(ps => ps.Specialty)
            .Include(np => np.Facility)
            .FirstOrDefaultAsync(np =>
                np.ProviderNetworkId == networkId &&
                np.ProviderId == providerId &&
                np.FacilityId == facilityId,
                ct);
    }

    public async Task<NetworkProvider?> GetMembershipByIdOrProviderAsync(Guid networkId, Guid idOrProviderId, CancellationToken ct = default)
    {
        return await _db.NetworkProviders
            .Include(np => np.Provider)
                .ThenInclude(p => p.ProviderSpecialties)
                    .ThenInclude(ps => ps.Specialty)
            .Include(np => np.Facility)
            .Where(np => np.ProviderNetworkId == networkId && (np.Id == idOrProviderId || np.ProviderId == idOrProviderId))
            .OrderByDescending(np => np.Id == idOrProviderId)
            .ThenByDescending(np => np.FacilityId != Guid.Empty)
            .ThenBy(np => np.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);
    }

    public Task RemoveProviderAsync(NetworkProvider entry, CancellationToken ct = default)
    {
        _db.NetworkProviders.Remove(entry);
        return Task.CompletedTask;
    }

    public async Task<List<Provider>> GetNetworkProvidersAsync(Guid tenantId, Guid networkId, CancellationToken ct = default)
    {
        return await _db.NetworkProviders
            .AsNoTracking()
            .Where(np => np.ProviderNetworkId == networkId && np.TenantId == tenantId)
            .Include(np => np.Provider)
                .ThenInclude(p => p.ProviderSpecialties)
                    .ThenInclude(ps => ps.Specialty)
            .Select(np => np.Provider)
            .OrderBy(p => p.Name)
            .ToListAsync(ct);
    }

    public async Task<List<NetworkProvider>> GetNetworkProviderMembershipsAsync(Guid tenantId, Guid networkId, CancellationToken ct = default)
    {
        return await _db.NetworkProviders
            .AsNoTracking()
            .Where(np => np.ProviderNetworkId == networkId && np.TenantId == tenantId)
            .Include(np => np.Provider)
                .ThenInclude(p => p.ProviderSpecialties)
                    .ThenInclude(ps => ps.Specialty)
            .Include(np => np.Facility)
            .OrderBy(np => np.Provider.OrganizationName ?? np.Provider.Name)
            .ThenBy(np => np.Facility.Name)
            .ThenBy(np => np.Facility.City)
            .ToListAsync(ct);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _db.SaveChangesAsync(ct);
    }

    // ── CC2-INT-B06-01: Shared provider registry global lookups ───────────────

    public async Task<List<Provider>> SearchProvidersGlobalAsync(
        string? name, string? phone, string? npi, string? city,
        int limit = 20, CancellationToken ct = default)
    {
        var q = _db.Providers.AsNoTracking().AsQueryable();

        // NPI exact match — highest priority, most specific
        if (!string.IsNullOrWhiteSpace(npi))
        {
            var npiTrimmed = npi.Trim();
            q = q.Where(p => p.Npi == npiTrimmed);
        }
        else
        {
            // Name contains (case-insensitive on MySQL collation)
            if (!string.IsNullOrWhiteSpace(name))
            {
                var nameTrimmed = name.Trim();
                q = q.Where(p => p.Name.Contains(nameTrimmed) ||
                                 (p.OrganizationName != null && p.OrganizationName.Contains(nameTrimmed)) ||
                                 p.ProviderFacilities.Any(pf => pf.Facility != null && pf.Facility.Name.Contains(nameTrimmed)));
            }

            // Phone: strip non-digits client-side, match normalized
            if (!string.IsNullOrWhiteSpace(phone))
            {
                var phoneTrimmed = phone.Trim();
                q = q.Where(p => p.Phone.Contains(phoneTrimmed) ||
                                 p.ProviderFacilities.Any(pf => pf.Facility != null && pf.Facility.Phone != null && pf.Facility.Phone.Contains(phoneTrimmed)));
            }

            // City
            if (!string.IsNullOrWhiteSpace(city))
            {
                var cityTrimmed = city.Trim();
                q = q.Where(p => p.City.Contains(cityTrimmed) ||
                                 p.ProviderFacilities.Any(pf => pf.Facility != null && pf.Facility.City.Contains(cityTrimmed)));
            }
        }

        return await IncludeProviderLookups(q)
            .OrderBy(p => p.Name)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<Provider?> GetProviderByIdGlobalAsync(Guid id, CancellationToken ct = default)
    {
        return await IncludeProviderLookups(_db.Providers.AsNoTracking())
            .FirstOrDefaultAsync(p => p.Id == id, ct);
    }

    public async Task<Provider?> GetProviderByNpiAsync(string npi, CancellationToken ct = default)
    {
        var trimmed = npi.Trim();
        return await IncludeProviderLookups(_db.Providers.AsNoTracking())
            .FirstOrDefaultAsync(p => p.Npi == trimmed, ct);
    }

    public async Task<Provider?> GetProviderByTenantEmailAsync(Guid tenantId, string email, CancellationToken ct = default)
    {
        var trimmed = email.Trim().ToLowerInvariant();
        return await IncludeProviderLookups(_db.Providers.AsNoTracking())
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Email == trimmed, ct);
    }

    public async Task AddProviderToRegistryAsync(Provider provider, CancellationToken ct = default)
    {
        await _db.Providers.AddAsync(provider, ct);
    }

    public Task UpdateProviderInRegistryAsync(Provider provider, CancellationToken ct = default)
    {
        _db.Providers.Update(provider);
        return Task.CompletedTask;
    }

    public async Task<Dictionary<string, Provider>> GetProvidersByNpisAsync(IEnumerable<string> npis, CancellationToken ct = default)
    {
        var normalized = npis
            .Where(npi => !string.IsNullOrWhiteSpace(npi))
            .Select(npi => npi.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (normalized.Count == 0) return new Dictionary<string, Provider>(StringComparer.Ordinal);

        var providers = await _db.Providers.AsNoTracking()
            .Where(p => p.Npi != null && normalized.Contains(p.Npi))
            .Include(p => p.ProviderSpecialties)
                .ThenInclude(ps => ps.Specialty)
            .Include(p => p.ProviderFacilities)
                .ThenInclude(pf => pf.Facility)
            .ToListAsync(ct);

        return providers
            .GroupBy(p => p.Npi!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.CreatedAtUtc).First(), StringComparer.Ordinal);
    }

    public async Task<Dictionary<string, Provider>> GetProvidersByTenantEmailsAsync(
        Guid tenantId, IEnumerable<string> emails, CancellationToken ct = default)
    {
        var normalized = emails
            .Where(email => !string.IsNullOrWhiteSpace(email))
            .Select(email => email.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (normalized.Count == 0) return new Dictionary<string, Provider>(StringComparer.Ordinal);

        var providers = await _db.Providers.AsNoTracking()
            .Where(p => p.TenantId == tenantId && normalized.Contains(p.Email))
            .Include(p => p.ProviderSpecialties)
                .ThenInclude(ps => ps.Specialty)
            .Include(p => p.ProviderFacilities)
                .ThenInclude(pf => pf.Facility)
            .ToListAsync(ct);

        return providers
            .GroupBy(p => p.Email, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.CreatedAtUtc).First(), StringComparer.Ordinal);
    }

    public async Task<HashSet<Guid>> GetNetworkProviderIdsAsync(Guid tenantId, Guid networkId, CancellationToken ct = default)
    {
        var providerIds = await _db.NetworkProviders
            .AsNoTracking()
            .Where(np => np.TenantId == tenantId && np.ProviderNetworkId == networkId)
            .Select(np => np.ProviderId)
            .ToListAsync(ct);

        return providerIds.ToHashSet();
    }

    public async Task<HashSet<string>> GetNetworkProviderLocationKeysAsync(Guid tenantId, Guid networkId, CancellationToken ct = default)
    {
        var keys = await _db.NetworkProviders
            .AsNoTracking()
            .Where(np => np.TenantId == tenantId && np.ProviderNetworkId == networkId)
            .Select(np => np.ProviderId.ToString() + "|" + np.FacilityId.ToString())
            .ToListAsync(ct);

        return keys.ToHashSet(StringComparer.Ordinal);
    }

    public async Task<Facility?> GetFacilityByIdAsync(Guid tenantId, Guid facilityId, CancellationToken ct = default)
    {
        return await _db.Facilities
            .FirstOrDefaultAsync(f => f.TenantId == tenantId && f.Id == facilityId, ct);
    }

    public async Task<Facility?> GetFacilityByIdGlobalAsync(Guid facilityId, CancellationToken ct = default)
    {
        return await _db.Facilities
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == facilityId, ct);
    }

    public async Task<Facility?> FindFacilityAsync(
        Guid tenantId,
        string name,
        string addressLine1,
        string city,
        string state,
        string? postalCode,
        CancellationToken ct = default)
    {
        var normalizedName = name.Trim();
        var normalizedAddress = addressLine1.Trim();
        var normalizedCity = city.Trim();
        var normalizedState = state.Trim().ToUpperInvariant();
        var normalizedPostal = string.IsNullOrWhiteSpace(postalCode) ? null : postalCode.Trim();

        return await _db.Facilities
            .FirstOrDefaultAsync(f =>
                f.TenantId == tenantId &&
                f.Name == normalizedName &&
                f.AddressLine1 == normalizedAddress &&
                f.City == normalizedCity &&
                f.State == normalizedState &&
                f.PostalCode == normalizedPostal,
                ct);
    }

    public async Task AddFacilityAsync(Facility facility, CancellationToken ct = default)
    {
        await _db.Facilities.AddAsync(facility, ct);
    }

    public Task UpdateFacilityAsync(Facility facility, CancellationToken ct = default)
    {
        _db.Facilities.Update(facility);
        return Task.CompletedTask;
    }

    public async Task<bool> HasOtherActiveNetworkProviderForFacilityAsync(
        Guid tenantId, Guid facilityId, Guid excludeNetworkProviderId, CancellationToken ct = default)
    {
        return await _db.NetworkProviders
            .AsNoTracking()
            .AnyAsync(np =>
                np.TenantId == tenantId &&
                np.FacilityId == facilityId &&
                np.Id != excludeNetworkProviderId &&
                np.IsActive,
                ct);
    }

    public async Task<ProviderFacility?> GetProviderFacilityAsync(Guid providerId, Guid facilityId, CancellationToken ct = default)
    {
        return await _db.ProviderFacilities
            .FirstOrDefaultAsync(pf => pf.ProviderId == providerId && pf.FacilityId == facilityId, ct);
    }

    public async Task<ProviderFacility?> GetPrimaryProviderFacilityAsync(Guid providerId, CancellationToken ct = default)
    {
        return await _db.ProviderFacilities
            .Include(pf => pf.Facility)
            .Where(pf => pf.ProviderId == providerId)
            .OrderByDescending(pf => pf.IsPrimary)
            .ThenBy(pf => pf.Facility!.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);
    }

    public async Task AddProviderFacilityAsync(ProviderFacility providerFacility, CancellationToken ct = default)
    {
        await _db.ProviderFacilities.AddAsync(providerFacility, ct);
    }

    public async Task<bool> IsProviderInTenantNetworkAsync(Guid tenantId, Guid providerId, CancellationToken ct = default)
    {
        return await _db.NetworkProviders
            .AsNoTracking()
            .AnyAsync(np =>
                np.ProviderId == providerId &&
                np.TenantId   == tenantId   &&
                _db.ProviderNetworks.Any(n => n.Id == np.ProviderNetworkId && n.TenantId == tenantId && !n.IsDeleted),
                ct);
    }

    public async Task<NetworkProvider?> GetTenantNetworkMembershipAsync(Guid tenantId, Guid networkProviderId, CancellationToken ct = default)
    {
        return await _db.NetworkProviders
            .AsNoTracking()
            .Include(np => np.Provider)
            .Include(np => np.Facility)
            .FirstOrDefaultAsync(np => np.Id == networkProviderId && np.TenantId == tenantId, ct);
    }

    public void ClearTracking()
    {
        _db.ChangeTracker.Clear();
    }

    public async Task SyncProviderCategoriesAsync(Guid providerId, List<Guid> categoryIds, CancellationToken ct = default)
    {
        var existing = await _db.ProviderCategories
            .Where(pc => pc.ProviderId == providerId)
            .ToListAsync(ct);

        _db.ProviderCategories.RemoveRange(existing);

        foreach (var catId in categoryIds)
        {
            _db.ProviderCategories.Add(new ProviderCategory
            {
                ProviderId = providerId,
                CategoryId = catId,
            });
        }
    }

    public async Task SyncProviderSpecialtiesAsync(Guid providerId, List<Guid> specialtyIds, CancellationToken ct = default)
    {
        var existing = await _db.ProviderSpecialties
            .Where(ps => ps.ProviderId == providerId)
            .ToListAsync(ct);

        _db.ProviderSpecialties.RemoveRange(existing);

        foreach (var item in specialtyIds.Distinct().Select((id, index) => new { Id = id, Index = index }))
        {
            _db.ProviderSpecialties.Add(new ProviderSpecialty
            {
                ProviderId = providerId,
                SpecialtyId = item.Id,
                IsPrimary = item.Index == 0
            });
        }
    }

    private static IQueryable<Provider> IncludeProviderLookups(IQueryable<Provider> query) =>
        query
            .Include(p => p.ProviderSpecialties)
                .ThenInclude(ps => ps.Specialty)
            .Include(p => p.ProviderFacilities)
                .ThenInclude(pf => pf.Facility);
}
