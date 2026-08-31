namespace CareConnect.Application.DTOs;

// ── Restricted, representative-only response shapes ──────────────────────────
// Deliberately hand-built and separate from ReferralResponse — never the admin DTO
// with fields hidden client-side. Only what's explicitly approved for representative
// visibility: reference/status/timeline info, the client's contact identity (name,
// DOB, phone, email — needed for a representative to actually follow up on a
// referral), and the receiving provider location. Medical/legal/financial/document/
// internal-note data and full audit history are never included here.

public class RepresentativeReferralAttributionRef
{
    public Guid Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
}

public class RepresentativeDisplayRef
{
    public string DisplayName { get; set; } = string.Empty;
}

public class RepresentativeClientRef
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public DateTime? DateOfBirth { get; set; }
    public string Phone { get; set; } = string.Empty;
    public string? Email { get; set; }
}

public class RepresentativeFacilityRef
{
    public string Name { get; set; } = string.Empty;
    public string AddressLine1 { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string? PostalCode { get; set; }
    public string? Phone { get; set; }
    public bool IsMobile { get; set; }
    public double? ServiceRadiusMiles { get; set; }
    public string? ServiceAreaLabel { get; set; }
}

public class RepresentativeStatusRef
{
    public string Code { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

public class RepresentativeMilestone
{
    public string Code { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; }
}

public class RepresentativeReferralListItem
{
    public Guid ReferralId { get; set; }
    public string ReferenceNumber { get; set; } = string.Empty;
    public DateTime SubmittedAtUtc { get; set; }
    public RepresentativeStatusRef Status { get; set; } = new();
    public RepresentativeDisplayRef LawFirm { get; set; } = new();
    public RepresentativeDisplayRef Provider { get; set; } = new();
    public RepresentativeFacilityRef? ProviderLocation { get; set; }
    public RepresentativeClientRef Client { get; set; } = new();
    public RepresentativeReferralAttributionRef ReferralAttribution { get; set; } = new();
    public DateTime LastUpdatedAtUtc { get; set; }
}

public class RepresentativeReferralDetailResponse
{
    public Guid ReferralId { get; set; }
    public string ReferenceNumber { get; set; } = string.Empty;
    public DateTime SubmittedAtUtc { get; set; }
    public RepresentativeStatusRef Status { get; set; } = new();
    public RepresentativeDisplayRef LawFirm { get; set; } = new();
    public RepresentativeDisplayRef Provider { get; set; } = new();
    public RepresentativeFacilityRef? ProviderLocation { get; set; }
    public RepresentativeClientRef Client { get; set; } = new();
    public RepresentativeReferralAttributionRef ReferralAttribution { get; set; } = new();
    public List<RepresentativeMilestone> Milestones { get; set; } = [];
    public DateTime LastUpdatedAtUtc { get; set; }
}

public class RepresentativeReferralMetricsResponse
{
    public int TotalAttributedReferrals { get; set; }
    /// <summary>Referral portal submissions awaiting law firm review before provider routing.</summary>
    public int PendingRequestReferrals { get; set; }
    /// <summary>Referral portal submissions accepted by the law firm and converted to routed referrals.</summary>
    public int AcceptedRequestReferrals { get; set; }
    /// <summary>Referral portal submissions declined by the law firm before provider routing.</summary>
    public int DeclinedRequestReferrals { get; set; }
    /// <summary>New or NewOpened — not yet accepted by the provider.</summary>
    public int PendingReferrals { get; set; }
    /// <summary>Accepted or InProgress — accepted by the provider, not yet resolved.</summary>
    public int AcceptedReferrals { get; set; }
    public int DeclinedReferrals { get; set; }
    public int CompletedReferrals { get; set; }
    public int CancelledReferrals { get; set; }

    public Dictionary<string, int> ReferralsByStatus { get; set; } = new();
}

public class GetRepresentativeReferralsQuery
{
    public DateTime? SubmittedFrom { get; set; }
    public DateTime? SubmittedTo { get; set; }
    public string? Status { get; set; }
    public Guid? ProviderId { get; set; }
    public Guid? LawFirmOrganizationId { get; set; }

    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}
