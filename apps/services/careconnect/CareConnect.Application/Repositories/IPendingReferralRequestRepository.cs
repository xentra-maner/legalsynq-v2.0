using CareConnect.Domain;

namespace CareConnect.Application.Repositories;

public interface IPendingReferralRequestRepository
{
    Task<(List<PendingReferralRequest> Items, int TotalCount)> SearchAsync(
        Guid tenantId, Guid lawFirmOrganizationId, string? status, int page, int pageSize, CancellationToken ct = default);
    Task<(List<PendingReferralRequest> Items, int TotalCount)> SearchForAttributionAsync(
        Guid tenantId,
        Guid referralAttributionId,
        string? status,
        DateTime? createdFrom,
        DateTime? createdTo,
        int page,
        int pageSize,
        CancellationToken ct = default);
    Task<int> CountForAttributionAsync(
        Guid tenantId,
        Guid referralAttributionId,
        string? status,
        DateTime? createdFrom,
        DateTime? createdTo,
        CancellationToken ct = default);
    Task<PendingReferralRequest?> GetForAttributionAsync(
        Guid tenantId,
        Guid referralAttributionId,
        Guid id,
        CancellationToken ct = default);
    Task<PendingReferralRequest?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken ct = default);
    Task AddAsync(PendingReferralRequest request, CancellationToken ct = default);
    Task UpdateAsync(PendingReferralRequest request, CancellationToken ct = default);
    Task UpdateAsync(PendingReferralRequest request, Referral referral, CancellationToken ct = default);
    Task UpdateAsync(PendingReferralRequest request, IReadOnlyCollection<Referral> referrals, CancellationToken ct = default);
}
