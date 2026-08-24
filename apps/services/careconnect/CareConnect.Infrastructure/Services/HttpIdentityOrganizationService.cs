// LSCC-010 / CC2-INT-B04: HTTP client that calls the Identity service to create/resolve
// a minimal PROVIDER Organization for the auto-provision flow.
//
// BLK-CC-01: Retired methods removed — check-code and self-provision now live in
// HttpTenantServiceClient (ITenantServiceClient) and HttpIdentityMembershipClient.
//
// Failure policy: ALL failures (network, 4xx, 5xx, parse) return null.
// The caller (AutoProvisionService) interprets null as "fall back to LSCC-009".
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using CareConnect.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CareConnect.Infrastructure.Services;

public sealed class HttpIdentityOrganizationService : IIdentityOrganizationService
{
    private readonly IHttpClientFactory                          _httpClientFactory;
    private readonly IdentityServiceOptions                      _options;
    private readonly ILogger<HttpIdentityOrganizationService>   _logger;
    private readonly bool                                        _isEnabled;

    public HttpIdentityOrganizationService(
        IHttpClientFactory                        httpClientFactory,
        IOptions<IdentityServiceOptions>          options,
        ILogger<HttpIdentityOrganizationService>  logger)
    {
        _httpClientFactory = httpClientFactory;
        _options           = options.Value;
        _logger            = logger;
        _isEnabled         = !string.IsNullOrWhiteSpace(_options.BaseUrl);

        if (!_isEnabled)
        {
            _logger.LogWarning(
                "LSCC-010 IdentityService:BaseUrl not configured. " +
                "Auto-provisioning org creation is disabled — " +
                "all auto-provision calls will fall back to LSCC-009.");
        }
    }

    public async Task<Guid?> EnsureProviderOrganizationAsync(
        Guid              tenantId,
        Guid              providerCcId,
        string            providerName,
        CancellationToken ct = default,
        bool              globalScope = false)
    {
        if (!_isEnabled)
        {
            _logger.LogDebug(
                "LSCC-010 Identity org creation skipped (BaseUrl not configured) for provider {ProviderId}.",
                providerCcId);
            return null;
        }

        try
        {
            using var client = BuildIdentityClient();
            client.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            var body = new
            {
                tenantId     = tenantId,
                providerCcId = providerCcId,
                providerName = providerName,
                globalScope  = globalScope,
            };

            using var response = await client.PostAsJsonAsync(
                "api/admin/organizations", body, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "LSCC-010 Identity org creation returned HTTP {Status} for provider {ProviderId}. Fallback triggered.",
                    (int)response.StatusCode, providerCcId);
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<CreateProviderOrgResponse>(
                cancellationToken: cts.Token);

            if (result is null || result.Id == Guid.Empty)
            {
                _logger.LogWarning(
                    "LSCC-010 Identity org creation returned null or empty Id for provider {ProviderId}.",
                    providerCcId);
                return null;
            }

            _logger.LogInformation(
                "LSCC-010 Identity org {OrgId} {IsNew} for provider {ProviderId}.",
                result.Id,
                result.IsNew ? "created" : "already exists",
                providerCcId);

            return result.Id;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "LSCC-010 Identity org creation timed out for provider {ProviderId}. Fallback triggered.",
                providerCcId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "LSCC-010 Identity org creation failed for provider {ProviderId}. Fallback triggered.",
                providerCcId);
            return null;
        }
    }

    // ── CC2-INT-B04: Token → Identity Bridge — user invitation ───────────────

    /// <inheritdoc />
    public async Task<ProvisionProviderUserResult?> InviteProviderUserAsync(
        Guid              orgId,
        string            email,
        string            firstName,
        string?           lastName,
        CancellationToken ct = default)
    {
        if (!_isEnabled)
        {
            _logger.LogDebug(
                "CC2-INT-B04 Identity user invitation skipped (BaseUrl not configured) for org {OrgId}.",
                orgId);
            return null;
        }

        if (string.IsNullOrWhiteSpace(email))
        {
            _logger.LogDebug(
                "CC2-INT-B04 Identity user invitation skipped (no email) for org {OrgId}.", orgId);
            return null;
        }

        try
        {
            using var client = BuildIdentityClient();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            var body = new
            {
                email     = email,
                firstName = firstName,
                lastName  = lastName,
            };

            using var response = await client.PostAsJsonAsync(
                $"api/admin/organizations/{orgId}/provision-user", body, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
                {
                    var conflictBody = await response.Content
                        .ReadFromJsonAsync<ErrorCodeResponse>(cancellationToken: cts.Token);
                    if (conflictBody?.Code == "OWNER_ENROLLMENT_BLOCKED")
                    {
                        _logger.LogWarning(
                            "CC2-INT-B04 Identity user provision blocked — owner enrollment for org {OrgId}.", orgId);
                        return new ProvisionProviderUserResult { IsOwnerBlocked = true };
                    }
                }

                _logger.LogWarning(
                    "CC2-INT-B04 Identity user provision returned HTTP {Status} for org {OrgId}. " +
                    "Invitation not sent — provider org link is still valid.",
                    (int)response.StatusCode, orgId);
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<ProvisionProviderUserResponse>(
                cancellationToken: cts.Token);

            if (result is null || result.UserId == Guid.Empty)
            {
                _logger.LogWarning(
                    "CC2-INT-B04 Identity user provision returned null/empty userId for org {OrgId}.",
                    orgId);
                return null;
            }

            _logger.LogInformation(
                "CC2-INT-B04 Identity user {UserId} {IsNew} for org {OrgId}. InvitationSent={InvitationSent}.",
                result.UserId,
                result.IsNew ? "created" : "already existed",
                orgId,
                result.InvitationSent);

            return new ProvisionProviderUserResult
            {
                UserId         = result.UserId,
                InvitationId   = result.InvitationId,
                IsNew          = result.IsNew,
                InvitationSent = result.InvitationSent,
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "CC2-INT-B04 Identity user provision timed out for org {OrgId}. Invitation not sent.", orgId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "CC2-INT-B04 Identity user provision failed for org {OrgId}. Invitation not sent.", orgId);
            return null;
        }
    }

    // ── CC2-ENROLL: Self-enrollment — direct password registration ────────────

    /// <inheritdoc />
    public async Task<SelfRegisterResult?> RegisterUserDirectlyAsync(
        Guid              orgId,
        Guid              tenantId,
        string            email,
        string            password,
        string            firstName,
        string?           lastName,
        string?           phone,
        CancellationToken ct = default,
        string?           title = null)
    {
        if (!_isEnabled)
        {
            _logger.LogDebug(
                "CC2-ENROLL Identity self-register skipped (BaseUrl not configured) for org {OrgId}.", orgId);
            return null;
        }

        try
        {
            using var client = BuildIdentityClient();
            using var cts    = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            var body = new { tenantId, email, password, firstName, lastName, phone, title };

            using var response = await client.PostAsJsonAsync(
                $"api/admin/organizations/{orgId}/self-register", body, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
                {
                    var conflictBody = await response.Content
                        .ReadFromJsonAsync<ErrorCodeResponse>(cancellationToken: cts.Token);
                    if (conflictBody?.Code == "OWNER_ENROLLMENT_BLOCKED")
                    {
                        _logger.LogWarning(
                            "CC2-ENROLL Identity self-register blocked — owner enrollment for org {OrgId}.", orgId);
                        return new SelfRegisterResult { IsOwnerBlocked = true };
                    }
                }

                _logger.LogWarning(
                    "CC2-ENROLL Identity self-register returned HTTP {Status} for org {OrgId}.",
                    (int)response.StatusCode, orgId);
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<SelfRegisterResponse>(
                cancellationToken: cts.Token);

            if (result is null || result.UserId == Guid.Empty)
            {
                _logger.LogWarning(
                    "CC2-ENROLL Identity self-register returned null/empty userId for org {OrgId}.", orgId);
                return null;
            }

            _logger.LogInformation(
                "CC2-ENROLL Identity user {UserId} {IsNew} for org {OrgId}.",
                result.UserId, result.IsNew ? "created" : "already existed", orgId);

            return new SelfRegisterResult { UserId = result.UserId, IsNew = result.IsNew };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("CC2-ENROLL Identity self-register timed out for org {OrgId}.", orgId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CC2-ENROLL Identity self-register failed for org {OrgId}.", orgId);
            return null;
        }
    }

    // ── CC2-ENROLL-FIRM: Law firm self-enrollment ────────────────────────────

    public async Task<Guid?> EnsureLawFirmOrganizationAsync(
        Guid              tenantId,
        string            firmName,
        string            contactEmail,
        CancellationToken ct = default)
    {
        if (!_isEnabled)
        {
            _logger.LogDebug(
                "CC2-ENROLL-FIRM Identity law-firm org creation skipped (BaseUrl not configured) for '{FirmName}'.",
                firmName);
            return null;
        }

        try
        {
            using var client = BuildIdentityClient();
            using var cts    = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            var body = new { tenantId, firmName, contactEmail };

            using var response = await client.PostAsJsonAsync(
                "api/admin/organizations/law-firm", body, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "CC2-ENROLL-FIRM Identity law-firm org creation returned HTTP {Status} for '{FirmName}'.",
                    (int)response.StatusCode, firmName);
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<CreateLawFirmOrgResponse>(
                cancellationToken: cts.Token);

            if (result is null || result.Id == Guid.Empty)
            {
                _logger.LogWarning(
                    "CC2-ENROLL-FIRM Identity law-firm org creation returned null or empty Id for '{FirmName}'.",
                    firmName);
                return null;
            }

            _logger.LogInformation(
                "CC2-ENROLL-FIRM Identity org {OrgId} {IsNew} for law firm '{FirmName}'.",
                result.Id, result.IsNew ? "created" : "already exists", firmName);

            return result.Id;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "CC2-ENROLL-FIRM Identity law-firm org creation timed out for '{FirmName}'.", firmName);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "CC2-ENROLL-FIRM Identity law-firm org creation failed for '{FirmName}'.", firmName);
            return null;
        }
    }

    // ── CC-PORTAL-CHECK: Referrer portal access lookup ────────────────────────

    /// <inheritdoc />
    public async Task<string> GetReferrerPortalAccessStatusAsync(
        Guid              tenantId,
        string            email,
        CancellationToken ct = default)
    {
        if (!_isEnabled || tenantId == Guid.Empty || string.IsNullOrWhiteSpace(email))
            return ReferrerPortalAccessStatuses.NoAccount;

        try
        {
            using var client = BuildIdentityClient();
            using var cts    = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            var url = $"api/internal/users/portal-access?tenantId={tenantId}&email={Uri.EscapeDataString(email.Trim())}";
            using var response = await client.GetAsync(url, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "CC-PORTAL-CHECK Identity portal-access returned HTTP {Status} for email check.",
                    (int)response.StatusCode);
                return ReferrerPortalAccessStatuses.NoAccount;
            }

            var result = await response.Content.ReadFromJsonAsync<PortalAccessResponse>(
                cancellationToken: cts.Token);

            return string.IsNullOrWhiteSpace(result?.Status)
                ? ReferrerPortalAccessStatuses.NoAccount
                : result.Status;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("CC-PORTAL-CHECK Identity portal-access check timed out.");
            return ReferrerPortalAccessStatuses.NoAccount;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CC-PORTAL-CHECK Identity portal-access check failed.");
            return ReferrerPortalAccessStatuses.NoAccount;
        }
    }

    // ── CC-OWNER-CHECK: Tenant owner referral block ────────────────────────

    /// <inheritdoc />
    public async Task<bool> CheckTenantOwnerEmailAsync(
        Guid              tenantId,
        string            email,
        CancellationToken ct = default)
    {
        if (!_isEnabled || tenantId == Guid.Empty || string.IsNullOrWhiteSpace(email))
            return false;

        try
        {
            using var client = BuildIdentityClient();
            using var cts    = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            var url = $"api/internal/users/is-owner?tenantId={tenantId}&email={Uri.EscapeDataString(email.Trim())}";
            using var response = await client.GetAsync(url, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "CC-OWNER-CHECK Identity is-owner returned HTTP {Status}.",
                    (int)response.StatusCode);
                return false;
            }

            var result = await response.Content.ReadFromJsonAsync<IsOwnerResponse>(
                cancellationToken: cts.Token);

            return result?.IsTenantOwner ?? false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("CC-OWNER-CHECK Identity is-owner check timed out.");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CC-OWNER-CHECK Identity is-owner check failed.");
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> CheckAnyTenantOwnerEmailAsync(
        string            email,
        CancellationToken ct = default)
    {
        if (!_isEnabled || string.IsNullOrWhiteSpace(email))
            return false;

        try
        {
            using var client = BuildIdentityClient();
            using var cts    = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            var url = $"api/internal/users/is-any-owner?email={Uri.EscapeDataString(email.Trim())}";
            using var response = await client.GetAsync(url, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "CC-OWNER-CHECK Identity is-any-owner returned HTTP {Status}.",
                    (int)response.StatusCode);
                return false;
            }

            var result = await response.Content.ReadFromJsonAsync<IsOwnerResponse>(
                cancellationToken: cts.Token);
            return result?.IsTenantOwner ?? false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("CC-OWNER-CHECK Identity is-any-owner check timed out.");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CC-OWNER-CHECK Identity is-any-owner check failed.");
            return false;
        }
    }

    public async Task<string?> GetOrganizationNameAsync(
        Guid              orgId,
        CancellationToken ct = default)
    {
        if (!_isEnabled || orgId == Guid.Empty)
            return null;

        try
        {
            using var client = BuildIdentityClient();
            using var cts    = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            using var response = await client.GetAsync(
                $"api/admin/organizations/{orgId}", cts.Token);

            if (!response.IsSuccessStatusCode)
                return null;

            var result = await response.Content.ReadFromJsonAsync<OrganizationLookupResponse>(
                cancellationToken: cts.Token);

            return string.IsNullOrWhiteSpace(result?.Name)
                ? null
                : result.Name.Trim();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Identity organization lookup timed out for org {OrgId}.", orgId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Identity organization lookup failed for org {OrgId}.", orgId);
            return null;
        }
    }

    public async Task<List<LawFirmOrganizationOption>> ListLawFirmOrganizationsAsync(
        Guid tenantId,
        CancellationToken ct = default)
    {
        if (!_isEnabled || tenantId == Guid.Empty)
            return [];

        try
        {
            using var client = BuildIdentityClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            using var response = await client.GetAsync(
                $"api/admin/organizations?tenantId={tenantId}&orgType=LAW_FIRM", cts.Token);

            if (!response.IsSuccessStatusCode)
                return [];

            var result = await response.Content.ReadFromJsonAsync<OrganizationListResponse>(
                cancellationToken: cts.Token);

            return result?.Items?
                .Where(o => o.Id != Guid.Empty && !string.IsNullOrWhiteSpace(o.DisplayName ?? o.Name))
                .Select(o => new LawFirmOrganizationOption
                {
                    Id = o.Id,
                    Name = (o.DisplayName ?? o.Name)!.Trim(),
                })
                .ToList() ?? [];
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Identity law-firm organization lookup timed out for tenant {TenantId}.", tenantId);
            return [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Identity law-firm organization lookup failed for tenant {TenantId}.", tenantId);
            return [];
        }
    }

    // ── Shared HTTP client builder ─────────────────────────────────────────────

    private HttpClient BuildIdentityClient()
    {
        var client = _httpClientFactory.CreateClient("IdentityService");
        client.BaseAddress = new Uri(_options.BaseUrl!.TrimEnd('/') + "/");
        client.Timeout     = TimeSpan.FromSeconds(_options.TimeoutSeconds);

        // BLK-SEC-01: Prefer ProvisioningToken (X-Provisioning-Token header) for internal calls.
        if (!string.IsNullOrWhiteSpace(_options.ProvisioningToken))
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "X-Provisioning-Token", _options.ProvisioningToken);
        }
        else if (!string.IsNullOrWhiteSpace(_options.AuthHeaderName) &&
                 !string.IsNullOrWhiteSpace(_options.AuthHeaderValue))
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                _options.AuthHeaderName, _options.AuthHeaderValue);
        }

        return client;
    }

    // ── Private response models ───────────────────────────────────────────────

    private sealed class CreateLawFirmOrgResponse
    {
        [JsonPropertyName("id")]
        public Guid   Id    { get; set; }

        [JsonPropertyName("name")]
        public string Name  { get; set; } = string.Empty;

        [JsonPropertyName("isNew")]
        public bool   IsNew { get; set; }
    }

    private sealed class CreateProviderOrgResponse
    {
        [JsonPropertyName("id")]
        public Guid   Id    { get; set; }

        [JsonPropertyName("name")]
        public string Name  { get; set; } = string.Empty;

        [JsonPropertyName("isNew")]
        public bool   IsNew { get; set; }
    }

    private sealed class ProvisionProviderUserResponse
    {
        [JsonPropertyName("userId")]
        public Guid  UserId         { get; set; }

        [JsonPropertyName("invitationId")]
        public Guid? InvitationId   { get; set; }

        [JsonPropertyName("isNew")]
        public bool  IsNew          { get; set; }

        [JsonPropertyName("invitationSent")]
        public bool  InvitationSent { get; set; }
    }

    private sealed class SelfRegisterResponse
    {
        [JsonPropertyName("userId")]
        public Guid UserId { get; set; }

        [JsonPropertyName("isNew")]
        public bool IsNew  { get; set; }
    }

    private sealed class PortalAccessResponse
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }
    }

    private sealed class OrganizationListResponse
    {
        [JsonPropertyName("items")]
        public List<OrganizationListItem>? Items { get; set; }
    }

    private sealed class OrganizationListItem
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }
    }

    private sealed class IsOwnerResponse
    {
        [JsonPropertyName("isTenantOwner")]
        public bool IsTenantOwner { get; set; }
    }

    private sealed class OrganizationLookupResponse
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private sealed class ErrorCodeResponse
    {
        [JsonPropertyName("code")]
        public string? Code { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }

    // ── LSV3-1083: Law-firm-scoped user management ──────────────────────────

    public async Task<(LawFirmUserOperationOutcome Outcome, IReadOnlyList<LawFirmUserSummary>? Items, string? Error)> ListOrganizationUsersAsync(
        Guid organizationId, CancellationToken ct = default)
    {
        if (!_isEnabled)
            return (LawFirmUserOperationOutcome.ServiceUnavailable, null, "Identity service is not configured.");

        try
        {
            using var client = BuildIdentityClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            using var response = await client.GetAsync($"api/internal/organizations/{organizationId}/users/", cts.Token);
            if (!response.IsSuccessStatusCode)
                return await FailureFromResponseAsync<IReadOnlyList<LawFirmUserSummary>>(response, cts.Token);

            var result = await response.Content.ReadFromJsonAsync<OrgUserListResponse>(cancellationToken: cts.Token);
            var items = (result?.Items ?? [])
                .Select(i => new LawFirmUserSummary
                {
                    UserId = i.UserId,
                    Email = i.Email,
                    FirstName = i.FirstName,
                    LastName = i.LastName,
                    IsActive = i.IsActive,
                    Status = i.Status ?? string.Empty,
                    Roles = (i.Roles ?? [])
                        .Select(r => new LawFirmUserRoleAssignmentInfo { AssignmentId = r.AssignmentId, RoleCode = r.RoleCode ?? string.Empty })
                        .ToList(),
                })
                .ToList();

            return (LawFirmUserOperationOutcome.Success, items, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Law-firm user list timed out for organization {OrganizationId}.", organizationId);
            return (LawFirmUserOperationOutcome.ServiceUnavailable, null, "Identity service timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Law-firm user list failed for organization {OrganizationId}.", organizationId);
            return (LawFirmUserOperationOutcome.ServiceUnavailable, null, "Identity service call failed.");
        }
    }

    public async Task<(LawFirmUserOperationOutcome Outcome, LawFirmUserInviteResult? Result, string? Error)> InviteOrganizationUserAsync(
        Guid organizationId, Guid tenantId, string email, string firstName, string lastName, string roleCode, CancellationToken ct = default)
    {
        if (!_isEnabled)
            return (LawFirmUserOperationOutcome.ServiceUnavailable, null, "Identity service is not configured.");

        try
        {
            using var client = BuildIdentityClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            using var response = await client.PostAsJsonAsync(
                $"api/internal/organizations/{organizationId}/users/invite",
                new { tenantId, email, firstName, lastName, roleCode },
                cts.Token);

            if (!response.IsSuccessStatusCode)
                return await FailureFromResponseAsync<LawFirmUserInviteResult>(response, cts.Token);

            var result = await response.Content.ReadFromJsonAsync<InviteOrgUserResponse>(cancellationToken: cts.Token);
            if (result is null)
                return (LawFirmUserOperationOutcome.ServiceUnavailable, null, "Identity service returned an empty response.");

            return (LawFirmUserOperationOutcome.Success, new LawFirmUserInviteResult
            {
                UserId = result.UserId,
                InvitationId = result.InvitationId,
                Email = result.Email,
                IsNew = result.IsNew,
            }, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Law-firm user invite timed out for organization {OrganizationId}.", organizationId);
            return (LawFirmUserOperationOutcome.ServiceUnavailable, null, "Identity service timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Law-firm user invite failed for organization {OrganizationId}.", organizationId);
            return (LawFirmUserOperationOutcome.ServiceUnavailable, null, "Identity service call failed.");
        }
    }

    public Task<(LawFirmUserOperationOutcome Outcome, string? Error)> ActivateOrganizationUserAsync(
        Guid organizationId, Guid userId, CancellationToken ct = default) =>
        SetOrganizationUserActiveStateAsync(organizationId, userId, activate: true, ct);

    public Task<(LawFirmUserOperationOutcome Outcome, string? Error)> DeactivateOrganizationUserAsync(
        Guid organizationId, Guid userId, CancellationToken ct = default) =>
        SetOrganizationUserActiveStateAsync(organizationId, userId, activate: false, ct);

    public async Task<(LawFirmUserOperationOutcome Outcome, string? Error)> ResendOrganizationUserInviteAsync(
        Guid organizationId, Guid userId, CancellationToken ct = default)
    {
        if (!_isEnabled)
            return (LawFirmUserOperationOutcome.ServiceUnavailable, "Identity service is not configured.");

        try
        {
            using var client = BuildIdentityClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            using var response = await client.PostAsync(
                $"api/internal/organizations/{organizationId}/users/{userId}/resend-invite", null, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                var (outcome, _, error) = await FailureFromResponseAsync<object>(response, cts.Token);
                return (outcome, error);
            }

            return (LawFirmUserOperationOutcome.Success, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Law-firm user invite resend timed out for organization {OrganizationId}.", organizationId);
            return (LawFirmUserOperationOutcome.ServiceUnavailable, "Identity service timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Law-firm user invite resend failed for organization {OrganizationId}.", organizationId);
            return (LawFirmUserOperationOutcome.ServiceUnavailable, "Identity service call failed.");
        }
    }

    private async Task<(LawFirmUserOperationOutcome Outcome, string? Error)> SetOrganizationUserActiveStateAsync(
        Guid organizationId, Guid userId, bool activate, CancellationToken ct)
    {
        if (!_isEnabled)
            return (LawFirmUserOperationOutcome.ServiceUnavailable, "Identity service is not configured.");

        try
        {
            using var client = BuildIdentityClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            var path = activate ? "activate" : "deactivate";
            using var response = await client.PostAsync(
                $"api/internal/organizations/{organizationId}/users/{userId}/{path}", null, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                var (outcome, _, error) = await FailureFromResponseAsync<object>(response, cts.Token);
                return (outcome, error);
            }

            return (LawFirmUserOperationOutcome.Success, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Law-firm user {Action} timed out for organization {OrganizationId}.", activate ? "activate" : "deactivate", organizationId);
            return (LawFirmUserOperationOutcome.ServiceUnavailable, "Identity service timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Law-firm user {Action} failed for organization {OrganizationId}.", activate ? "activate" : "deactivate", organizationId);
            return (LawFirmUserOperationOutcome.ServiceUnavailable, "Identity service call failed.");
        }
    }

    public async Task<(LawFirmUserOperationOutcome Outcome, Guid? AssignmentId, string? Error)> AssignOrganizationUserRoleAsync(
        Guid organizationId, Guid tenantId, Guid userId, string roleCode, CancellationToken ct = default)
    {
        if (!_isEnabled)
            return (LawFirmUserOperationOutcome.ServiceUnavailable, null, "Identity service is not configured.");

        try
        {
            using var client = BuildIdentityClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            using var response = await client.PostAsJsonAsync(
                $"api/internal/organizations/{organizationId}/users/{userId}/product-roles",
                new { tenantId, roleCode },
                cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                var (outcome, _, error) = await FailureFromResponseAsync<object>(response, cts.Token);
                return (outcome, null, error);
            }

            var result = await response.Content.ReadFromJsonAsync<AssignRoleResponse>(cancellationToken: cts.Token);
            return (LawFirmUserOperationOutcome.Success, result?.AssignmentId, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Law-firm user role assignment timed out for organization {OrganizationId}.", organizationId);
            return (LawFirmUserOperationOutcome.ServiceUnavailable, null, "Identity service timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Law-firm user role assignment failed for organization {OrganizationId}.", organizationId);
            return (LawFirmUserOperationOutcome.ServiceUnavailable, null, "Identity service call failed.");
        }
    }

    public async Task<(LawFirmUserOperationOutcome Outcome, string? Error)> RevokeOrganizationUserRoleAsync(
        Guid organizationId, Guid userId, Guid assignmentId, CancellationToken ct = default)
    {
        if (!_isEnabled)
            return (LawFirmUserOperationOutcome.ServiceUnavailable, "Identity service is not configured.");

        try
        {
            using var client = BuildIdentityClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            using var response = await client.DeleteAsync(
                $"api/internal/organizations/{organizationId}/users/{userId}/product-roles/{assignmentId}", cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                var (outcome, _, error) = await FailureFromResponseAsync<object>(response, cts.Token);
                return (outcome, error);
            }

            return (LawFirmUserOperationOutcome.Success, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Law-firm user role revoke timed out for organization {OrganizationId}.", organizationId);
            return (LawFirmUserOperationOutcome.ServiceUnavailable, "Identity service timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Law-firm user role revoke failed for organization {OrganizationId}.", organizationId);
            return (LawFirmUserOperationOutcome.ServiceUnavailable, "Identity service call failed.");
        }
    }

    private static async Task<(LawFirmUserOperationOutcome Outcome, T? Value, string? Error)> FailureFromResponseAsync<T>(
        HttpResponseMessage response, CancellationToken ct) where T : class
    {
        string? error = null;
        try
        {
            var body = await response.Content.ReadFromJsonAsync<ErrorCodeResponse>(cancellationToken: ct);
            error = body?.Error ?? body?.Code;
        }
        catch
        {
            // Non-JSON or empty error body — fall through with a generic message.
        }

        var outcome = response.StatusCode switch
        {
            System.Net.HttpStatusCode.NotFound => LawFirmUserOperationOutcome.NotFound,
            System.Net.HttpStatusCode.Conflict => LawFirmUserOperationOutcome.Conflict,
            System.Net.HttpStatusCode.BadRequest => LawFirmUserOperationOutcome.BadRequest,
            System.Net.HttpStatusCode.Forbidden => LawFirmUserOperationOutcome.BadRequest,
            System.Net.HttpStatusCode.UnprocessableEntity => LawFirmUserOperationOutcome.BadRequest,
            _ => LawFirmUserOperationOutcome.ServiceUnavailable,
        };

        return (outcome, null, error ?? $"Identity service returned {(int)response.StatusCode}.");
    }

    private sealed class OrgUserListResponse
    {
        [JsonPropertyName("items")]
        public List<OrgUserListItemDto>? Items { get; set; }
    }

    private sealed class OrgUserListItemDto
    {
        [JsonPropertyName("userId")]
        public Guid UserId { get; set; }

        [JsonPropertyName("email")]
        public string Email { get; set; } = string.Empty;

        [JsonPropertyName("firstName")]
        public string FirstName { get; set; } = string.Empty;

        [JsonPropertyName("lastName")]
        public string LastName { get; set; } = string.Empty;

        [JsonPropertyName("isActive")]
        public bool IsActive { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("roles")]
        public List<OrgUserRoleDto>? Roles { get; set; }
    }

    private sealed class OrgUserRoleDto
    {
        [JsonPropertyName("assignmentId")]
        public Guid AssignmentId { get; set; }

        [JsonPropertyName("roleCode")]
        public string? RoleCode { get; set; }
    }

    private sealed class InviteOrgUserResponse
    {
        [JsonPropertyName("userId")]
        public Guid UserId { get; set; }

        [JsonPropertyName("invitationId")]
        public Guid? InvitationId { get; set; }

        [JsonPropertyName("email")]
        public string Email { get; set; } = string.Empty;

        [JsonPropertyName("isNew")]
        public bool IsNew { get; set; }
    }

    private sealed class AssignRoleResponse
    {
        [JsonPropertyName("assignmentId")]
        public Guid AssignmentId { get; set; }
    }
}
