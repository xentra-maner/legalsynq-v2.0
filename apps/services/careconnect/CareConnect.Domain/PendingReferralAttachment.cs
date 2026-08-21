using BuildingBlocks.Domain;

namespace CareConnect.Domain;

public class PendingReferralAttachment : AuditableEntity
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid PendingReferralRequestId { get; private set; }
    public string FileName { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = string.Empty;
    public long FileSizeBytes { get; private set; }
    public string? ExternalDocumentId { get; private set; }
    public string? ExternalStorageProvider { get; private set; }
    public string Status { get; private set; } = AttachmentStatus.Pending;
    public string? Notes { get; private set; }

    public PendingReferralRequest? PendingReferralRequest { get; private set; }

    private PendingReferralAttachment() { }

    public static PendingReferralAttachment Create(
        Guid tenantId,
        Guid pendingReferralRequestId,
        string fileName,
        string contentType,
        long fileSizeBytes,
        string? externalDocumentId,
        string? externalStorageProvider,
        string status,
        string? notes,
        Guid? createdByUserId)
    {
        return new PendingReferralAttachment
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            PendingReferralRequestId = pendingReferralRequestId,
            FileName = fileName.Trim(),
            ContentType = contentType.Trim(),
            FileSizeBytes = fileSizeBytes,
            ExternalDocumentId = externalDocumentId?.Trim(),
            ExternalStorageProvider = externalStorageProvider?.Trim(),
            Status = status,
            Notes = notes?.Trim(),
            CreatedByUserId = createdByUserId,
            UpdatedByUserId = createdByUserId,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        };
    }
}
