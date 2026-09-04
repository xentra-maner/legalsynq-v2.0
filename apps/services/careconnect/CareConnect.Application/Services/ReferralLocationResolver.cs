using CareConnect.Domain;

namespace CareConnect.Application.Services;

/// <summary>
/// Resolves the display location for a referral: the specific facility it was routed to
/// (<see cref="Referral.FacilityId"/>) when known, falling back to the provider's own
/// address for legacy/single-location referrals with no facility association.
///
/// Prefers <c>Referral.Facility</c> and <c>Referral.Provider</c> (populated via
/// <c>.Include(r => r.Facility)</c> / <c>.Include(r => r.Provider)</c>) when loaded. Callers
/// that only have a freshly-constructed, not-yet-reloaded <see cref="Referral"/> (e.g. right
/// after <c>Referral.Create()</c>, before any DB round-trip) should pass the already-loaded
/// <see cref="Provider"/> they have on hand via <paramref name="providerFallback"/> — it is
/// used only when <c>referral.Provider</c> itself is null.
/// </summary>
public static class ReferralLocationResolver
{
    public static ReferralLocation Resolve(Referral referral, Provider? providerFallback = null)
    {
        ArgumentNullException.ThrowIfNull(referral);

        if (referral.Facility is { } facility &&
            (!referral.FacilityId.HasValue || providerFallback is null || providerFallback.Id == referral.ProviderId))
        {
            return new ReferralLocation(
                FacilityName:  facility.Name,
                AddressLine1:  facility.AddressLine1,
                City:          facility.City,
                State:         facility.State,
                PostalCode:    facility.PostalCode ?? string.Empty,
                IsMobile:      facility.IsMobile);
        }

        var provider = providerFallback ?? referral.Provider;
        return new ReferralLocation(
            FacilityName:  null,
            AddressLine1:  provider?.AddressLine1 ?? string.Empty,
            City:          provider?.City ?? string.Empty,
            State:         provider?.State ?? string.Empty,
            PostalCode:    provider?.PostalCode ?? string.Empty,
            IsMobile:      false);
    }
}

public sealed record ReferralLocation(
    string? FacilityName,
    string  AddressLine1,
    string  City,
    string  State,
    string  PostalCode,
    bool    IsMobile = false);
