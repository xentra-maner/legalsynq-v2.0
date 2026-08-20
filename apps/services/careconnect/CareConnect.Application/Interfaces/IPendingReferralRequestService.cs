using CareConnect.Application.DTOs;

namespace CareConnect.Application.Interfaces;

public interface IPendingReferralRequestService
{
    Task<List<LawFirmOptionResponse>> ListLawFirmOptionsAsync(Guid tenantId, CancellationToken ct = default);
    Task<PendingReferralRequestResponse> CreateAsync(
        Guid tenantId, Guid referralAttributionId, CreatePendingReferralRequest request, CancellationToken ct = default);
    Task<PagedResponse<PendingReferralRequestResponse>> SearchForLawFirmAsync(
        Guid tenantId, Guid lawFirmOrganizationId, string? status, int page, int pageSize, CancellationToken ct = default);
    Task<PendingReferralRequestResponse?> GetForLawFirmAsync(
        Guid tenantId, Guid lawFirmOrganizationId, Guid id, CancellationToken ct = default);
    Task<ReferralResponse> ConvertAsync(
        Guid tenantId, Guid lawFirmOrganizationId, Guid id, Guid? userId, ConvertPendingReferralRequest request, CancellationToken ct = default);
}
