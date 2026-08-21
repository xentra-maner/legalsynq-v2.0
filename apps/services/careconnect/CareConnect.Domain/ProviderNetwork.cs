using BuildingBlocks.Domain;

namespace CareConnect.Domain;

// CC2-INT-B06: Tenant-scoped provider networks for the Network Manager role.
public class ProviderNetwork : AuditableEntity
{
    public Guid   Id          { get; private set; }
    public Guid   TenantId    { get; private set; }
    public string Name        { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public bool   IsDeleted   { get; private set; }
    /// <summary>
    /// LSV3-1084: the organization that created this network. Null for networks created
    /// before this field existed (treated as tenant-admin-owned — every existing network
    /// predates CareConnectReferrerAdmin). A CareConnectReferrerAdmin caller without the
    /// NetworkManager role or a system admin role may only rename/delete a network they
    /// created themselves.
    /// </summary>
    public Guid?  OwningOrganizationId { get; private set; }

    public List<NetworkProvider> NetworkProviders { get; private set; } = new();

    private ProviderNetwork() { }

    public static ProviderNetwork Create(Guid tenantId, string name, string description, Guid? owningOrganizationId = null)
    {
        return new ProviderNetwork
        {
            Id          = Guid.CreateVersion7(),
            TenantId    = tenantId,
            Name        = name.Trim(),
            Description = description.Trim(),
            IsDeleted   = false,
            OwningOrganizationId = owningOrganizationId,
        };
    }

    public void Update(string name, string description)
    {
        Name        = name.Trim();
        Description = description.Trim();
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void Delete()
    {
        IsDeleted    = true;
        UpdatedAtUtc = DateTime.UtcNow;
    }
}
