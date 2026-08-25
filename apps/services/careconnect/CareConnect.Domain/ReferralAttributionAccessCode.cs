using BuildingBlocks.Domain;

namespace CareConnect.Domain;

/// <summary>
/// A tenant-admin-generated access code scoped to one Referral Origination. There is no
/// login and no "redeemer" — the code itself is presented anonymously on every
/// representative-portal request and re-validated server-side each time (see
/// ReferralAttributionAccessCodeService.VerifyAsync). This mirrors the platform's other
/// anonymous, code-gated CareConnect surface (the public network directory) rather than
/// the earlier admin-typed user-linking model (ReferralAttributionUserAccess).
/// </summary>
public class ReferralAttributionAccessCode : AuditableEntity
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid ReferralAttributionId { get; private set; }
    public string CodeHash { get; private set; } = string.Empty;
    public bool IsActive { get; private set; }
    public DateTime? AccessStartAtUtc { get; private set; }
    public DateTime? AccessEndAtUtc { get; private set; }

    public ReferralAttribution? ReferralAttribution { get; private set; }

    private ReferralAttributionAccessCode() { }

    public static ReferralAttributionAccessCode Create(
        Guid      tenantId,
        Guid      referralAttributionId,
        string    codeHash,
        DateTime? accessStartAtUtc,
        DateTime? accessEndAtUtc,
        Guid?     createdByUserId)
    {
        if (string.IsNullOrWhiteSpace(codeHash))
            throw new ArgumentException("CodeHash is required.", nameof(codeHash));

        if (accessStartAtUtc.HasValue && accessEndAtUtc.HasValue && accessEndAtUtc.Value <= accessStartAtUtc.Value)
            throw new ArgumentException("AccessEndAtUtc must be after AccessStartAtUtc.", nameof(accessEndAtUtc));

        var now = DateTime.UtcNow;
        return new ReferralAttributionAccessCode
        {
            Id                    = Guid.CreateVersion7(),
            TenantId              = tenantId,
            ReferralAttributionId = referralAttributionId,
            CodeHash              = codeHash,
            IsActive              = true,
            AccessStartAtUtc      = accessStartAtUtc,
            AccessEndAtUtc        = accessEndAtUtc,
            CreatedByUserId       = createdByUserId,
            UpdatedByUserId       = createdByUserId,
            CreatedAtUtc          = now,
            UpdatedAtUtc          = now
        };
    }

    public void SetActive(bool isActive, Guid? updatedByUserId)
    {
        IsActive        = isActive;
        UpdatedByUserId = updatedByUserId;
        UpdatedAtUtc    = DateTime.UtcNow;
    }

    /// <summary>
    /// Whether this code currently grants access: active, the current time falls within
    /// [AccessStartAtUtc, AccessEndAtUtc) when dates are configured, AND the parent
    /// attribution is itself still active. Deactivating an attribution immediately cuts
    /// off anyone currently using its code. Callers must pass the attribution's current
    /// IsActive explicitly rather than this entity reaching through its (possibly-unloaded)
    /// ReferralAttribution navigation property.
    /// </summary>
    public bool IsValidAt(DateTime nowUtc, bool attributionIsActive) =>
        IsActive && attributionIsActive && WithinWindow(nowUtc);

    private bool WithinWindow(DateTime nowUtc)
    {
        if (AccessStartAtUtc.HasValue && nowUtc < AccessStartAtUtc.Value) return false;
        if (AccessEndAtUtc.HasValue && nowUtc >= AccessEndAtUtc.Value) return false;
        return true;
    }
}
