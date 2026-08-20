using BuildingBlocks.Domain;

namespace CareConnect.Domain;

public class Referral : AuditableEntity
{
    public static class ValidStatuses
    {
        // ── Canonical statuses (source of truth) ─────────────────────────
        public const string New        = "New";
        public const string NewOpened  = "NewOpened";
        public const string Accepted   = "Accepted";
        public const string InProgress = "InProgress";
        public const string Completed  = "Completed";
        public const string Declined   = "Declined";
        public const string Cancelled  = "Cancelled";

        public static readonly IReadOnlyList<string> All =
            new[] { New, NewOpened, Accepted, InProgress, Completed, Declined, Cancelled };

        // ── Legacy compat aliases (read-only, accepted on ingest, never produced) ─
        // LSCC-01-001-01: Scheduled demoted from canonical to legacy.
        // Existing rows with Status='Scheduled' are migrated to 'InProgress'
        // via 20260402000000_ReferralInProgressState migration.
        // The entry is retained here so pre-migration data in transit can
        // still be normalized without hard errors.
        public static class Legacy
        {
            public const string Received  = "Received";
            public const string Contacted = "Contacted";
            public const string Scheduled = "Scheduled";

            /// <summary>
            /// Maps a legacy status string to its canonical equivalent.
            /// Returns the input unchanged when it is already canonical.
            /// </summary>
            public static string Normalize(string status) => status switch
            {
                Received  => Accepted,
                Contacted => Accepted,
                Scheduled => InProgress,
                _         => status
            };
        }
    }

    public static class ValidUrgencies
    {
        public const string Low       = "Low";
        public const string Normal    = "Normal";
        public const string Urgent    = "Urgent";
        public const string Emergency = "Emergency";

        public static readonly IReadOnlyList<string> All =
            new[] { Low, Normal, Urgent, Emergency };
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }

    // ── Multi-org workflow participants ──────────────────────────────────
    public Guid? ReferringOrganizationId { get; private set; }
    public Guid? ReceivingOrganizationId { get; private set; }

    // ── Subject party (first-class client record) ────────────────────────
    public Guid? SubjectPartyId { get; private set; }
    public string? SubjectNameSnapshot { get; private set; }
    public DateOnly? SubjectDobSnapshot { get; private set; }

    // ── Provider routing ─────────────────────────────────────────────────
    public Guid ProviderId { get; private set; }
    public Guid? FacilityId { get; private set; }

    // Phase 5: explicit relationship context linking referrer ↔ receiver orgs
    public Guid? OrganizationRelationshipId { get; private set; }

    // ── Legacy inline client fields (kept during migration window) ────────
    public string ClientFirstName { get; private set; } = string.Empty;
    public string ClientLastName { get; private set; } = string.Empty;
    public DateTime? ClientDob { get; private set; }
    public string ClientPhone { get; private set; } = string.Empty;
    public string ClientEmail { get; private set; } = string.Empty;

    // ── Referral detail ──────────────────────────────────────────────────
    public string? CaseNumber { get; private set; }
    public string? RequestedService { get; private set; }
    public string Urgency { get; private set; } = string.Empty;
    public string Status { get; private set; } = ValidStatuses.New;
    public string? Notes { get; private set; }
    public string? DeclineNotes { get; private set; }
    public DateOnly? DateOfAccident { get; private set; }
    public Guid? TreatmentTypeId { get; private set; }
    public string Origin { get; private set; } = ReferralOrigin.LawFirm;
    public string? LienCompanyName { get; private set; }
    public string? LienCompanyEmail { get; private set; }

    // ── Referrer contact (stored at creation for email notifications) ─────
    // "Pending" status in LSCC-005 spec ≡ "New" status in this domain model.
    public string? ReferrerEmail { get; private set; }
    public string? ReferrerName  { get; private set; }
    public string? ReferrerFirstName { get; private set; }
    public string? ReferrerLastName  { get; private set; }
    public string? ReferrerFirmName  { get; private set; }
    public string? ReferrerPhone    { get; private set; }

    // ── LSCC-005-01: Token versioning for revocation ─────────────────────
    // Incrementing this value invalidates all previously issued view tokens.
    // New tokens are generated using the current version; old tokens with a
    // mismatched version are rejected as revoked.
    public int TokenVersion { get; private set; } = 1;

    // ── Referral Attribution (optional; who/what originated the referral) ─
    public Guid? ReferralAttributionId { get; private set; }

    public Provider? Provider { get; private set; }
    public Facility? Facility { get; private set; }
    public Party? SubjectParty { get; private set; }
    public ReferralAttribution? ReferralAttribution { get; private set; }

    private Referral() { }

    /// <summary>
    /// Create a referral with full multi-org context.
    /// If both referringOrganizationId and receivingOrganizationId are known and a matching
    /// OrganizationRelationship exists in Identity, pass organizationRelationshipId to link
    /// this referral to the formal relationship graph.
    /// </summary>
    public static Referral Create(
        Guid tenantId,
        Guid? referringOrganizationId,
        Guid? receivingOrganizationId,
        Guid providerId,
        Guid? subjectPartyId,
        string? subjectNameSnapshot,
        DateOnly? subjectDobSnapshot,
        string clientFirstName,
        string clientLastName,
        DateTime? clientDob,
        string clientPhone,
        string clientEmail,
        string? caseNumber,
        string? requestedService,
        string urgency,
        string? notes,
        Guid? createdByUserId,
        Guid? organizationRelationshipId = null,
        string? referrerEmail = null,
        string? referrerName = null,
        string? referrerFirstName = null,
        string? referrerLastName = null,
        string? referrerFirmName = null,
        string? referrerPhone = null,
        Guid? treatmentTypeId = null,
        DateOnly? dateOfAccident = null,
        Guid? facilityId = null,
        Guid? referralAttributionId = null,
        string origin = ReferralOrigin.LawFirm,
        string? lienCompanyName = null,
        string? lienCompanyEmail = null)
    {
        var now = DateTime.UtcNow;

        // referrerFirstName/referrerLastName (public referral path) take precedence over the
        // legacy single referrerName field (authenticated/JWT path) — when supplied, ReferrerName
        // is computed from them so every existing reader keeps seeing the same single string.
        var hasSplitReferrerName = !string.IsNullOrWhiteSpace(referrerFirstName) || !string.IsNullOrWhiteSpace(referrerLastName);
        var computedReferrerName = hasSplitReferrerName
            ? string.Join(" ", new[] { referrerFirstName, referrerLastName }
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p!.Trim()))
            : referrerName?.Trim();

        return new Referral
        {
            Id                         = Guid.CreateVersion7(),
            TenantId                   = tenantId,
            ReferringOrganizationId    = referringOrganizationId,
            ReceivingOrganizationId    = receivingOrganizationId,
            ProviderId                 = providerId,
            FacilityId                 = facilityId,
            OrganizationRelationshipId = organizationRelationshipId,
            SubjectPartyId             = subjectPartyId,
            SubjectNameSnapshot        = subjectNameSnapshot?.Trim(),
            SubjectDobSnapshot         = subjectDobSnapshot,
            ClientFirstName            = clientFirstName.Trim(),
            ClientLastName             = clientLastName.Trim(),
            ClientDob                  = clientDob,
            ClientPhone                = clientPhone.Trim(),
            ClientEmail                = clientEmail.Trim(),
            CaseNumber                 = caseNumber?.Trim(),
            RequestedService           = requestedService?.Trim(),
            Urgency                    = urgency,
            Status                     = ValidStatuses.New,
            Notes                      = notes?.Trim(),
            DateOfAccident             = dateOfAccident,
            TreatmentTypeId            = treatmentTypeId,
            Origin                     = ReferralOrigin.All.Contains(origin) ? origin : ReferralOrigin.LawFirm,
            LienCompanyName            = lienCompanyName?.Trim(),
            LienCompanyEmail           = lienCompanyEmail?.Trim(),
            ReferrerEmail              = referrerEmail?.Trim(),
            ReferrerName               = computedReferrerName,
            ReferrerFirstName          = referrerFirstName?.Trim(),
            ReferrerLastName           = referrerLastName?.Trim(),
            ReferrerFirmName           = referrerFirmName?.Trim(),
            ReferrerPhone              = referrerPhone?.Trim(),
            TokenVersion               = 1,
            ReferralAttributionId      = referralAttributionId,
            CreatedByUserId            = createdByUserId,
            UpdatedByUserId            = createdByUserId,
            CreatedAtUtc               = now,
            UpdatedAtUtc               = now
        };
    }

    /// <summary>
    /// Transitions this referral from New → NewOpened when a receiver first views it.
    /// No-op if the referral is already past the New state.
    /// </summary>
    public bool MarkAsOpened()
    {
        if (Status != ValidStatuses.New) return false;
        Status       = ValidStatuses.NewOpened;
        UpdatedAtUtc = DateTime.UtcNow;
        return true;
    }

    /// <summary>
    /// Transitions this referral from New/NewOpened → Accepted.
    /// Used by both authenticated provider users and the public token-based accept flow.
    /// </summary>
    public void Accept(Guid? updatedByUserId)
    {
        Status          = ValidStatuses.Accepted;
        UpdatedByUserId = updatedByUserId;
        UpdatedAtUtc    = DateTime.UtcNow;
    }

    /// <summary>
    /// Transitions this referral from New/NewOpened → Declined.
    /// Used by both authenticated provider users and the public token-based decline flow.
    /// </summary>
    public void Decline(Guid? updatedByUserId, string? declineNotes = null)
    {
        Status          = ValidStatuses.Declined;
        DeclineNotes    = declineNotes?.Trim();
        UpdatedByUserId = updatedByUserId;
        UpdatedAtUtc    = DateTime.UtcNow;
    }

    /// <summary>
    /// Transitions this referral from Accepted/InProgress → Completed.
    /// </summary>
    public void Complete(Guid? updatedByUserId)
    {
        Status          = ValidStatuses.Completed;
        UpdatedByUserId = updatedByUserId;
        UpdatedAtUtc    = DateTime.UtcNow;
    }

    /// <summary>
    /// Transitions this referral to Cancelled from any non-terminal state.
    /// </summary>
    public void Cancel(Guid? updatedByUserId)
    {
        Status          = ValidStatuses.Cancelled;
        UpdatedByUserId = updatedByUserId;
        UpdatedAtUtc    = DateTime.UtcNow;
    }

    /// <summary>
    /// LSCC-005-01: Invalidates all previously issued view tokens by incrementing
    /// the token version. Any token carrying an older version will be rejected.
    /// </summary>
    public void IncrementTokenVersion()
    {
        TokenVersion    += 1;
        UpdatedAtUtc     = DateTime.UtcNow;
    }

    /// <summary>
    /// Phase C: link this referral to a formal OrganizationRelationship after creation.
    /// Used when the relationship is resolved asynchronously or after an import/backfill.
    /// </summary>
    public void SetOrganizationRelationshipId(Guid organizationRelationshipId)
    {
        OrganizationRelationshipId = organizationRelationshipId;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void Update(string? requestedService, string urgency, string status, string? notes, Guid? updatedByUserId,
        Guid? treatmentTypeId = null, bool clearTreatmentType = false, DateOnly? dateOfAccident = null, bool clearDateOfAccident = false)
    {
        RequestedService = requestedService?.Trim();
        Urgency          = urgency;
        Status           = status;
        Notes            = notes?.Trim();
        if (dateOfAccident.HasValue)     DateOfAccident = dateOfAccident.Value;
        else if (clearDateOfAccident)    DateOfAccident = null;
        if (treatmentTypeId.HasValue)   TreatmentTypeId = treatmentTypeId.Value;
        else if (clearTreatmentType)    TreatmentTypeId = null;
        UpdatedByUserId  = updatedByUserId;
        UpdatedAtUtc     = DateTime.UtcNow;
    }

    public void LinkParty(Guid partyId, string nameSnapshot, DateOnly? dobSnapshot)
    {
        SubjectPartyId      = partyId;
        SubjectNameSnapshot = nameSnapshot;
        SubjectDobSnapshot  = dobSnapshot;
        UpdatedAtUtc        = DateTime.UtcNow;
    }

    /// <summary>
    /// Reassigns this referral to a different provider, updating the provider ID and
    /// the receiving organization context. Incrementing the token version ensures any
    /// previously issued view tokens (tied to the old provider) are revoked automatically.
    /// </summary>
    public void ReassignProvider(Guid newProviderId, Guid? newReceivingOrganizationId, Guid? updatedByUserId)
    {
        ProviderId              = newProviderId;
        ReceivingOrganizationId = newReceivingOrganizationId;
        TokenVersion           += 1;
        UpdatedByUserId         = updatedByUserId;
        UpdatedAtUtc            = DateTime.UtcNow;
    }
}
