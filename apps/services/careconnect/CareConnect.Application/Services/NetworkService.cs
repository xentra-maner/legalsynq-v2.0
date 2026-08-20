using System.Globalization;
using BuildingBlocks.Exceptions;
using CareConnect.Application.DTOs;
using CareConnect.Application.Helpers;
using CareConnect.Application.Interfaces;
using CareConnect.Application.Repositories;
using CareConnect.Domain;
using Microsoft.Extensions.Logging;

namespace CareConnect.Application.Services;

// CC2-INT-B06 / CC2-INT-B06-01 — provider network management with shared provider registry
public class NetworkService : INetworkService
{
    private readonly INetworkRepository _networks;
    private readonly ICategoryRepository _categories;
    private readonly ISpecialtyRepository _specialties;
    private readonly IProviderImportParser _providerImportParser;
    private readonly ILogger<NetworkService> _logger;

    public NetworkService(
        INetworkRepository networks,
        ICategoryRepository categories,
        ISpecialtyRepository specialties,
        IProviderImportParser providerImportParser,
        ILogger<NetworkService> logger)
    {
        _networks = networks;
        _categories = categories;
        _specialties = specialties;
        _providerImportParser = providerImportParser;
        _logger = logger;
    }

    // ── Network CRUD ─────────────────────────────────────────────────────────

    public async Task<List<NetworkSummaryResponse>> GetAllAsync(Guid tenantId, CancellationToken ct = default)
    {
        var networks = await _networks.GetAllByTenantAsync(tenantId, ct);

        var tasks = networks.Select(async n =>
        {
            var detail = await _networks.GetWithProvidersAsync(tenantId, n.Id, ct);
            return ToSummary(n, detail?.NetworkProviders.Count ?? 0);
        });

        return (await Task.WhenAll(tasks)).ToList();
    }

    public async Task<NetworkDetailResponse> GetByIdAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        var network = await _networks.GetWithProvidersAsync(tenantId, id, ct)
            ?? throw new NotFoundException($"Network {id} not found.");

        return ToDetail(network);
    }

    public async Task<NetworkSummaryResponse> CreateAsync(
        Guid tenantId, Guid? userId, CreateNetworkRequest request, CancellationToken ct = default)
    {
        ValidateName(request.Name);

        if (await _networks.NameExistsAsync(tenantId, request.Name.Trim(), ct: ct))
            throw new ValidationException("Duplicate network name.",
                new() { ["name"] = [$"A network named '{request.Name.Trim()}' already exists."] });

        var network = ProviderNetwork.Create(tenantId, request.Name, request.Description ?? string.Empty);
        await _networks.AddAsync(network, ct);
        await _networks.SaveChangesAsync(ct);

        _logger.LogInformation("Network {NetworkId} created for tenant {TenantId}.", network.Id, tenantId);

        return ToSummary(network, 0);
    }

    public async Task<NetworkSummaryResponse> UpdateAsync(
        Guid tenantId, Guid id, Guid? userId, UpdateNetworkRequest request, CancellationToken ct = default)
    {
        ValidateName(request.Name);

        var network = await _networks.GetWithProvidersAsync(tenantId, id, ct)
            ?? throw new NotFoundException($"Network {id} not found.");

        if (await _networks.NameExistsAsync(tenantId, request.Name.Trim(), excludeId: id, ct: ct))
            throw new ValidationException("Duplicate network name.",
                new() { ["name"] = [$"A network named '{request.Name.Trim()}' already exists."] });

        network.Update(request.Name, request.Description ?? string.Empty);
        await _networks.SaveChangesAsync(ct);

        return ToSummary(network, network.NetworkProviders.Count);
    }

    public async Task DeleteAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        var network = await _networks.GetByIdAsync(tenantId, id, ct)
            ?? throw new NotFoundException($"Network {id} not found.");

        network.Delete();
        await _networks.SaveChangesAsync(ct);

        _logger.LogInformation("Network {NetworkId} soft-deleted for tenant {TenantId}.", id, tenantId);
    }

    // ── Shared provider registry — search/import/match-or-create ────────────

    public async Task<List<ProviderSearchResult>> SearchProvidersAsync(
        string? name, string? phone, string? npi, string? city, CancellationToken ct = default)
    {
        var providers = await _networks.SearchProvidersGlobalAsync(name, phone, npi, city, limit: 20, ct: ct);
        return providers
            .SelectMany(ToSearchResults)
            .OrderBy(r => r.OrganizationName ?? r.Name)
            .ThenBy(r => r.FacilityName ?? r.City)
            .ToList();
    }

    public async Task<ProviderImportSummaryResponse> ImportProvidersAsync(
        Guid networkId,
        Stream fileStream,
        string fileName,
        bool dryRun,
        Guid? userId,
        CancellationToken ct = default)
    {
        var network = await _networks.GetByIdGlobalAsync(networkId, ct)
            ?? throw new NotFoundException($"Network {networkId} not found.");
        var networkTenantId = network.TenantId;

        _logger.LogInformation(
            "Provider import started: TenantId={TenantId} NetworkId={NetworkId} FileName={FileName} DryRun={DryRun}",
            networkTenantId, networkId, fileName, dryRun);

        var parsed = await _providerImportParser.ParseAsync(fileStream, fileName, ct);
        var npis = parsed.Rows
            .Select(r => NormalizeOptional(r.Npi))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var emails = parsed.Rows
            .Select(r => NormalizeEmail(r.Email))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var providersByNpi = await _networks.GetProvidersByNpisAsync(npis, ct);
        var providersByEmail = await _networks.GetProvidersByTenantEmailsAsync(networkTenantId, emails, ct);
        var providerLocationKeysInNetwork = await _networks.GetNetworkProviderLocationKeysAsync(networkTenantId, networkId, ct);

        var rows = new List<ProviderImportRowResult>(parsed.Rows.Count);
        var createdProviders = 0;
        var createdFacilities = 0;
        var reusedByNpi = 0;
        var reusedByEmail = 0;
        var linkedLocations = 0;
        var alreadyInNetwork = 0;
        var failedRows = 0;
        var validRows = 0;
        var processedRows = 0;
        var specialtyIdsByProviderId = new Dictionary<Guid, List<Guid>>();

        foreach (var parsedRow in parsed.Rows)
        {
            if (!TryNormalizeImportRow(parsedRow, networkTenantId, out var normalized, out var errors))
            {
                failedRows++;
                rows.Add(new ProviderImportRowResult(
                    parsedRow.RowNumber,
                    parsedRow.SourceKey,
                    "failed",
                    null,
                    null,
                    null,
                    "Row validation failed.",
                    null,
                    errors));
                continue;
            }

            validRows++;

            try
            {
                if (normalized.TenantId != networkTenantId)
                {
                    failedRows++;
                    rows.Add(new ProviderImportRowResult(
                        parsedRow.RowNumber,
                        parsedRow.SourceKey,
                        "failed",
                        null,
                        null,
                        null,
                        "Row tenant does not match the target network tenant.",
                        normalized,
                        [$"tenantId {normalized.TenantId} does not match network tenant {networkTenantId}."]));
                    continue;
                }

                var resolution = await ResolveImportProviderAsync(
                    networkTenantId,
                    userId,
                    normalized,
                    providersByNpi,
                    providersByEmail,
                    ct);
                var provider = resolution.Provider;
                var status = resolution.Status;
                var message = resolution.Message;
                var facilityResolution = await ResolveImportFacilityAsync(
                    networkTenantId,
                    userId,
                    normalized,
                    ct);
                var facility = facilityResolution.Facility;
                var membershipKey = NetworkProviderLocationKey(provider.Id, facility.Id);

                if (providerLocationKeysInNetwork.Contains(membershipKey))
                {
                    alreadyInNetwork++;
                    processedRows++;
                    rows.Add(new ProviderImportRowResult(
                        parsedRow.RowNumber,
                        parsedRow.SourceKey,
                        "already_in_network",
                        provider.Id,
                        facility.Id,
                        null,
                        "Provider location is already linked to this network.",
                        normalized,
                        []));
                    continue;
                }

                if (!dryRun)
                {
                    var mergedSpecialtyIds = MergeImportedSpecialtyIds(
                        provider,
                        resolution.SpecialtyIds,
                        specialtyIdsByProviderId);

                    if (status == "created")
                    {
                        await _networks.AddProviderToRegistryAsync(provider, ct);
                        if (resolution.CategoryIds.Count > 0)
                            await _networks.SyncProviderCategoriesAsync(provider.Id, resolution.CategoryIds, ct);
                    }
                    await _networks.SyncProviderSpecialtiesAsync(provider.Id, mergedSpecialtyIds, ct);
                    specialtyIdsByProviderId[provider.Id] = mergedSpecialtyIds;

                    if (facilityResolution.Status == "created")
                        await _networks.AddFacilityAsync(facility, ct);
                    else if (facilityResolution.Status == "updated")
                        await _networks.UpdateFacilityAsync(facility, ct);

                    await EnsureProviderFacilityAsync(provider.Id, facility.Id, isPrimary: true, ct);

                    var membership = NetworkProvider.Create(
                        networkTenantId,
                        networkId,
                        provider.Id,
                        facility.Id,
                        normalized.IsActive,
                        normalized.AcceptingReferrals);
                    await _networks.AddProviderAsync(membership, ct);
                    await _networks.SaveChangesAsync(ct);

                    rows.Add(new ProviderImportRowResult(
                        parsedRow.RowNumber,
                        parsedRow.SourceKey,
                        status,
                        provider.Id,
                        facility.Id,
                        membership.Id,
                        message,
                        normalized,
                        []));
                }
                else
                {
                    specialtyIdsByProviderId[provider.Id] = MergeImportedSpecialtyIds(
                        provider,
                        resolution.SpecialtyIds,
                        specialtyIdsByProviderId);

                    rows.Add(new ProviderImportRowResult(
                        parsedRow.RowNumber,
                        parsedRow.SourceKey,
                        status,
                        provider.Id,
                        facility.Id,
                        null,
                        message,
                        normalized,
                        []));
                }

                providerLocationKeysInNetwork.Add(membershipKey);
                CacheResolvedProvider(provider, providersByNpi, providersByEmail);

                processedRows++;
                linkedLocations++;
                if (facilityResolution.Status == "created")
                    createdFacilities++;
                switch (status)
                {
                    case "created":
                        createdProviders++;
                        break;
                    case "reused_npi":
                        reusedByNpi++;
                        break;
                    case "reused_email":
                        reusedByEmail++;
                        break;
                }

            }
            catch (ValidationException ex)
            {
                _networks.ClearTracking();
                failedRows++;
                var rowErrors = ex.Errors.Values.SelectMany(v => v).ToList();
                _logger.LogWarning(
                    ex,
                    "Provider import row validation failed: TenantId={TenantId} NetworkId={NetworkId} FileName={FileName} RowNumber={RowNumber}",
                    networkTenantId, networkId, fileName, parsedRow.RowNumber);

                rows.Add(new ProviderImportRowResult(
                    parsedRow.RowNumber,
                    parsedRow.SourceKey,
                    "failed",
                    null,
                    null,
                    null,
                    "Row validation failed.",
                    normalized,
                    rowErrors.Count == 0 ? [ex.Message] : rowErrors));
            }
            catch (Exception ex)
            {
                _networks.ClearTracking();
                failedRows++;
                _logger.LogWarning(
                    ex,
                    "Provider import row failed: TenantId={TenantId} NetworkId={NetworkId} FileName={FileName} RowNumber={RowNumber}",
                    networkTenantId, networkId, fileName, parsedRow.RowNumber);

                rows.Add(new ProviderImportRowResult(
                    parsedRow.RowNumber,
                    parsedRow.SourceKey,
                    "failed",
                    null,
                    null,
                    null,
                    "Row import failed.",
                    normalized,
                    [ex.Message]));
            }
        }

        _logger.LogInformation(
            "Provider import completed: TenantId={TenantId} NetworkId={NetworkId} FileName={FileName} DryRun={DryRun} TotalRows={TotalRows} Created={CreatedProviders} CreatedFacilities={CreatedFacilities} LinkedLocations={LinkedLocations} ReusedByNpi={ReusedByNpi} ReusedByEmail={ReusedByEmail} AlreadyInNetwork={AlreadyInNetwork} FailedRows={FailedRows}",
            networkTenantId, networkId, fileName, dryRun, parsed.TotalRows, createdProviders, createdFacilities, linkedLocations, reusedByNpi, reusedByEmail, alreadyInNetwork, failedRows);

        return new ProviderImportSummaryResponse(
            TenantId: networkTenantId,
            NetworkId: networkId,
            FileName: fileName,
            DryRun: dryRun,
            TotalRows: parsed.TotalRows,
            ValidRows: validRows,
            ProcessedRows: processedRows,
            CreatedProviders: createdProviders,
            CreatedFacilities: createdFacilities,
            ReusedByNpi: reusedByNpi,
            ReusedByEmail: reusedByEmail,
            LinkedLocations: linkedLocations,
            AlreadyInNetwork: alreadyInNetwork,
            FailedRows: failedRows,
            Rows: rows);
    }

    public async Task<NetworkProviderItem> AddProviderAsync(
        Guid tenantId,
        Guid networkId,
        AddProviderToNetworkRequest request,
        Guid? userId,
        CancellationToken ct = default,
        Guid? owningOrganizationId = null,
        bool isTenantAdmin = false)
    {
        _ = await _networks.GetByIdAsync(tenantId, networkId, ct)
            ?? throw new NotFoundException($"Network {networkId} not found.");

        Provider provider;
        Facility facility;
        bool isActive;
        bool acceptingReferrals;

        if (request.ExistingProviderId.HasValue && request.ExistingFacilityId.HasValue && request.NewProvider is not null)
        {
            throw new ValidationException("Choose either an existing location or a new location, not both.",
                new() { ["request"] = ["ExistingFacilityId and NewProvider cannot be supplied together."] });
        }

        if (request.ExistingProviderId.HasValue)
        {
            provider = await _networks.GetProviderByIdGlobalAsync(request.ExistingProviderId.Value, ct)
                ?? throw new NotFoundException($"Provider {request.ExistingProviderId.Value} not found in the shared registry.");

            if (request.NewProvider is { } location)
            {
                ValidateNewProviderLocation(location);
                ValidateGeoFields(location.Latitude, location.Longitude, location.GeoPointSource);

                facility = await EnsureFacilityAsync(
                    tenantId,
                    FacilityNameForProvider(provider, location.OrganizationName),
                    location.AddressLine1,
                    location.City,
                    location.State,
                    location.PostalCode,
                    location.Phone,
                    location.IsActive,
                    userId,
                    location.Email,
                    location.Latitude,
                    location.Longitude,
                    location.GeoPointSource,
                    location.IsMobile,
                    location.ServiceRadiusMiles,
                    ct);
                isActive = location.IsActive;
                acceptingReferrals = location.AcceptingReferrals;
            }
            else
            {
                facility = request.ExistingFacilityId.HasValue
                    ? await _networks.GetFacilityByIdAsync(tenantId, request.ExistingFacilityId.Value, ct)
                        ?? throw new NotFoundException($"Facility {request.ExistingFacilityId.Value} not found.")
                    : await EnsurePrimaryFacilityAsync(tenantId, provider, userId, ct);
                isActive = provider.IsActive;
                acceptingReferrals = provider.AcceptingReferrals;
            }
        }
        else if (request.NewProvider is { } np)
        {
            ValidateNewProvider(np);
            ValidateGeoFields(np.Latitude, np.Longitude, np.GeoPointSource);
            provider = await ResolveProviderForAddAsync(tenantId, np, userId, ct);
            facility = await EnsureFacilityAsync(
                tenantId,
                FacilityNameForProvider(provider, np.OrganizationName),
                np.AddressLine1,
                np.City,
                np.State,
                np.PostalCode,
                np.Phone,
                np.IsActive,
                userId,
                np.Email,
                np.Latitude,
                np.Longitude,
                np.GeoPointSource,
                np.IsMobile,
                np.ServiceRadiusMiles,
                ct);
            isActive = np.IsActive;
            acceptingReferrals = np.AcceptingReferrals;
        }
        else
        {
            throw new ValidationException("Validation failed.",
                new() { ["request"] = ["Either ExistingProviderId or NewProvider must be provided."] });
        }

        await EnsureProviderFacilityAsync(provider.Id, facility.Id, isPrimary: true, ct);

        var existing = await _networks.GetMembershipAsync(networkId, provider.Id, facility.Id, ct);
        var visibility = ResolveVisibility(request.NewProvider?.Visibility, isTenantAdmin);
        if (existing is null)
            await _networks.AddProviderAsync(NetworkProvider.Create(tenantId, networkId, provider.Id, facility.Id, isActive, acceptingReferrals, owningOrganizationId, visibility), ct);
        else
            _logger.LogDebug("Provider {ProviderId} facility {FacilityId} already in network {NetworkId} — no-op.", provider.Id, facility.Id, networkId);

        await _networks.SaveChangesAsync(ct);

        var loaded = await _networks.GetMembershipAsync(networkId, provider.Id, facility.Id, ct);
        // Use loaded.Provider/loaded.Facility (fresh, fully-included), not the local provider/
        // facility variables — those can be missing just-synced specialties (see the comment
        // on GetMembershipAsync's include list).
        return loaded is not null
            ? ToProviderItem(loaded)
            : ToProviderItem(existing ?? NetworkProvider.Create(tenantId, networkId, provider.Id, facility.Id, isActive, acceptingReferrals, owningOrganizationId, visibility), provider, facility);
    }

    public async Task RemoveProviderAsync(
        Guid tenantId, Guid networkId, Guid providerId, bool cascadeFacility, Guid? userId, CancellationToken ct = default)
    {
        _ = await _networks.GetByIdAsync(tenantId, networkId, ct)
            ?? throw new NotFoundException($"Network {networkId} not found.");

        var entry = await _networks.GetMembershipByIdOrProviderAsync(networkId, providerId, ct)
            ?? throw new NotFoundException($"Provider or network membership {providerId} is not a member of network {networkId}.");

        entry.UpdateStatus(isActive: false, acceptingReferrals: false);

        // cascadeFacility: true only for "Delete location" (the Facilities panel button).
        // A Facility row can be shared across multiple NetworkProvider memberships (see
        // EnsureFacilityAsync dedup-by-address), so only tag it inactive when this was the
        // last active membership pointing at it — otherwise we'd hide a location that's
        // still in active use under a different provider/network. Some legacy memberships
        // have no Facility (FacilityId == Guid.Empty — see GetMembershipByIdOrProviderAsync's
        // ordering), so guard against a null navigation before deactivating it.
        var facilityDeactivated = false;
        if (cascadeFacility && entry.Facility is not null)
        {
            var facilityStillInUse = await _networks.HasOtherActiveNetworkProviderForFacilityAsync(
                tenantId, entry.FacilityId, entry.Id, ct);
            if (!facilityStillInUse)
            {
                entry.Facility.Deactivate(userId);
                facilityDeactivated = true;
            }
        }

        await _networks.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Provider {ProviderId} facility {FacilityId} soft-deleted from network {NetworkId} (association preserved as inactive; facility tagged inactive: {FacilityDeactivated}).",
            entry.ProviderId, entry.FacilityId, networkId, facilityDeactivated);
    }

    public async Task<List<NetworkProviderMarker>> GetMarkersAsync(
        Guid tenantId, Guid networkId, CancellationToken ct = default)
    {
        _ = await _networks.GetByIdAsync(tenantId, networkId, ct)
            ?? throw new NotFoundException($"Network {networkId} not found.");

        var memberships = await _networks.GetNetworkProviderMembershipsAsync(tenantId, networkId, ct);

        return memberships
            .Select(np =>
            {
                var p = np.Provider;
                var f = np.Facility;
                var specialties = MapSpecialties(p.ProviderSpecialties);
                var primarySpecialty = specialties.FirstOrDefault();
                return new NetworkProviderMarker(
                    np.Id,
                    np.Id,
                    p.Id,
                    f.Id,
                    p.Name,
                    p.Title,
                    p.OrganizationName,
                    f.Name,
                    f.City,
                    f.State,
                    f.AddressLine1,
                    f.PostalCode,
                    f.Email ?? p.Email,
                    f.Phone ?? p.Phone,
                    np.AcceptingReferrals,
                    np.IsActive,
                    np.OwningOrganizationId,
                    np.Visibility,
                    f.Latitude ?? p.Latitude ?? 0.0,
                    f.Longitude ?? p.Longitude ?? 0.0,
                    f.GeoPointSource ?? p.GeoPointSource,
                    specialties,
                    primarySpecialty?.Id,
                    primarySpecialty?.Name,
                    f.IsMobile,
                    f.ServiceRadiusMiles,
                    f.IsMobile ? f.AddressLine1 : null);
            })
            .ToList();
    }

    public async Task<NetworkProviderItem> UpdateProviderAsync(
        Guid tenantId,
        Guid networkId,
        Guid providerId,
        UpdateNetworkProviderRequest request,
        Guid? userId,
        CancellationToken ct = default,
        bool isTenantAdmin = false)
    {
        _ = await _networks.GetByIdAsync(tenantId, networkId, ct)
            ?? throw new NotFoundException($"Network {networkId} not found.");

        var membership = await _networks.GetMembershipByIdOrProviderAsync(networkId, providerId, ct)
            ?? throw new NotFoundException($"Provider or network membership {providerId} is not a member of network {networkId}.");

        ValidateNetworkProviderFields(
            request.FirstName,
            request.LastName,
            request.Email,
            request.Phone,
            request.AddressLine1,
            request.City,
            request.State,
            request.PostalCode,
            request.Title,
            request.IsMobile,
            request.ServiceRadiusMiles);
        ValidateGeoFields(request.Latitude, request.Longitude, request.GeoPointSource);
        var specialtyIds = await ValidateSpecialtyIdsAsync(request.SpecialtyIds, ct);

        var provider = membership.Provider
            ?? await _networks.GetProviderByIdGlobalAsync(membership.ProviderId, ct)
            ?? throw new NotFoundException($"Provider {membership.ProviderId} not found in the shared registry.");
        var facility = membership.Facility
            ?? await _networks.GetFacilityByIdAsync(tenantId, membership.FacilityId, ct)
            ?? throw new NotFoundException($"Facility {membership.FacilityId} not found.");

        provider.Update(
            name: BuildProviderDisplayName(request.Title, request.FirstName, request.LastName),
            organizationName: request.OrganizationName,
            email: request.Email.Trim().ToLowerInvariant(),
            phone: request.Phone,
            addressLine1: request.AddressLine1,
            city: request.City,
            state: request.State.Trim().ToUpperInvariant(),
            // Provider's own address is the legacy pre-Facility location; the real, nullable
            // PostalCode for mobile providers lives on the Facility updated below.
            postalCode: request.PostalCode ?? string.Empty,
            isActive: provider.IsActive,
            acceptingReferrals: provider.AcceptingReferrals,
            updatedByUserId: userId,
            latitude: request.Latitude ?? provider.Latitude,
            longitude: request.Longitude ?? provider.Longitude,
            geoPointSource: request.Latitude.HasValue ? request.GeoPointSource : provider.GeoPointSource,
            firstName: request.FirstName,
            lastName: request.LastName,
            title: request.Title);
        facility.Update(
            name: FacilityNameForProvider(provider, request.FacilityName ?? request.OrganizationName),
            addressLine1: request.AddressLine1,
            city: request.City,
            state: request.State.Trim().ToUpperInvariant(),
            postalCode: request.PostalCode,
            phone: request.Phone,
            // Preserve Facility.IsActive here — the "Active" checkbox on this form only
            // toggles the NetworkProvider membership's own operational status (below), a
            // separate, pre-existing feature. Facility.IsActive is the true "was this
            // location deleted" flag and should only ever be flipped by RemoveProviderAsync.
            isActive: facility.IsActive,
            updatedByUserId: userId,
            email: request.Email.Trim().ToLowerInvariant(),
            latitude: request.Latitude ?? facility.Latitude,
            longitude: request.Longitude ?? facility.Longitude,
            geoPointSource: request.Latitude.HasValue ? request.GeoPointSource : facility.GeoPointSource,
            isMobile: request.IsMobile,
            serviceRadiusMiles: request.ServiceRadiusMiles);
        membership.UpdateStatus(request.IsActive, request.AcceptingReferrals);
        if (!string.IsNullOrWhiteSpace(request.Visibility))
        {
            if (!isTenantAdmin)
                throw new ForbiddenException("Only tenant administrators can change provider visibility.");
            membership.SetVisibility(request.Visibility, userId);
        }

        await _networks.UpdateProviderInRegistryAsync(provider, ct);
        await _networks.UpdateFacilityAsync(facility, ct);
        await _networks.SyncProviderSpecialtiesAsync(provider.Id, specialtyIds, ct);
        await _networks.SaveChangesAsync(ct);

        var loaded = await _networks.GetMembershipByIdOrProviderAsync(networkId, membership.Id, ct);
        return loaded is not null ? ToProviderItem(loaded) : ToProviderItem(membership, provider, facility);
    }

    // ── Mapping helpers ───────────────────────────────────────────────────────

    private static NetworkSummaryResponse ToSummary(ProviderNetwork n, int providerCount) =>
        new(n.Id, n.Name, n.Description, providerCount, n.CreatedAtUtc, n.UpdatedAtUtc);

    private static NetworkDetailResponse ToDetail(ProviderNetwork n) =>
        new(
            n.Id,
            n.Name,
            n.Description,
            n.NetworkProviders.Select(ToProviderItem).ToList(),
            n.CreatedAtUtc,
            n.UpdatedAtUtc);

    private static NetworkProviderItem ToProviderItem(NetworkProvider np)
        => ToProviderItem(np, np.Provider, np.Facility);

    private static NetworkProviderItem ToProviderItem(NetworkProvider np, Provider p, Facility f)
    {
        var specialties = MapSpecialties(p.ProviderSpecialties);
        var primarySpecialty = specialties.FirstOrDefault();
        return new(np.Id, np.Id, p.Id, f.Id, p.Name, p.Title, p.OrganizationName, f.Name,
            f.Email ?? p.Email, f.Phone ?? p.Phone, f.City, f.State,
            f.AddressLine1, f.PostalCode, np.IsActive, np.AcceptingReferrals, np.OwningOrganizationId, np.Visibility, p.AccessStage,
            specialties,
            primarySpecialty?.Id,
            primarySpecialty?.Name,
            f.IsActive,
            f.IsMobile,
            f.ServiceRadiusMiles,
            f.IsMobile ? f.AddressLine1 : null);
    }

    private static IEnumerable<ProviderSearchResult> ToSearchResults(Provider p)
    {
        var specialties = MapSpecialties(p.ProviderSpecialties);
        var primarySpecialty = specialties.FirstOrDefault();
        var facilities = p.ProviderFacilities
            .Where(pf => pf.Facility is not null)
            .OrderByDescending(pf => pf.IsPrimary)
            .ThenBy(pf => pf.Facility!.Name)
            .Select(pf => pf.Facility!)
            .ToList();

        if (facilities.Count == 0)
        {
            yield return new(p.Id, null, null, p.Name, p.Title, p.OrganizationName, p.Email, p.Phone,
                p.City, p.State, p.AddressLine1, p.PostalCode,
                p.Npi, p.IsActive, p.AcceptingReferrals, p.AccessStage,
                specialties,
                primarySpecialty?.Id,
                primarySpecialty?.Name);
            yield break;
        }

        foreach (var facility in facilities)
        {
            yield return new(p.Id, facility.Id, facility.Name, p.Name, p.Title, p.OrganizationName, facility.Email ?? p.Email, facility.Phone ?? p.Phone,
            facility.City, facility.State,
            facility.AddressLine1, facility.PostalCode,
            p.Npi, p.IsActive, p.AcceptingReferrals, p.AccessStage,
            specialties,
            primarySpecialty?.Id,
            primarySpecialty?.Name,
            facility.IsMobile,
            facility.ServiceRadiusMiles,
            facility.IsMobile ? facility.AddressLine1 : null);
        }
    }

    private static string ResolveVisibility(string? requestedVisibility, bool isTenantAdmin)
    {
        if (string.IsNullOrWhiteSpace(requestedVisibility))
            return isTenantAdmin ? ProviderVisibility.Public : ProviderVisibility.Private;
        if (!ProviderVisibility.All.Contains(requestedVisibility))
            throw new ValidationException("Validation failed.",
                new() { ["visibility"] = [$"Visibility must be one of: {string.Join(", ", ProviderVisibility.All)}."] });
        if (!isTenantAdmin && requestedVisibility != ProviderVisibility.Private)
            throw new ForbiddenException("Only tenant administrators can make providers public.");
        return requestedVisibility;
    }

    private async Task<Provider> ResolveProviderForAddAsync(
        Guid tenantId,
        NewProviderData np,
        Guid? userId,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(np.Npi))
        {
            var byNpi = await _networks.GetProviderByNpiAsync(np.Npi, ct);
            if (byNpi is not null)
            {
                _logger.LogInformation(
                    "Provider NPI {Npi} already exists (Id={ProviderId}); rejecting new-provider creation.",
                    np.Npi, byNpi.Id);
                ThrowDuplicateProvider(
                    $"A provider with NPI {np.Npi.Trim()} already exists. Search for the existing provider and use Add new location.");
            }
        }

        var byEmail = await _networks.GetProviderByTenantEmailAsync(tenantId, np.Email, ct);
        if (byEmail is not null)
        {
            _logger.LogInformation(
                "Provider email {Email} already exists for tenant {TenantId} (Id={ProviderId}); rejecting new-provider creation.",
                np.Email, tenantId, byEmail.Id);
            ThrowDuplicateProvider(
                "A provider with this email already exists. Search for the existing provider and use Add new location.");
        }

        var provider = Provider.Create(
            tenantId: tenantId,
            name: BuildProviderDisplayName(np.Title, np.FirstName, np.LastName),
            firstName: np.FirstName,
            lastName: np.LastName,
            title: np.Title,
            organizationName: np.OrganizationName,
            email: np.Email.Trim().ToLowerInvariant(),
            phone: np.Phone,
            addressLine1: np.AddressLine1,
            city: np.City,
            state: np.State,
            // Provider's own address fields are the legacy pre-Facility location (kept for
            // backward compat / fallback search) — the real, nullable PostalCode for mobile
            // providers lives on the Facility created below via EnsureFacilityAsync.
            postalCode: np.PostalCode ?? string.Empty,
            isActive: np.IsActive,
            acceptingReferrals: np.AcceptingReferrals,
            createdByUserId: userId,
            latitude: np.Latitude,
            longitude: np.Longitude,
            geoPointSource: np.GeoPointSource,
            npi: NormalizeOptional(np.Npi));

        await _networks.AddProviderToRegistryAsync(provider, ct);

        if (np.CategoryCodes is { Count: > 0 } codes)
        {
            var categoryEntities = await _categories.GetByCodesAsync(codes, ct);
            var orderedIds = new List<Guid>();

            if (!string.IsNullOrWhiteSpace(np.PrimaryCategoryCode))
            {
                var primary = categoryEntities.FirstOrDefault(
                    c => string.Equals(c.Code, np.PrimaryCategoryCode, StringComparison.OrdinalIgnoreCase));
                if (primary is not null) orderedIds.Add(primary.Id);
            }

            orderedIds.AddRange(categoryEntities
                .Where(c => !string.Equals(c.Code, np.PrimaryCategoryCode, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Id));

            if (orderedIds.Count > 0)
                await _networks.SyncProviderCategoriesAsync(provider.Id, orderedIds, ct);
        }

        var specialtyIds = await ResolveSpecialtyIdsByCodesAsync(
            np.SpecialtyCodes,
            np.PrimarySpecialtyCode,
            requireAtLeastOne: true,
            ct);
        await _networks.SyncProviderSpecialtiesAsync(provider.Id, specialtyIds, ct);

        _logger.LogInformation(
            "New provider {ProviderId} ({Name}) registered in shared registry by tenant {TenantId}.",
            provider.Id, provider.Name, tenantId);

        return provider;
    }

    private static void ThrowDuplicateProvider(string message)
    {
        throw new ConflictException(message, "DUPLICATE_PROVIDER");
    }

    private async Task<ImportProviderResolution> ResolveImportProviderAsync(
        Guid tenantId,
        Guid? userId,
        ProviderImportNormalizedRow normalized,
        Dictionary<string, Provider> providersByNpi,
        Dictionary<string, Provider> providersByEmail,
        CancellationToken ct)
    {
        var specialtyIds = await ResolveSpecialtyIdsByCodesAsync(
            normalized.SpecialtyCodes,
            normalized.PrimarySpecialtyCode,
            requireAtLeastOne: true,
            ct);

        if (!string.IsNullOrWhiteSpace(normalized.Npi) &&
            providersByNpi.TryGetValue(normalized.Npi, out var byNpi))
        {
            return new ImportProviderResolution(
                byNpi,
                "reused_npi",
                "Matched existing provider by NPI.",
                [],
                specialtyIds);
        }

        if (providersByEmail.TryGetValue(normalized.Email, out var byEmail))
        {
            return new ImportProviderResolution(
                byEmail,
                "reused_email",
                "Matched existing provider by tenant email.",
                [],
                specialtyIds);
        }

        var categoryIds = await ResolveCategoryIdsByCodesAsync(
            normalized.CategoryCodes,
            normalized.PrimaryCategoryCode,
            ct);

        var provider = Provider.Create(
            tenantId: tenantId,
            name: BuildProviderDisplayName(normalized.Title, normalized.FirstName, normalized.LastName),
            firstName: normalized.FirstName,
            lastName: normalized.LastName,
            title: normalized.Title,
            organizationName: normalized.OrganizationName,
            email: normalized.Email,
            phone: normalized.Phone,
            addressLine1: normalized.AddressLine1,
            city: normalized.City,
            state: normalized.State,
            // Provider's own address is the legacy pre-Facility location; the real, nullable
            // PostalCode for mobile providers lives on the Facility resolved separately below.
            postalCode: normalized.PostalCode ?? string.Empty,
            isActive: normalized.IsActive,
            acceptingReferrals: normalized.AcceptingReferrals,
            createdByUserId: userId,
            latitude: normalized.Latitude,
            longitude: normalized.Longitude,
            geoPointSource: normalized.GeoPointSource,
            npi: normalized.Npi);

        return new ImportProviderResolution(
            provider,
            "created",
            "Provider will be created and linked to the network.",
            categoryIds,
            specialtyIds);
    }

    private static void CacheResolvedProvider(
        Provider provider,
        Dictionary<string, Provider> providersByNpi,
        Dictionary<string, Provider> providersByEmail)
    {
        if (!string.IsNullOrWhiteSpace(provider.Npi))
            providersByNpi[provider.Npi] = provider;

        providersByEmail[provider.Email] = provider;
    }

    // Every import row gets its own Facility row, even when name/address/city/state/ZIP match
    // a facility created earlier in the same (or a prior) import — unlike EnsureFacilityAsync
    // (used by the manual "Add provider" flow), which intentionally dedups by address. Provider
    // CSV rows are per-provider-per-location records; two providers sharing a building (e.g. two
    // physicians in the same practice) must not be collapsed onto one shared Facility.
    private Task<FacilityResolution> ResolveImportFacilityAsync(
        Guid tenantId,
        Guid? userId,
        ProviderImportNormalizedRow normalized,
        CancellationToken ct)
    {
        var facility = Facility.Create(
            tenantId,
            normalized.FacilityName,
            normalized.AddressLine1,
            normalized.City,
            normalized.State,
            normalized.PostalCode,
            normalized.Phone,
            normalized.IsActive,
            userId,
            normalized.Email,
            normalized.Latitude,
            normalized.Longitude,
            normalized.GeoPointSource,
            normalized.IsMobile,
            normalized.ServiceRadiusMiles);

        return Task.FromResult(new FacilityResolution(facility, "created"));
    }

    private async Task<Facility> EnsureFacilityAsync(
        Guid tenantId,
        string name,
        string addressLine1,
        string city,
        string state,
        string? postalCode,
        string phone,
        bool isActive,
        Guid? userId,
        string email,
        double? latitude,
        double? longitude,
        string? geoPointSource,
        bool isMobile,
        double? serviceRadiusMiles,
        CancellationToken ct)
    {
        var facility = await _networks.FindFacilityAsync(tenantId, name, addressLine1, city, state, postalCode, ct);
        if (facility is not null)
        {
            facility.Update(
                name,
                addressLine1,
                city,
                state.Trim().ToUpperInvariant(),
                postalCode,
                phone,
                isActive,
                userId,
                email.Trim().ToLowerInvariant(),
                latitude ?? facility.Latitude,
                longitude ?? facility.Longitude,
                latitude.HasValue ? geoPointSource : facility.GeoPointSource,
                isMobile,
                serviceRadiusMiles);
            await _networks.UpdateFacilityAsync(facility, ct);
            return facility;
        }

        facility = Facility.Create(
            tenantId,
            name,
            addressLine1,
            city,
            state.Trim().ToUpperInvariant(),
            postalCode,
            phone,
            isActive,
            userId,
            email.Trim().ToLowerInvariant(),
            latitude,
            longitude,
            geoPointSource,
            isMobile,
            serviceRadiusMiles);
        await _networks.AddFacilityAsync(facility, ct);
        return facility;
    }

    private async Task<Facility> EnsurePrimaryFacilityAsync(Guid tenantId, Provider provider, Guid? userId, CancellationToken ct)
    {
        var existing = await _networks.GetPrimaryProviderFacilityAsync(provider.Id, ct);
        if (existing?.Facility is not null)
            return existing.Facility;

        var facility = await EnsureFacilityAsync(
            tenantId,
            FacilityNameForProvider(provider, provider.OrganizationName),
            provider.AddressLine1,
            provider.City,
            provider.State,
            provider.PostalCode,
            provider.Phone,
            provider.IsActive,
            userId,
            provider.Email,
            provider.Latitude,
            provider.Longitude,
            provider.GeoPointSource,
            isMobile: false,
            serviceRadiusMiles: null,
            ct);

        await EnsureProviderFacilityAsync(provider.Id, facility.Id, isPrimary: true, ct);
        return facility;
    }

    private async Task EnsureProviderFacilityAsync(Guid providerId, Guid facilityId, bool isPrimary, CancellationToken ct)
    {
        var existing = await _networks.GetProviderFacilityAsync(providerId, facilityId, ct);
        if (existing is null)
            await _networks.AddProviderFacilityAsync(ProviderFacility.Create(providerId, facilityId, isPrimary), ct);
    }

    private static string FacilityNameForProvider(Provider provider, string? organizationName)
        => NormalizeOptional(organizationName) ?? provider.OrganizationName ?? provider.Name;

    private static string NetworkProviderLocationKey(Guid providerId, Guid facilityId)
        => providerId.ToString() + "|" + facilityId.ToString();

    private static List<Guid> MergeImportedSpecialtyIds(
        Provider provider,
        List<Guid> importedSpecialtyIds,
        Dictionary<Guid, List<Guid>> specialtyIdsByProviderId)
    {
        var merged = specialtyIdsByProviderId.TryGetValue(provider.Id, out var known)
            ? [.. known]
            : provider.ProviderSpecialties
                .OrderByDescending(ps => ps.IsPrimary)
                .ThenBy(ps => ps.Specialty?.Name ?? string.Empty)
                .Select(ps => ps.SpecialtyId)
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();

        foreach (var specialtyId in importedSpecialtyIds.Where(id => id != Guid.Empty).Distinct())
        {
            if (!merged.Contains(specialtyId))
                merged.Add(specialtyId);
        }

        return merged;
    }

    private static bool TryNormalizeImportRow(
        ProviderImportParsedRow parsedRow,
        Guid networkTenantId,
        out ProviderImportNormalizedRow normalized,
        out List<string> errors)
    {
        errors = [];

        var tenantId = string.IsNullOrWhiteSpace(parsedRow.TenantId)
            ? networkTenantId
            : ParseRequiredGuid(parsedRow.TenantId, "tenantId must be a valid GUID.", errors) ?? networkTenantId;
        var providerNameParts = SplitImportedProviderName(parsedRow.ProviderName);
        var title = NormalizeOptional(parsedRow.Title) ?? providerNameParts.Title;
        var facilityName = NormalizeRequired(parsedRow.FacilityName ?? parsedRow.OrganizationName, "Medical facility is required.", errors);
        var firstName = NormalizeOptional(parsedRow.FirstName) ?? providerNameParts.FirstName;
        var lastName = NormalizeOptional(parsedRow.LastName) ?? providerNameParts.LastName;
        var email = NormalizeRequired(parsedRow.Email, "Provider email is required.", errors, NormalizeEmail);
        string? phone;
        if (string.IsNullOrWhiteSpace(parsedRow.Phone))
        {
            errors.Add("Provider phone is required.");
            phone = null;
        }
        else if (!PhoneNumberHelper.TryNormalizeOptionalUsPhone(parsedRow.Phone, out phone))
        {
            errors.Add("Provider phone must be a valid 10-digit US phone number.");
        }
        var addressLine1 = NormalizeRequired(parsedRow.AddressLine1, "Address is required.", errors);
        var city = NormalizeRequired(parsedRow.City, "City is required.", errors);
        var state = NormalizeRequired(parsedRow.State, "State is required.", errors, v => v.Trim().ToUpperInvariant());

        // Explicit opt-in only — a blank ZIP alone does not imply mobile, so ordinary bad-data
        // rows still fail the way they always have.
        if (!TryParseOptionalBoolean(parsedRow.IsMobileRaw, defaultValue: false, out var isMobile, out var isMobileError))
            errors.Add(isMobileError);

        var postalCode = isMobile
            ? NormalizeOptional(parsedRow.PostalCode)
            : NormalizeRequired(parsedRow.PostalCode, "Postal code is required.", errors);

        double? serviceRadiusMiles = null;
        if (isMobile)
        {
            serviceRadiusMiles = ParseOptionalDouble(parsedRow.ServiceRadiusMilesRaw, "Service radius", errors)
                ?? ProviderGeoHelper.DefaultServiceRadiusMiles;
            var serviceAreaErrors = new Dictionary<string, string[]>();
            ProviderGeoHelper.ValidateServiceArea(isMobile, serviceRadiusMiles, serviceAreaErrors);
            errors.AddRange(serviceAreaErrors.Values.SelectMany(v => v));
        }

        var categoryCodes = NormalizeCodeList(parsedRow.CategoryCodesRaw);
        var specialtyCodes = NormalizeCodeList(parsedRow.SpecialtyCodesRaw);
        if (specialtyCodes.Count == 0 && categoryCodes.Count > 0)
            specialtyCodes = [.. categoryCodes];
        var primaryCategoryCode = NormalizeOptional(parsedRow.PrimaryCategoryCode) is { } rawPrimaryCategory
            ? Specialty.NormalizeCode(rawPrimaryCategory)
            : categoryCodes.FirstOrDefault();
        var primarySpecialtyCode = NormalizeOptional(parsedRow.PrimarySpecialtyCode) is { } rawPrimarySpecialty
            ? NormalizeSpecialtyAlias(rawPrimarySpecialty)
            : specialtyCodes.FirstOrDefault();

        if (!TryParseOptionalBoolean(parsedRow.IsActiveRaw, defaultValue: true, out var isActive, out var isActiveError))
            errors.Add(isActiveError);

        if (!TryParseOptionalBoolean(parsedRow.AcceptingReferralsRaw, defaultValue: true, out var acceptingReferrals, out var acceptingError))
            errors.Add(acceptingError);

        if (title?.Length > 50)
            errors.Add("Title must be 50 characters or fewer.");

        var latitude = ParseOptionalDouble(parsedRow.LatitudeRaw, "Latitude", errors);
        var longitude = ParseOptionalDouble(parsedRow.LongitudeRaw, "Longitude", errors);
        var geoPointSource = NormalizeGeoPointSource(parsedRow.GeoPointSource, latitude.HasValue || longitude.HasValue);

        var geoErrors = new Dictionary<string, string[]>();
        ProviderGeoHelper.ValidateGeoFields(latitude, longitude, geoPointSource, geoErrors);
        errors.AddRange(geoErrors.Values.SelectMany(v => v));

        if (errors.Count > 0)
        {
            normalized = default!;
            return false;
        }

        normalized = new ProviderImportNormalizedRow(
            TenantId: tenantId,
            Title: title,
            FirstName: firstName ?? string.Empty,
            LastName: lastName ?? string.Empty,
            OrganizationName: NormalizeOptional(parsedRow.OrganizationName) ?? facilityName,
            FacilityName: facilityName!,
            Npi: NormalizeOptional(parsedRow.Npi),
            Email: email!,
            Phone: phone!,
            AddressLine1: addressLine1!,
            City: city!,
            State: state!,
            PostalCode: postalCode,
            IsActive: isActive,
            AcceptingReferrals: acceptingReferrals,
            CategoryCodes: categoryCodes,
            PrimaryCategoryCode: primaryCategoryCode,
            SpecialtyCodes: specialtyCodes,
            PrimarySpecialtyCode: primarySpecialtyCode,
            Latitude: latitude,
            Longitude: longitude,
            GeoPointSource: geoPointSource,
            IsMobile: isMobile,
            ServiceRadiusMiles: isMobile ? serviceRadiusMiles : null);

        return true;
    }

    private static Guid? ParseRequiredGuid(string? value, string errorMessage, List<string> errors)
    {
        var normalized = NormalizeOptional(value);
        if (normalized is null || !Guid.TryParse(normalized, out var parsed))
        {
            errors.Add(errorMessage);
            return null;
        }

        return parsed;
    }

    private static string? NormalizeRequired(
        string? value,
        string errorMessage,
        List<string> errors,
        Func<string, string?>? normalize = null)
    {
        var normalized = normalize is null ? NormalizeOptional(value) : NormalizeOptional(value) is { } text ? normalize(text) : null;
        if (string.IsNullOrWhiteSpace(normalized))
            errors.Add(errorMessage);
        return normalized;
    }

    private static string? NormalizeOptional(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Trim();
    }

    private static string? NormalizeEmail(string? value)
    {
        var normalized = NormalizeOptional(value);
        return normalized?.ToLowerInvariant();
    }

    private static List<string> NormalizeCodeList(string? raw)
    {
        var normalized = NormalizeOptional(raw);
        if (normalized is null) return [];

        return normalized
            .Split([',', ';', '|', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeSpecialtyAlias)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string NormalizeSpecialtyAlias(string value)
    {
        var code = Specialty.NormalizeCode(value);
        return code switch
        {
            "CHIRO" => "CHIROPRACTOR",
            _ => code
        };
    }

    private static (string? Title, string? FirstName, string? LastName) SplitImportedProviderName(string? name)
    {
        var normalized = NormalizeOptional(name);
        if (normalized is null) return (null, null, null);

        var parts = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (parts.Count == 0) return (null, null, null);

        string? title = null;
        var first = parts[0].TrimEnd('.').ToLowerInvariant();
        if (first is "dr" or "mr" or "mrs" or "ms" or "prof")
        {
            title = first switch
            {
                "dr" => "Dr.",
                "mr" => "Mr.",
                "mrs" => "Mrs.",
                "ms" => "Ms.",
                "prof" => "Prof.",
                _ => parts[0]
            };
            parts.RemoveAt(0);
        }

        if (parts.Count == 0) return (title, null, null);
        if (parts.Count == 1) return (title, parts[0], string.Empty);
        return (title, string.Join(" ", parts.Take(parts.Count - 1)), parts[^1]);
    }

    private static double? ParseOptionalDouble(string? raw, string fieldName, List<string> errors)
    {
        var normalized = NormalizeOptional(raw);
        if (normalized is null) return null;

        if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return value;

        errors.Add($"{fieldName} must be a valid number.");
        return null;
    }

    private static string? NormalizeGeoPointSource(string? raw, bool hasCoordinates)
    {
        var normalized = NormalizeOptional(raw);
        if (normalized is null)
            return hasCoordinates ? GeoPointSource.Imported : null;

        var key = normalized.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        return key switch
        {
            "manual" => GeoPointSource.Manual,
            "geocoded" or "geocode" or "nominatim" or "google" or "googlemaps" or "googleplaces" => GeoPointSource.Geocoded,
            "imported" or "import" or "csv" or "migration" or "migrated" => GeoPointSource.Imported,
            _ when GeoPointSource.All.Contains(normalized) => normalized,
            _ => normalized
        };
    }

    private static string BuildProviderDisplayName(string? title, string firstName, string lastName)
    {
        return string.Join(" ", new[] { NormalizeOptional(title), NormalizeOptional(firstName), NormalizeOptional(lastName) }
            .Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private static bool TryParseOptionalBoolean(
        string? raw,
        bool defaultValue,
        out bool value,
        out string error)
    {
        error = string.Empty;
        var normalized = NormalizeOptional(raw);
        if (normalized is null)
        {
            value = defaultValue;
            return true;
        }

        switch (normalized.ToLowerInvariant())
        {
            case "true":
            case "1":
            case "yes":
            case "y":
                value = true;
                return true;
            case "false":
            case "0":
            case "no":
            case "n":
                value = false;
                return true;
            default:
                value = defaultValue;
                error = $"Boolean value '{raw}' is invalid. Use true/false, 1/0, yes/no, or y/n.";
                return false;
        }
    }

    // ── Validation ────────────────────────────────────────────────────────────

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ValidationException("Validation failed.",
                new() { ["name"] = ["Network name is required."] });
        if (name.Trim().Length > 200)
            throw new ValidationException("Validation failed.",
                new() { ["name"] = ["Network name must be 200 characters or fewer."] });
    }

    private static void ValidateNewProvider(NewProviderData np)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(np.FirstName))
            errors["firstName"] = ["Provider first name is required."];
        if (string.IsNullOrWhiteSpace(np.LastName))
            errors["lastName"] = ["Provider last name is required."];
        if (string.IsNullOrWhiteSpace(np.Email))
            errors["email"] = ["Provider email is required."];
        if (string.IsNullOrWhiteSpace(np.Phone))
            errors["phone"] = ["Provider phone is required."];
        if (string.IsNullOrWhiteSpace(np.AddressLine1))
            errors["addressLine1"] = ["Address is required."];
        if (string.IsNullOrWhiteSpace(np.City))
            errors["city"] = ["City is required."];
        if (string.IsNullOrWhiteSpace(np.State))
            errors["state"] = ["State is required."];
        if (!np.IsMobile && string.IsNullOrWhiteSpace(np.PostalCode))
            errors["postalCode"] = ["Postal code is required."];
        if (np.Title?.Trim().Length > 50)
            errors["title"] = ["Title must be 50 characters or fewer."];
        if (np.SpecialtyCodes is null || np.SpecialtyCodes.Count == 0)
            errors["specialtyCodes"] = ["Select at least one specialty."];
        ProviderGeoHelper.ValidateServiceArea(np.IsMobile, np.ServiceRadiusMiles, errors);
        if (errors.Count > 0)
            throw new ValidationException("Validation failed.", errors);
    }

    private static void ValidateNewProviderLocation(NewProviderData np)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(np.Email))
            errors["email"] = ["Location email is required."];
        if (string.IsNullOrWhiteSpace(np.Phone))
            errors["phone"] = ["Location phone is required."];
        if (string.IsNullOrWhiteSpace(np.AddressLine1))
            errors["addressLine1"] = ["Address is required."];
        if (string.IsNullOrWhiteSpace(np.City))
            errors["city"] = ["City is required."];
        if (string.IsNullOrWhiteSpace(np.State))
            errors["state"] = ["State is required."];
        if (!np.IsMobile && string.IsNullOrWhiteSpace(np.PostalCode))
            errors["postalCode"] = ["Postal code is required."];
        ProviderGeoHelper.ValidateServiceArea(np.IsMobile, np.ServiceRadiusMiles, errors);
        if (errors.Count > 0)
            throw new ValidationException("Validation failed.", errors);
    }

    private static void ValidateNetworkProviderFields(
        string firstName,
        string lastName,
        string email,
        string phone,
        string addressLine1,
        string city,
        string state,
        string? postalCode,
        string? title,
        bool isMobile,
        double? serviceRadiusMiles)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(firstName))
            errors["firstName"] = ["Provider first name is required."];
        if (string.IsNullOrWhiteSpace(lastName))
            errors["lastName"] = ["Provider last name is required."];
        if (string.IsNullOrWhiteSpace(email))
            errors["email"] = ["Provider email is required."];
        if (string.IsNullOrWhiteSpace(phone))
            errors["phone"] = ["Provider phone is required."];
        if (string.IsNullOrWhiteSpace(addressLine1))
            errors["addressLine1"] = ["Address is required."];
        if (string.IsNullOrWhiteSpace(city))
            errors["city"] = ["City is required."];
        if (string.IsNullOrWhiteSpace(state))
            errors["state"] = ["State is required."];
        if (!isMobile && string.IsNullOrWhiteSpace(postalCode))
            errors["postalCode"] = ["Postal code is required."];
        if (title?.Trim().Length > 50)
            errors["title"] = ["Title must be 50 characters or fewer."];
        ProviderGeoHelper.ValidateServiceArea(isMobile, serviceRadiusMiles, errors);
        if (errors.Count > 0)
            throw new ValidationException("Validation failed.", errors);
    }

    private static void ValidateGeoFields(double? latitude, double? longitude, string? geoPointSource)
    {
        var errors = new Dictionary<string, string[]>();
        ProviderGeoHelper.ValidateGeoFields(latitude, longitude, geoPointSource, errors);
        if (errors.Count > 0)
            throw new ValidationException("Validation failed.", errors);
    }

    private async Task<List<Guid>> ValidateSpecialtyIdsAsync(List<Guid> specialtyIds, CancellationToken ct)
    {
        var distinct = specialtyIds.Distinct().ToList();
        if (distinct.Count == 0)
            throw new ValidationException("Validation failed.",
                new() { ["specialtyIds"] = ["Select at least one specialty."] });

        var active = await _specialties.GetActiveByIdsAsync(distinct, ct);
        if (active.Count != distinct.Count)
            throw new ValidationException("Validation failed.",
                new() { ["specialtyIds"] = ["One or more selected specialties are inactive or invalid."] });

        return distinct;
    }

    private async Task<List<Guid>> ResolveCategoryIdsByCodesAsync(
        List<string> categoryCodes,
        string? primaryCategoryCode,
        CancellationToken ct)
    {
        if (categoryCodes.Count == 0) return [];

        var categoryEntities = await _categories.GetByCodesAsync(categoryCodes, ct);
        var expectedCount = categoryCodes
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(Specialty.NormalizeCode)
            .Distinct(StringComparer.Ordinal)
            .Count();
        if (categoryEntities.Count != expectedCount)
            throw new ValidationException("Validation failed.",
                new() { ["categoryCodes"] = ["One or more selected categories are inactive or invalid."] });

        var orderedIds = new List<Guid>();
        if (!string.IsNullOrWhiteSpace(primaryCategoryCode))
        {
            var primary = categoryEntities.FirstOrDefault(
                c => string.Equals(c.Code, Specialty.NormalizeCode(primaryCategoryCode), StringComparison.Ordinal));
            if (primary is not null) orderedIds.Add(primary.Id);
        }

        orderedIds.AddRange(categoryEntities
            .Where(c => !orderedIds.Contains(c.Id))
            .OrderBy(c => c.Name)
            .Select(c => c.Id));

        return orderedIds;
    }

    private async Task<List<Guid>> ResolveSpecialtyIdsByCodesAsync(
        List<string>? specialtyCodes,
        string? primarySpecialtyCode,
        bool requireAtLeastOne,
        CancellationToken ct)
    {
        if (specialtyCodes is null || specialtyCodes.Count == 0)
        {
            if (!requireAtLeastOne) return [];
            throw new ValidationException("Validation failed.",
                new() { ["specialtyCodes"] = ["Select at least one specialty."] });
        }

        var specialtyEntities = await _specialties.GetActiveByCodesAsync(specialtyCodes, ct);
        if (specialtyEntities.Count != specialtyCodes.Where(c => !string.IsNullOrWhiteSpace(c)).Select(Specialty.NormalizeCode).Distinct().Count())
            throw new ValidationException("Validation failed.",
                new() { ["specialtyCodes"] = ["One or more selected specialties are inactive or invalid."] });

        var orderedIds = new List<Guid>();
        if (!string.IsNullOrWhiteSpace(primarySpecialtyCode))
        {
            var primary = specialtyEntities.FirstOrDefault(
                s => string.Equals(s.Code, Specialty.NormalizeCode(primarySpecialtyCode), StringComparison.Ordinal));
            if (primary is not null) orderedIds.Add(primary.Id);
        }

        orderedIds.AddRange(specialtyEntities
            .Where(s => !orderedIds.Contains(s.Id))
            .OrderBy(s => s.Name)
            .Select(s => s.Id));

        return orderedIds;
    }

    private static List<SpecialtyResponse> MapSpecialties(IEnumerable<ProviderSpecialty> providerSpecialties)
    {
        return providerSpecialties
            .Where(ps => ps.Specialty != null)
            .OrderByDescending(ps => ps.IsPrimary)
            .ThenBy(ps => ps.Specialty!.Name)
            .Select(ps => SpecialtyService.ToResponse(ps.Specialty!))
            .ToList();
    }

    private sealed record ImportProviderResolution(
        Provider Provider,
        string Status,
        string Message,
        List<Guid> CategoryIds,
        List<Guid> SpecialtyIds);

    private sealed record FacilityResolution(Facility Facility, string Status);
}
