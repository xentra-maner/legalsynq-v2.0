using CareConnect.Application.Repositories;
using CareConnect.Domain;
using CareConnect.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace CareConnect.Infrastructure.Repositories;

public sealed class PendingReferralRequestRepository : IPendingReferralRequestRepository
{
    private readonly CareConnectDbContext _db;

    public PendingReferralRequestRepository(CareConnectDbContext db)
    {
        _db = db;
    }

    public async Task<(List<PendingReferralRequest> Items, int TotalCount)> SearchAsync(
        Guid tenantId, Guid lawFirmOrganizationId, string? status, int page, int pageSize, CancellationToken ct = default)
    {
        var q = _db.PendingReferralRequests
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.LawFirmOrganizationId == lawFirmOrganizationId);

        if (!string.IsNullOrWhiteSpace(status))
            q = q.Where(r => r.Status == status);

        var total = await q.CountAsync(ct);
        var items = await q
            .OrderByDescending(r => r.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Include(r => r.ReferralAttribution)
            .Include(r => r.ProviderPreferences)
            .ToListAsync(ct);

        return (items, total);
    }

    public async Task<(List<PendingReferralRequest> Items, int TotalCount)> SearchForAttributionAsync(
        Guid tenantId,
        Guid referralAttributionId,
        string? status,
        DateTime? createdFrom,
        DateTime? createdTo,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        var q = _db.PendingReferralRequests
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.ReferralAttributionId == referralAttributionId);

        if (!string.IsNullOrWhiteSpace(status))
            q = q.Where(r => r.Status == status);

        if (createdFrom.HasValue)
            q = q.Where(r => r.CreatedAtUtc >= createdFrom.Value);

        if (createdTo.HasValue)
        {
            var to = createdTo.Value;
            q = IsDateOnlyFilter(to)
                ? q.Where(r => r.CreatedAtUtc < to.AddDays(1))
                : q.Where(r => r.CreatedAtUtc <= to);
        }

        var total = await q.CountAsync(ct);
        var items = await q
            .OrderByDescending(r => r.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Include(r => r.ReferralAttribution)
            .Include(r => r.ProviderPreferences)
            .ToListAsync(ct);

        return (items, total);
    }

    public async Task<int> CountForAttributionAsync(
        Guid tenantId,
        Guid referralAttributionId,
        string? status,
        DateTime? createdFrom,
        DateTime? createdTo,
        CancellationToken ct = default)
    {
        var q = _db.PendingReferralRequests
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.ReferralAttributionId == referralAttributionId);

        if (!string.IsNullOrWhiteSpace(status))
            q = q.Where(r => r.Status == status);

        if (createdFrom.HasValue)
            q = q.Where(r => r.CreatedAtUtc >= createdFrom.Value);

        if (createdTo.HasValue)
        {
            var to = createdTo.Value;
            q = IsDateOnlyFilter(to)
                ? q.Where(r => r.CreatedAtUtc < to.AddDays(1))
                : q.Where(r => r.CreatedAtUtc <= to);
        }

        return await q.CountAsync(ct);
    }

    private static bool IsDateOnlyFilter(DateTime value) => value.TimeOfDay == TimeSpan.Zero;

    public async Task<PendingReferralRequest?> GetForAttributionAsync(
        Guid tenantId,
        Guid referralAttributionId,
        Guid id,
        CancellationToken ct = default)
    {
        return await _db.PendingReferralRequests
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.ReferralAttributionId == referralAttributionId && r.Id == id)
            .Include(r => r.ReferralAttribution)
            .Include(r => r.ProviderPreferences)
            .Include(r => r.Attachments)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<PendingReferralRequest?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        return await _db.PendingReferralRequests
            .Where(r => r.TenantId == tenantId && r.Id == id)
            .Include(r => r.ReferralAttribution)
            .Include(r => r.ProviderPreferences)
            .Include(r => r.Attachments)
            .FirstOrDefaultAsync(ct);
    }

    public async Task AddAsync(PendingReferralRequest request, CancellationToken ct = default)
    {
        await _db.PendingReferralRequests.AddAsync(request, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(PendingReferralRequest request, CancellationToken ct = default)
    {
        _db.PendingReferralRequests.Update(request);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(PendingReferralRequest request, Referral referral, CancellationToken ct = default)
    {
        await _db.Referrals.AddAsync(referral, ct);
        _db.PendingReferralRequests.Update(request);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(PendingReferralRequest request, IReadOnlyCollection<Referral> referrals, CancellationToken ct = default)
    {
        await _db.Referrals.AddRangeAsync(referrals, ct);
        _db.PendingReferralRequests.Update(request);
        await _db.SaveChangesAsync(ct);
    }
}
