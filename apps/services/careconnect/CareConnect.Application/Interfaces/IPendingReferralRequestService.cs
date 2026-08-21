using CareConnect.Application.DTOs;

namespace CareConnect.Application.Interfaces;

public interface IPendingReferralRequestService
{
    Task<List<LawFirmOptionResponse>> ListLawFirmOptionsAsync(Guid tenantId, CancellationToken ct = default);
    Task<PendingReferralRequestResponse> CreateAsync(
        Guid tenantId, Guid referralAttributionId, CreatePendingReferralRequest request, CancellationToken ct = default);
    Task<PagedResponse<PendingReferralRequestResponse>> SearchForAttributionAsync(
        Guid tenantId,
        Guid referralAttributionId,
        string? status,
        DateTime? createdFrom,
        DateTime? createdTo,
        int page,
        int pageSize,
        CancellationToken ct = default);
    Task<PendingReferralRequestResponse?> GetForAttributionAsync(
        Guid tenantId, Guid referralAttributionId, Guid id, CancellationToken ct = default);
    Task<PagedResponse<PendingReferralRequestResponse>> SearchForLawFirmAsync(
        Guid tenantId, Guid lawFirmOrganizationId, string? status, int page, int pageSize, CancellationToken ct = default);
    Task<PendingReferralRequestResponse?> GetForLawFirmAsync(
        Guid tenantId, Guid lawFirmOrganizationId, Guid id, CancellationToken ct = default);
    Task<PendingReferralRequestResponse> UpdateForLawFirmAsync(
        Guid tenantId, Guid lawFirmOrganizationId, Guid id, Guid? userId, UpdatePendingReferralRequest request, CancellationToken ct = default);
    Task<PendingReferralRequestResponse> CancelForLawFirmAsync(
        Guid tenantId, Guid lawFirmOrganizationId, Guid id, Guid? userId, CancellationToken ct = default);
    Task<AttachmentMetadataResponse> UploadAttachmentForLawFirmAsync(
        Guid tenantId,
        Guid lawFirmOrganizationId,
        Guid id,
        Guid? userId,
        Stream fileContent,
        string fileName,
        string contentType,
        long fileSizeBytes,
        CancellationToken ct = default);
    Task<AttachmentMetadataResponse> UploadAttachmentForAttributionAsync(
        Guid tenantId,
        Guid referralAttributionId,
        Guid id,
        Stream fileContent,
        string fileName,
        string contentType,
        long fileSizeBytes,
        CancellationToken ct = default);
    Task<SignedUrlResponse?> GetAttachmentSignedUrlForLawFirmAsync(
        Guid tenantId,
        Guid lawFirmOrganizationId,
        Guid id,
        Guid attachmentId,
        bool isDownload,
        CancellationToken ct = default);
    Task<ReferralResponse> ConvertAsync(
        Guid tenantId, Guid lawFirmOrganizationId, Guid id, Guid? userId, ConvertPendingReferralRequest request, CancellationToken ct = default);
}
