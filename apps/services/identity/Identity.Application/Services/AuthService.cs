using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using BuildingBlocks.Authorization;
using BuildingBlocks.DataGovernance;
using Identity.Application.DTOs;
using Identity.Application.Exceptions;
using Identity.Application.Interfaces;
using Identity.Domain;
using LegalSynq.AuditClient;
using LegalSynq.AuditClient.DTOs;
using LegalSynq.AuditClient.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Identity.Application.Services;

public class AuthService : IAuthService
{
    private const string CareConnectPortalRestrictionMessage =
        "This account is not eligible to access the CareConnect portal.";
    private const string SynqLienPortalRestrictionMessage =
        "This account is not eligible to access the SynqLien funding portal.";

    private readonly IUserRepository _userRepository;
    private readonly ITenantRepository _tenantRepository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly IAuditEventClient _auditClient;
    private readonly IEffectiveAccessService _effectiveAccessService;
    private readonly IDeviceSessionService _deviceSessionService;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        IUserRepository userRepository,
        ITenantRepository tenantRepository,
        IPasswordHasher passwordHasher,
        IJwtTokenService jwtTokenService,
        IAuditEventClient auditClient,
        IEffectiveAccessService effectiveAccessService,
        IDeviceSessionService deviceSessionService,
        IHttpContextAccessor httpContextAccessor,
        ILogger<AuthService> logger)
    {
        _userRepository = userRepository;
        _tenantRepository = tenantRepository;
        _passwordHasher = passwordHasher;
        _jwtTokenService = jwtTokenService;
        _auditClient = auditClient;
        _effectiveAccessService = effectiveAccessService;
        _deviceSessionService = deviceSessionService;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    private string? CurrentCorrelationId =>
        _httpContextAccessor.HttpContext?.Items["CorrelationId"]?.ToString();

    public async Task<LoginResponse> LoginAsync(LoginRequest request, string? ipAddress = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        // Canonical audit helpers — used when a login failure must be emitted before re-throwing.
        // fire-and-observe: never awaited, never allowed to gate the primary auth response.
        var tenantCodeNorm  = (request.TenantCode ?? string.Empty).ToLowerInvariant().Trim();

        if (string.IsNullOrEmpty(request.Email) || request.Email != request.Email.Trim())
            throw new UnauthorizedAccessException();

        var emailNorm       = request.Email.ToLowerInvariant();

        // AUTH-CC01: Common-portal email-based tenant resolution.
        // When a common portal cannot resolve a tenant from the subdomain, the BFF sets
        // ResolveByEmail=true and may provide PortalProductCode. We look up the user globally
        // by email, find an eligible tenant for that portal product, then proceed with the
        // normal password-verification path. Null PortalProductCode keeps historical
        // CareConnect behavior for older callers.
        if (request.ResolveByEmail)
        {
            var portalProductCode = NormalizePortalProductCode(request.PortalProductCode);
            if (portalProductCode is null)
            {
                EmitLoginFailed(emailNorm, tenantCode: "common-portal", userId: null, reason: "UnsupportedPortalProduct", ipAddress: ipAddress);
                throw new UnauthorizedAccessException();
            }

            var portalProductName = PortalProductAuditName(portalProductCode);
            _logger.LogInformation(
                "AUTH-CC01: email-based tenant resolution for product={ProductCode} email={EmailMasked}",
                portalProductCode, PiiGuard.MaskEmail(emailNorm));

            var globalUser = await _userRepository.GetByEmailAsync(emailNorm, ct);
            if (globalUser is null || !globalUser.IsActive)
            {
                _logger.LogWarning(
                    "AUTH-CC01: LoginAsync failed: UserNotFoundOrInactive emailMasked={EmailMasked} ip={Ip}",
                    PiiGuard.MaskEmail(emailNorm), ipAddress);
                EmitLoginFailed(emailNorm, tenantCode: "common-portal", userId: null, reason: "UserNotFound", ipAddress: ipAddress);
                throw new UnauthorizedAccessException();
            }

            var userTenantMemberships = await _userRepository.GetActiveTenantMembershipsAsync(globalUser.Id, ct);
            var activeMemberships = userTenantMemberships
                .Where(ut => ut.Tenant is not null && ut.Tenant.IsActive)
                .ToList();

            if (activeMemberships.Count == 0)
            {
                _logger.LogWarning(
                    "AUTH-CC01: LoginAsync failed: TenantNotFoundOrInactive userId={UserId} emailMasked={EmailMasked} ip={Ip}",
                    globalUser.Id, PiiGuard.MaskEmail(emailNorm), ipAddress);
                EmitLoginFailed(emailNorm, tenantCode: "common-portal", userId: null, reason: "TenantNotFound", ipAddress: ipAddress);
                throw new UnauthorizedAccessException();
            }

            Tenant? globalTenant = null;

            if (request.TenantId.HasValue && request.TenantId.Value != Guid.Empty)
            {
                globalTenant = activeMemberships
                    .Where(ut => ut.TenantId == request.TenantId.Value)
                    .Select(ut => ut.Tenant)
                    .FirstOrDefault();
            }

            if (globalTenant is null && !string.IsNullOrWhiteSpace(request.Subdomain))
            {
                var requestedSubdomain = request.Subdomain.Trim().ToLowerInvariant();
                globalTenant = activeMemberships
                    .Where(ut => string.Equals(ut.Tenant?.Subdomain, requestedSubdomain, StringComparison.OrdinalIgnoreCase))
                    .Select(ut => ut.Tenant)
                    .FirstOrDefault();
            }

            if (globalTenant is null)
            {
                var portalProductPrefix = portalProductCode + ":";
                foreach (var membership in activeMemberships)
                {
                    var membershipTenant = membership.Tenant!;
                    var access = await _effectiveAccessService.GetEffectiveAccessAsync(
                        membershipTenant.Id, globalUser.Id, ct);

                    var hasPortalProduct = access.Products.Contains(
                        portalProductCode,
                        StringComparer.OrdinalIgnoreCase);
                    var hasPortalRole = access.ProductRolesFlat.Any(r =>
                        r.StartsWith(portalProductPrefix, StringComparison.OrdinalIgnoreCase));

                    if (hasPortalProduct && hasPortalRole)
                    {
                        globalTenant = membershipTenant;
                        break;
                    }
                }
            }

            if (globalTenant is null)
            {
                _logger.LogWarning(
                    "AUTH-CC01: LoginAsync failed: No{ProductName}Tenant userId={UserId} emailMasked={EmailMasked} ip={Ip}",
                    portalProductName, globalUser.Id, PiiGuard.MaskEmail(emailNorm), ipAddress);
                EmitLoginFailed(
                    emailNorm,
                    tenantCode: "common-portal",
                    userId: globalUser.Id.ToString(),
                    reason: $"No{portalProductName}Tenant",
                    ipAddress: ipAddress);
                throw new UnauthorizedAccessException();
            }

            // Re-use the tail of the normal flow (lock check, password verify, JWT issue).
            // We construct a synthetic request scoped to the resolved tenant so we can fall
            // through to the shared code below without duplication.
            request = request with { TenantCode = globalTenant.Code, TenantId = globalTenant.Id, ResolveByEmail = false };
            tenantCodeNorm = globalTenant.Code.ToLowerInvariant().Trim();
            _logger.LogInformation(
                "AUTH-CC01: Resolved tenant {TenantCode} ({TenantId}) for email={EmailMasked}",
                globalTenant.Code, globalTenant.Id, PiiGuard.MaskEmail(emailNorm));

            // Skip all tenant-lookup branches; go straight to user+lock+password checks.
            if (globalUser.IsLocked)
            {
                _logger.LogWarning(
                    "AUTH-CC01: LoginAsync failed: AccountLocked userId={UserId} tenantCode={TenantCode} emailMasked={EmailMasked} ip={Ip}",
                    globalUser.Id, globalTenant.Code, PiiGuard.MaskEmail(emailNorm), ipAddress);
                EmitLoginFailed(emailNorm, tenantCode: globalTenant.Code, userId: globalUser.Id.ToString(), reason: "AccountLocked", ipAddress: ipAddress);
                EmitLockedLoginBlocked(globalUser, globalTenant, ipAddress);
                throw new UnauthorizedAccessException();
            }

            var portalPasswordValid = _passwordHasher.Verify(request.Password, globalUser.PasswordHash);
            if (!portalPasswordValid)
            {
                _logger.LogWarning(
                    "AUTH-CC01: LoginAsync failed: InvalidCredentials userId={UserId} tenantCode={TenantCode} emailMasked={EmailMasked} ip={Ip}",
                    globalUser.Id, globalTenant.Code, PiiGuard.MaskEmail(emailNorm), ipAddress);
                EmitLoginFailed(emailNorm, tenantCode: globalTenant.Code, userId: globalUser.Id.ToString(), reason: "InvalidCredentials", ipAddress: ipAddress);
                throw new UnauthorizedAccessException();
            }

            var portalUserWithRoles = await _userRepository.GetByIdWithRolesAsync(globalUser.Id, ct);
            if (portalUserWithRoles is null)
            {
                EmitLoginFailed(emailNorm, tenantCode: globalTenant.Code, userId: globalUser.Id.ToString(), reason: "RoleLookupFailed", ipAddress: ipAddress);
                throw new UnauthorizedAccessException();
            }

            // Delegate the rest (role extraction, membership, JWT) to the shared tail.
            return await BuildLoginResponseAsync(
                portalUserWithRoles,
                globalTenant,
                request,
                sw,
                ipAddress,
                ct,
                requiredPortalProductCode: portalProductCode);
        }

        var tenant = await _tenantRepository.GetByCodeAsync(tenantCodeNorm, ct);

        if (tenant is null)
        {
            var upperCode = (request.TenantCode ?? string.Empty).ToUpperInvariant().Trim();
            tenant = await _tenantRepository.GetByCodeAsync(upperCode, ct);
        }

        if (tenant is null && !string.IsNullOrWhiteSpace(request.Subdomain))
        {
            var subNorm = request.Subdomain.ToLowerInvariant().Trim();
            _logger.LogInformation("Code lookup missed for {Code}, trying subdomain {Subdomain}", tenantCodeNorm, subNorm);
            tenant = await _tenantRepository.GetBySubdomainAsync(subNorm, ct);
        }

        // AUTH-B01: Final fallback — use the Tenant-service-resolved TenantId when both
        // code and subdomain lookups miss. This handles the case where the common portal
        // (e.g. careconnect-demo.legalsynq.com) has its canonical record in the Tenant
        // service but the Identity idt_Tenants write-through row carries a different code
        // or has no subdomain populated yet.
        //
        // More importantly, treat a matching TenantId as authoritative even when a
        // code/subdomain lookup already returned a row: the BFF has already resolved the
        // tenant from the Tenant service, so a non-Active provisioning status in Identity
        // is just stale write-through state and must not block login.
        var tenantConfirmedByTenantService = false;
        if (tenant is null && request.TenantId.HasValue && request.TenantId.Value != Guid.Empty)
        {
            _logger.LogInformation(
                "AUTH-B01: Code+subdomain lookup missed for {Code}, trying TenantId fallback {TenantId}",
                tenantCodeNorm, request.TenantId.Value);
            tenant = await _tenantRepository.GetByIdAsync(request.TenantId.Value, ct);
            if (tenant is not null)
                tenantConfirmedByTenantService = true;
        }

        if (!tenantConfirmedByTenantService
            && tenant is not null
            && request.TenantId.HasValue
            && request.TenantId.Value != Guid.Empty
            && tenant.Id == request.TenantId.Value)
        {
            tenantConfirmedByTenantService = true;
        }

        if (tenant is null || !tenant.IsActive)
        {
            var reason = tenant is null ? "TenantNotFound" : "TenantInactive";
            _logger.LogWarning(
                "LoginAsync failed: branch={Reason} tenantCode={TenantCode} emailMasked={EmailMasked} ip={Ip}",
                reason, tenantCodeNorm, PiiGuard.MaskEmail(emailNorm), ipAddress);
            EmitLoginFailed(emailNorm, tenantCode: tenantCodeNorm, userId: null, reason: reason, ipAddress: ipAddress);
            throw new UnauthorizedAccessException();
        }

        // Skip provisioning-status guards when the tenant was resolved by the
        // Tenant-service-authoritative TenantId: the stub in Identity may still be
        // Pending, but the real tenant is live.
        if (!tenantConfirmedByTenantService)
        {
            if (tenant.ProvisioningStatus == ProvisioningStatus.Verifying)
            {
                EmitLoginFailed(emailNorm, tenantCode: tenantCodeNorm, userId: null, reason: "TenantVerificationRetrying", ipAddress: ipAddress);
                throw new InvalidOperationException(
                    $"Tenant '{tenantCodeNorm}' is currently verifying DNS configuration. " +
                    "This process typically completes within a few minutes. Please try again shortly.");
            }

            if (tenant.ProvisioningStatus != ProvisioningStatus.Active)
            {
                EmitLoginFailed(emailNorm, tenantCode: tenantCodeNorm, userId: null, reason: "TenantNotProvisioned", ipAddress: ipAddress);
                throw new InvalidOperationException($"Tenant '{tenantCodeNorm}' is not fully provisioned (status: {tenant.ProvisioningStatus}). Please wait for setup to complete.");
            }
        }

        var normalizedEmail = emailNorm;
        var user = await _userRepository.GetByTenantAndEmailAsync(tenant.Id, normalizedEmail, ct);
        if (user is null || !user.IsActive)
        {
            var reason = user is null ? "UserNotFound" : "UserInactive";
            _logger.LogWarning(
                "LoginAsync failed: branch={Reason} tenantCode={TenantCode} emailMasked={EmailMasked} ip={Ip}",
                reason, tenant.Code, PiiGuard.MaskEmail(normalizedEmail), ipAddress);
            EmitLoginFailed(normalizedEmail, tenantCode: tenant.Code, userId: null, reason: reason, ipAddress: ipAddress);
            throw new UnauthorizedAccessException();
        }

        // UIX-003-03: reject locked accounts (checked after IsActive so lock state is independent).
        if (user.IsLocked)
        {
            _logger.LogWarning(
                "LoginAsync failed: branch=AccountLocked userId={UserId} tenantCode={TenantCode} emailMasked={EmailMasked} ip={Ip}",
                user.Id, tenant.Code, PiiGuard.MaskEmail(normalizedEmail), ipAddress);
            EmitLoginFailed(normalizedEmail, tenantCode: tenant.Code, userId: user.Id.ToString(), reason: "AccountLocked", ipAddress: ipAddress);
            EmitLockedLoginBlocked(user, tenant, ipAddress);
            throw new UnauthorizedAccessException();
        }

        var valid = _passwordHasher.Verify(request.Password, user.PasswordHash);
        if (!valid)
        {
            _logger.LogWarning(
                "LoginAsync failed: branch=InvalidCredentials userId={UserId} tenantCode={TenantCode} emailMasked={EmailMasked} ip={Ip}",
                user.Id, tenant.Code, PiiGuard.MaskEmail(normalizedEmail), ipAddress);
            EmitLoginFailed(normalizedEmail, tenantCode: tenant.Code, userId: user.Id.ToString(), reason: "InvalidCredentials", ipAddress: ipAddress);
            throw new UnauthorizedAccessException();
        }

        var userWithRoles = await _userRepository.GetByIdWithRolesAsync(user.Id, ct);
        if (userWithRoles is null)
        {
            _logger.LogWarning(
                "LoginAsync failed: branch=RoleLookupFailed userId={UserId} tenantCode={TenantCode} emailMasked={EmailMasked} ip={Ip}",
                user.Id, tenant.Code, PiiGuard.MaskEmail(normalizedEmail), ipAddress);
            EmitLoginFailed(normalizedEmail, tenantCode: tenant.Code, userId: user.Id.ToString(), reason: "RoleLookupFailed", ipAddress: ipAddress);
            throw new UnauthorizedAccessException();
        }

        return await BuildLoginResponseAsync(userWithRoles, tenant, request, sw, ipAddress, ct, requireTenantAccess: true);
    }

    /// <summary>
    /// Shared tail for LoginAsync: given a fully-validated user (with roles loaded) and their
    /// resolved tenant, assembles the JWT, audit event, and LoginResponse.  Called from both
    /// the normal tenant-code path and the AUTH-CC01 common-portal email-resolution path.
    /// </summary>
    private async Task<LoginResponse> BuildLoginResponseAsync(
        User   userWithRoles,
        Tenant tenant,
        LoginRequest request,
        Stopwatch sw,
        string? ipAddress,
        CancellationToken ct,
        string? requiredPortalProductCode = null,
        bool requireTenantAccess = false)
    {
        // Common-portal and tenant-portal guards are mutually exclusive. Passing both
        // would make the system-role tenant guard run before product eligibility, which is
        // semantically wrong and would hide portal-policy failures.
        Debug.Assert(!(requiredPortalProductCode is not null && requireTenantAccess),
            "requiredPortalProductCode and requireTenantAccess are mutually exclusive guard paths.");

        // Phase G: ScopedRoleAssignments (GLOBAL) is the sole authoritative role source.
        // UserRoles table has been dropped (migration 20260330200004).
        // LS-ID-CC-001: restrict to system roles only (Scope = Platform or Tenant).
        // Product roles (e.g. CARECONNECT_REFERRER) may exist as GLOBAL ScopedRoleAssignments
        // due to the phase-G backfill from old UserRoles rows; they must not satisfy the
        // tenant portal guard, which only applies to PlatformAdmin / TenantAdmin.
        // Note: s.IsActive is guaranteed by the filtered Include in GetByIdWithRolesAsync;
        // no need to recheck it here.
        var roleNames = userWithRoles.ScopedRoleAssignments
            .Where(s => s.ScopeType == Domain.ScopedRoleAssignment.ScopeTypes.Global
                     && s.Role.Scope is Domain.RoleScopes.Platform or Domain.RoleScopes.Tenant)
            .Select(s => s.Role.Name)
            .ToList();

        // Phase 6B: operator portal guard — CC-only users (no GLOBAL system role) are blocked.
        // Checked before product role logic so the guard fires independently of CC auto-inject.
        if (requireTenantAccess && roleNames.Count == 0)
        {
            EmitLoginFailed(
                userWithRoles.Email,
                tenantCode: tenant.Code,
                userId:     userWithRoles.Id.ToString(),
                reason:     "CareConnectUserOnTenantPortal",
                ipAddress:  ipAddress);
            throw new UnauthorizedAccessException();
        }

        // Load org membership for JWT context (tenant-scoped: always use the login tenant's org).
        var orgMembership = await _userRepository.GetPrimaryOrgMembershipAsync(userWithRoles.Id, tenant.Id, ct);
        var org = orgMembership?.Organization;

        // LS-COR-AUT-003/006: compute effective access from the single source-of-truth model.
        // All product roles come exclusively from EffectiveAccessService (direct + group-inherited).
        var effectiveAccess = await _effectiveAccessService.GetEffectiveAccessAsync(tenant.Id, userWithRoles.Id, ct);

        // Phase H: derive org_type code from OrganizationTypeId FK (authoritative) when available;
        // fall back to the stored OrgType string for compatibility.
        // TODO [Phase H — remove OrgType string]: remove OrgType string from UserResponse once column is dropped.
        var orgTypeForResponse = org is not null
            ? (Domain.OrgTypeMapper.TryResolveCode(org.OrganizationTypeId) ?? org.OrgType)
            : null;

        var productRolesFlat = effectiveAccess.ProductRolesFlat;

        // Load all active tenant memberships to populate tenant_ids JWT claim and LoginResponse.Tenants.
        var tenantMemberships = await _userRepository.GetActiveTenantMembershipsAsync(userWithRoles.Id, ct);

        // Common portal logins require explicit product access and a role from that product.
        if (requiredPortalProductCode is not null
            && !effectiveAccess.Products.Contains(requiredPortalProductCode, StringComparer.OrdinalIgnoreCase))
        {
            EmitLoginFailed(
                userWithRoles.Email,
                tenantCode: tenant.Code,
                userId:     userWithRoles.Id.ToString(),
                reason:     $"No{PortalProductAuditName(requiredPortalProductCode)}Product",
                ipAddress:  ipAddress);
            throw new UnauthorizedAccessException();
        }

        if (requiredPortalProductCode is not null
            && !productRolesFlat.Any(r => r.StartsWith(requiredPortalProductCode + ":", StringComparison.OrdinalIgnoreCase)))
        {
            EmitLoginFailed(
                userWithRoles.Email,
                tenantCode: tenant.Code,
                userId:     userWithRoles.Id.ToString(),
                reason:     $"No{PortalProductAuditName(requiredPortalProductCode)}Role",
                ipAddress:  ipAddress);
            throw new UnauthorizedAccessException();
        }

        if (requiredPortalProductCode == BuildingBlocks.Authorization.ProductCodes.SynqCareConnect
            && !IsEligibleForCareConnectPortal(productRolesFlat, roleNames))
        {
            EmitLoginFailed(
                userWithRoles.Email,
                tenantCode: tenant.Code,
                userId:     userWithRoles.Id.ToString(),
                reason:     "CareConnectPortalRoleRestricted",
                ipAddress:  ipAddress);
            throw new CareConnectPortalRoleRestrictedException(CareConnectPortalRestrictionMessage);
        }

        if (requiredPortalProductCode == BuildingBlocks.Authorization.ProductCodes.SynqLiens
            && !IsEligibleForSynqLienFundingPortal(productRolesFlat, roleNames))
        {
            EmitLoginFailed(
                userWithRoles.Email,
                tenantCode: tenant.Code,
                userId:     userWithRoles.Id.ToString(),
                reason:     "SynqLienPortalRoleRestricted",
                ipAddress:  ipAddress);
            throw new SynqLienPortalRoleRestrictedException(SynqLienPortalRestrictionMessage);
        }

        var (token, expiresAtUtc) = _jwtTokenService.GenerateToken(
            userWithRoles, tenant, roleNames, org, productRolesFlat,
            sessionTimeoutMinutes: tenant.SessionTimeoutMinutes,
            productCodes: effectiveAccess.Products,
            permissions: effectiveAccess.Permissions,
            tenantIds: tenantMemberships.Select(ut => ut.TenantId));

        // Build TenantSummary list for the LoginResponse from the membership rows.
        var tenantSummaries = tenantMemberships
            .Select(ut => new DTOs.TenantSummary(ut.TenantId, ut.Tenant?.Code ?? ut.TenantId.ToString()))
            .ToList()
            .AsReadOnly();

        // Fix: use the active login tenant's Id for UserResponse, not the user's stored TenantId.
        var userResponse = new UserResponse(
            userWithRoles.Id,
            tenant.Id,
            userWithRoles.Email,
            userWithRoles.FirstName,
            userWithRoles.LastName,
            userWithRoles.IsActive,
            roleNames,
            org?.Id,
            orgTypeForResponse,
            productRolesFlat,
            Title: userWithRoles.Title);

        // Canonical audit: fire-and-observe — never throw, never gate login on audit success.
        var now = DateTimeOffset.UtcNow;
        _ = _auditClient.IngestAsync(new IngestAuditEventRequest
        {
            EventType     = "identity.user.login.succeeded",
            EventCategory = EventCategory.Security,
            SourceSystem  = "identity-service",
            SourceService = "auth-api",
            Visibility    = VisibilityScope.User,
            Severity      = SeverityLevel.Info,
            OccurredAtUtc = now,
            Scope = new AuditEventScopeDto
            {
                ScopeType = ScopeType.Tenant,
                TenantId  = tenant.Id.ToString(),
            },
            Actor = new AuditEventActorDto
            {
                Id        = userWithRoles.Id.ToString(),
                Type      = ActorType.User,
                Name      = $"{userWithRoles.FirstName} {userWithRoles.LastName}".Trim(),
                IpAddress = ipAddress,
            },
            Entity = new AuditEventEntityDto { Type = "User", Id = userWithRoles.Id.ToString() },
            Action      = "LoginSucceeded",
            Description = $"User (id={userWithRoles.Id}) authenticated successfully in tenant {tenant.Code}.",
            Metadata    = JsonSerializer.Serialize(new { tenantCode = tenant.Code }),
            CorrelationId  = CurrentCorrelationId,
            IdempotencyKey = IdempotencyKey.ForWithTimestamp(now, "identity-service", "identity.user.login.succeeded", userWithRoles.Id.ToString()),
            Tags = ["auth", "login"],
        });

        // UIX-003-03: update LastLoginAtUtc. Best-effort — never gate login on this write.
        try
        {
            userWithRoles.RecordLogin();
            await _userRepository.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist LastLoginAtUtc for user {UserId}. Non-fatal.", userWithRoles.Id);
        }

        sw.Stop();
        _logger.LogInformation(
            "LoginPerf userId={UserId} tenantId={TenantId} elapsedMs={ElapsedMs} accessVersion={AccessVersion}",
            userWithRoles.Id, tenant.Id, sw.ElapsedMilliseconds, userWithRoles.AccessVersion);

        // BE-BIO: opt-in device-session/refresh-token issuance for biometric login.
        // Absent DeviceInfo (every existing caller today), the response shape below
        // is byte-for-byte identical to before this feature existed (BE-BIO-024).
        // Best-effort: a failure here must not fail the primary login the user is
        // actually waiting on — the client simply won't receive a refresh token and
        // biometric enrollment stays unavailable this session.
        string? refreshToken = null;
        DateTime? refreshTokenExpiresAtUtc = null;
        Guid? deviceSessionId = null;
        if (request.DeviceInfo is not null)
        {
            try
            {
                var deviceSession = await _deviceSessionService.CreateDeviceSessionAsync(
                    userWithRoles.Id, tenant.Id, request.DeviceInfo, token, expiresAtUtc, ct);
                refreshToken = deviceSession.RefreshToken;
                refreshTokenExpiresAtUtc = deviceSession.RefreshTokenExpiresAtUtc;
                deviceSessionId = deviceSession.DeviceSessionId;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "BE-BIO: failed to create device session for user {UserId} during login. Login proceeds without a refresh token.", userWithRoles.Id);
            }
        }

        return new LoginResponse(token, expiresAtUtc, userResponse, tenantSummaries, refreshToken, refreshTokenExpiresAtUtc, deviceSessionId);
    }

    /// <summary>
    /// Assembles an AuthMeResponse from a validated ClaimsPrincipal.
    /// Most fields come from JWT claims; AvatarDocumentId is fetched from DB
    /// since it changes independently of the token lifecycle.
    ///
    /// UIX-003-03: validates SessionVersion from the JWT against the DB value.
    /// If the token's session_version is older than the current DB value,
    /// the session is considered force-logged-out and the request is rejected.
    /// </summary>
    public async Task<AuthMeResponse> GetCurrentUserAsync(ClaimsPrincipal principal, CancellationToken ct = default)
    {
        var userId     = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue("sub")
            ?? throw new InvalidOperationException("sub claim missing");
        var email      = principal.FindFirstValue(ClaimTypes.Email)
            ?? principal.FindFirstValue("email")
            ?? string.Empty;
        var tenantId   = principal.FindFirstValue("tenant_id")   ?? string.Empty;
        var tenantCode = principal.FindFirstValue("tenant_code") ?? string.Empty;
        var orgId      = principal.FindFirstValue("org_id");
        var orgType    = principal.FindFirstValue("org_type");
        // org_name is baked into the JWT at login time from Organization.DisplayName.
        // A rename after login won't be reflected until the user re-authenticates.
        // This is intentional: org_name is display-only and not used for authorization.
        var orgName    = principal.FindFirstValue("org_name");

        var productRoles = principal.FindAll("product_roles")
            .Select(c => c.Value)
            .ToList();

        // Identity.Api uses MapInboundClaims=false, so JWT "role" claims are stored
        // in the ClaimsPrincipal with type "role" (the short JWT name), NOT with
        // ClaimTypes.Role (the long Microsoft URI).  Use the short name here so
        // that auth/me correctly reflects whatever roles are in the JWT.
        var systemRoles = principal.FindAll("role")
            .Select(c => c.Value)
            .ToList();

        // LS-ID-TNT-009: Read the user-specific effective product codes baked into the JWT
        // by EffectiveAccessService at login time. These reflect direct grants, group
        // inheritance, TenantAdmin auto-grant, and LegacyDefault — and are kept fresh by
        // the access_version stale-token check above. The product_codes claim stores
        // backend codes (e.g. "SYNQ_FUND"); map them to the same frontend-friendly codes
        // used by enabledProducts so the product switcher can apply a single filter.
        var rawUserProductCodes = principal.FindAll("product_codes")
            .Select(c => c.Value)
            .ToList();
        var userProducts = rawUserProductCodes
            .Select(code => DbToFrontendProductCode.TryGetValue(code, out var fc) ? fc : code)
            .ToList();

        // Derive expiry from the "exp" claim (Unix epoch seconds)
        var expClaim    = principal.FindFirstValue("exp");
        var expiresAtUtc = expClaim is not null && long.TryParse(expClaim, out var expUnix)
            ? DateTimeOffset.FromUnixTimeSeconds(expUnix).UtcDateTime
            : DateTime.UtcNow.AddMinutes(60);

        // Read per-tenant idle session timeout embedded at login time.
        var timeoutClaim = principal.FindFirstValue("session_timeout_minutes");
        var sessionTimeoutMinutes = timeoutClaim is not null && int.TryParse(timeoutClaim, out var tm) ? tm : 30;

        // AvatarDocumentId is not in the JWT (changes independently) — fetch from DB.
        // UIX-003-03: also validate SessionVersion and IsLocked from DB.
        var tenantGuidParsed = Guid.TryParse(tenantId, out var tenantGuid);
        Guid? avatarDocumentId = null;
        string? phone = null;
        Organization? organization = null;
        if (Guid.TryParse(userId, out var userGuid))
        {
            var user = await _userRepository.GetByIdAsync(userGuid, ct);

            if (user == null)
                throw new UnauthorizedAccessException("User not found.");

            // UIX-003-03: reject locked accounts immediately — they cannot use existing sessions.
            if (user.IsLocked)
                throw new UnauthorizedAccessException("Account is locked.");

            avatarDocumentId = user.AvatarDocumentId;
            phone            = user.Phone;

            // UIX-003-03: validate session version. Tokens from before a force-logout
            // or lock will have an older session_version and must be rejected.
            // If the claim is absent (old tokens before this feature), allow through.
            var sessionVersionClaim = principal.FindFirstValue("session_version");
            if (sessionVersionClaim is not null
                && int.TryParse(sessionVersionClaim, out var tokenVersion)
                && tokenVersion < user.SessionVersion)
            {
                EmitSessionInvalidated(userId, tenantId, email);
                throw new UnauthorizedAccessException("Session has been invalidated.");
            }

            // LS-COR-AUT-003: validate access version. Tokens from before an access
            // change will have a stale access_version and must be rejected.
            var accessVersionClaim = principal.FindFirstValue("access_version");
            if (accessVersionClaim is not null
                && int.TryParse(accessVersionClaim, out var tokenAccessVersion)
                && tokenAccessVersion < user.AccessVersion)
            {
                EmitAccessVersionStale(userId, tenantId, email, user.AccessVersion, tokenAccessVersion);
                throw new UnauthorizedAccessException("Access has been updated. Please re-authenticate.");
            }

            // Refresh org context from the current DB membership so profile/session UX
            // reflects org type and name edits without requiring a fresh login.
            var membership = tenantGuidParsed
                ? await _userRepository.GetPrimaryOrgMembershipAsync(userGuid, tenantGuid, ct)
                : await _userRepository.GetPrimaryOrgMembershipAsync(userGuid, ct);
            organization = membership?.Organization;
            if (organization is not null)
            {
                orgId = organization.Id.ToString();
                orgType = Domain.OrgTypeMapper.TryResolveCode(organization.OrganizationTypeId)
                    ?? organization.OrgType;
                orgName = organization.DisplayName ?? organization.Name;
            }
        }

        // Resolve which products are enabled at the tenant level.
        // Returns frontend-friendly codes (e.g. "SynqFund", "CareConnect") so the
        // tenant portal can filter its product tiles without knowing DB internals.
        List<string> enabledProducts = [];
        if (tenantGuidParsed)
        {
            var dbCodes = await _tenantRepository.GetEnabledProductCodesAsync(tenantGuid, ct);
            enabledProducts = dbCodes
                .Select(code => DbToFrontendProductCode.TryGetValue(code, out var fc) ? fc : code)
                .ToList();
        }

        // LS-ID-TNT-015: Extract effective permission codes from the JWT so the frontend
        // can perform permission-aware UI rendering without a separate API call.
        // Permissions are embedded at login time from role→permission assignments.
        // Frontend checks are UX-only; backend enforcement (LS-ID-TNT-012) is authoritative.
        var permissions = principal.FindAll("permissions")
            .Select(c => c.Value)
            .ToList();

        string? refreshedAccessToken = null;
        if (Guid.TryParse(userId, out var refreshUserId) && tenantGuidParsed)
        {
            var refreshUser = await _userRepository.GetByIdWithRolesAsync(refreshUserId, ct);
            var refreshTenant = await _tenantRepository.GetByIdAsync(tenantGuid, ct);

            if (refreshUser is not null && refreshTenant is not null)
            {
                var tenantMemberships = await _userRepository.GetActiveTenantMembershipsAsync(refreshUser.Id, ct);
                var rawTenantIds = principal.FindAll("tenant_ids")
                    .Select(claim => Guid.TryParse(claim.Value, out var parsedTenantId) ? parsedTenantId : (Guid?)null)
                    .Where(value => value.HasValue)
                    .Select(value => value!.Value);
                var tenantIds = rawTenantIds
                    .Concat(tenantMemberships.Select(ut => ut.TenantId))
                    .Distinct()
                    .ToArray();

                var (renewedToken, renewedExpiresAtUtc) = _jwtTokenService.GenerateToken(
                    refreshUser,
                    refreshTenant,
                    systemRoles,
                    organization,
                    productRoles,
                    sessionTimeoutMinutes: sessionTimeoutMinutes,
                    productCodes: rawUserProductCodes,
                    permissions: permissions,
                    tenantIds: tenantIds);

                refreshedAccessToken = renewedToken;
                expiresAtUtc = renewedExpiresAtUtc;
            }
        }

        return new AuthMeResponse(
            UserId:                 userId,
            Email:                  email,
            TenantId:               tenantId,
            TenantCode:             tenantCode,
            OrgId:                  orgId,
            OrgType:                orgType,
            OrgName:                orgName,
            ProductRoles:           productRoles,
            SystemRoles:            systemRoles,
            ExpiresAtUtc:           expiresAtUtc,
            SessionTimeoutMinutes:  sessionTimeoutMinutes,
            AvatarDocumentId:       avatarDocumentId,
            EnabledProducts:        enabledProducts,
            Phone:                  phone,
            UserProducts:           userProducts,
            Permissions:            permissions,
            RefreshedAccessToken:   refreshedAccessToken);
    }

    private static bool IsEligibleForCareConnectPortal(
        IReadOnlyCollection<string> productRolesFlat,
        IReadOnlyCollection<string> roleNames)
    {
        if (roleNames.Count > 0)
            return false;

        var careConnectRoles = productRolesFlat
            .Where(r => r.StartsWith(BuildingBlocks.Authorization.ProductCodes.SynqCareConnect + ":", StringComparison.OrdinalIgnoreCase))
            .Select(r => r[(BuildingBlocks.Authorization.ProductCodes.SynqCareConnect.Length + 1)..])
            .ToList();

        if (careConnectRoles.Count == 0)
            return false;

        return careConnectRoles.All(role =>
            string.Equals(role, ProductRoleCodes.CareConnectReceiver, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, ProductRoleCodes.CareConnectReferrer, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, ProductRoleCodes.CareConnectReferrerAdmin, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsEligibleForSynqLienFundingPortal(
        IReadOnlyCollection<string> productRolesFlat,
        IReadOnlyCollection<string> roleNames)
    {
        if (roleNames.Count > 0)
            return false;

        var synqLienRoles = productRolesFlat
            .Where(r => r.StartsWith(BuildingBlocks.Authorization.ProductCodes.SynqLiens + ":", StringComparison.OrdinalIgnoreCase))
            .Select(r => r[(BuildingBlocks.Authorization.ProductCodes.SynqLiens.Length + 1)..])
            .ToList();

        if (synqLienRoles.Count == 0)
            return false;

        return synqLienRoles.All(role =>
            string.Equals(role, ProductRoleCodes.SynqLienBuyer, StringComparison.OrdinalIgnoreCase));
    }

    private static string? NormalizePortalProductCode(string? productCode)
    {
        var normalized = string.IsNullOrWhiteSpace(productCode)
            ? BuildingBlocks.Authorization.ProductCodes.SynqCareConnect
            : productCode.Trim().ToUpperInvariant();

        return normalized switch
        {
            BuildingBlocks.Authorization.ProductCodes.SynqCareConnect => BuildingBlocks.Authorization.ProductCodes.SynqCareConnect,
            BuildingBlocks.Authorization.ProductCodes.SynqLiens => BuildingBlocks.Authorization.ProductCodes.SynqLiens,
            _ => null,
        };
    }

    private static string PortalProductAuditName(string productCode)
    {
        return productCode.Equals(BuildingBlocks.Authorization.ProductCodes.SynqLiens, StringComparison.OrdinalIgnoreCase)
            ? "SynqLien"
            : "CareConnect";
    }

    // Maps the DB product Code column → the frontend ProductCode (TypeScript).
    // Keep in sync with AdminEndpoints.DbToFrontendProductCode.
    private static readonly Dictionary<string, string> DbToFrontendProductCode
        = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SYNQ_FUND"]        = "SynqFund",
        ["SYNQ_LIENS"]       = "SynqLien",
        ["SYNQ_CARECONNECT"] = "CareConnect",
        ["SYNQ_AI"]          = "Xenia",
        ["XENIA"]            = "Xenia",
        ["SYNQ_INSIGHTS"]    = "SynqInsights",
        ["SYNQ_BILL"]        = "SynqBill",
        ["SYNQ_RX"]          = "SynqRx",
        ["SYNQ_PAYOUT"]      = "SynqPayout",
    };

    // ── Canonical audit helpers ────────────────────────────────────────────────

    /// <summary>
    /// Emits a <c>identity.user.login.failed</c> canonical audit event.
    ///
    /// Fire-and-observe: the returned Task is discarded. This method never throws,
    /// never awaits the ingestion call, and never gates the primary auth failure response.
    ///
    /// The failure reason is stored as metadata only. The HTTP response to the caller
    /// never reveals which specific check failed (tenant/user/password) — the caller
    /// always receives 401 Unauthorized.
    ///
    /// HIPAA §164.312(b): failed login attempts are a required audit event.
    /// </summary>
    private void EmitLoginFailed(string email, string tenantCode, string? userId, string reason, string? ipAddress = null)
    {
        var now = DateTimeOffset.UtcNow;
        _ = _auditClient.IngestAsync(new IngestAuditEventRequest
        {
            EventType     = "identity.user.login.failed",
            EventCategory = EventCategory.Security,
            SourceSystem  = "identity-service",
            SourceService = "auth-api",
            Visibility    = VisibilityScope.Tenant,
            Severity      = SeverityLevel.Warn,
            OccurredAtUtc = now,
            Scope = new AuditEventScopeDto
            {
                ScopeType = ScopeType.Tenant,
                TenantId  = null,
            },
            Actor = new AuditEventActorDto
            {
                Id        = userId,
                Type      = ActorType.User,
                Name      = PiiGuard.MaskEmail(email),
                IpAddress = ipAddress,
            },
            Entity      = userId is not null ? new AuditEventEntityDto { Type = "User", Id = userId } : null,
            Action      = "LoginFailed",
            Description = $"Failed login attempt for '{PiiGuard.MaskEmail(email)}' in tenant '{tenantCode}'.",
            Metadata    = System.Text.Json.JsonSerializer.Serialize(new
            {
                tenantCode,
                failureReason = reason,
            }),
            CorrelationId  = CurrentCorrelationId,
            IdempotencyKey = IdempotencyKey.ForWithTimestamp(now, "identity-service", "identity.user.login.failed", email),
            Tags = ["auth", "login", "failure", "security"],
        });
    }

    /// <summary>
    /// LS-ID-TNT-017-002: emits <c>identity.session.invalidated</c> when a JWT's
    /// <c>session_version</c> is older than the DB value (e.g. after a force-logout).
    /// Fire-and-observe. Never throws, never gates the rejection response.
    /// </summary>
    private void EmitSessionInvalidated(string userId, string tenantId, string email)
    {
        var now = DateTimeOffset.UtcNow;
        _ = _auditClient.IngestAsync(new IngestAuditEventRequest
        {
            EventType     = "identity.session.invalidated",
            EventCategory = EventCategory.Security,
            SourceSystem  = "identity-service",
            SourceService = "auth-api",
            Visibility    = VisibilityScope.Tenant,
            Severity      = SeverityLevel.Warn,
            OccurredAtUtc = now,
            Scope = new AuditEventScopeDto
            {
                ScopeType = ScopeType.Tenant,
                TenantId  = tenantId,
            },
            Actor = new AuditEventActorDto
            {
                Id   = userId,
                Type = ActorType.User,
                Name = email,
            },
            Entity      = new AuditEventEntityDto { Type = "User", Id = userId },
            Action      = "SessionInvalidated",
            Description = $"Session invalidated for user '{email}' — JWT session_version is stale (force-logout or account lock).",
            Metadata    = JsonSerializer.Serialize(new { reason = "SessionVersionStale" }),
            CorrelationId  = CurrentCorrelationId,
            IdempotencyKey = IdempotencyKey.ForWithTimestamp(
                now, "identity-service", "identity.session.invalidated", userId),
            Tags = ["auth", "session", "invalidated", "security"],
        });
    }

    /// <summary>
    /// LS-ID-TNT-017-002: emits <c>identity.access.version.stale</c> when a JWT's
    /// <c>access_version</c> is older than the DB value (e.g. after a permission change).
    /// Signals that the user must re-authenticate to acquire a fresh JWT with updated claims.
    /// Fire-and-observe. Never throws, never gates the rejection response.
    /// </summary>
    private void EmitAccessVersionStale(
        string userId, string tenantId, string email,
        int currentAccessVersion, int tokenAccessVersion)
    {
        var now = DateTimeOffset.UtcNow;
        _ = _auditClient.IngestAsync(new IngestAuditEventRequest
        {
            EventType     = "identity.access.version.stale",
            EventCategory = EventCategory.Security,
            SourceSystem  = "identity-service",
            SourceService = "auth-api",
            Visibility    = VisibilityScope.Tenant,
            Severity      = SeverityLevel.Warn,
            OccurredAtUtc = now,
            Scope = new AuditEventScopeDto
            {
                ScopeType = ScopeType.Tenant,
                TenantId  = tenantId,
            },
            Actor = new AuditEventActorDto
            {
                Id   = userId,
                Type = ActorType.User,
                Name = email,
            },
            Entity      = new AuditEventEntityDto { Type = "User", Id = userId },
            Action      = "AccessVersionStale",
            Description = $"Stale access_version detected for user '{email}' — re-authentication required (permission change since last login).",
            Metadata    = JsonSerializer.Serialize(new
            {
                reason               = "AccessVersionStale",
                tokenAccessVersion,
                currentAccessVersion,
            }),
            CorrelationId  = CurrentCorrelationId,
            IdempotencyKey = IdempotencyKey.ForWithTimestamp(
                now, "identity-service", "identity.access.version.stale", userId),
            Tags = ["auth", "access-version", "stale", "security", "re-auth"],
        });
    }

    /// <summary>
    /// UIX-003-03: emits a dedicated <c>identity.user.login.blocked</c> event when a locked
    /// account attempts to authenticate. Separate from the generic login.failed event so
    /// security dashboards can distinguish intentional locks from bad credentials.
    /// Fire-and-observe.
    /// </summary>
    private void EmitLockedLoginBlocked(User user, Tenant tenant, string? ipAddress)
    {
        var now = DateTimeOffset.UtcNow;
        _ = _auditClient.IngestAsync(new IngestAuditEventRequest
        {
            EventType     = "identity.user.login.blocked",
            EventCategory = EventCategory.Security,
            SourceSystem  = "identity-service",
            SourceService = "auth-api",
            Visibility    = VisibilityScope.Tenant,
            Severity      = SeverityLevel.Warn,
            OccurredAtUtc = now,
            Scope = new AuditEventScopeDto
            {
                ScopeType = ScopeType.Tenant,
                TenantId  = tenant.Id.ToString(),
            },
            Actor = new AuditEventActorDto
            {
                Id        = user.Id.ToString(),
                Type      = ActorType.User,
                Name      = PiiGuard.MaskEmail(user.Email),
                IpAddress = ipAddress,
            },
            Entity      = new AuditEventEntityDto { Type = "User", Id = user.Id.ToString() },
            Action      = "LoginBlocked",
            Description = $"Login attempt blocked for locked account (userId={user.Id}) in tenant {tenant.Code}.",
            Metadata    = JsonSerializer.Serialize(new { tenantCode = tenant.Code, userId = user.Id.ToString(), reason = "AccountLocked" }),
            CorrelationId  = CurrentCorrelationId,
            IdempotencyKey = IdempotencyKey.ForWithTimestamp(now, "identity-service", "identity.user.login.blocked", user.Id.ToString()),
            Tags = ["auth", "login", "blocked", "security", "locked"],
        });
    }

}
