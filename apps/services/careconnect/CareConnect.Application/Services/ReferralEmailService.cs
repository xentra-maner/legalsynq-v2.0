using System.Security.Cryptography;
using System.Text;
using BuildingBlocks.DataGovernance;
using CareConnect.Application.DTOs;
using CareConnect.Application.Interfaces;
using CareConnect.Application.Repositories;
using CareConnect.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CareConnect.Application.Services;

/// <summary>
/// LSCC-005 / LSCC-005-01 / LSCC-005-02 / LSCC-01-002: Implements secure token generation and
/// email notification dispatch for the referral flow.
///
/// Token format (URL-safe Base64, LSCC-005-01):
///   {referralId}:{tokenVersion}:{expiryUnixSeconds}:{hmacHex}
///
/// The HMAC-SHA256 covers "{referralId}:{tokenVersion}:{expiryUnixSeconds}" using a
/// secret key from configuration key "ReferralToken:Secret". Falls back to a development
/// constant when the key is absent (NOT suitable for production).
///
/// Token revocation: incrementing a referral's TokenVersion invalidates all previously
/// issued tokens. ValidateViewToken returns the embedded version; callers must verify
/// it matches the referral's current TokenVersion.
///
/// Email strategy: a CareConnectNotification DB record is always written first
/// (Pending status). An SMTP send is then attempted; success → Sent (AttemptCount++),
/// failure → Failed (AttemptCount++, FailureReason stored). Never a silent failure.
/// </summary>
public class ReferralEmailService : IReferralEmailService
{
    private const int TokenExpiryDays      = 30;
    private readonly INotificationRepository       _notifications;
    private readonly INotificationsProducer        _producer;
    private readonly ILogger<ReferralEmailService> _logger;
    private readonly ITenantServiceClient          _tenantClient;
    private readonly ITenantSubdomainCache         _subdomainCache;
    private readonly SemaphoreSlim _notificationUpdateLock = new(1, 1);
    private readonly ReferralRuntimeOptions _options;

    public ReferralEmailService(
        INotificationRepository       notifications,
        INotificationsProducer        producer,
        IConfiguration                configuration,
        ITenantServiceClient          tenantClient,
        ITenantSubdomainCache         subdomainCache,
        ILogger<ReferralEmailService> logger)
    {
        _notifications  = notifications;
        _producer       = producer;
        _logger         = logger;
        _tenantClient   = tenantClient;
        _subdomainCache = subdomainCache;
        _options        = ReferralRuntimeOptions.FromConfiguration(configuration);

        if (_options.UsingDevelopmentFallbackSecret)
        {
            _logger.LogWarning(
                "ReferralToken:Secret is not configured. Using development fallback — DO NOT use this in production.");
        }
    }

    // ── Tenant URL helper ─────────────────────────────────────────────────────
    //
    // Builds a tenant-branded URL:
    //   AppBaseDomain configured → https://{subdomain}.{AppBaseDomain}{path}
    //   AppBaseDomain empty / subdomain unavailable → {AppBaseUrl}{path}
    //
    // Subdomain is sourced from ITenantSubdomainCache (Singleton), so the result
    // survives across DI scopes and request lifetimes.

    private async Task<string> BuildTenantUrlAsync(
        Guid tenantId, string path, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_options.AppBaseDomain))
            return _options.AppBaseUrl + path;

        if (!_subdomainCache.TryGetValue(tenantId, out var subdomain))
        {
            subdomain = await _tenantClient.GetSubdomainAsync(tenantId, ct);
            if (!string.IsNullOrWhiteSpace(subdomain))
                _subdomainCache.TryAdd(tenantId, subdomain!);
        }

        if (string.IsNullOrWhiteSpace(subdomain))
        {
            _logger.LogDebug(
                "ReferralEmailService: subdomain not found for tenant {TenantId} — using AppBaseUrl fallback.",
                tenantId);
            return _options.AppBaseUrl + path;
        }

        return $"https://{subdomain}.{_options.AppBaseDomain}{path}";
    }

    // ── Token helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// LSCC-005-01: Generates a 4-part HMAC-signed view token.
    /// Format: {referralId}:{tokenVersion}:{expiry}:{hmacHex}, Base64url-encoded.
    /// The version is embedded so revocation can be detected without a DB lookup
    /// at the HMAC validation step.
    /// </summary>
    public string GenerateViewToken(Guid referralId, int tokenVersion)
    {
        var expiry  = DateTimeOffset.UtcNow.AddDays(TokenExpiryDays).ToUnixTimeSeconds();
        var payload = $"{referralId}:{tokenVersion}:{expiry}";
        var sig     = ComputeHmac(payload);
        var raw     = $"{payload}:{sig}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw))
                      .TrimEnd('=')
                      .Replace('+', '-')
                      .Replace('/', '_');
    }

    /// <summary>
    /// LSCC-005-01: Validates a view token. Returns a ViewTokenValidationResult containing
    /// the referralId and the embedded tokenVersion. Returns null if the token is
    /// invalid, tampered with, or expired.
    ///
    /// The caller must also verify that result.TokenVersion matches the referral's
    /// current TokenVersion to detect revoked tokens.
    /// </summary>
    public ReferralTokenValidationOutcome ValidateViewTokenDetailed(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return ReferralTokenValidationOutcome.Failure(ReferralTokenFailureReasons.Missing);

        try
        {
            var padded  = token.Replace('-', '+').Replace('_', '/');
            var mod     = padded.Length % 4;
            if (mod != 0) padded += new string('=', 4 - mod);
            var raw     = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            var parts   = raw.Split(':');

            // LSCC-005-01: 4-part format: referralId:version:expiry:hmac
            if (parts.Length != 4)
                return ReferralTokenValidationOutcome.Failure(ReferralTokenFailureReasons.Malformed);

            var referralId   = Guid.Parse(parts[0]);
            var tokenVersion = int.Parse(parts[1]);
            var expiry       = long.Parse(parts[2]);
            var sig          = parts[3];

            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiry)
            {
                _logger.LogInformation("Referral view token expired for referral {ReferralId}", referralId);
                return ReferralTokenValidationOutcome.Failure(
                    ReferralTokenFailureReasons.Expired,
                    referralId,
                    tokenVersion);
            }

            var expectedSig = ComputeHmac($"{referralId}:{tokenVersion}:{expiry}");
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(sig),
                    Encoding.UTF8.GetBytes(expectedSig)))
            {
                _logger.LogWarning("Referral view token HMAC mismatch — possible tampering.");
                return ReferralTokenValidationOutcome.Failure(
                    ReferralTokenFailureReasons.Malformed,
                    referralId,
                    tokenVersion);
            }

            return ReferralTokenValidationOutcome.Success(referralId, tokenVersion);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse referral view token.");
            return ReferralTokenValidationOutcome.Failure(ReferralTokenFailureReasons.Malformed);
        }
    }

    public ViewTokenValidationResult? ValidateViewToken(string token)
    {
        var detailed = ValidateViewTokenDetailed(token);
        return detailed.IsValid && detailed.ReferralId.HasValue && detailed.TokenVersion.HasValue
            ? new ViewTokenValidationResult(detailed.ReferralId.Value, detailed.TokenVersion.Value)
            : null;
    }

    private string ComputeHmac(string payload)
    {
        var keyBytes     = Encoding.UTF8.GetBytes(_options.TokenSecret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        using var hmac   = new HMACSHA256(keyBytes);
        return Convert.ToHexString(hmac.ComputeHash(payloadBytes)).ToLowerInvariant();
    }

    private async Task<string> BuildProviderEntryLinkAsync(
        Referral referral,
        Provider provider,
        string token,
        CancellationToken ct)
    {
        var path = $"/referrals/thread?token={token}";
        return await BuildTenantUrlAsync(referral.TenantId, path, ct);
    }

    // ── Email dispatch ────────────────────────────────────────────────────────

    public Task SendNewReferralNotificationAsync(
        Referral          referral,
        Provider          provider,
        CancellationToken ct = default)
        => SendNewReferralNotificationAsync(referral, provider, null, ct);

    public async Task SendNewReferralNotificationAsync(
        Referral          referral,
        Provider          provider,
        string?           treatmentTypeName,
        CancellationToken ct = default)
    {
        var token = GenerateViewToken(referral.Id, referral.TokenVersion);

        // Build both tenant URLs in parallel — independent lookups.
        var urls = await Task.WhenAll(
            BuildProviderEntryLinkAsync(referral, provider, token, ct),
            BuildTenantUrlAsync(referral.TenantId, $"/referrals/firm-status?token={token}", ct));
        var providerEntryLink = urls[0];
        var firmStatusLink = urls[1];

        var tasks = new List<Task>();

        // ── Provider notification ──────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(provider.Email))
        {
            var dedupeKey = $"referral:{referral.Id}:created:provider";
            var subject   = $"New referral from {referral.ReferrerFirmName}";
            var body      = BuildNewReferralEmailHtml(referral, provider, providerEntryLink, treatmentTypeName);

            var notification = CareConnectNotification.Create(
                tenantId:          referral.TenantId,
                notificationType:  NotificationType.ReferralCreated,
                relatedEntityType: NotificationRelatedEntityType.Referral,
                relatedEntityId:   referral.Id,
                recipientType:     NotificationRecipientType.Provider,
                recipientAddress:  provider.Email,
                subject:           subject,
                message:           providerEntryLink,
                scheduledForUtc:   null,
                createdByUserId:   referral.CreatedByUserId,
                triggerSource:     NotificationSource.Initial,
                dedupeKey:         dedupeKey);

            if (await _notifications.TryAddWithDedupeAsync(notification, ct))
                // LSCC-005-02: schedule retry on failure (attempt 1 → retry after 5 min)
                tasks.Add(TrySendAndUpdateAsync(notification, provider.Email, subject, body, ct,
                    nextRetryAfterUtcOnFailure: ReferralRetryPolicy.GetNextRetryAfter(1)));
            else
                _logger.LogInformation("Duplicate new-referral provider notification skipped for referral {ReferralId}.", referral.Id);
        }
        else
        {
            _logger.LogWarning(
                "Cannot send new-referral provider notification: provider {ProviderId} has no email address.",
                provider.Id);
        }

        // ── Referrer submission confirmation ───────────────────────────────
        // LSCC-005-03: Independent of provider path — referrer always receives
        // their submission confirmation as long as ReferrerEmail is present.
        if (!string.IsNullOrWhiteSpace(referral.ReferrerEmail))
        {
            var refDedupeKey = $"referral:{referral.Id}:created:referrer";
            var refSubject   = $"Referral submitted — {referral.ClientFirstName} {referral.ClientLastName}";
            var refBody      = BuildReferrerSubmissionHtml(referral, provider, firmStatusLink, treatmentTypeName);

            var refNotif = CareConnectNotification.Create(
                tenantId:          referral.TenantId,
                notificationType:  NotificationType.ReferralCreated,
                relatedEntityType: NotificationRelatedEntityType.Referral,
                relatedEntityId:   referral.Id,
                recipientType:     NotificationRecipientType.InternalUser,
                recipientAddress:  referral.ReferrerEmail,
                subject:           refSubject,
                message:           firmStatusLink,
                scheduledForUtc:   null,
                createdByUserId:   referral.CreatedByUserId,
                triggerSource:     NotificationSource.Initial,
                dedupeKey:         refDedupeKey);

            if (await _notifications.TryAddWithDedupeAsync(refNotif, ct))
                tasks.Add(TrySendAndUpdateAsync(refNotif, referral.ReferrerEmail, refSubject, refBody, ct,
                    nextRetryAfterUtcOnFailure: ReferralRetryPolicy.GetNextRetryAfter(1)));
            else
                _logger.LogInformation("Duplicate new-referral referrer notification skipped for referral {ReferralId}.", referral.Id);
        }

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// LSCC-005-01: Resends the provider notification email for an existing referral.
    /// Creates a new notification record (type: ReferralEmailResent) and sends the
    /// email with a fresh token using the referral's CURRENT TokenVersion.
    /// Old revoked tokens (lower version) cannot be reinstated by resend.
    /// </summary>
    public async Task ResendNewReferralNotificationAsync(
        Referral referral,
        Provider provider,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(provider.Email))
        {
            _logger.LogWarning(
                "Cannot resend referral notification: provider {ProviderId} has no email address.",
                provider.Id);
            return;
        }

        var token      = GenerateViewToken(referral.Id, referral.TokenVersion);
        var providerEntryLink = await BuildProviderEntryLinkAsync(referral, provider, token, ct);
        var subject    = $"Referral (resent) — {referral.ClientFirstName} {referral.ClientLastName}";
        var body       = BuildNewReferralEmailHtml(referral, provider, providerEntryLink);

        var notification = CareConnectNotification.Create(
            tenantId:          referral.TenantId,
            notificationType:  NotificationType.ReferralEmailResent,
            relatedEntityType: NotificationRelatedEntityType.Referral,
            relatedEntityId:   referral.Id,
            recipientType:     NotificationRecipientType.Provider,
            recipientAddress:  provider.Email,
            subject:           subject,
            message:           providerEntryLink,
            scheduledForUtc:   null,
            createdByUserId:   null,
            triggerSource:     NotificationSource.ManualResend);

        await _notifications.AddAsync(notification, ct);

        _logger.LogInformation(
            "Resending referral notification for referral {ReferralId} (TokenVersion={Version}) to {EmailMasked}.",
            referral.Id, referral.TokenVersion, PiiGuard.MaskEmail(provider.Email));

        await TrySendAndUpdateAsync(notification, provider.Email, subject, body, ct);
    }

    /// <summary>
    /// CC2-INT-B03: PROVIDER_ASSIGNED event — fires when a provider is explicitly assigned
    /// to an existing referral (e.g. re-assignment or delayed assignment after creation).
    /// Routes through the platform Notifications service exactly like all other events.
    /// </summary>
    public async Task SendProviderAssignedNotificationAsync(
        Referral referral,
        Provider provider,
        Guid?    actingUserId,
        string   dedupeKeySuffix = "",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(provider.Email))
        {
            _logger.LogWarning(
                "Cannot send provider-assigned notification: provider {ProviderId} has no email address.",
                provider.Id);
            return;
        }

        var dedupeKey  = $"referral:{referral.Id}:provider_assigned:{provider.Id}{dedupeKeySuffix}";
        var token      = GenerateViewToken(referral.Id, referral.TokenVersion);
        var providerEntryLink = await BuildProviderEntryLinkAsync(referral, provider, token, ct);
        var subject    = $"You have been assigned a referral — {referral.ClientFirstName} {referral.ClientLastName}";
        var body       = BuildProviderAssignedEmailHtml(referral, provider, providerEntryLink);

        var notification = CareConnectNotification.Create(
            tenantId:          referral.TenantId,
            notificationType:  NotificationType.ReferralProviderAssigned,
            relatedEntityType: NotificationRelatedEntityType.Referral,
            relatedEntityId:   referral.Id,
            recipientType:     NotificationRecipientType.Provider,
            recipientAddress:  provider.Email,
            subject:           subject,
            message:           providerEntryLink,
            scheduledForUtc:   null,
            createdByUserId:   actingUserId,
            triggerSource:     NotificationSource.Initial,
            dedupeKey:         dedupeKey);

        if (!await _notifications.TryAddWithDedupeAsync(notification, ct))
        {
            _logger.LogInformation(
                "Duplicate provider-assigned notification skipped for referral {ReferralId}.", referral.Id);
            return;
        }

        await TrySendAndUpdateAsync(notification, provider.Email, subject, body, ct,
            nextRetryAfterUtcOnFailure: ReferralRetryPolicy.GetNextRetryAfter(1));
    }

    public async Task SendAcceptanceConfirmationsAsync(
        Referral referral,
        Provider provider,
        CancellationToken ct = default)
    {
        var tasks = new List<Task>();
        var dedupePrefix = $"referral:{referral.Id}:accepted";

        if (!string.IsNullOrWhiteSpace(provider.Email))
        {
            var provDedupeKey = $"{dedupePrefix}:provider";
            var provSubject = $"Referral accepted — {referral.ClientFirstName} {referral.ClientLastName}";
            var provBody    = BuildProviderAcceptanceHtml(referral, provider);

            var provNotif = CareConnectNotification.Create(
                tenantId:          referral.TenantId,
                notificationType:  NotificationType.ReferralAcceptedProvider,
                relatedEntityType: NotificationRelatedEntityType.Referral,
                relatedEntityId:   referral.Id,
                recipientType:     NotificationRecipientType.Provider,
                recipientAddress:  provider.Email,
                subject:           provSubject,
                message:           null,
                scheduledForUtc:   null,
                createdByUserId:   null,
                triggerSource:     NotificationSource.Initial,
                dedupeKey:         provDedupeKey);

            if (await _notifications.TryAddWithDedupeAsync(provNotif, ct))
            {
                tasks.Add(TrySendAndUpdateAsync(provNotif, provider.Email, provSubject, provBody, ct,
                    nextRetryAfterUtcOnFailure: ReferralRetryPolicy.GetNextRetryAfter(1)));
            }
        }
        else
        {
            _logger.LogWarning(
                "Skipping provider acceptance email: provider {ProviderId} has no email address.",
                provider.Id);
        }

        if (!string.IsNullOrWhiteSpace(referral.ReferrerEmail))
        {
            var refDedupeKey    = $"{dedupePrefix}:referrer";
            var refSubject      = $"Your referral was accepted — {referral.ClientFirstName} {referral.ClientLastName}";
            var acceptToken     = GenerateViewToken(referral.Id, referral.TokenVersion);
            var acceptStatusUrl = await BuildTenantUrlAsync(referral.TenantId, $"/referrals/firm-status?token={acceptToken}", ct);
            var refBody         = BuildReferrerAcceptanceHtml(referral, provider, acceptStatusUrl);

            var refNotif = CareConnectNotification.Create(
                tenantId:          referral.TenantId,
                notificationType:  NotificationType.ReferralAcceptedReferrer,
                relatedEntityType: NotificationRelatedEntityType.Referral,
                relatedEntityId:   referral.Id,
                recipientType:     NotificationRecipientType.InternalUser,
                recipientAddress:  referral.ReferrerEmail,
                subject:           refSubject,
                message:           null,
                scheduledForUtc:   null,
                createdByUserId:   null,
                triggerSource:     NotificationSource.Initial,
                dedupeKey:         refDedupeKey);

            if (await _notifications.TryAddWithDedupeAsync(refNotif, ct))
            {
                tasks.Add(TrySendAndUpdateAsync(refNotif, referral.ReferrerEmail, refSubject, refBody, ct,
                    nextRetryAfterUtcOnFailure: ReferralRetryPolicy.GetNextRetryAfter(1)));
            }
        }
        else
        {
            _logger.LogWarning(
                "Skipping referrer acceptance email for referral {ReferralId}: no ReferrerEmail stored.",
                referral.Id);
        }

        if (!string.IsNullOrWhiteSpace(referral.ClientEmail))
        {
            var clientDedupeKey = $"{dedupePrefix}:client";
            var clientSubject = $"Your case has been accepted — {provider.OrganizationName ?? provider.Name}";
            var clientBody    = BuildClientAcceptanceHtml(referral, provider);

            var clientNotif = CareConnectNotification.Create(
                tenantId:          referral.TenantId,
                notificationType:  NotificationType.ReferralAcceptedClient,
                relatedEntityType: NotificationRelatedEntityType.Referral,
                relatedEntityId:   referral.Id,
                recipientType:     NotificationRecipientType.ClientEmail,
                recipientAddress:  referral.ClientEmail,
                subject:           clientSubject,
                message:           null,
                scheduledForUtc:   null,
                createdByUserId:   null,
                triggerSource:     NotificationSource.Initial,
                dedupeKey:         clientDedupeKey);

            if (await _notifications.TryAddWithDedupeAsync(clientNotif, ct))
            {
                tasks.Add(TrySendAndUpdateAsync(clientNotif, referral.ClientEmail, clientSubject, clientBody, ct,
                    nextRetryAfterUtcOnFailure: ReferralRetryPolicy.GetNextRetryAfter(1)));
            }
        }
        else
        {
            _logger.LogWarning(
                "Skipping client acceptance email for referral {ReferralId}: no ClientEmail stored. " +
                "Acceptance is not blocked — provider and referrer have been notified.",
                referral.Id);
        }

        await Task.WhenAll(tasks);
    }

    // ── CCX-002: Rejection notifications ─────────────────────────────────────

    public async Task SendRejectionNotificationsAsync(
        Referral referral,
        Provider provider,
        Guid? actingUserId,
        CancellationToken ct = default)
    {
        var tasks = new List<Task>();

        var dedupePrefix = $"referral:{referral.Id}:declined";

        if (!string.IsNullOrWhiteSpace(provider.Email))
        {
            var provDedupeKey = $"{dedupePrefix}:provider";
            var provSubject = $"Referral declined — {referral.ClientFirstName} {referral.ClientLastName}";
            var provBody    = BuildProviderRejectionHtml(referral, provider);

            var provNotif = CareConnectNotification.Create(
                tenantId:          referral.TenantId,
                notificationType:  NotificationType.ReferralRejectedProvider,
                relatedEntityType: NotificationRelatedEntityType.Referral,
                relatedEntityId:   referral.Id,
                recipientType:     NotificationRecipientType.Provider,
                recipientAddress:  provider.Email,
                subject:           provSubject,
                message:           null,
                scheduledForUtc:   null,
                createdByUserId:   actingUserId,
                triggerSource:     NotificationSource.Initial,
                dedupeKey:         provDedupeKey);

            if (await _notifications.TryAddWithDedupeAsync(provNotif, ct))
            {
                tasks.Add(TrySendAndUpdateAsync(provNotif, provider.Email, provSubject, provBody, ct,
                    nextRetryAfterUtcOnFailure: ReferralRetryPolicy.GetNextRetryAfter(1)));
            }
        }
        else
        {
            _logger.LogWarning(
                "Skipping provider rejection email for referral {ReferralId}: provider {ProviderId} has no email address.",
                referral.Id, provider.Id);
        }

        if (!string.IsNullOrWhiteSpace(referral.ReferrerEmail))
        {
            var refDedupeKey    = $"{dedupePrefix}:referrer";
            var refSubject      = $"Your referral was declined — {referral.ClientFirstName} {referral.ClientLastName}";
            var rejectToken     = GenerateViewToken(referral.Id, referral.TokenVersion);
            var rejectStatusUrl = await BuildTenantUrlAsync(referral.TenantId, $"/referrals/firm-status?token={rejectToken}", ct);
            var refBody         = BuildReferrerRejectionHtml(referral, provider, rejectStatusUrl);

            var refNotif = CareConnectNotification.Create(
                tenantId:          referral.TenantId,
                notificationType:  NotificationType.ReferralRejectedReferrer,
                relatedEntityType: NotificationRelatedEntityType.Referral,
                relatedEntityId:   referral.Id,
                recipientType:     NotificationRecipientType.InternalUser,
                recipientAddress:  referral.ReferrerEmail,
                subject:           refSubject,
                message:           null,
                scheduledForUtc:   null,
                createdByUserId:   actingUserId,
                triggerSource:     NotificationSource.Initial,
                dedupeKey:         refDedupeKey);

            if (await _notifications.TryAddWithDedupeAsync(refNotif, ct))
            {
                tasks.Add(TrySendAndUpdateAsync(refNotif, referral.ReferrerEmail, refSubject, refBody, ct,
                    nextRetryAfterUtcOnFailure: ReferralRetryPolicy.GetNextRetryAfter(1)));
            }
        }

        await Task.WhenAll(tasks);
    }

    // ── CCX-002: Cancellation notifications ───────────────────────────────────

    public async Task SendCancellationNotificationsAsync(
        Referral referral,
        Provider provider,
        Guid? actingUserId,
        CancellationToken ct = default)
    {
        var tasks = new List<Task>();

        var dedupePrefix = $"referral:{referral.Id}:cancelled";

        if (!string.IsNullOrWhiteSpace(provider.Email))
        {
            var provDedupeKey = $"{dedupePrefix}:provider";
            var provSubject = $"Referral cancelled — {referral.ClientFirstName} {referral.ClientLastName}";
            var provBody    = BuildProviderCancellationHtml(referral, provider);

            var provNotif = CareConnectNotification.Create(
                tenantId:          referral.TenantId,
                notificationType:  NotificationType.ReferralCancelledProvider,
                relatedEntityType: NotificationRelatedEntityType.Referral,
                relatedEntityId:   referral.Id,
                recipientType:     NotificationRecipientType.Provider,
                recipientAddress:  provider.Email,
                subject:           provSubject,
                message:           null,
                scheduledForUtc:   null,
                createdByUserId:   actingUserId,
                triggerSource:     NotificationSource.Initial,
                dedupeKey:         provDedupeKey);

            if (await _notifications.TryAddWithDedupeAsync(provNotif, ct))
            {
                tasks.Add(TrySendAndUpdateAsync(provNotif, provider.Email, provSubject, provBody, ct,
                    nextRetryAfterUtcOnFailure: ReferralRetryPolicy.GetNextRetryAfter(1)));
            }
        }
        else
        {
            _logger.LogWarning(
                "Skipping provider cancellation email for referral {ReferralId}: provider {ProviderId} has no email address.",
                referral.Id, provider.Id);
        }

        if (!string.IsNullOrWhiteSpace(referral.ReferrerEmail))
        {
            var refDedupeKey   = $"{dedupePrefix}:referrer";
            var refSubject     = $"Your referral was cancelled — {referral.ClientFirstName} {referral.ClientLastName}";
            var cancelToken    = GenerateViewToken(referral.Id, referral.TokenVersion);
            var cancelStatusUrl = await BuildTenantUrlAsync(referral.TenantId, $"/referrals/firm-status?token={cancelToken}", ct);
            var refBody        = BuildReferrerCancellationHtml(referral, provider, cancelStatusUrl);

            var refNotif = CareConnectNotification.Create(
                tenantId:          referral.TenantId,
                notificationType:  NotificationType.ReferralCancelledReferrer,
                relatedEntityType: NotificationRelatedEntityType.Referral,
                relatedEntityId:   referral.Id,
                recipientType:     NotificationRecipientType.InternalUser,
                recipientAddress:  referral.ReferrerEmail,
                subject:           refSubject,
                message:           null,
                scheduledForUtc:   null,
                createdByUserId:   actingUserId,
                triggerSource:     NotificationSource.Initial,
                dedupeKey:         refDedupeKey);

            if (await _notifications.TryAddWithDedupeAsync(refNotif, ct))
            {
                tasks.Add(TrySendAndUpdateAsync(refNotif, referral.ReferrerEmail, refSubject, refBody, ct,
                    nextRetryAfterUtcOnFailure: ReferralRetryPolicy.GetNextRetryAfter(1)));
            }
        }

        await Task.WhenAll(tasks);
    }

    // ── Retry (LSCC-005-02) ───────────────────────────────────────────────────

    /// <summary>
    /// LSCC-005-02: Re-attempts an email send for an existing failed notification record.
    /// Called exclusively by <c>ReferralEmailRetryWorker</c>.
    ///
    /// Rebuilds the email body from the current referral/provider state (ensuring any
    /// token revocation is reflected). Updates the same notification record in-place —
    /// no new record is created.
    ///
    /// On success: notification.Status → Sent, NextRetryAfterUtc cleared.
    /// On failure: notification.Status remains Failed, NextRetryAfterUtc updated if
    ///   further retries are available, or cleared if MaxAttempts is reached.
    /// </summary>
    public async Task RetryNotificationAsync(
        CareConnectNotification notification,
        Referral                referral,
        Provider                provider,
        CancellationToken       ct = default)
    {
        string subject, body, toAddress;

        switch (notification.NotificationType)
        {
            case NotificationType.ReferralCreated:
            case NotificationType.ReferralEmailAutoRetry:
            {
                if (string.IsNullOrWhiteSpace(provider.Email))
                {
                    _logger.LogWarning(
                        "RetryNotificationAsync: provider {ProviderId} has no email. Clearing retry for notification {Id}.",
                        provider.Id, notification.Id);
                    notification.ClearRetrySchedule();
                    await UpdateNotificationAsync(notification, ct);
                    return;
                }
                var token      = GenerateViewToken(referral.Id, referral.TokenVersion);
                var providerEntryLink = await BuildProviderEntryLinkAsync(referral, provider, token, ct);
                subject   = $"New referral from {referral.ReferrerFirmName}";
                body      = BuildNewReferralEmailHtml(referral, provider, providerEntryLink);
                toAddress = provider.Email;
                break;
            }
            case NotificationType.ReferralAcceptedProvider:
            {
                if (string.IsNullOrWhiteSpace(provider.Email))
                {
                    notification.ClearRetrySchedule();
                    await UpdateNotificationAsync(notification, ct);
                    return;
                }
                subject   = $"Referral accepted — {referral.ClientFirstName} {referral.ClientLastName}";
                body      = BuildProviderAcceptanceHtml(referral, provider);
                toAddress = provider.Email;
                break;
            }
            case NotificationType.ReferralAcceptedReferrer:
            {
                toAddress = notification.RecipientAddress ?? string.Empty;
                if (string.IsNullOrWhiteSpace(toAddress))
                {
                    notification.ClearRetrySchedule();
                    await UpdateNotificationAsync(notification, ct);
                    return;
                }
                var retryAcceptToken  = GenerateViewToken(referral.Id, referral.TokenVersion);
                var retryAcceptStatus = await BuildTenantUrlAsync(referral.TenantId, $"/referrals/firm-status?token={retryAcceptToken}", ct);
                subject = $"Your referral was accepted — {referral.ClientFirstName} {referral.ClientLastName}";
                body    = BuildReferrerAcceptanceHtml(referral, provider, retryAcceptStatus);
                break;
            }
            // LSCC-01-002: client acceptance email retry
            case NotificationType.ReferralAcceptedClient:
            {
                toAddress = notification.RecipientAddress ?? string.Empty;
                if (string.IsNullOrWhiteSpace(toAddress))
                {
                    notification.ClearRetrySchedule();
                    await UpdateNotificationAsync(notification, ct);
                    return;
                }
                subject = $"Your case has been accepted — {provider.OrganizationName ?? provider.Name}";
                body    = BuildClientAcceptanceHtml(referral, provider);
                break;
            }
            case NotificationType.ReferralRejectedProvider:
            {
                if (string.IsNullOrWhiteSpace(provider.Email))
                {
                    notification.ClearRetrySchedule();
                    await UpdateNotificationAsync(notification, ct);
                    return;
                }
                subject   = $"Referral declined — {referral.ClientFirstName} {referral.ClientLastName}";
                body      = BuildProviderRejectionHtml(referral, provider);
                toAddress = provider.Email;
                break;
            }
            case NotificationType.ReferralRejectedReferrer:
            {
                toAddress = notification.RecipientAddress ?? string.Empty;
                if (string.IsNullOrWhiteSpace(toAddress))
                {
                    notification.ClearRetrySchedule();
                    await UpdateNotificationAsync(notification, ct);
                    return;
                }
                var retryRejectToken  = GenerateViewToken(referral.Id, referral.TokenVersion);
                var retryRejectStatus = await BuildTenantUrlAsync(referral.TenantId, $"/referrals/firm-status?token={retryRejectToken}", ct);
                subject = $"Your referral was declined — {referral.ClientFirstName} {referral.ClientLastName}";
                body    = BuildReferrerRejectionHtml(referral, provider, retryRejectStatus);
                break;
            }
            case NotificationType.ReferralCancelledProvider:
            {
                if (string.IsNullOrWhiteSpace(provider.Email))
                {
                    notification.ClearRetrySchedule();
                    await UpdateNotificationAsync(notification, ct);
                    return;
                }
                subject   = $"Referral cancelled — {referral.ClientFirstName} {referral.ClientLastName}";
                body      = BuildProviderCancellationHtml(referral, provider);
                toAddress = provider.Email;
                break;
            }
            case NotificationType.ReferralCancelledReferrer:
            {
                toAddress = notification.RecipientAddress ?? string.Empty;
                if (string.IsNullOrWhiteSpace(toAddress))
                {
                    notification.ClearRetrySchedule();
                    await UpdateNotificationAsync(notification, ct);
                    return;
                }
                var retryCancelToken  = GenerateViewToken(referral.Id, referral.TokenVersion);
                var retryCancelStatus = await BuildTenantUrlAsync(referral.TenantId, $"/referrals/firm-status?token={retryCancelToken}", ct);
                subject = $"Your referral was cancelled — {referral.ClientFirstName} {referral.ClientLastName}";
                body    = BuildReferrerCancellationHtml(referral, provider, retryCancelStatus);
                break;
            }
            case NotificationType.ReferralProviderAssigned:
            {
                if (string.IsNullOrWhiteSpace(provider.Email))
                {
                    notification.ClearRetrySchedule();
                    await UpdateNotificationAsync(notification, ct);
                    return;
                }
                var token      = GenerateViewToken(referral.Id, referral.TokenVersion);
                var providerEntryLink = await BuildProviderEntryLinkAsync(referral, provider, token, ct);
                subject   = $"You have been assigned a referral — {referral.ClientFirstName} {referral.ClientLastName}";
                body      = BuildProviderAssignedEmailHtml(referral, provider, providerEntryLink);
                toAddress = provider.Email;
                break;
            }
            default:
                _logger.LogWarning(
                    "RetryNotificationAsync: unsupported type '{Type}' for notification {Id}. Clearing retry.",
                    notification.NotificationType, notification.Id);
                notification.ClearRetrySchedule();
                await UpdateNotificationAsync(notification, ct);
                return;
        }

        // The next-retry time is calculated AFTER this attempt succeeds/fails.
        // AttemptCount will be incremented by MarkSent/MarkFailed, so we calculate
        // GetNextRetryAfter for (currentAttemptCount + 1) which is what it will be post-failure.
        var nextRetryAfterUtcOnFailure = ReferralRetryPolicy.GetNextRetryAfter(notification.AttemptCount + 1);

        _logger.LogInformation(
            "RetryNotificationAsync: notification {Id} (type={Type}, attempt={Attempt}/{Max}) for referral {ReferralId}.",
            notification.Id, notification.NotificationType,
            notification.AttemptCount + 1, ReferralRetryPolicy.MaxAttempts, referral.Id);

        await TrySendAndUpdateAsync(notification, toAddress, subject, body, ct, nextRetryAfterUtcOnFailure);
    }

    public async Task SendCommentNotificationAsync(
        Referral        referral,
        ReferralComment comment,
        CancellationToken ct = default)
    {
        var token          = GenerateViewToken(referral.Id, referral.TokenVersion);
        var providerEntryLink = referral.Provider is not null
            ? await BuildProviderEntryLinkAsync(referral, referral.Provider, token, ct)
            : await BuildTenantUrlAsync(referral.TenantId, $"/referrals/thread?token={token}", ct);
        var threadLink     = await BuildTenantUrlAsync(referral.TenantId, $"/referrals/thread?token={token}", ct);
        var firmStatusLink = await BuildTenantUrlAsync(referral.TenantId, $"/referrals/firm-status?token={token}", ct);

        var provName = referral.Provider is not null
            ? (string.IsNullOrWhiteSpace(referral.Provider.OrganizationName)
                ? referral.Provider.Name
                : referral.Provider.OrganizationName)
            : "Provider";

        var isFromReferrer = comment.SenderType == "referrer";

        // Notify the other party — use role-specific link in notification email
        string? toAddress;
        string  recipientLabel;
        string  notifLink;
        if (isFromReferrer)
        {
            // referrer posted → notify provider with canonical provider entry link
            toAddress      = referral.Provider?.Email;
            recipientLabel = provName;
            notifLink      = providerEntryLink;
        }
        else
        {
            // provider posted → notify referrer with firm-status link
            toAddress      = referral.ReferrerEmail;
            recipientLabel = referral.ReferrerName ?? "the law firm";
            notifLink      = firmStatusLink;
        }

        if (string.IsNullOrWhiteSpace(toAddress)) return;

        var subject  = $"New message on referral — {referral.ClientFirstName} {referral.ClientLastName}";
        var body     = BuildCommentNotificationHtml(referral, comment, provName, notifLink);
        var dedupeKey = $"referral:{referral.Id}:comment:{comment.Id}";

        var recipientType = isFromReferrer
            ? NotificationRecipientType.Provider
            : NotificationRecipientType.InternalUser;

        var notification = CareConnectNotification.Create(
            tenantId:          referral.TenantId,
            notificationType:  NotificationType.ReferralCreated,
            relatedEntityType: NotificationRelatedEntityType.Referral,
            relatedEntityId:   referral.Id,
            recipientType:     recipientType,
            recipientAddress:  toAddress,
            subject:           subject,
            message:           notifLink,
            scheduledForUtc:   null,
            createdByUserId:   null,
            triggerSource:     NotificationSource.Initial,
            dedupeKey:         dedupeKey);

        if (!await _notifications.TryAddWithDedupeAsync(notification, ct)) return;
        await TrySendAndUpdateAsync(notification, toAddress, subject, body, ct);

        _logger.LogInformation(
            "ReferralThread: comment notification sent to {RecipientLabel} for referral {ReferralId}.",
            recipientLabel, referral.Id);
    }

    // ── Internal: submit to Notifications service + update domain status ─────

    private async Task TrySendAndUpdateAsync(
        CareConnectNotification notification,
        string toAddress,
        string subject,
        string body,
        CancellationToken ct,
        DateTime? nextRetryAfterUtcOnFailure = null)
    {
        var eventKey       = NotificationTypeToEventKey(notification.NotificationType);
        var idempotencyKey = notification.DedupeKey ?? notification.Id.ToString();
        var correlationId  = notification.RelatedEntityId.ToString();

        try
        {
            // LS-NOTIF-CORE-023: route through platform Notifications service.
            // Submission success → mark domain record Sent.
            // Actual delivery and per-delivery retry are owned by Notifications service.
            await _producer.SubmitAsync(
                tenantId:      notification.TenantId,
                eventKey:      eventKey,
                toAddress:     toAddress,
                subject:       subject,
                htmlBody:      body,
                idempotencyKey: idempotencyKey,
                correlationId:  correlationId,
                ct:            ct);

            notification.MarkSent();
            await UpdateNotificationAsync(notification, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Notification submission failed for {NotificationId} (event={EventKey}) to {Recipient}. " +
                "Domain record marked Failed; ReferralEmailRetryWorker will re-submit.",
                notification.Id, eventKey, toAddress);
            // LSCC-005-02: pass nextRetryAfterUtc so the retry worker knows when to re-submit
            notification.MarkFailed(ex.Message, nextRetryAfterUtcOnFailure);
            try { await UpdateNotificationAsync(notification, ct); }
            catch (Exception saveEx)
            {
                _logger.LogError(saveEx,
                    "Also failed to persist failure state for notification {NotificationId}.",
                    notification.Id);
            }
        }
    }

    private async Task UpdateNotificationAsync(CareConnectNotification notification, CancellationToken ct)
    {
        await _notificationUpdateLock.WaitAsync(ct);
        try
        {
            await _notifications.UpdateAsync(notification, ct);
        }
        finally
        {
            _notificationUpdateLock.Release();
        }
    }

    /// <summary>
    /// LS-NOTIF-CORE-023: Maps a CareConnect domain NotificationType constant to the
    /// canonical eventKey used in the platform Notifications service producer contract.
    /// </summary>
    private static string NotificationTypeToEventKey(string notificationType) =>
        notificationType switch
        {
            NotificationType.ReferralCreated           => "referral.created",
            NotificationType.ReferralProviderAssigned  => "referral.provider_assigned",
            NotificationType.ReferralEmailResent       => "referral.invite.resent",
            NotificationType.ReferralEmailAutoRetry    => "referral.invite.retry",
            NotificationType.ReferralAcceptedProvider => "referral.accepted.provider",
            NotificationType.ReferralAcceptedReferrer => "referral.accepted.referrer",
            NotificationType.ReferralAcceptedClient   => "referral.accepted.client",
            NotificationType.ReferralRejectedProvider => "referral.declined.provider",
            NotificationType.ReferralRejectedReferrer => "referral.declined.referrer",
            NotificationType.ReferralCancelledProvider => "referral.cancelled.provider",
            NotificationType.ReferralCancelledReferrer => "referral.cancelled.referrer",
            _                                          => "careconnect.notification",
        };

    // ── HTML email templates ──────────────────────────────────────────────────

    // ── Layout helpers ────────────────────────────────────────────────────────

    /// <summary>Single table row; skipped when value is null/empty.</summary>
    private static string Row(string label, string? value, bool bold = false)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var v = bold ? $"<strong>{value}</strong>" : value;
        return $"""<tr><td style="padding:10px 0;border-bottom:1px solid #f0f0f0;color:#6b7280;width:160px;vertical-align:top;font-size:14px">{label}</td><td style="padding:10px 0;border-bottom:1px solid #f0f0f0;font-size:14px;color:#111">{v}</td></tr>""";
    }

    /// <summary>
    /// Formats the referral's resolved location (facility-first, provider-fallback — see
    /// <see cref="ReferralLocationResolver"/>) as "Address, City, State ZIP", skipping any
    /// missing parts. Returns null when no address is available.
    /// </summary>
    private static string? FormatLocation(Referral r, Provider p)
    {
        var location   = ReferralLocationResolver.Resolve(r, p);
        var addressLine = string.Join(", ", new[] { location.AddressLine1, location.City, location.State }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
        var withZip = string.IsNullOrWhiteSpace(location.PostalCode)
            ? addressLine
            : $"{addressLine} {location.PostalCode}".Trim();

        return string.IsNullOrWhiteSpace(withZip) ? null : withZip;
    }

    private static string? ReferralOriginationName(Referral r)
    {
        var firstName = r.ReferralAttribution?.FirstName?.Trim();
        var lastName = r.ReferralAttribution?.LastName?.Trim();
        var name = string.Join(" ", new[] { firstName, lastName }.Where(part => !string.IsNullOrWhiteSpace(part)));
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>Titled section with orange-gradient rule and a data table.</summary>
    private static string Section(string title, string rows)
        => string.IsNullOrEmpty(rows) ? "" : $"""
            <div style="margin-top:28px">
              <h3 style="margin:0 0 6px;color:#1a56db;font-size:13px;font-weight:700;letter-spacing:.06em;text-transform:uppercase">{title}</h3>
              <div style="height:2px;background:linear-gradient(to right,#e05e26,#f9a825);margin-bottom:4px"></div>
              <table style="border-collapse:collapse;width:100%">{rows}</table>
            </div>
            """;

    /// <summary>Shared email chrome: outer wrapper, dark-navy header, white card, optional footer callout.</summary>
    private static string Wrap(string headerTitle, string bodyHtml, string? footerHtml = null)
    {
        var footer = footerHtml is not null
            ? $"""<tr><td style="padding:0 32px 32px"><div style="background:#f8f9fa;border-left:4px solid #e05e26;border-radius:4px;padding:14px 16px;font-size:13px;color:#4b5563;font-style:italic">{footerHtml}</div></td></tr>"""
            : "";
        return $"""
            <!DOCTYPE html>
            <html>
            <head><meta charset="UTF-8"><meta name="viewport" content="width=device-width,initial-scale=1"></head>
            <body style="margin:0;padding:0;background:#f1f5f9;font-family:Arial,Helvetica,sans-serif">
              <table width="100%" cellpadding="0" cellspacing="0" style="background:#f1f5f9">
                <tr><td align="center" style="padding:24px 12px">
                  <table width="640" cellpadding="0" cellspacing="0" style="max-width:640px;width:100%;background:#fff;border-radius:8px;overflow:hidden;box-shadow:0 1px 4px rgba(0,0,0,.10)">
                    <tr><td style="background:#1e3160;padding:28px 32px">
                      <span style="color:#fff;font-size:20px;font-weight:700;letter-spacing:-.01em">{headerTitle}</span>
                    </td></tr>
                    <tr><td style="padding:28px 32px 24px">
                      {bodyHtml}
                    </td></tr>
                    {footer}
                  </table>
                </td></tr>
              </table>
            </body>
            </html>
            """;
    }

    // ── Template builders ─────────────────────────────────────────────────────

    private static string BuildNewReferralEmailHtml(Referral r, Provider p, string entryLink, string? treatmentTypeName = null)
    {
        var provName       = string.IsNullOrWhiteSpace(p.OrganizationName) ? p.Name : p.OrganizationName;
        var firmName       = r.ReferrerFirmName;
        var referrerLabel  = firmName ?? r.ReferrerName ?? "the referring party";
        var clientDob      = r.ClientDob.HasValue ? r.ClientDob.Value.ToString("yyyy-MM-dd") : null;
        var ctaLabel       = "View Referral";
        var introCopy      = "Use the button below to review this referral in CareConnect.";

        var clientRows =
            Row("Full Name",         $"{r.ClientFirstName} {r.ClientLastName}".Trim(), bold: true) +
            Row("Phone",             string.IsNullOrWhiteSpace(r.ClientPhone) ? null : r.ClientPhone) +
            Row("Date of Birth",     clientDob) +
            Row("Service",           r.RequestedService) +
            Row("Case #",            r.CaseNumber) +
            Row("Urgency",           r.Urgency) +
            Row("Date of Accident",  r.DateOfAccident?.ToString("yyyy-MM-dd")) +
            Row("Type of Treatment", treatmentTypeName);

        var referrerRows =
            Row("Name",     r.ReferrerName) +
            Row("Law Firm", firmName) +
            Row("Email",    r.ReferrerEmail is not null
                ? $"<a href='mailto:{r.ReferrerEmail}' style='color:#1a56db'>{r.ReferrerEmail}</a>"
                : null);

        var lienCompanyRows =
            Row("Name",  r.LienCompanyName) +
            Row("Email", r.LienCompanyEmail is not null
                ? $"<a href='mailto:{r.LienCompanyEmail}' style='color:#1a56db'>{r.LienCompanyEmail}</a>"
                : null);

        var notesBlock = r.Notes is not null
            ? $"""
              <div style="margin-top:28px">
                <h3 style="margin:0 0 6px;color:#1a56db;font-size:13px;font-weight:700;letter-spacing:.06em;text-transform:uppercase">Notes</h3>
                <div style="height:2px;background:linear-gradient(to right,#e05e26,#f9a825);margin-bottom:4px"></div>
                <p style="font-size:14px;color:#374151;margin:12px 0 0;white-space:pre-wrap">{r.Notes}</p>
              </div>
              """
            : "";

        var footer = $"This referral was sent on behalf of <strong>{referrerLabel}</strong>."
            + (r.ReferrerEmail is not null
                ? $" Please reply directly to <a href='mailto:{r.ReferrerEmail}' style='color:#1a56db'>{r.ReferrerEmail}</a> with any questions."
                : "");

        var locationLine = FormatLocation(r, p) is { } location
            ? $"""<p style="margin:12px 0 0;font-size:14px;color:#4b5563"><strong>Provider Location:</strong> {location}</p>"""
            : "";

        var body = $"""
            <p style="margin:0 0 16px;font-size:15px">Dear <strong>{provName}</strong>,</p>
            <p style="margin:0 0 4px;font-size:15px;color:#374151">
              Please find below a referral request from <strong>{referrerLabel}</strong>.
              Kindly schedule an appointment at your earliest convenience.
            </p>
            <p style="margin:12px 0 4px;font-size:14px;color:#4b5563">{introCopy}</p>
            {locationLine}
            {Section("Client Information", clientRows)}
            {Section("Referring Case Manager", referrerRows)}
            {Section("Lien Company", lienCompanyRows)}
            {notesBlock}
            <p style="margin-top:28px">
              <a href="{entryLink}" style="display:inline-block;background:#1a56db;color:#fff;padding:12px 28px;border-radius:6px;text-decoration:none;font-weight:700;font-size:14px">{ctaLabel}</a>
            </p>
            <p style="margin-top:12px;font-size:11px;color:#9ca3af">This link expires in 30 days.</p>
            """;

        return Wrap($"{provName} \u2013 Referral Request", body, footer);
    }

    private static string BuildReferrerSubmissionHtml(Referral r, Provider p, string viewLink, string? treatmentTypeName = null)
    {
        var provName   = string.IsNullOrWhiteSpace(p.OrganizationName) ? p.Name : p.OrganizationName;
        var greeting   = r.ReferrerName is { Length: > 0 } n ? $"Dear <strong>{n}</strong>," : "Hello,";
        var firmName   = r.ReferrerFirmName;

        var patientRows =
            Row("Full Name",         $"{r.ClientFirstName} {r.ClientLastName}".Trim(), bold: true) +
            Row("Service",           r.RequestedService) +
            Row("Urgency",           r.Urgency) +
            Row("Date of Accident",  r.DateOfAccident?.ToString("yyyy-MM-dd")) +
            Row("Type of Treatment", treatmentTypeName) +
            Row("Referral Origination", ReferralOriginationName(r)) +
            Row("Notes",             r.Notes);

        var providerRows =
            Row("Provider", provName, bold: true) +
            Row("Provider Location", FormatLocation(r, p)) +
            Row("Contact",  string.IsNullOrWhiteSpace(p.Phone) ? null : p.Phone);

        var lienCompanyRows =
            Row("Name",  r.LienCompanyName) +
            Row("Email", r.LienCompanyEmail is not null
                ? $"<a href='mailto:{r.LienCompanyEmail}' style='color:#1a56db'>{r.LienCompanyEmail}</a>"
                : null);

        var footer = firmName is not null
            ? $"This referral was submitted on behalf of <strong>{firmName}</strong>."
            : "Your referral has been submitted successfully.";

        var body = $"""
            <p style="margin:0 0 16px;font-size:15px">{greeting}</p>
            <p style="margin:0 0 4px;font-size:15px;color:#374151">
              Your referral for <strong>{r.ClientFirstName} {r.ClientLastName}</strong>
              has been sent to <strong>{provName}</strong>.
              The provider has been notified and will reach out to coordinate care.
            </p>
            {Section("Patient Details", patientRows)}
            {Section("Provider", providerRows)}
            {Section("Lien Company", lienCompanyRows)}
            <div style="text-align:center;margin:28px 0">
              <a href="{viewLink}" style="display:inline-block;background:#1a56db;color:#fff;padding:12px 28px;border-radius:6px;text-decoration:none;font-weight:700;font-size:14px">Track Referral Status</a>
            </div>
            <p style="margin:0;font-size:13px;color:#6b7280">Use the link above to check the referral status and send messages to the provider. For full access to all your referrals in one place, upgrade to the CareConnect portal.</p>
            """;

        return Wrap("Referral Submitted", body, footer);
    }

    private static string BuildProviderAssignedEmailHtml(Referral r, Provider p, string entryLink)
    {
        var provName      = string.IsNullOrWhiteSpace(p.OrganizationName) ? p.Name : p.OrganizationName;
        var firmName      = r.ReferrerFirmName;
        var referrerLabel = firmName ?? r.ReferrerName ?? "the referring party";
        var clientDob     = r.ClientDob.HasValue ? r.ClientDob.Value.ToString("yyyy-MM-dd") : null;
        var ctaLabel      = "View Referral";

        var clientRows =
            Row("Full Name",     $"{r.ClientFirstName} {r.ClientLastName}".Trim(), bold: true) +
            Row("Phone",         string.IsNullOrWhiteSpace(r.ClientPhone) ? null : r.ClientPhone) +
            Row("Date of Birth", clientDob) +
            Row("Service",       r.RequestedService) +
            Row("Case #",        r.CaseNumber) +
            Row("Urgency",           r.Urgency) +
            Row("Date of Accident",  r.DateOfAccident?.ToString("yyyy-MM-dd"));

        var referrerRows =
            Row("Name",     r.ReferrerName) +
            Row("Law Firm", firmName) +
            Row("Email",    r.ReferrerEmail is not null
                ? $"<a href='mailto:{r.ReferrerEmail}' style='color:#1a56db'>{r.ReferrerEmail}</a>"
                : null);

        var lienCompanyRows =
            Row("Name",  r.LienCompanyName) +
            Row("Email", r.LienCompanyEmail is not null
                ? $"<a href='mailto:{r.LienCompanyEmail}' style='color:#1a56db'>{r.LienCompanyEmail}</a>"
                : null);

        var notesBlock = r.Notes is not null
            ? $"""
              <div style="margin-top:28px">
                <h3 style="margin:0 0 6px;color:#1a56db;font-size:13px;font-weight:700;letter-spacing:.06em;text-transform:uppercase">Notes</h3>
                <div style="height:2px;background:linear-gradient(to right,#e05e26,#f9a825);margin-bottom:4px"></div>
                <p style="font-size:14px;color:#374151;margin:12px 0 0;white-space:pre-wrap">{r.Notes}</p>
              </div>
              """
            : "";

        var footer = $"This referral was sent on behalf of <strong>{referrerLabel}</strong>."
            + (r.ReferrerEmail is not null
                ? $" Please reply to <a href='mailto:{r.ReferrerEmail}' style='color:#1a56db'>{r.ReferrerEmail}</a> with any questions."
                : "");

        var locationLine = FormatLocation(r, p) is { } location
            ? $"""<p style="margin:12px 0 0;font-size:14px;color:#4b5563"><strong>Provider Location:</strong> {location}</p>"""
            : "";

        var body = $"""
            <p style="margin:0 0 16px;font-size:15px">Dear <strong>{provName}</strong>,</p>
            <p style="margin:0 0 4px;font-size:15px;color:#374151">
              A referral from <strong>{referrerLabel}</strong> has been assigned to you.
              Please review the details below and schedule an appointment at your earliest convenience.
            </p>
            {locationLine}
            {Section("Client Information", clientRows)}
            {Section("Referring Case Manager", referrerRows)}
            {Section("Lien Company", lienCompanyRows)}
            {notesBlock}
            <p style="margin-top:28px">
              <a href="{entryLink}" style="display:inline-block;background:#1a56db;color:#fff;padding:12px 28px;border-radius:6px;text-decoration:none;font-weight:700;font-size:14px">{ctaLabel}</a>
            </p>
            <p style="margin-top:12px;font-size:11px;color:#9ca3af">This link expires in 30 days.</p>
            """;

        return Wrap($"{provName} \u2013 Referral Assigned", body, footer);
    }

    private static string BuildProviderAcceptanceHtml(Referral r, Provider p)
    {
        var provName = string.IsNullOrWhiteSpace(p.OrganizationName) ? p.Name : p.OrganizationName;

        var summaryRows =
            Row("Patient",  $"{r.ClientFirstName} {r.ClientLastName}".Trim(), bold: true) +
            Row("Service",  r.RequestedService) +
            Row("Referrer", r.ReferrerName) +
            Row("Case #",   r.CaseNumber);

        var body = $"""
            <p style="margin:0 0 16px;font-size:15px">Dear <strong>{provName}</strong>,</p>
            <p style="margin:0 0 4px;font-size:15px;color:#374151">
              You have successfully accepted the referral below.
              The referring party has been notified and you may begin coordinating care directly.
            </p>
            {Section("Referral Summary", summaryRows)}
            """;

        return Wrap("Referral Accepted", body);
    }

    private static string BuildReferrerAcceptanceHtml(Referral r, Provider p, string statusLink)
    {
        var provName  = string.IsNullOrWhiteSpace(p.OrganizationName) ? p.Name : p.OrganizationName;
        var greeting  = r.ReferrerName is { Length: > 0 } n ? $"Dear <strong>{n}</strong>," : "Hello,";

        var summaryRows =
            Row("Patient",  $"{r.ClientFirstName} {r.ClientLastName}".Trim(), bold: true) +
            Row("Service",  r.RequestedService) +
            Row("Provider", provName, bold: true) +
            Row("Referral Origination", ReferralOriginationName(r)) +
            Row("Case #",   r.CaseNumber);

        var footer = $"<strong>{provName}</strong> will be in touch with your client to continue coordinating care.";

        var body = $"""
            <p style="margin:0 0 16px;font-size:15px">{greeting}</p>
            <p style="margin:0 0 4px;font-size:15px;color:#374151">
              Great news — <strong>{provName}</strong> has accepted your referral for
              <strong>{r.ClientFirstName} {r.ClientLastName}</strong>.
            </p>
            {Section("Referral Summary", summaryRows)}
            <div style="text-align:center;margin:28px 0">
              <a href="{statusLink}" style="display:inline-block;background:#1a56db;color:#fff;padding:12px 28px;border-radius:6px;text-decoration:none;font-weight:700;font-size:14px">View Referral Status</a>
            </div>
            """;

        return Wrap("Your Referral Was Accepted", body, footer);
    }

    private static string BuildClientAcceptanceHtml(Referral r, Provider p)
    {
        var provName = string.IsNullOrWhiteSpace(p.OrganizationName) ? p.Name : p.OrganizationName;

        var body = $"""
            <p style="margin:0 0 16px;font-size:15px">Dear <strong>{r.ClientFirstName}</strong>,</p>
            <p style="margin:0 0 4px;font-size:15px;color:#374151">
              We are pleased to let you know that your case for
              <strong>{r.RequestedService}</strong> has been accepted by <strong>{provName}</strong>.
            </p>
            <p style="margin-top:16px;font-size:15px;color:#374151">
              <strong>{provName}</strong> will be reaching out to you directly to discuss next steps.
            </p>
            """;

        var footer = "If you have any questions in the meantime, please contact the party who referred you.";

        return Wrap("Your Case Has Been Accepted", body, footer);
    }

    private static string BuildProviderRejectionHtml(Referral r, Provider p)
    {
        var provName = string.IsNullOrWhiteSpace(p.OrganizationName) ? p.Name : p.OrganizationName;

        var summaryRows =
            Row("Patient", $"{r.ClientFirstName} {r.ClientLastName}".Trim(), bold: true) +
            Row("Service", r.RequestedService) +
            Row("Case #",  r.CaseNumber);

        var body = $"""
            <p style="margin:0 0 16px;font-size:15px">Dear <strong>{provName}</strong>,</p>
            <p style="margin:0 0 4px;font-size:15px;color:#374151">
              You have declined the referral below. The referring party has been notified of your decision.
            </p>
            {Section("Referral Summary", summaryRows)}
            """;

        return Wrap("Referral Declined", body);
    }

    private static string BuildReferrerRejectionHtml(Referral r, Provider p, string statusLink)
    {
        var provName = string.IsNullOrWhiteSpace(p.OrganizationName) ? p.Name : p.OrganizationName;
        var greeting = r.ReferrerName is { Length: > 0 } n ? $"Dear <strong>{n}</strong>," : "Hello,";

        var summaryRows =
            Row("Patient",  $"{r.ClientFirstName} {r.ClientLastName}".Trim(), bold: true) +
            Row("Service",  r.RequestedService) +
            Row("Provider", provName) +
            Row("Referral Origination", ReferralOriginationName(r)) +
            Row("Case #",   r.CaseNumber);

        var footer = $"You may search for an alternative provider or contact <strong>{provName}</strong> directly for more information.";

        var body = $"""
            <p style="margin:0 0 16px;font-size:15px">{greeting}</p>
            <p style="margin:0 0 4px;font-size:15px;color:#374151">
              Unfortunately, <strong>{provName}</strong> has declined your referral for
              <strong>{r.ClientFirstName} {r.ClientLastName}</strong>.
            </p>
            {Section("Referral Summary", summaryRows)}
            <div style="text-align:center;margin:28px 0">
              <a href="{statusLink}" style="display:inline-block;background:#6b7280;color:#fff;padding:12px 28px;border-radius:6px;text-decoration:none;font-weight:700;font-size:14px">View Referral Status</a>
            </div>
            """;

        return Wrap("Your Referral Was Declined", body, footer);
    }

    private static string BuildProviderCancellationHtml(Referral r, Provider p)
    {
        var provName = string.IsNullOrWhiteSpace(p.OrganizationName) ? p.Name : p.OrganizationName;

        var summaryRows =
            Row("Patient", $"{r.ClientFirstName} {r.ClientLastName}".Trim(), bold: true) +
            Row("Service", r.RequestedService) +
            Row("Case #",  r.CaseNumber);

        var body = $"""
            <p style="margin:0 0 16px;font-size:15px">Dear <strong>{provName}</strong>,</p>
            <p style="margin:0 0 4px;font-size:15px;color:#374151">
              The referral below has been cancelled. No further action is required on your part.
            </p>
            {Section("Referral Summary", summaryRows)}
            """;

        return Wrap("Referral Cancelled", body);
    }

    private static string BuildReferrerCancellationHtml(Referral r, Provider p, string statusLink)
    {
        var provName = string.IsNullOrWhiteSpace(p.OrganizationName) ? p.Name : p.OrganizationName;
        var greeting = r.ReferrerName is { Length: > 0 } n ? $"Dear <strong>{n}</strong>," : "Hello,";

        var summaryRows =
            Row("Patient",  $"{r.ClientFirstName} {r.ClientLastName}".Trim(), bold: true) +
            Row("Service",  r.RequestedService) +
            Row("Provider", provName) +
            Row("Referral Origination", ReferralOriginationName(r)) +
            Row("Case #",   r.CaseNumber);

        var footer = "If this cancellation was unexpected, please contact the involved parties for more information.";

        var body = $"""
            <p style="margin:0 0 16px;font-size:15px">{greeting}</p>
            <p style="margin:0 0 4px;font-size:15px;color:#374151">
              Your referral for <strong>{r.ClientFirstName} {r.ClientLastName}</strong>
              to <strong>{provName}</strong> has been cancelled.
            </p>
            {Section("Referral Summary", summaryRows)}
            <div style="text-align:center;margin:28px 0">
              <a href="{statusLink}" style="display:inline-block;background:#6b7280;color:#fff;padding:12px 28px;border-radius:6px;text-decoration:none;font-weight:700;font-size:14px">View Referral Status</a>
            </div>
            """;

        return Wrap("Your Referral Was Cancelled", body, footer);
    }

    private static string BuildCommentNotificationHtml(Referral r, ReferralComment comment, string provName, string threadLink)
    {
        var isFromReferrer = comment.SenderType == "referrer";
        var senderLabel    = isFromReferrer ? (r.ReferrerName ?? "the law firm") : provName;
        var recipientLabel = isFromReferrer ? provName : (r.ReferrerName ?? "the law firm");

        var summaryRows =
            Row("Patient",  $"{r.ClientFirstName} {r.ClientLastName}".Trim(), bold: true) +
            Row("Service",  r.RequestedService) +
            Row("From",     senderLabel) +
            Row("Referral Origination", isFromReferrer ? null : ReferralOriginationName(r));

        var footer = "Reply directly in the referral thread using the button above.";
        IReadOnlyCollection<ReferralAttachment> commentAttachments = comment.Attachments ?? [];
        var attachmentCount = commentAttachments.Count;
        var attachmentHtml = attachmentCount == 0
            ? string.Empty
            : $"""
              <p style="margin:0 0 16px;font-size:14px;color:#374151">
                {attachmentCount} attachment{(attachmentCount == 1 ? "" : "s")} included:
                {System.Net.WebUtility.HtmlEncode(string.Join(", ", commentAttachments.Select(a => a.FileName)))}
              </p>
              """;

        var body = $"""
            <p style="margin:0 0 16px;font-size:15px">Hello,</p>
            <p style="margin:0 0 16px;font-size:15px;color:#374151">
              <strong>{senderLabel}</strong> sent a message regarding the referral for
              <strong>{r.ClientFirstName} {r.ClientLastName}</strong>.
            </p>
            <div style="background:#f3f4f6;border-left:4px solid #1a56db;padding:14px 18px;border-radius:4px;margin:0 0 24px">
              <p style="margin:0;font-size:14px;color:#111827;line-height:1.6">{System.Net.WebUtility.HtmlEncode(comment.Message)}</p>
            </div>
            {attachmentHtml}
            {Section("Referral Details", summaryRows)}
            <div style="text-align:center;margin:28px 0">
              <a href="{threadLink}" style="display:inline-block;background:#1a56db;color:#fff;padding:12px 28px;border-radius:6px;text-decoration:none;font-weight:700;font-size:14px">View Thread &amp; Reply</a>
            </div>
            """;

        return Wrap($"New message from {senderLabel}", body, footer);
    }
}
