using CareConnect.Domain;

namespace CareConnect.Application.Repositories;

public interface IPendingReferralAttachmentRepository
{
    Task<List<PendingReferralAttachment>> GetByRequestAsync(
        Guid tenantId, Guid pendingReferralRequestId, CancellationToken ct = default);
    Task AddAsync(PendingReferralAttachment attachment, CancellationToken ct = default);
}
