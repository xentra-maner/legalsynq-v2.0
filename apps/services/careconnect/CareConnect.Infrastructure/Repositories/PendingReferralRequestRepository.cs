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

    public async Task<PendingReferralRequest?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        return await _db.PendingReferralRequests
            .Where(r => r.TenantId == tenantId && r.Id == id)
            .Include(r => r.ReferralAttribution)
            .Include(r => r.ProviderPreferences)
            .FirstOrDefaultAsync(ct);
    }

    public async Task AddAsync(PendingReferralRequest request, CancellationToken ct = default)
    {
        await _db.PendingReferralRequests.AddAsync(request, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(PendingReferralRequest request, Referral referral, CancellationToken ct = default)
    {
        await _db.Referrals.AddAsync(referral, ct);
        _db.PendingReferralRequests.Update(request);
        await _db.SaveChangesAsync(ct);
    }
}
