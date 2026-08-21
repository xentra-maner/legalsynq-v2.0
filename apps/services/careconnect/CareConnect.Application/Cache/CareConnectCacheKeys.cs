namespace CareConnect.Application.Cache;

/// <summary>
/// BLK-PERF-02: Centralised cache key definitions for CareConnect.
///
/// Design rules:
///   1. ALL keys that scope to a tenant MUST include {tenantId} — cross-tenant
///      cache collisions are a security regression.
///   2. Platform-wide keys (no tenant scope) are only allowed for genuinely
///      shared, non-sensitive reference data (e.g. categories).
///   3. Key format: {service}:{scope}:{data-type}:{dimensions}
///      - service  = "cc"
///      - scope    = "pub" (public/anonymous) | "admin" | "tenant"
///      - data-type and dimensions identify the specific entry
///
/// Every constant or method here has a corresponding TTL recommendation
/// documented in CareConnectCacheTtl.
/// </summary>
public static class CareConnectCacheKeys
{
    // ── Public network surface (BLK-PERF-02 §3) ──────────────────────────────
    // Used by the anonymous public network directory.
    // All keys are tenant-scoped — a different tenant NEVER shares an entry.

    /// <summary>Network list (summary with provider count) for one tenant.</summary>
    public static string PublicNetworkList(Guid tenantId)
        => $"cc:pub:network:list:{tenantId}";

    /// <summary>
    /// Network list scoped to a specific referring organization (e.g. a law firm) —
    /// includes tenant-owned networks plus that organization's own network(s).
    /// Separate key from the unscoped list so one law firm's filtered view is never
    /// served to another. Not tracked in PublicNetworkInvalidationKeys (per-org keys
    /// can't be enumerated); relies on the short TTL to pick up changes.
    /// </summary>
    public static string PublicNetworkList(Guid tenantId, Guid organizationId)
        => $"cc:pub:network:list:{tenantId}:{organizationId}";

    /// <summary>Full detail (providers + markers) for one network.</summary>
    public static string PublicNetworkDetail(Guid tenantId, Guid networkId)
        => $"cc:pub:network:detail:{tenantId}:{networkId}";

    /// <summary>Provider list for one network.</summary>
    public static string PublicNetworkProviders(Guid tenantId, Guid networkId)
        => $"cc:pub:network:providers:{tenantId}:{networkId}";

    /// <summary>Provider map markers for one network.</summary>
    public static string PublicNetworkMarkers(Guid tenantId, Guid networkId)
        => $"cc:pub:network:markers:{tenantId}:{networkId}";

    // ── Platform-wide reference data ──────────────────────────────────────────
    // Safe to share across tenants because categories contain NO tenant-specific
    // data (they are global classification codes defined by platform admins).

    /// <summary>All active categories (platform-wide, no tenant scope).</summary>
    public const string Categories = "cc:categories";

    /// <summary>All active specialties (platform-wide, no tenant scope).</summary>
    public const string Specialties = "cc:specialties";

    // ── Admin dashboard (BLK-PERF-02 §8) ─────────────────────────────────────
    // Short TTL so admins see near-real-time data.
    // tenantScopeKey = tenantId.ToString() for TenantAdmin, "platform" for PlatformAdmin.

    /// <summary>Admin dashboard aggregate counts scoped by tenant or platform.</summary>
    public static string AdminDashboard(string tenantScopeKey)
        => $"cc:admin:dashboard:{tenantScopeKey}";

    // ── Invalidation helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Returns all public-network cache keys for the given tenant that should be
    /// removed when network configuration changes (network update/delete,
    /// provider add/remove).
    ///
    /// Note: IMemoryCache does not support wildcard removal. The list is exhaustive
    /// for the specific network ID known at the call site. The network-list key is
    /// always included because provider counts change whenever membership changes.
    /// </summary>
    public static IEnumerable<string> PublicNetworkInvalidationKeys(Guid tenantId, Guid networkId)
    {
        yield return PublicNetworkList(tenantId);
        yield return PublicNetworkDetail(tenantId, networkId);
        yield return PublicNetworkProviders(tenantId, networkId);
        yield return PublicNetworkMarkers(tenantId, networkId);
    }

    /// <summary>
    /// All public-network keys that are scoped to tenantId (but not to a specific
    /// network), for use when an entire network is deleted (all per-network keys
    /// cannot be enumerated without knowledge of existing network IDs).
    /// </summary>
    public static IEnumerable<string> PublicNetworkListKeys(Guid tenantId)
    {
        yield return PublicNetworkList(tenantId);
    }
}

/// <summary>
/// BLK-PERF-02: Canonical TTL values for each cache entry category.
/// Kept as constants so each usage site can reference the same value
/// without magic numbers.
/// </summary>
public static class CareConnectCacheTtl
{
    /// <summary>
    /// Public network list and detail: 60 seconds.
    /// Network configuration changes infrequently; writes invalidate immediately.
    /// </summary>
    public static readonly TimeSpan PublicNetwork = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Category reference data: 300 seconds (5 minutes).
    /// Categories change only via platform admin; no per-tenant variation.
    /// </summary>
    public static readonly TimeSpan Categories = TimeSpan.FromSeconds(300);

    /// <summary>
    /// Specialty reference data: 300 seconds (5 minutes).
    /// Specialties change only via platform admin; no per-tenant variation.
    /// </summary>
    public static readonly TimeSpan Specialties = TimeSpan.FromSeconds(300);

    /// <summary>
    /// Admin dashboard counts: 15 seconds.
    /// Short TTL keeps counts near-real-time while reducing DB load on repeat loads.
    /// </summary>
    public static readonly TimeSpan AdminDashboard = TimeSpan.FromSeconds(15);
}
