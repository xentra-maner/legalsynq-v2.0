using CareConnect.Application.DTOs;
using CareConnect.Application.Interfaces;
using CareConnect.Application.Repositories;
using CareConnect.Domain;

namespace CareConnect.Application.Services;

/// <summary>
/// Anonymous, read-only referral access for the Representative Portal. Callers (see
/// PublicRepresentativeEndpoints) must verify the access code and resolve its
/// referralAttributionId themselves on every call — this service trusts the pair it's
/// given and scopes every query to it, never widening beyond the single attribution.
/// </summary>
public class RepresentativeReferralService : IRepresentativeReferralService
{
    private readonly IReferralRepository _referrals;
    private readonly IPendingReferralRequestRepository _pendingReferralRequests;
    private readonly IIdentityOrganizationService _identityOrganizationService;

    public RepresentativeReferralService(
        IReferralRepository referrals,
        IPendingReferralRequestRepository pendingReferralRequests,
        IIdentityOrganizationService identityOrganizationService)
    {
        _referrals = referrals;
        _pendingReferralRequests = pendingReferralRequests;
        _identityOrganizationService = identityOrganizationService;
    }

    public async Task<PagedResponse<RepresentativeReferralListItem>> SearchAsync(
        Guid tenantId, Guid referralAttributionId, GetRepresentativeReferralsQuery query, CancellationToken ct = default)
    {
        var internalQuery = new GetReferralsQuery
        {
            Status = query.Status,
            ProviderId = query.ProviderId,
            CreatedFrom = query.SubmittedFrom,
            CreatedTo = query.SubmittedTo,
            Page = query.Page,
            PageSize = query.PageSize,
            RestrictedToAttributionIds = [referralAttributionId],
        };

        var (items, totalCount) = await _referrals.SearchAsync(tenantId, internalQuery, ct);

        if (query.LawFirmOrganizationId.HasValue)
            items = items.Where(r => r.ReferringOrganizationId == query.LawFirmOrganizationId.Value).ToList();

        var lawFirmNames = await ResolveLawFirmNamesAsync(items, ct);

        return new PagedResponse<RepresentativeReferralListItem>
        {
            Page = query.Page,
            PageSize = query.PageSize,
            TotalCount = totalCount,
            Items = items.Select(r => ToListItem(r, lawFirmNames)).ToList(),
        };
    }

    public async Task<RepresentativeReferralDetailResponse?> GetByIdAsync(
        Guid tenantId, Guid referralAttributionId, Guid referralId, CancellationToken ct = default)
    {
        var referral = await _referrals.GetByIdForAttributionsAsync(tenantId, referralId, [referralAttributionId], ct);
        if (referral is null)
            return null;

        var lawFirmNames = await ResolveLawFirmNamesAsync([referral], ct);
        return ToDetail(referral, lawFirmNames);
    }

    public async Task<RepresentativeReferralMetricsResponse> GetMetricsAsync(
        Guid tenantId, Guid referralAttributionId, DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        var allQuery = new GetReferralsQuery { Page = 1, PageSize = int.MaxValue, RestrictedToAttributionIds = [referralAttributionId] };
        var (allItems, _) = await _referrals.SearchAsync(tenantId, allQuery, ct);

        // Same field the "My Referrals" list filters on (submittedFrom/submittedTo →
        // CreatedFrom/CreatedTo) — every metric below respects the range, matching that
        // list's behavior instead of only the (now-removed) separate "in range" count.
        var inRangeItems = allItems.Where(r =>
            (!from.HasValue || r.CreatedAtUtc >= from.Value) &&
            (!to.HasValue || r.CreatedAtUtc <= to.Value)).ToList();

        var byStatus = inRangeItems
            .GroupBy(r => r.Status)
            .ToDictionary(g => g.Key, g => g.Count());

        var pendingRequestReferrals = await _pendingReferralRequests.CountForAttributionAsync(
            tenantId,
            referralAttributionId,
            PendingReferralRequest.Statuses.PendingReview,
            from,
            to,
            ct);
        var acceptedRequestReferrals = await _pendingReferralRequests.CountForAttributionAsync(
            tenantId,
            referralAttributionId,
            PendingReferralRequest.Statuses.Converted,
            from,
            to,
            ct);
        var declinedRequestReferrals = await _pendingReferralRequests.CountForAttributionAsync(
            tenantId,
            referralAttributionId,
            PendingReferralRequest.Statuses.Cancelled,
            from,
            to,
            ct);

        return new RepresentativeReferralMetricsResponse
        {
            TotalAttributedReferrals = inRangeItems.Count,
            PendingRequestReferrals = pendingRequestReferrals,
            AcceptedRequestReferrals = acceptedRequestReferrals,
            DeclinedRequestReferrals = declinedRequestReferrals,
            PendingReferrals = inRangeItems.Count(r => IsPending(r.Status)),
            AcceptedReferrals = inRangeItems.Count(r => IsAccepted(r.Status)),
            DeclinedReferrals = inRangeItems.Count(r => r.Status == Referral.ValidStatuses.Declined),
            CompletedReferrals = inRangeItems.Count(r => r.Status == Referral.ValidStatuses.Completed),
            CancelledReferrals = inRangeItems.Count(r => r.Status == Referral.ValidStatuses.Cancelled),
            ReferralsByStatus = byStatus,
        };
    }

    // ── Classification ───────────────────────────────────────────────────────
    // "Pending" / "Accepted" mirror the same status groupings the authenticated CareConnect
    // dashboard uses elsewhere (see ReferralService's "Pending (New) status" convention and
    // ReferralFunnel's Accepted/Scheduled/Completed staging) — not a bespoke definition.

    private static bool IsPending(string status) =>
        status is Referral.ValidStatuses.New or Referral.ValidStatuses.NewOpened;

    private static bool IsAccepted(string status) =>
        status is Referral.ValidStatuses.Accepted or Referral.ValidStatuses.InProgress;

    private static string StatusDisplayName(string status) => status switch
    {
        Referral.ValidStatuses.New => "New",
        Referral.ValidStatuses.NewOpened => "New",
        Referral.ValidStatuses.Accepted => "Accepted",
        Referral.ValidStatuses.InProgress => "In Progress",
        Referral.ValidStatuses.Completed => "Completed",
        Referral.ValidStatuses.Declined => "Declined",
        Referral.ValidStatuses.Cancelled => "Cancelled",
        _ => status,
    };

    // ── Mapping ──────────────────────────────────────────────────────────────

    private static string ReferenceNumberFor(Referral r) => "CC-" + r.Id.ToString("N")[..8].ToUpperInvariant();

    private async Task<Dictionary<Guid, string>> ResolveLawFirmNamesAsync(List<Referral> items, CancellationToken ct)
    {
        var orgIds = items.Where(r => r.ReferringOrganizationId.HasValue)
            .Select(r => r.ReferringOrganizationId!.Value)
            .Distinct()
            .ToList();

        var result = new Dictionary<Guid, string>();
        foreach (var orgId in orgIds)
            result[orgId] = await _identityOrganizationService.GetOrganizationNameAsync(orgId, ct) ?? "—";

        return result;
    }

    private static RepresentativeReferralListItem ToListItem(Referral r, Dictionary<Guid, string> lawFirmNames) => new()
    {
        ReferralId = r.Id,
        ReferenceNumber = ReferenceNumberFor(r),
        SubmittedAtUtc = r.CreatedAtUtc,
        Status = new RepresentativeStatusRef { Code = r.Status, DisplayName = StatusDisplayName(r.Status) },
        LawFirm = new RepresentativeDisplayRef
        {
            DisplayName = r.ReferringOrganizationId.HasValue && lawFirmNames.TryGetValue(r.ReferringOrganizationId.Value, out var name)
                ? name
                : (r.ReferrerFirmName ?? "—"),
        },
        Provider = new RepresentativeDisplayRef { DisplayName = r.Provider?.OrganizationName ?? r.Provider?.Name ?? "—" },
        ProviderLocation = ToFacilityRef(r),
        Client = ToClientRef(r),
        ReferralAttribution = ToAttributionRef(r),
        LastUpdatedAtUtc = r.UpdatedAtUtc,
    };

    private static RepresentativeReferralDetailResponse ToDetail(Referral r, Dictionary<Guid, string> lawFirmNames) => new()
    {
        ReferralId = r.Id,
        ReferenceNumber = ReferenceNumberFor(r),
        SubmittedAtUtc = r.CreatedAtUtc,
        Status = new RepresentativeStatusRef { Code = r.Status, DisplayName = StatusDisplayName(r.Status) },
        LawFirm = new RepresentativeDisplayRef
        {
            DisplayName = r.ReferringOrganizationId.HasValue && lawFirmNames.TryGetValue(r.ReferringOrganizationId.Value, out var name)
                ? name
                : (r.ReferrerFirmName ?? "—"),
        },
        Provider = new RepresentativeDisplayRef { DisplayName = r.Provider?.OrganizationName ?? r.Provider?.Name ?? "—" },
        ProviderLocation = ToFacilityRef(r),
        Client = ToClientRef(r),
        ReferralAttribution = ToAttributionRef(r),
        Milestones = BuildMilestones(r),
        LastUpdatedAtUtc = r.UpdatedAtUtc,
    };

    private static RepresentativeReferralAttributionRef ToAttributionRef(Referral r) => new()
    {
        Id = r.ReferralAttributionId ?? Guid.Empty,
        FirstName = r.ReferralAttribution?.FirstName ?? string.Empty,
        LastName = r.ReferralAttribution?.LastName ?? string.Empty,
    };

    private static RepresentativeClientRef ToClientRef(Referral r) => new()
    {
        FirstName = r.ClientFirstName,
        LastName = r.ClientLastName,
        DateOfBirth = r.ClientDob,
        Phone = r.ClientPhone,
        Email = string.IsNullOrWhiteSpace(r.ClientEmail) ? null : r.ClientEmail,
    };

    private static RepresentativeFacilityRef? ToFacilityRef(Referral r)
    {
        if (r.Facility is null) return null;
        return new RepresentativeFacilityRef
        {
            Name = r.Facility.Name,
            AddressLine1 = r.Facility.AddressLine1,
            City = r.Facility.City,
            State = r.Facility.State,
            PostalCode = r.Facility.PostalCode,
            Phone = r.Facility.Phone,
            IsMobile = r.Facility.IsMobile,
            ServiceRadiusMiles = r.Facility.ServiceRadiusMiles,
            ServiceAreaLabel = r.Facility.IsMobile ? r.Facility.AddressLine1 : null,
        };
    }

    /// <summary>
    /// Status milestone dates "when available" — derived from the fields already on the
    /// aggregate rather than the full admin status-history/audit trail (which stays out of
    /// the representative DTO). Extend here if/when more milestone timestamps are approved
    /// for representative visibility.
    /// </summary>
    private static List<RepresentativeMilestone> BuildMilestones(Referral r)
    {
        var milestones = new List<RepresentativeMilestone>
        {
            new() { Code = "SUBMITTED", DisplayName = "Submitted", OccurredAtUtc = r.CreatedAtUtc },
        };

        if (r.Status is not (Referral.ValidStatuses.New or Referral.ValidStatuses.NewOpened))
            milestones.Add(new RepresentativeMilestone { Code = "LAST_UPDATED", DisplayName = StatusDisplayName(r.Status), OccurredAtUtc = r.UpdatedAtUtc });

        return milestones;
    }
}
