namespace Identity.Domain;

/// <summary>
/// PUM-B02: Scope classification for roles.
/// Platform — admin.legalsynq.net operators.
/// Tenant   — [subdomain] tenant users.
/// Product  — product-specific roles (matched to a ProductRole entry).
/// </summary>
public static class RoleScopes
{
    public const string Platform = "Platform";
    public const string Tenant   = "Tenant";
    public const string Product  = "Product";

    private static readonly HashSet<string> _valid =
        [Platform, Tenant, Product];

    public static bool IsValid(string? value) =>
        value is not null && _valid.Contains(value, StringComparer.Ordinal);
}

public class Role
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public bool IsSystemRole { get; private set; }
    /// <summary>PUM-B02-R03: Platform | Tenant | Product scope classification.</summary>
    public string? Scope { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    public Tenant Tenant { get; private set; } = null!;
    public ICollection<RolePermissionAssignment> RolePermissionAssignments { get; private set; } = [];

    private Role() { }

    public static Role Create(
        Guid tenantId,
        string name,
        string? description = null,
        bool isSystemRole = false,
        string? scope = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var now = DateTime.UtcNow;
        return new Role
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Name = name.Trim(),
            Description = description?.Trim(),
            IsSystemRole = isSystemRole,
            Scope = scope?.Trim(),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
    }

    public void SetScope(string scope)
    {
        Scope = scope?.Trim();
        UpdatedAtUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Renames / re-describes a custom (non-system) role. Trims inputs, requires a
    /// non-blank name, and treats a blank description as null. Idempotent: returns
    /// false (and touches nothing) when neither value changes. System-role
    /// protection is enforced by the caller.
    /// </summary>
    public bool Update(string name, string? description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var n = name.Trim();
        var d = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (string.Equals(Name, n, StringComparison.Ordinal) &&
            string.Equals(Description, d, StringComparison.Ordinal)) return false;
        Name         = n;
        Description   = d;
        UpdatedAtUtc = DateTime.UtcNow;
        return true;
    }
}
