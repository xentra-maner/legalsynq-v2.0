// BLK-PERF-01: All read-only queries use AsNoTracking() to avoid EF Core change-tracking overhead.
using CareConnect.Application.DTOs;
using CareConnect.Application.Repositories;
using CareConnect.Domain;
using CareConnect.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Text.RegularExpressions;

namespace CareConnect.Infrastructure.Repositories;

public class ReferralRepository : IReferralRepository
{
    private static readonly HashSet<string> ReferralSearchStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "at", "by", "client", "clients", "contact", "contacts",
        "find", "for", "from", "in", "last", "latest", "law", "list", "look",
        "lookup", "me", "name", "of", "org", "recent", "recently",
        "organization", "organizations", "patient", "patients", "provider", "providers",
        "referral", "referrals", "referrer", "referrers", "search", "sent", "show",
        "the", "to", "up", "with",
    };

    private readonly CareConnectDbContext _db;

    public ReferralRepository(CareConnectDbContext db)
    {
        _db = db;
    }

    public async Task<(List<Referral> Items, int TotalCount)> SearchAsync(Guid tenantId, GetReferralsQuery query, CancellationToken ct = default)
    {
        IQueryable<Referral> q;
        var scopedTenantIds = query.TenantIds?
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        if (query.CrossTenantReceiver && query.ReceivingOrgId.HasValue)
        {
            q = _db.Referrals
                .AsNoTracking()
                .Where(r =>
                    r.ReceivingOrganizationId == query.ReceivingOrgId.Value ||
                    (r.Provider != null && r.Provider.OrganizationId == query.ReceivingOrgId.Value));
        }
        else if (query.CrossTenantReferrer &&
                 (query.ReferringOrgId.HasValue || !string.IsNullOrWhiteSpace(query.ReferrerEmail)))
        {
            // Mirrors the CrossTenantReceiver branch above: match on org/email identity
            // rather than gating on TenantId first, so the result is immune to any
            // tenant-ID drift between the Tenant service and Identity.
            var referrerEmailLower = query.ReferrerEmail?.Trim().ToLower();
            q = _db.Referrals
                .AsNoTracking()
                .Where(r =>
                    (query.ReferringOrgId.HasValue && r.ReferringOrganizationId == query.ReferringOrgId.Value) ||
                    (!string.IsNullOrWhiteSpace(referrerEmailLower) && r.ReferrerEmail != null &&
                     r.ReferringOrganizationId == null &&
                     r.ReferrerEmail.ToLower() == referrerEmailLower));
        }
        else if (scopedTenantIds is { Count: > 0 })
        {
            q = _db.Referrals
                .AsNoTracking()
                .Where(r => scopedTenantIds.Contains(r.TenantId));
        }
        else
        {
            q = _db.Referrals
                .AsNoTracking()
                .Where(r => r.TenantId == tenantId);
        }

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            // Comma-separated values group multiple raw statuses under one filter option
            // (e.g. the Representative Portal's "Pending" = New,NewOpened) without needing
            // a separate grouped-status concept on the domain model.
            // List<T>, not string[]: EF's LINQ interpreter mis-evaluates array.Contains(x) in
            // .NET 10 (it resolves to MemoryExtensions.Contains(ReadOnlySpan<T>, T), which the
            // parameter-extracting expression visitor cannot compile — see TypeLoadException on
            // 'System.ReadOnlySpan`1[System.String]'). List<T>.Contains is an unambiguous
            // instance method and EF translates it to SQL IN exactly the same way.
            var statuses = query.Status
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            q = q.Where(r => statuses.Contains(r.Status));
        }

        if (query.ProviderId.HasValue)
            q = q.Where(r => r.ProviderId == query.ProviderId.Value);

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            foreach (var token in BuildSearchTokens(query.SearchText, ReferralSearchStopWords))
            {
                var search = token;
                q = q.Where(r =>
                    r.ClientFirstName.ToLower().Contains(search) ||
                    r.ClientLastName.ToLower().Contains(search) ||
                    (r.ClientFirstName.ToLower() + " " + r.ClientLastName.ToLower()).Contains(search) ||
                    (r.SubjectNameSnapshot != null && r.SubjectNameSnapshot.ToLower().Contains(search)) ||
                    (r.CaseNumber != null && r.CaseNumber.ToLower().Contains(search)) ||
                    (r.ReferrerName != null && r.ReferrerName.ToLower().Contains(search)) ||
                    (r.ReferrerFirmName != null && r.ReferrerFirmName.ToLower().Contains(search)) ||
                    (r.ReferrerEmail != null && r.ReferrerEmail.ToLower().Contains(search)) ||
                    (r.Provider != null && (
                        r.Provider.Name.ToLower().Contains(search) ||
                        (r.Provider.OrganizationName != null && r.Provider.OrganizationName.ToLower().Contains(search)))));
            }
        }

        if (!string.IsNullOrWhiteSpace(query.ClientName))
        {
            var name = query.ClientName.Trim().ToLower();
            q = q.Where(r =>
                r.ClientFirstName.ToLower().Contains(name) ||
                r.ClientLastName.ToLower().Contains(name) ||
                (r.ClientFirstName.ToLower() + " " + r.ClientLastName.ToLower()).Contains(name));
        }

        if (!string.IsNullOrWhiteSpace(query.CaseNumber))
        {
            var cn = query.CaseNumber.Trim().ToLower();
            q = q.Where(r => r.CaseNumber != null && r.CaseNumber.ToLower().Contains(cn));
        }

        if (!string.IsNullOrWhiteSpace(query.ProviderName))
        {
            foreach (var token in BuildSearchTokens(query.ProviderName, ReferralSearchStopWords))
            {
                var providerName = token;
                q = q.Where(r => r.Provider != null && (
                    r.Provider.Name.ToLower().Contains(providerName) ||
                    (r.Provider.OrganizationName != null && r.Provider.OrganizationName.ToLower().Contains(providerName))));
            }
        }

        if (!string.IsNullOrWhiteSpace(query.ReferrerName))
        {
            foreach (var token in BuildSearchTokens(query.ReferrerName, ReferralSearchStopWords))
            {
                var referrerName = token;
                q = q.Where(r =>
                    (r.ReferrerName != null && r.ReferrerName.ToLower().Contains(referrerName)) ||
                    (r.ReferrerFirmName != null && r.ReferrerFirmName.ToLower().Contains(referrerName)));
            }
        }

        if (!string.IsNullOrWhiteSpace(query.Urgency))
            q = q.Where(r => r.Urgency == query.Urgency);

        if (query.CreatedFrom.HasValue)
            q = q.Where(r => r.CreatedAtUtc >= query.CreatedFrom.Value);

        if (query.CreatedTo.HasValue)
        {
            var createdTo = query.CreatedTo.Value;
            if (createdTo.TimeOfDay == TimeSpan.Zero)
            {
                var exclusiveCreatedTo = createdTo.Date.AddDays(1);
                q = q.Where(r => r.CreatedAtUtc < exclusiveCreatedTo);
            }
            else
            {
                q = q.Where(r => r.CreatedAtUtc <= createdTo);
            }
        }

        // CC-REFERRER-EMAIL: include referrals by org ID, and also any publicly-submitted
        // referrals (ReferringOrganizationId IS NULL) whose ReferrerEmail matches the
        // caller's email — covering referrals sent before the law firm activated their portal.
        // Skipped when CrossTenantReferrer already applied this exact filter above.
        if (!query.CrossTenantReferrer &&
            (query.ReferringOrgId.HasValue || !string.IsNullOrWhiteSpace(query.ReferrerEmail)))
        {
            var emailLower = query.ReferrerEmail?.Trim().ToLower();
            q = q.Where(r =>
                (query.ReferringOrgId.HasValue && r.ReferringOrganizationId == query.ReferringOrgId.Value) ||
                (!string.IsNullOrWhiteSpace(emailLower) && r.ReferrerEmail != null &&
                 r.ReferringOrganizationId == null &&
                 r.ReferrerEmail.ToLower() == emailLower));
        }

        if (!query.CrossTenantReceiver && query.ReceivingOrgId.HasValue)
            q = q.Where(r =>
                r.ReceivingOrganizationId == query.ReceivingOrgId.Value ||
                (r.Provider != null && r.Provider.OrganizationId == query.ReceivingOrgId.Value));

        if (query.ReferralAttributionId.HasValue)
            q = q.Where(r => r.ReferralAttributionId == query.ReferralAttributionId.Value);

        // Referral Representative visibility scope — applied last, unconditionally, on top of
        // whichever branch built `q` above. This is the single enforcement point: every caller
        // of SearchAsync that sets RestrictedToAttributionIds is scoped here, with no code path
        // that can reach the return below without passing through this filter.
        if (query.RestrictedToAttributionIds is not null)
        {
            var allowedIds = query.RestrictedToAttributionIds;
            q = q.Where(r => r.ReferralAttributionId != null && allowedIds.Contains(r.ReferralAttributionId.Value));
        }

        var totalCount = await q.CountAsync(ct);

        var skip = (query.Page - 1) * query.PageSize;
        var items = await q
            .OrderByDescending(r => r.CreatedAtUtc)
            .Skip(skip)
            .Take(query.PageSize)
            .Include(r => r.Provider)
            .Include(r => r.Facility)
            .Include(r => r.ReferralAttribution)
            .ToListAsync(ct);

        return (items, totalCount);
    }

    private static IReadOnlyList<string> BuildSearchTokens(string? value, ISet<string> stopWords)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        var tokens = Regex.Split(value.Trim().ToLowerInvariant(), "[^a-z0-9]+")
            .Where(token => token.Length > 1 && !stopWords.Contains(token))
            .Distinct()
            .ToList();

        if (tokens.Count > 1 && tokens.Any(token => token.Length > 2))
        {
            var descriptiveTokens = tokens
                .Where(token => token.Length > 2 || token.Any(char.IsDigit))
                .ToList();

            if (descriptiveTokens.Count > 0)
                tokens = descriptiveTokens;
        }

        return tokens.Count > 0
            ? tokens
            : [value.Trim().ToLowerInvariant()];
    }

    public async Task<Referral?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        return await _db.Referrals
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.Id == id)
            .Include(r => r.Provider)
            .Include(r => r.Facility)
            .Include(r => r.ReferralAttribution)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<Referral?> GetByIdForAttributionsAsync(Guid tenantId, Guid id, IReadOnlyList<Guid> allowedAttributionIds, CancellationToken ct = default)
    {
        if (allowedAttributionIds.Count == 0)
            return null;

        return await _db.Referrals
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId
                     && r.Id == id
                     && r.ReferralAttributionId != null
                     && allowedAttributionIds.Contains(r.ReferralAttributionId.Value))
            .Include(r => r.Provider)
            .Include(r => r.Facility)
            .Include(r => r.ReferralAttribution)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<Referral?> GetByIdGlobalAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.Referrals
            .AsNoTracking()
            .Where(r => r.Id == id)
            .Include(r => r.Provider)
            .Include(r => r.Facility)
            .Include(r => r.ReferralAttribution)
            .FirstOrDefaultAsync(ct);
    }

    public async Task AddAsync(Referral referral, CancellationToken ct = default)
    {
        await _db.Referrals.AddAsync(referral, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(Referral referral, ReferralStatusHistory? history = null, ReferralProviderReassignment? providerReassignment = null, CancellationToken ct = default)
    {
        _db.Referrals.Update(referral);

        if (history is not null)
            await _db.ReferralStatusHistories.AddAsync(history, ct);

        if (providerReassignment is not null)
            await _db.ReferralProviderReassignments.AddAsync(providerReassignment, ct);

        await _db.SaveChangesAsync(ct);
    }

    public Task<int> BackfillReferringOrganizationByEmailAsync(
        Guid tenantId,
        string referrerEmail,
        Guid organizationId,
        CancellationToken ct = default)
    {
        var emailLower = referrerEmail.Trim().ToLowerInvariant();

        return _db.Referrals
            .Where(r => r.TenantId == tenantId
                     && r.ReferringOrganizationId == null
                     && r.ReferrerEmail != null
                     && r.ReferrerEmail.ToLower() == emailLower)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(r => r.ReferringOrganizationId, organizationId),
                ct);
    }

    public Task<int> BackfillReceivingOrganizationAsync(
        Guid tenantId,
        Guid providerId,
        Guid organizationId,
        CancellationToken ct = default)
    {
        return _db.Referrals
            .Where(r => r.TenantId == tenantId
                     && r.ProviderId == providerId
                     && r.ReceivingOrganizationId != organizationId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(r => r.ReceivingOrganizationId, organizationId),
                ct);
    }

    public async Task<List<ReferralStatusHistory>> GetHistoryByReferralAsync(Guid tenantId, Guid referralId, CancellationToken ct = default)
    {
        return await _db.ReferralStatusHistories
            .AsNoTracking()
            .Where(h => h.TenantId == tenantId && h.ReferralId == referralId)
            .OrderByDescending(h => h.ChangedAtUtc)
            .ToListAsync(ct);
    }

    public async Task AddProviderReassignmentAsync(ReferralProviderReassignment reassignment, CancellationToken ct = default)
    {
        await _db.ReferralProviderReassignments.AddAsync(reassignment, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<List<ReferralProviderReassignment>> GetProviderReassignmentsByReferralAsync(Guid tenantId, Guid referralId, CancellationToken ct = default)
    {
        return await _db.ReferralProviderReassignments
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.ReferralId == referralId)
            .OrderBy(r => r.ReassignedAtUtc)
            .ToListAsync(ct);
    }

    public async Task<string?> GetTreatmentTypeNameAsync(Guid id, CancellationToken ct = default)
    {
        var conn = _db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await ((System.Data.Common.DbConnection)conn).OpenAsync(ct);
        await using var cmd = ((System.Data.Common.DbConnection)conn).CreateCommand();
        cmd.CommandText = "SELECT `Name` FROM `cc_TreatmentTypes` WHERE `Id` = @id AND `IsActive` = 1 LIMIT 1";
        var param = cmd.CreateParameter();
        param.ParameterName = "@id";
        param.Value = id.ToString();
        cmd.Parameters.Add(param);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is string s ? s : null;
    }

    public async Task<Dictionary<Guid, string>> GetProviderNetworkNamesAsync(IEnumerable<Guid> providerIds, CancellationToken ct = default)
    {
        var ids = providerIds.Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<Guid, string>();

        // Fetch matching (ProviderId, NetworkName) pairs from the database.
        // GroupBy + First() cannot be reliably translated to SQL by EF Core / Pomelo MySQL,
        // so we pull the flat list into memory and group in .NET.
        var rows = await _db.NetworkProviders
            .AsNoTracking()
            .Where(np => ids.Contains(np.ProviderId))
            .Join(_db.ProviderNetworks,
                np => np.ProviderNetworkId,
                pn => pn.Id,
                (np, pn) => new { np.ProviderId, pn.Name })
            .ToListAsync(ct);

        // One provider may belong to multiple networks; keep the first network name per provider.
        return rows
            .GroupBy(x => x.ProviderId)
            .ToDictionary(g => g.Key, g => g.First().Name);
    }
}
