namespace CareConnect.Application.DTOs;

public class ReferralCommentResponse
{
    public Guid Id { get; init; }
    public string SenderType { get; init; } = string.Empty;
    public string SenderName { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public DateTime CreatedAtUtc { get; init; }
    public IReadOnlyList<ReferralMessageAttachmentResponse> Attachments { get; init; } = [];
}

public class CreateReferralCommentRequest
{
    public string Message { get; set; } = string.Empty;
}

public class ReferralThreadAttachmentResponse
{
    public Guid Id { get; init; }
    public string FileName { get; init; } = string.Empty;
    public string ContentType { get; init; } = string.Empty;
    public long FileSizeBytes { get; init; }
}

public class ReferralMessageAttachmentResponse
{
    public Guid Id { get; init; }
    public string FileName { get; init; } = string.Empty;
    public string ContentType { get; init; } = string.Empty;
    public long FileSizeBytes { get; init; }
    public DateTime CreatedAtUtc { get; init; }
}

public sealed record ReferralMessageAttachmentUpload(
    Stream FileContent,
    string FileName,
    string ContentType,
    long FileSizeBytes);

public class PublicReferralThreadResponse
{
    public Guid ReferralId { get; init; }
    public Guid TenantId { get; init; }
    public Guid ProviderId { get; init; }
    public string Status { get; init; } = string.Empty;
    public string ClientName { get; init; } = string.Empty;
    public string? ClientPhone { get; init; }
    public string? ClientEmail { get; init; }
    public string? ClientDob { get; init; }
    public string? CaseNumber { get; init; }
    public string Service { get; init; } = string.Empty;
    public string? Urgency { get; init; }
    public string? Notes { get; init; }
    public string ProviderName { get; init; } = string.Empty;
    public string? ProviderTitle { get; init; }
    public string? ProviderFirstName { get; init; }
    public string? ProviderLastName { get; init; }
    public string ProviderEmail { get; init; } = string.Empty;
    public string ProviderPhone { get; init; } = string.Empty;
    // Referral location — the specific facility this referral was routed to, falling back
    // to the provider's own address for legacy/single-location referrals. See ReferralLocationResolver.
    public string? FacilityName { get; init; }
    public string LocationAddressLine1 { get; init; } = string.Empty;
    public string LocationCity { get; init; } = string.Empty;
    public string LocationState { get; init; } = string.Empty;
    public string LocationPostalCode { get; init; } = string.Empty;
    // True when the referral's location is a mobile/roaming facility — LocationAddressLine1
    // holds a human-readable service-area label rather than a real street address in that case.
    public bool LocationIsMobile { get; init; }
    public string? ReferrerFirmName { get; init; }
    public string? ReferrerPhone { get; init; }
    public string? ReferrerName { get; init; }
    public string? ReferrerFirstName { get; init; }
    public string? ReferrerLastName { get; init; }
    public string? ReferrerEmail { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public bool ProviderHasAccount { get; init; }
    public string? DateOfAccident { get; init; }
    public Guid?   TreatmentTypeId   { get; init; }
    public string? TreatmentTypeName { get; init; }
    public string? LienCompanyName { get; init; }
    public string? LienCompanyEmail { get; init; }
    public IReadOnlyList<ReferralCommentResponse> Comments { get; init; } = [];
    public IReadOnlyList<ReferralThreadAttachmentResponse> Attachments { get; init; } = [];
}
