namespace CareConnect.Application.DTOs;

public class ReferralAttributionResponse
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; }
    public int? DisplayOrder { get; set; }

    /// <summary>True when at least one referral currently carries this origination.
    /// Tenant admins use this to know a destructive delete isn't available — deactivate instead.</summary>
    public bool IsUsed { get; set; }

    /// <summary>Count of active (IsActive) representative access codes generated for this origination.</summary>
    public int ActiveAccessCodeCount { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public class CreateReferralAttributionRequest
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public int? DisplayOrder { get; set; }
}

public class UpdateReferralAttributionRequest
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int? DisplayOrder { get; set; }
}

public class SetReferralAttributionActiveRequest
{
    public bool IsActive { get; set; }
}

/// <summary>Admin-facing origination field on a referral: id and name only.</summary>
public class ReferralAttributionSummary
{
    public Guid Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public bool IsActive { get; set; }
}
