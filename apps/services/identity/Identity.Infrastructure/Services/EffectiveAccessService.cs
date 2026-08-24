using System.Diagnostics;
using BuildingBlocks.Authorization;
using ProductCodes = BuildingBlocks.Authorization.ProductCodes;
using Identity.Application.Interfaces;
using Identity.Domain;
using Identity.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Identity.Infrastructure.Services;

public class EffectiveAccessService : IEffectiveAccessService
{
    private readonly IdentityDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly ILogger<EffectiveAccessService> _logger;

    private static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromMinutes(5);
    private static long _cacheHits;
    private static long _cacheMisses;

    public EffectiveAccessService(
        IdentityDbContext db,
        IMemoryCache cache,
        ILogger<EffectiveAccessService> logger)
    {
        _db = db;
        _cache = cache;
        _logger = logger;
    }

    public static string BuildCacheKey(Guid tenantId, Guid userId, int accessVersion) =>
        $"ea:{tenantId}:{userId}:{accessVersion}";

    public static (long Hits, long Misses) GetCacheStats() => (_cacheHits, _cacheMisses);

    public async Task<EffectiveAccessResult> GetEffectiveAccessAsync(
        Guid tenantId, Guid userId, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        var accessVersion = await _db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.AccessVersion)
            .FirstOrDefaultAsync(ct);

        var cacheKey = BuildCacheKey(tenantId, userId, accessVersion);

        if (_cache.TryGetValue(cacheKey, out EffectiveAccessResult? cached) && cached != null)
        {
            Interlocked.Increment(ref _cacheHits);
            sw.Stop();
            _logger.LogDebug(
                "EffectiveAccess cache HIT for user {UserId} tenant {TenantId} v{Version} in {ElapsedMs}ms.",
                userId, tenantId, accessVersion, sw.ElapsedMilliseconds);
            return cached;
        }

        Interlocked.Increment(ref _cacheMisses);

        var result = await ComputeEffectiveAccessAsync(tenantId, userId, ct);

        _cache.Set(cacheKey, result, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = DefaultCacheTtl,
            Size = 1
        });

        sw.Stop();
        _logger.LogInformation(
            "EffectiveAccess cache MISS for user {UserId} tenant {TenantId} v{Version}: computed in {ElapsedMs}ms. Products={ProductCount}, Roles={RoleCount}.",
            userId, tenantId, accessVersion, sw.ElapsedMilliseconds,
            result.Products.Count, result.ProductRolesFlat.Count);

        return result;
    }

    private async Task<EffectiveAccessResult> ComputeEffectiveAccessAsync(
        Guid tenantId, Guid userId, CancellationToken ct)
    {
        var activeEntitlements = await _db.TenantProducts
            .Where(tp => tp.TenantId == tenantId && tp.IsEnabled)
            .Select(tp => tp.Product.Code)
            .ToListAsync(ct);

        if (activeEntitlements.Count == 0)
        {
            _logger.LogDebug("No active entitlements for tenant {TenantId}.", tenantId);
            return new EffectiveAccessResult([], new(), [], [], [], [], [], []);
        }

        var entitlementSet = new HashSet<string>(activeEntitlements, StringComparer.OrdinalIgnoreCase);

        var isTenantAdmin = await _db.ScopedRoleAssignments
            .AnyAsync(s => s.UserId == userId
                && s.IsActive
                && s.ScopeType == ScopedRoleAssignment.ScopeTypes.Global
                && s.Role.Name == "TenantAdmin", ct);

        var directProducts = await _db.UserProductAccessRecords
            .Where(a => a.TenantId == tenantId && a.UserId == userId && a.AccessStatus == AccessStatus.Granted)
            .Select(a => a.ProductCode)
            .ToListAsync(ct);

        var activeGroupIds = await _db.AccessGroupMemberships
            .Where(m => m.TenantId == tenantId && m.UserId == userId && m.MembershipStatus == MembershipStatus.Active)
            .Select(m => m.GroupId)
            .ToListAsync(ct);

        var activeGroups = activeGroupIds.Count > 0
            ? await _db.AccessGroups
                .Where(g => activeGroupIds.Contains(g.Id) && g.TenantId == tenantId && g.Status == GroupStatus.Active)
                .ToDictionaryAsync(g => g.Id, g => g.Name, ct)
            : new Dictionary<Guid, string>();

        var validGroupIds = activeGroups.Keys.ToList();

        var inheritedProducts = validGroupIds.Count > 0
            ? await _db.GroupProductAccessRecords
                .Where(a => a.TenantId == tenantId && validGroupIds.Contains(a.GroupId) && a.AccessStatus == AccessStatus.Granted)
                .Select(a => new { a.ProductCode, a.GroupId })
                .ToListAsync(ct)
            : [];

        var productSources = new List<EffectiveProductEntry>();
        var effectiveProductSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (isTenantAdmin)
        {
            foreach (var code in activeEntitlements)
            {
                if (effectiveProductSet.Add(code))
                    productSources.Add(new EffectiveProductEntry(code, "TenantAdmin"));
            }
        }

        foreach (var code in directProducts)
        {
            if (entitlementSet.Contains(code) && effectiveProductSet.Add(code))
                productSources.Add(new EffectiveProductEntry(code, "Direct"));
        }

        foreach (var ip in inheritedProducts)
        {
            if (!entitlementSet.Contains(ip.ProductCode)) continue;
            if (effectiveProductSet.Add(ip.ProductCode))
            {
                activeGroups.TryGetValue(ip.GroupId, out var gn);
                productSources.Add(new EffectiveProductEntry(ip.ProductCode, "Inherited", ip.GroupId, gn));
            }
        }

        var effectiveProducts = effectiveProductSet
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var directRoles = await _db.UserRoleAssignments
            .Where(a => a.TenantId == tenantId && a.UserId == userId && a.AssignmentStatus == AssignmentStatus.Active)
            .ToListAsync(ct);

        var inheritedRoles = validGroupIds.Count > 0
            ? await _db.GroupRoleAssignments
                .Where(a => a.TenantId == tenantId && validGroupIds.Contains(a.GroupId) && a.AssignmentStatus == AssignmentStatus.Active)
                .ToListAsync(ct)
            : [];

        var productRoles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var tenantRoles = new List<string>();
        var roleSources = new List<EffectiveRoleEntry>();
        var seenRoleKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddRole(string roleCode, string? productCode, string source, Guid? groupId, string? groupName)
        {
            var key = $"{productCode ?? "__TENANT__"}:{roleCode}";
            if (!seenRoleKeys.Add(key)) return;

            roleSources.Add(new EffectiveRoleEntry(roleCode, productCode, source, groupId, groupName));

            if (productCode == null)
            {
                tenantRoles.Add(roleCode);
                return;
            }

            if (!effectiveProductSet.Contains(productCode)) return;

            if (!productRoles.TryGetValue(productCode, out var roleList))
            {
                roleList = new List<string>();
                productRoles[productCode] = roleList;
            }
            roleList.Add(roleCode);
        }

        var tenantAdminRoleCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (isTenantAdmin)
        {
            var entitledProductRoles = await _db.ProductRoles
                .Where(pr => pr.IsActive && pr.Product.IsActive
                    && activeEntitlements.Contains(pr.Product.Code))
                .Select(pr => new { pr.Code, ProductCode = pr.Product.Code })
                .ToListAsync(ct);

            foreach (var pr in entitledProductRoles)
            {
                AddRole(pr.Code, pr.ProductCode, "TenantAdmin", null, null);
                tenantAdminRoleCodes.Add(pr.Code);
            }

            _logger.LogDebug(
                "TenantAdmin auto-grant for user {UserId} in tenant {TenantId}: {ProductCount} products, {RoleCount} product roles.",
                userId, tenantId, activeEntitlements.Count, entitledProductRoles.Count);
        }

        foreach (var r in directRoles)
            AddRole(r.RoleCode, r.ProductCode, "Direct", null, null);

        foreach (var r in inheritedRoles)
        {
            activeGroups.TryGetValue(r.GroupId, out var gn);
            AddRole(r.RoleCode, r.ProductCode, "Inherited", r.GroupId, gn);
        }

        // LSV3-1083: a CareConnectReferrerAdmin (law-firm Super Admin/Manager) can do
        // everything a plain CareConnectReferrer can — submit referrals, browse networks,
        // etc. — in addition to their own firm-management capabilities. Rather than
        // dual-assigning both roles at every grant path (invite, tenant-admin role
        // assignment UI, seed data), imply it once here so every existing Referrer-gated
        // check (backend RequireProductRole, frontend nav requiredRoles) already sees it.
        if (productRoles.TryGetValue(ProductCodes.SynqCareConnect, out var careConnectRoles)
            && careConnectRoles.Contains(ProductRoleCodes.CareConnectReferrerAdmin, StringComparer.OrdinalIgnoreCase)
            && !careConnectRoles.Contains(ProductRoleCodes.CareConnectReferrer, StringComparer.OrdinalIgnoreCase))
        {
            careConnectRoles.Add(ProductRoleCodes.CareConnectReferrer);
            roleSources.Add(new EffectiveRoleEntry(ProductRoleCodes.CareConnectReferrer, ProductCodes.SynqCareConnect, "Implied", null, null));
        }

        var productRolesFlat = new List<string>();
        foreach (var (product, roles) in productRoles.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var role in roles.OrderBy(r => r, StringComparer.OrdinalIgnoreCase))
                productRolesFlat.Add($"{product}:{role}");
        }

        var (permissions, permissionSources) = await ResolvePermissionsAsync(
            tenantId, userId, effectiveProductSet, directRoles, inheritedRoles, activeGroups, tenantAdminRoleCodes, ct);

        _logger.LogDebug(
            "Effective access for user {UserId} in tenant {TenantId}: {ProductCount} products ({DirectCount} direct, {InheritedCount} inherited), {RoleCount} product roles, {TenantRoleCount} tenant roles, {PermissionCount} permissions.",
            userId, tenantId, effectiveProducts.Count,
            productSources.Count(s => s.Source == "Direct"),
            productSources.Count(s => s.Source == "Inherited"),
            productRolesFlat.Count, tenantRoles.Count, permissions.Count);

        return new EffectiveAccessResult(effectiveProducts, productRoles, productRolesFlat, tenantRoles, productSources, roleSources, permissions, permissionSources);
    }

    private async Task<(List<string> Permissions, List<EffectivePermissionEntry> Sources)> ResolvePermissionsAsync(
        Guid tenantId,
        Guid userId,
        HashSet<string> effectiveProductSet,
        List<UserRoleAssignment> directRoles,
        List<GroupRoleAssignment> inheritedRoles,
        Dictionary<Guid, string> activeGroups,
        HashSet<string> tenantAdminRoleCodes,
        CancellationToken ct)
    {
        var allRoleCodes = directRoles
            .Where(r => r.ProductCode != null)
            .Select(r => r.RoleCode)
            .Concat(inheritedRoles.Where(r => r.ProductCode != null).Select(r => r.RoleCode))
            .Concat(tenantAdminRoleCodes)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // LS-ID-TNT-011: even if there are no product role codes, we still need to
        // resolve tenant-level permissions via system role → RolePermissionAssignment.
        // Do not short-circuit here; always reach the system-role resolution below.

        var rolePermissions = await _db.ProductRoles
            .Where(pr => allRoleCodes.Contains(pr.Code) && pr.IsActive)
            .Join(_db.RolePermissionMappings,
                pr => pr.Id,
                rc => rc.ProductRoleId,
                (pr, rc) => new { pr.Code, pr.ProductId, pr.Product, rc.PermissionId })
            .Join(_db.Permissions.Where(c => c.IsActive),
                x => x.PermissionId,
                c => c.Id,
                (x, c) => new
                {
                    RoleCode = x.Code,
                    ProductCode = x.Product.Code,
                    RoleProductId = x.ProductId,
                    PermissionProductId = c.ProductId,
                    PermissionCode = c.Code,
                })
            .Where(x => x.RoleProductId == x.PermissionProductId)
            .ToListAsync(ct);

        var permissionSources = new List<EffectivePermissionEntry>();
        var seenPermissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var perm in rolePermissions)
        {
            if (!effectiveProductSet.Contains(perm.ProductCode)) continue;

            var permCode = perm.PermissionCode;

            foreach (var dr in directRoles.Where(r =>
                string.Equals(r.RoleCode, perm.RoleCode, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.ProductCode, perm.ProductCode, StringComparison.OrdinalIgnoreCase)))
            {
                if (seenPermissions.Add(permCode + ":Direct"))
                    permissionSources.Add(new EffectivePermissionEntry(permCode, perm.ProductCode, "Direct", perm.RoleCode));
            }

            foreach (var ir in inheritedRoles.Where(r =>
                string.Equals(r.RoleCode, perm.RoleCode, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.ProductCode, perm.ProductCode, StringComparison.OrdinalIgnoreCase)))
            {
                activeGroups.TryGetValue(ir.GroupId, out var gn);
                var sourceKey = $"{permCode}:Inherited:{ir.GroupId}";
                if (seenPermissions.Add(sourceKey))
                    permissionSources.Add(new EffectivePermissionEntry(permCode, perm.ProductCode, "Inherited", perm.RoleCode, ir.GroupId, gn));
            }

            if (tenantAdminRoleCodes.Contains(perm.RoleCode))
            {
                if (seenPermissions.Add(permCode + ":TenantAdmin"))
                    permissionSources.Add(new EffectivePermissionEntry(permCode, perm.ProductCode, "TenantAdmin", perm.RoleCode));
            }
        }

        // ── LS-ID-TNT-011: Tenant-level permissions via system role → RolePermissionAssignment ──
        // Look up active global-scoped system role names for this user, then load the
        // corresponding permissions from idt_RoleCapabilityAssignments.  This is separate
        // from the product-role → RolePermissionMapping path above; tenant permissions live
        // under the SYNQ_PLATFORM pseudo-product and are not gated on product entitlement.
        var systemRoleNames = await _db.ScopedRoleAssignments
            .Where(s => s.UserId == userId
                && s.IsActive
                && s.ScopeType == ScopedRoleAssignment.ScopeTypes.Global)
            .Select(s => s.Role.Name)
            .Distinct()
            .ToListAsync(ct);

        if (systemRoleNames.Count > 0)
        {
            var systemRolePerms = await _db.RolePermissionAssignments
                .Where(a => systemRoleNames.Contains(a.Role.Name) && a.Permission.IsActive)
                .Select(a => new { RoleName = a.Role.Name, PermCode = a.Permission.Code, ProductCode = a.Permission.Product.Code })
                .ToListAsync(ct);

            foreach (var tp in systemRolePerms)
            {
                var sourceKey = $"{tp.PermCode}:SystemRole:{tp.RoleName}";
                if (seenPermissions.Add(sourceKey))
                    permissionSources.Add(new EffectivePermissionEntry(tp.PermCode, tp.ProductCode, "SystemRole", tp.RoleName));
            }
        }

        var permissions = permissionSources
            .Select(p => p.PermissionCode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return (permissions, permissionSources);
    }
}
