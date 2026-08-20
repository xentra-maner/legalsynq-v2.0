namespace CareConnect.Domain;

public class PendingReferralProviderPreference
{
    public Guid Id { get; private set; }
    public Guid PendingReferralRequestId { get; private set; }
    public Guid ProviderId { get; private set; }
    public Guid? FacilityId { get; private set; }
    public string ProviderName { get; private set; } = string.Empty;
    public string? FacilityName { get; private set; }
    public int DisplayOrder { get; private set; }

    public PendingReferralRequest? PendingReferralRequest { get; private set; }

    private PendingReferralProviderPreference() { }

    public static PendingReferralProviderPreference Create(
        Guid pendingReferralRequestId,
        Guid providerId,
        Guid? facilityId,
        string providerName,
        string? facilityName,
        int displayOrder)
    {
        return new PendingReferralProviderPreference
        {
            Id = Guid.CreateVersion7(),
            PendingReferralRequestId = pendingReferralRequestId,
            ProviderId = providerId,
            FacilityId = facilityId,
            ProviderName = providerName.Trim(),
            FacilityName = facilityName?.Trim(),
            DisplayOrder = displayOrder,
        };
    }
}
