using BuildingBlocks.Domain;

namespace CareConnect.Domain;

public class PendingReferralRequest : AuditableEntity
{
    public static class Statuses
    {
        public const string PendingReview = "PendingReview";
        public const string Converted = "Converted";
        public const string Cancelled = "Cancelled";

        public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
        {
            PendingReview,
            Converted,
            Cancelled,
        };
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid LawFirmOrganizationId { get; private set; }
    public Guid ReferralAttributionId { get; private set; }
    public string Origin { get; private set; } = ReferralOrigin.ReferralAssociate;

    public string ClientFirstName { get; private set; } = string.Empty;
    public string ClientLastName { get; private set; } = string.Empty;
    public DateTime? ClientDob { get; private set; }
    public string ClientPhone { get; private set; } = string.Empty;
    public string ClientEmail { get; private set; } = string.Empty;
    public string? CaseNumber { get; private set; }
    public string? RequestedService { get; private set; }
    public string Urgency { get; private set; } = string.Empty;
    public Guid? TreatmentTypeId { get; private set; }
    public DateOnly? DateOfAccident { get; private set; }
    public Guid? RecommendedProviderId { get; private set; }
    public Guid? RecommendedFacilityId { get; private set; }
    public string? RecommendedProviderName { get; private set; }
    public string? RecommendedFacilityName { get; private set; }
    public string? Notes { get; private set; }
    public string? LienCompanyName { get; private set; }
    public string? LienCompanyEmail { get; private set; }

    public string Status { get; private set; } = Statuses.PendingReview;
    public Guid? ConvertedReferralId { get; private set; }
    public DateTime? ConvertedAtUtc { get; private set; }
    public Guid? ConvertedByUserId { get; private set; }

    public ReferralAttribution? ReferralAttribution { get; private set; }
    public Referral? ConvertedReferral { get; private set; }
    public List<PendingReferralProviderPreference> ProviderPreferences { get; private set; } = new();
    public List<PendingReferralAttachment> Attachments { get; private set; } = new();

    private PendingReferralRequest() { }

    public static PendingReferralRequest Create(
        Guid tenantId,
        Guid lawFirmOrganizationId,
        Guid referralAttributionId,
        string clientFirstName,
        string clientLastName,
        DateTime? clientDob,
        string clientPhone,
        string clientEmail,
        string? caseNumber,
        string? requestedService,
        string urgency,
        Guid? treatmentTypeId,
        DateOnly? dateOfAccident,
        string? notes,
        string? lienCompanyName,
        string? lienCompanyEmail)
    {
        var now = DateTime.UtcNow;
        return new PendingReferralRequest
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            LawFirmOrganizationId = lawFirmOrganizationId,
            ReferralAttributionId = referralAttributionId,
            Origin = ReferralOrigin.ReferralAssociate,
            ClientFirstName = clientFirstName.Trim(),
            ClientLastName = clientLastName.Trim(),
            ClientDob = clientDob,
            ClientPhone = clientPhone.Trim(),
            ClientEmail = clientEmail.Trim(),
            CaseNumber = caseNumber?.Trim(),
            RequestedService = requestedService?.Trim(),
            Urgency = urgency,
            TreatmentTypeId = treatmentTypeId,
            DateOfAccident = dateOfAccident,
            Notes = notes?.Trim(),
            LienCompanyName = lienCompanyName?.Trim(),
            LienCompanyEmail = lienCompanyEmail?.Trim(),
            Status = Statuses.PendingReview,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
    }

    public void AddProviderPreference(
        Guid providerId,
        Guid? facilityId,
        string providerName,
        string? facilityName,
        int displayOrder)
    {
        var preference = PendingReferralProviderPreference.Create(
            Id,
            providerId,
            facilityId,
            providerName,
            facilityName,
            displayOrder);

        ProviderPreferences.Add(preference);

        if (displayOrder == 0)
        {
            RecommendedProviderId = providerId;
            RecommendedFacilityId = facilityId;
            RecommendedProviderName = providerName.Trim();
            RecommendedFacilityName = facilityName?.Trim();
        }
    }

    public void MarkConverted(Guid referralId, Guid? convertedByUserId)
    {
        if (Status != Statuses.PendingReview)
            throw new InvalidOperationException("Pending referral request is not pending review.");

        Status = Statuses.Converted;
        ConvertedReferralId = referralId;
        ConvertedAtUtc = DateTime.UtcNow;
        ConvertedByUserId = convertedByUserId;
        UpdatedByUserId = convertedByUserId;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void UpdateReviewDetails(
        string clientFirstName,
        string clientLastName,
        DateTime? clientDob,
        string clientPhone,
        string clientEmail,
        string? caseNumber,
        string? requestedService,
        string urgency,
        Guid? treatmentTypeId,
        DateOnly? dateOfAccident,
        string? notes,
        string? lienCompanyName,
        string? lienCompanyEmail,
        Guid? updatedByUserId)
    {
        if (Status != Statuses.PendingReview)
            throw new InvalidOperationException("Pending referral request is not pending review.");

        ClientFirstName = clientFirstName.Trim();
        ClientLastName = clientLastName.Trim();
        ClientDob = clientDob;
        ClientPhone = clientPhone.Trim();
        ClientEmail = clientEmail.Trim();
        CaseNumber = caseNumber?.Trim();
        RequestedService = requestedService?.Trim();
        Urgency = urgency;
        TreatmentTypeId = treatmentTypeId;
        DateOfAccident = dateOfAccident;
        Notes = notes?.Trim();
        LienCompanyName = lienCompanyName?.Trim();
        LienCompanyEmail = lienCompanyEmail?.Trim();
        UpdatedByUserId = updatedByUserId;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void MarkCancelled(Guid? cancelledByUserId)
    {
        if (Status != Statuses.PendingReview)
            throw new InvalidOperationException("Pending referral request is not pending review.");

        Status = Statuses.Cancelled;
        UpdatedByUserId = cancelledByUserId;
        UpdatedAtUtc = DateTime.UtcNow;
    }
}
