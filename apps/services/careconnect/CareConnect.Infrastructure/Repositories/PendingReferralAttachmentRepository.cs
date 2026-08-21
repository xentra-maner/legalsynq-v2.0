using CareConnect.Application.Repositories;
using CareConnect.Domain;
using CareConnect.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace CareConnect.Infrastructure.Repositories;

public class PendingReferralAttachmentRepository : IPendingReferralAttachmentRepository
{
    private readonly CareConnectDbContext _db;

    public PendingReferralAttachmentRepository(CareConnectDbContext db)
    {
        _db = db;
    }

    public async Task<List<PendingReferralAttachment>> GetByRequestAsync(
        Guid tenantId, Guid pendingReferralRequestId, CancellationToken ct = default)
        => await _db.PendingReferralAttachments
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.PendingReferralRequestId == pendingReferralRequestId)
            .OrderByDescending(a => a.CreatedAtUtc)
            .ToListAsync(ct);

    public async Task AddAsync(PendingReferralAttachment attachment, CancellationToken ct = default)
    {
        await _db.PendingReferralAttachments.AddAsync(attachment, ct);
        await _db.SaveChangesAsync(ct);
    }
}
