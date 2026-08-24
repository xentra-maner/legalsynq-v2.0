namespace Identity.Domain;

public class Organization
{
    public Guid Id { get; private set; }
    public Guid? TenantId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? DisplayName { get; private set; }
    public string OrgType { get; private set; } = string.Empty;

    // Platform Phase 1: typed org-type FK (nullable during migration window; backfilled from OrgType)
    public Guid? OrganizationTypeId { get; private set; }

    public string ProviderMode { get; private set; } = ProviderModes.Sell;

    public bool IsActive { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }
    public Guid? CreatedByUserId { get; private set; }
    public Guid? UpdatedByUserId { get; private set; }

    /// <summary>LSV3-1083: the designated owner of this organization (mirrors <see cref="Tenant.OwnerUserId"/>).</summary>
    public Guid? OwnerUserId { get; private set; }

    public Tenant? Tenant { get; private set; }
    public OrganizationType? OrganizationTypeRef { get; private set; }
    public ICollection<OrganizationDomain> Domains { get; private set; } = [];
    public ICollection<OrganizationProduct> OrganizationProducts { get; private set; } = [];
    public ICollection<UserOrganizationMembership> Memberships { get; private set; } = [];
    public ICollection<OrganizationRelationship> OutgoingRelationships { get; private set; } = [];
    public ICollection<OrganizationRelationship> IncomingRelationships { get; private set; } = [];

    private Organization() { }

    /// <summary>
    /// Phase I canonical create: OrganizationTypeId is the primary input.
    /// OrgType is derived from OrgTypeMapper and kept for backward compatibility.
    /// Use this overload when the catalog ID is already resolved.
    /// </summary>
    public static Organization Create(
        Guid    tenantId,
        string  name,
        Guid    organizationTypeId,
        string? displayName     = null,
        Guid?   createdByUserId = null)
    {
        var orgTypeCode = OrgTypeMapper.TryResolveCode(organizationTypeId)
            ?? throw new ArgumentException(
                $"OrganizationTypeId '{organizationTypeId}' is not in the OrgTypeMapper catalog.",
                nameof(organizationTypeId));

        return Create(tenantId, name, orgTypeCode, organizationTypeId, displayName, createdByUserId);
    }

    /// <summary>
    /// Legacy create: accepts OrgType string only (backward compatible).
    /// OrgType must be valid per the static OrgType class.
    /// Callers should prefer the overload that also supplies organizationTypeId.
    /// </summary>
    public static Organization Create(
        Guid tenantId,
        string name,
        string orgType,
        string? displayName = null,
        Guid? createdByUserId = null)
        => Create(tenantId, name, orgType, organizationTypeId: null, displayName, createdByUserId);

    public static Organization CreateGlobal(
        string name,
        string orgType,
        string? displayName = null,
        Guid? createdByUserId = null)
        => Create(tenantId: null, name, orgType, organizationTypeId: null, displayName, createdByUserId);

    /// <summary>
    /// Canonical create: accepts both the string OrgType (for backward compat / JWT claims)
    /// and the new OrganizationTypeId FK (Phase 1).
    /// When organizationTypeId is supplied the string OrgType should match the catalog record.
    /// When only orgType is supplied, OrganizationTypeId is left null until a backfill resolves it.
    /// </summary>
    public static Organization Create(
        Guid?  tenantId,
        string name,
        string orgType,
        Guid?  organizationTypeId,
        string? displayName      = null,
        Guid?  createdByUserId   = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(orgType);

        if (!Identity.Domain.OrgType.IsValid(orgType))
            throw new ArgumentException($"Invalid OrgType: {orgType}", nameof(orgType));

        // Phase H: auto-resolve OrganizationTypeId from the OrgType code when not explicitly
        // supplied. Ensures the FK is always populated for any recognized OrgType string.
        // TODO [Phase H — remove OrgType string]: once string column is dropped, callers pass only ID.
        var resolvedTypeId = organizationTypeId ?? OrgTypeMapper.TryResolve(orgType);

        var now = DateTime.UtcNow;
        return new Organization
        {
            Id                 = Guid.CreateVersion7(),
            TenantId           = tenantId,
            Name               = name.Trim(),
            DisplayName        = displayName?.Trim(),
            OrgType            = orgType,                   // TODO [Phase H — remove OrgType string]
            OrganizationTypeId = resolvedTypeId,
            IsActive           = true,
            CreatedAtUtc       = now,
            UpdatedAtUtc       = now,
            CreatedByUserId    = createdByUserId,
            UpdatedByUserId    = createdByUserId
        };
    }

    /// <summary>
    /// Phase A: assign the canonical OrganizationTypeId after creation or during backfill.
    /// The orgTypeCode should match OrgType string already stored on this entity for consistency.
    /// </summary>
    public void AssignOrganizationType(Guid organizationTypeId, string orgTypeCode)
    {
        // Phase I: enforce catalog consistency — if OrgTypeMapper knows this ID,
        // prefer the catalog-derived code over any caller-supplied string so that
        // OrgType column never drifts away from OrganizationTypeId.
        var catalogCode = OrgTypeMapper.TryResolveCode(organizationTypeId);
        if (catalogCode is not null &&
            !string.Equals(catalogCode, orgTypeCode, StringComparison.OrdinalIgnoreCase))
        {
            orgTypeCode = catalogCode;
        }

        OrganizationTypeId = organizationTypeId;
        // Keep OrgType string in sync so JWT claims remain backward-compatible.
        if (!string.IsNullOrWhiteSpace(orgTypeCode))
            OrgType = orgTypeCode;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Updates mutable display fields.  Supply organizationTypeId to set (or correct)
    /// the typed FK at the same time; omit to leave it unchanged.
    /// When organizationTypeId is provided the orgType string is kept in sync so
    /// JWT claims remain backward-compatible.
    /// </summary>
    public void Update(string name, string? displayName, Guid? updatedByUserId,
        Guid?   organizationTypeId = null,
        string? orgTypeCode        = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
        DisplayName = displayName?.Trim();
        UpdatedByUserId = updatedByUserId;
        UpdatedAtUtc = DateTime.UtcNow;

        if (organizationTypeId.HasValue)
            AssignOrganizationType(organizationTypeId.Value, orgTypeCode ?? OrgType);
    }

    public void SetProviderMode(string mode, Guid? updatedByUserId = null)
    {
        ProviderMode = ProviderModes.Normalize(mode);
        UpdatedByUserId = updatedByUserId;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void Deactivate(Guid? updatedByUserId)
    {
        IsActive = false;
        UpdatedByUserId = updatedByUserId;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void SetOwner(Guid ownerUserId)
    {
        if (ownerUserId == Guid.Empty)
            throw new ArgumentException("ownerUserId must not be empty.", nameof(ownerUserId));
        OwnerUserId  = ownerUserId;
        UpdatedAtUtc = DateTime.UtcNow;
    }
}
