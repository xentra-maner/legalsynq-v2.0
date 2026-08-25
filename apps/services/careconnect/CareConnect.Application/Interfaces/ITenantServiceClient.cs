// BLK-CC-01: Typed client abstraction for Tenant service calls from CareConnect.
// Tenant service = tenant lifecycle (code availability, provisioning).
// This is the ONLY place in CareConnect that talks to the Tenant service for tenant creation.
namespace CareConnect.Application.Interfaces;

/// <summary>
/// Cross-service client for the Tenant service — tenant lifecycle operations only.
/// Used during provider onboarding to replace the retired Identity tenant endpoints.
/// </summary>
public interface ITenantServiceClient
{
    /// <summary>
    /// Checks whether a tenant code is available for a new tenant.
    /// Calls GET /api/v1/tenants/check-code?code={code} on the Tenant service.
    ///
    /// Returns null on any infrastructure failure (network, timeout, 5xx).
    /// Callers must treat null as soft-unknown (provision step will enforce uniqueness).
    /// </summary>
    Task<TenantCodeCheckResult?> CheckCodeAsync(
        string            code,
        CancellationToken ct = default);

    /// <summary>
    /// Provisions a new tenant (name + code) via the Tenant service.
    /// Calls POST /api/v1/tenants/provision on the Tenant service.
    ///
    /// Returns the provisioned tenant details on success.
    /// Returns a typed failure result (IsSuccess=false, FailureCode="CODE_TAKEN") on 409.
    /// Returns null on any unexpected infrastructure failure.
    /// </summary>
    Task<TenantProvisionResult?> ProvisionAsync(
        string            tenantName,
        string            tenantCode,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the subdomain slug for a given tenant ID.
    /// Calls GET /api/v1/tenants/{id}/subdomain on the Tenant service.
    ///
    /// Returns null when the tenant is not found, the Tenant service is unreachable,
    /// or BaseUrl is not configured. Callers must fall back to AppBaseUrl when null.
    /// </summary>
    Task<string?> GetSubdomainAsync(
        Guid              tenantId,
        CancellationToken ct = default);

    /// <summary>
    /// Resolves the tenant display name for a given tenant ID.
    /// Calls GET /api/v1/public/resolve/by-id/{id} on the Tenant service.
    ///
    /// Returns null when the tenant is not found, the Tenant service is unreachable,
    /// or BaseUrl is not configured.
    /// </summary>
    Task<string?> GetDisplayNameAsync(
        Guid              tenantId,
        CancellationToken ct = default);

    /// <summary>
    /// Resolves the effective IANA timezone for a tenant.
    /// Calls GET /api/v1/public/tenants/{id}/settings/timezone on the Tenant service.
    ///
    /// Returns null when the tenant is not found, the Tenant service is unreachable,
    /// or BaseUrl is not configured.
    /// </summary>
    Task<string?> GetTimezoneAsync(
        Guid              tenantId,
        CancellationToken ct = default);

    /// <summary>
    /// Resolves a tenant ID from a tenant code.
    /// Calls GET /api/v1/public/resolve/by-code/{code} on the Tenant service.
    ///
    /// Used for idempotent, environment-portable seed operations (e.g. the initial
    /// Referral Origination seed) that must not hardcode a tenant GUID.
    ///
    /// Returns null when the code does not resolve, the Tenant service is unreachable,
    /// or BaseUrl is not configured.
    /// </summary>
    Task<Guid?> ResolveTenantIdByCodeAsync(
        string            tenantCode,
        CancellationToken ct = default);

    /// <summary>
    /// Checks a tenant-scoped feature capability via the Tenant service's existing
    /// capability store (Tenant.Domain.TenantCapability) — the platform's established
    /// tenant-feature system. Used for gates such as
    /// "careconnect.referral_representative_portal" rather than a bespoke flag mechanism.
    ///
    /// Returns false — never throws — on any infrastructure failure, missing configuration,
    /// or unconfigured capability, so an unreachable Tenant service fails closed (disabled)
    /// rather than silently authorizing representative portal access.
    /// </summary>
    Task<bool> IsCapabilityEnabledAsync(
        Guid              tenantId,
        string            capabilityKey,
        CancellationToken ct = default);
}

// ── Result types ───────────────────────────────────────────────────────────────

public sealed class TenantCodeCheckResult
{
    public bool    Available      { get; init; }
    public string  NormalizedCode { get; init; } = string.Empty;
    public string? Message        { get; init; }
}

public sealed class TenantProvisionResult
{
    /// <summary>True on success. False for known business errors (e.g. CODE_TAKEN).</summary>
    public bool    IsSuccess   { get; init; } = true;

    /// <summary>
    /// Machine-readable failure code when IsSuccess=false.
    /// "CODE_TAKEN" — tenant code already in use (Tenant service 409).
    /// </summary>
    public string? FailureCode { get; init; }

    public Guid   TenantId   { get; init; }
    public string TenantCode { get; init; } = string.Empty;
    public string Subdomain  { get; init; } = string.Empty;
}
