using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BuildingBlocks.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Identity.Infrastructure.Services;

/// <summary>
/// LS-NOTIF-CORE-024: Sends transactional identity emails via the canonical
/// Notifications service endpoint (<c>POST /v1/notifications</c>).
///
/// <para>
/// Replaces the old <c>POST /internal/send-email</c> passthrough. Identity
/// is now a first-class Notifications producer:
/// <list type="bullet">
///   <item><c>productKey = "identity"</c>, <c>sourceSystem = "identity-service"</c></item>
///   <item><c>eventKey</c> = <c>identity.user.password.reset</c> or <c>identity.user.invite.sent</c></item>
///   <item>Auth via <c>NotificationsAuthDelegatingHandler</c> (service JWT when configured,
///         legacy <c>X-Tenant-Id</c> fallback otherwise)</item>
/// </list>
/// </para>
///
/// <para>
/// Identity remains responsible for token generation, link construction, and
/// inline HTML rendering. Notifications handles delivery, retry, provider
/// routing, and observability.
/// </para>
///
/// <para>
/// When <c>NotificationsService:BaseUrl</c> is not configured, both methods
/// return <c>EmailConfigured = false</c> and log an Error-level entry so the
/// misconfiguration is visible in aggregated logs. Callers should surface this
/// as an error rather than a silent fallback.
/// </para>
/// </summary>
public interface INotificationsEmailClient
{
    /// <summary>
    /// Dispatches a password-reset email to <paramref name="toEmail"/>.
    /// </summary>
    Task<(bool EmailConfigured, bool Success, string? Error)> SendPasswordResetEmailAsync(
        string            toEmail,
        string            displayName,
        string            resetLink,
        Guid              tenantId,
        CancellationToken ct = default);

    /// <summary>
    /// LS-ID-TNT-007: Dispatches a user-invitation email to <paramref name="toEmail"/>.
    /// Contains the activation link the invitee uses to set their password and
    /// activate their account via <c>POST /api/auth/accept-invite</c>.
    /// </summary>
    Task<(bool EmailConfigured, bool Success, string? Error)> SendInviteEmailAsync(
        string            toEmail,
        string            displayName,
        string            activationLink,
        Guid              tenantId,
        CancellationToken ct = default);

    /// <summary>
    /// Dispatches a CareConnect law-firm user invitation email.
    /// </summary>
    Task<(bool EmailConfigured, bool Success, string? Error)> SendCareConnectLawFirmInviteEmailAsync(
        string            toEmail,
        string            displayName,
        string            activationLink,
        Guid              tenantId,
        CancellationToken ct = default);

    /// <summary>
    /// Dispatches the approval and administrator-account setup email created by
    /// the tenant self-registration review flow.
    /// </summary>
    Task<(bool EmailConfigured, bool Success, string? Error)> SendTenantRegistrationApprovedEmailAsync(
        string            toEmail,
        string            displayName,
        string            tenantName,
        string            activationLink,
        int               expiryHours,
        Guid              tenantId,
        CancellationToken ct = default);

    /// <summary>
    /// Dispatches a tenant-access-granted notification email to an existing active user
    /// who has been provisioned on an additional tenant. No set-password link is included
    /// since the user already has a password. Best-effort — callers should log but not
    /// fail on a false return value.
    /// </summary>
    Task<(bool EmailConfigured, bool Success, string? Error)> SendTenantAccessGrantedEmailAsync(
        string            toEmail,
        string            displayName,
        string            tenantName,
        string            portalUrl,
        Guid              tenantId,
        CancellationToken ct = default);
}

public sealed class NotificationsEmailClient : INotificationsEmailClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private const string PasswordResetEventKey        = "identity.user.password.reset";
    private const string InviteEventKey               = "identity.user.invite.sent";
    private const string CareConnectLawFirmInviteEventKey = "identity.careconnect.law_firm.invite.sent";
    private const string TenantRegistrationApprovedEventKey = "identity.tenant.registration.approved";
    private const string TenantAccessGrantedEventKey  = "identity.user.tenant.access.granted";
    private const string PasswordResetSubject         = "Reset your LegalSynq password";
    private const string InviteSubject                = "You've been invited to LegalSynq";
    private const string CareConnectLawFirmInviteSubject = "You've been invited to CareConnect";
    private const string TenantRegistrationApprovedSubject = "Your LegalSynq tenant application has been accepted";
    private const string TenantAccessGrantedSubject   = "You now have access to a new LegalSynq network";

    private readonly IHttpClientFactory                _httpClientFactory;
    private readonly NotificationsServiceOptions       _options;
    private readonly ILogger<NotificationsEmailClient> _logger;

    public NotificationsEmailClient(
        IHttpClientFactory                    httpClientFactory,
        IOptions<NotificationsServiceOptions> options,
        ILogger<NotificationsEmailClient>     logger)
    {
        _httpClientFactory = httpClientFactory;
        _options           = options.Value;
        _logger            = logger;
    }

    public Task<(bool EmailConfigured, bool Success, string? Error)> SendPasswordResetEmailAsync(
        string            toEmail,
        string            displayName,
        string            resetLink,
        Guid              tenantId,
        CancellationToken ct = default)
    {
        var body = new
        {
            type    = PasswordResetEventKey,
            subject = PasswordResetSubject,
            html    = BuildPasswordResetHtmlBody(displayName, resetLink),
            body    = $"Reset your LegalSynq password\n\nHello {displayName},\n\nClick the link below to reset your password (expires in 24 hours):\n{resetLink}\n\nIf you didn't request a password reset, you can safely ignore this email.",
        };

        var templateData = new Dictionary<string, string>
        {
            ["displayName"] = displayName,
            ["resetLink"]   = resetLink,
            ["subject"]     = PasswordResetSubject,
        };

        return SubmitAsync(
            eventKey:     PasswordResetEventKey,
            subject:      PasswordResetSubject,
            toEmail:      toEmail,
            tenantId:     tenantId,
            body:         body,
            templateData: templateData,
            logTag:       "LS-NOTIF-CORE-024/password-reset",
            ct:           ct);
    }

    public Task<(bool EmailConfigured, bool Success, string? Error)> SendInviteEmailAsync(
        string            toEmail,
        string            displayName,
        string            activationLink,
        Guid              tenantId,
        CancellationToken ct = default)
    {
        var body = new
        {
            type    = InviteEventKey,
            subject = InviteSubject,
            html    = BuildInviteHtmlBody(displayName, activationLink),
            body    = $"You've been invited to LegalSynq\n\nHello {displayName},\n\nAn administrator has invited you to join LegalSynq. Click the link below to accept (expires in 72 hours):\n{activationLink}\n\nIf you weren't expecting this invitation, you can safely ignore this email.",
        };

        var templateData = new Dictionary<string, string>
        {
            ["displayName"]    = displayName,
            ["activationLink"] = activationLink,
            ["subject"]        = InviteSubject,
        };

        return SubmitAsync(
            eventKey:     InviteEventKey,
            subject:      InviteSubject,
            toEmail:      toEmail,
            tenantId:     tenantId,
            body:         body,
            templateData: templateData,
            logTag:       "LS-NOTIF-CORE-024/invite",
            ct:           ct);
    }

    public Task<(bool EmailConfigured, bool Success, string? Error)> SendCareConnectLawFirmInviteEmailAsync(
        string            toEmail,
        string            displayName,
        string            activationLink,
        Guid              tenantId,
        CancellationToken ct = default)
    {
        var body = new
        {
            type    = CareConnectLawFirmInviteEventKey,
            subject = CareConnectLawFirmInviteSubject,
            html    = BuildCareConnectLawFirmInviteHtmlBody(displayName, activationLink),
            body    = $"You've been invited to CareConnect\n\nHello {displayName},\n\nYour law firm administrator has invited you to LegalSynq CareConnect. Set your password to activate your account and start managing referrals. This link expires in 72 hours:\n{activationLink}\n\nIf you weren't expecting this invitation, you can safely ignore this email.",
            attachments = LegalSynqEmailBranding.CreateInlineLogoAttachment(),
        };

        var templateData = new Dictionary<string, string>
        {
            ["displayName"]    = displayName,
            ["activationLink"] = activationLink,
            ["subject"]        = CareConnectLawFirmInviteSubject,
        };

        return SubmitAsync(
            eventKey:     CareConnectLawFirmInviteEventKey,
            subject:      CareConnectLawFirmInviteSubject,
            toEmail:      toEmail,
            tenantId:     tenantId,
            body:         body,
            templateData: templateData,
            logTag:       "careconnect-law-firm-invite",
            ct:           ct);
    }

    public Task<(bool EmailConfigured, bool Success, string? Error)> SendTenantRegistrationApprovedEmailAsync(
        string            toEmail,
        string            displayName,
        string            tenantName,
        string            activationLink,
        int               expiryHours,
        Guid              tenantId,
        CancellationToken ct = default)
    {
        var body = new
        {
            type    = TenantRegistrationApprovedEventKey,
            subject = TenantRegistrationApprovedSubject,
            html    = BuildTenantRegistrationApprovedHtmlBody(displayName, tenantName, activationLink, expiryHours),
            body    = $"Your LegalSynq tenant application has been accepted\n\nHello {displayName},\n\nGreat news — your application for {tenantName} has been accepted. We are now provisioning your LegalSynq tenant. Set your password to activate your tenant administrator account while setup continues (link expires in {expiryHours} hours):\n{activationLink}\n\nIf you did not submit this application, please contact LegalSynq support.",
            attachments = LegalSynqEmailBranding.CreateInlineLogoAttachment(),
        };

        var templateData = new Dictionary<string, string>
        {
            ["displayName"]    = displayName,
            ["tenantName"]     = tenantName,
            ["activationLink"] = activationLink,
            ["subject"]        = TenantRegistrationApprovedSubject,
        };

        return SubmitAsync(
            eventKey:     TenantRegistrationApprovedEventKey,
            subject:      TenantRegistrationApprovedSubject,
            toEmail:      toEmail,
            tenantId:     tenantId,
            body:         body,
            templateData: templateData,
            logTag:       "tenant-registration-approved",
            ct:           ct);
    }

    public Task<(bool EmailConfigured, bool Success, string? Error)> SendTenantAccessGrantedEmailAsync(
        string            toEmail,
        string            displayName,
        string            tenantName,
        string            portalUrl,
        Guid              tenantId,
        CancellationToken ct = default)
    {
        var body = new
        {
            type    = TenantAccessGrantedEventKey,
            subject = TenantAccessGrantedSubject,
            html    = BuildTenantAccessGrantedHtmlBody(displayName, tenantName, portalUrl),
            body    = $"You now have access to a new LegalSynq network\n\nHello {displayName},\n\nYou have been granted access to {tenantName} on LegalSynq. " +
                      $"Sign in using your existing credentials at:\n{portalUrl}\n\nIf you weren't expecting this, please contact your administrator.",
        };

        var templateData = new Dictionary<string, string>
        {
            ["displayName"] = displayName,
            ["tenantName"]  = tenantName,
            ["portalUrl"]   = portalUrl,
            ["subject"]     = TenantAccessGrantedSubject,
        };

        return SubmitAsync(
            eventKey:     TenantAccessGrantedEventKey,
            subject:      TenantAccessGrantedSubject,
            toEmail:      toEmail,
            tenantId:     tenantId,
            body:         body,
            templateData: templateData,
            logTag:       "LS-NOTIF-CORE-024/tenant-access-granted",
            ct:           ct);
    }

    // ── Core submission ───────────────────────────────────────────────────────

    private async Task<(bool EmailConfigured, bool Success, string? Error)> SubmitAsync(
        string            eventKey,
        string            subject,
        string            toEmail,
        Guid              tenantId,
        object            body,
        Dictionary<string, string> templateData,
        string            logTag,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            _logger.LogError(
                "[{Tag}] NotificationsService:BaseUrl is not configured. " +
                "Invitation/reset email for {Email} (tenant={TenantId}) will NOT be delivered. " +
                "Set NotificationsService:BaseUrl in configuration to enable email delivery.",
                logTag, toEmail, tenantId);
            return (EmailConfigured: false, Success: false, Error: "NotificationsService:BaseUrl is not configured.");
        }

        try
        {
            using var client = _httpClientFactory.CreateClient("NotificationsService");
            client.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
            client.Timeout     = TimeSpan.FromSeconds(_options.TimeoutSeconds);

            var request = new NotificationsProducerRequest
            {
                Channel        = NotificationTaxonomy.Channels.Email,
                ProductKey     = NotificationTaxonomy.Identity.ProductKey,
                EventKey       = eventKey,
                SourceSystem   = NotificationTaxonomy.Identity.SourceSystem,
                Subject        = subject,
                IdempotencyKey = Guid.CreateVersion7().ToString("N"),
                Recipient      = new NotificationsRecipient
                {
                    Email    = toEmail,
                    TenantId = tenantId.ToString(),
                },
                Message        = body,
                TemplateData   = templateData,
                Metadata       = new Dictionary<string, string>
                {
                    ["tenantId"] = tenantId.ToString(),
                },
            };

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/notifications");
            httpRequest.Headers.Add("X-Tenant-Id", tenantId.ToString());
            httpRequest.Content = JsonContent.Create(request, options: JsonOpts);

            using var response = await client.SendAsync(httpRequest, ct);

            if (response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync(ct);

                string? notifStatus = null;
                string? failureCategory = null;
                string? lastErrorMessage = null;
                string? notificationId = null;

                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(responseBody);
                    var root = doc.RootElement;
                    notifStatus      = root.TryGetProperty("status",           out var sp) ? sp.GetString() : null;
                    failureCategory  = root.TryGetProperty("failureCategory",  out var fp) ? fp.GetString() : null;
                    lastErrorMessage = root.TryGetProperty("lastErrorMessage", out var ep) ? ep.GetString() : null;
                    notificationId   = root.TryGetProperty("id",               out var ip) ? ip.GetString() : null;
                }
                catch { }

                if (notifStatus == "sent")
                {
                    _logger.LogInformation(
                        "[{Tag}] Email dispatched to {Email} (tenant={TenantId}) notificationId={NotificationId}.",
                        logTag, toEmail, tenantId, notificationId ?? "(unknown)");
                    return (EmailConfigured: true, Success: true, Error: null);
                }

                _logger.LogWarning(
                    "[{Tag}] Notifications service accepted but delivery did NOT complete. " +
                    "Status={Status} FailureCategory={FailureCategory} Error={Error} " +
                    "NotificationId={NotificationId} Email={Email} TenantId={TenantId}. " +
                    "Check the notifications service for delivery details.",
                    logTag, notifStatus ?? "unknown", failureCategory ?? "unknown",
                    lastErrorMessage ?? "(none)", notificationId ?? "(unknown)", toEmail, tenantId);

                return (
                    EmailConfigured: true,
                    Success:         false,
                    Error:           $"Notification delivery failed with status '{notifStatus ?? "unknown"}' " +
                                     $"({failureCategory ?? "unknown"}).");
            }

            var errorBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning(
                "[{Tag}] Notifications service returned HTTP {Status} for {Email} (tenant={TenantId}). Body: {Body}",
                logTag, (int)response.StatusCode, toEmail, tenantId,
                errorBody.Length > 500 ? errorBody[..500] : errorBody);
            return (
                EmailConfigured: true,
                Success:         false,
                Error:           $"Notifications service returned HTTP {(int)response.StatusCode}.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[{Tag}] Email dispatch threw for {Email} (tenant={TenantId}).",
                logTag, toEmail, tenantId);
            return (
                EmailConfigured: true,
                Success:         false,
                Error:           $"Email delivery error: {ex.GetType().Name}.");
        }
    }

    // ── Inline HTML templates ─────────────────────────────────────────────────
    // Identity owns template rendering until tenant templates are registered
    // in the Notifications template catalog (LS-NOTIF-CORE-022 follow-up).

    private static string BuildPasswordResetHtmlBody(string name, string link)
    {
        var safeName = HtmlEncode(name);
        var safeLink = HtmlEncode(link);

        return $"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1.0" />
          <title>Reset your LegalSynq password</title>
        </head>
        <body style="margin:0;padding:32px 0;background:#f9fafb;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;">
          <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:520px;margin:0 auto;">
            <tr>
              <td style="background:#ffffff;border-radius:12px;padding:40px;border:1px solid #e5e7eb;">

                <!-- Wordmark -->
                <p style="margin:0 0 4px;font-size:22px;font-weight:700;color:#111827;letter-spacing:-0.3px;">LegalSynq</p>
                <hr style="border:none;border-top:1px solid #f3f4f6;margin:16px 0 28px;" />

                <!-- Heading -->
                <h1 style="margin:0 0 12px;font-size:20px;font-weight:700;color:#111827;">Password reset request</h1>

                <!-- Body text -->
                <p style="margin:0 0 24px;font-size:15px;line-height:1.65;color:#374151;">
                  Hello <strong>{safeName}</strong>,<br /><br />
                  An administrator has requested a password reset for your
                  <strong>LegalSynq</strong> account. Click the button below to
                  set a new password. This link expires in&nbsp;24&nbsp;hours.
                </p>

                <!-- CTA button -->
                <table role="presentation" cellpadding="0" cellspacing="0" style="margin-bottom:28px;">
                  <tr>
                    <td style="border-radius:8px;background:#f97316;">
                      <a href="{safeLink}"
                         style="display:inline-block;padding:13px 28px;font-size:15px;font-weight:600;
                                color:#ffffff;text-decoration:none;border-radius:8px;">
                        Reset my password
                      </a>
                    </td>
                  </tr>
                </table>

                <!-- Plain-text link fallback -->
                <p style="margin:0 0 4px;font-size:13px;color:#6b7280;">Or copy and paste this link into your browser:</p>
                <p style="margin:0 0 28px;font-size:13px;color:#f97316;word-break:break-all;">
                  <a href="{safeLink}" style="color:#f97316;">{safeLink}</a>
                </p>

                <hr style="border:none;border-top:1px solid #f3f4f6;margin:0 0 20px;" />

                <!-- Footer -->
                <p style="margin:0;font-size:13px;line-height:1.5;color:#9ca3af;">
                  If you didn&rsquo;t request a password reset, you can safely ignore
                  this email. Your password will not change until you follow the
                  link above.
                </p>

              </td>
            </tr>
          </table>
        </body>
        </html>
        """;
    }

    private static string BuildInviteHtmlBody(string name, string link)
    {
        var safeName = HtmlEncode(name);
        var safeLink = HtmlEncode(link);

        return $"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1.0" />
          <title>You've been invited to LegalSynq</title>
        </head>
        <body style="margin:0;padding:32px 0;background:#f9fafb;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;">
          <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:520px;margin:0 auto;">
            <tr>
              <td style="background:#ffffff;border-radius:12px;padding:40px;border:1px solid #e5e7eb;">

                <!-- Wordmark -->
                <p style="margin:0 0 4px;font-size:22px;font-weight:700;color:#111827;letter-spacing:-0.3px;">LegalSynq</p>
                <hr style="border:none;border-top:1px solid #f3f4f6;margin:16px 0 28px;" />

                <!-- Heading -->
                <h1 style="margin:0 0 12px;font-size:20px;font-weight:700;color:#111827;">You've been invited</h1>

                <!-- Body text -->
                <p style="margin:0 0 24px;font-size:15px;line-height:1.65;color:#374151;">
                  Hello <strong>{safeName}</strong>,<br /><br />
                  An administrator has invited you to join <strong>LegalSynq</strong>.
                  Click the button below to set your password and activate your account.
                  This link expires in&nbsp;72&nbsp;hours.
                </p>

                <!-- CTA button -->
                <table role="presentation" cellpadding="0" cellspacing="0" style="margin-bottom:28px;">
                  <tr>
                    <td style="border-radius:8px;background:#f97316;">
                      <a href="{safeLink}"
                         style="display:inline-block;padding:13px 28px;font-size:15px;font-weight:600;
                                color:#ffffff;text-decoration:none;border-radius:8px;">
                        Accept invitation
                      </a>
                    </td>
                  </tr>
                </table>

                <!-- Plain-text link fallback -->
                <p style="margin:0 0 4px;font-size:13px;color:#6b7280;">Or copy and paste this link into your browser:</p>
                <p style="margin:0 0 28px;font-size:13px;color:#f97316;word-break:break-all;">
                  <a href="{safeLink}" style="color:#f97316;">{safeLink}</a>
                </p>

                <hr style="border:none;border-top:1px solid #f3f4f6;margin:0 0 20px;" />

                <!-- Footer -->
                <p style="margin:0;font-size:13px;line-height:1.5;color:#9ca3af;">
                  If you weren&rsquo;t expecting this invitation, you can safely ignore
                  this email. No account will be created unless you follow the link above.
                </p>

              </td>
            </tr>
          </table>
        </body>
        </html>
        """;
    }

    private static string BuildTenantRegistrationApprovedHtmlBody(string name, string tenantName, string link, int expiryHours)
    {
        var safeName       = HtmlEncode(name);
        var safeTenantName = HtmlEncode(tenantName);
        var safeLink       = HtmlEncode(link);

        return $"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1.0" />
          <title>Your LegalSynq tenant application has been accepted</title>
        </head>
        <body style="margin:0;padding:32px 16px;background:#f9fafb;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;">
          <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:520px;margin:0 auto;">
            <tr>
              <td style="background:#ffffff;border-radius:12px;padding:40px;border:1px solid #e5e7eb;">

                <img src="{LegalSynqEmailBranding.LogoSource}" width="145" alt="LegalSynq" style="display:block;width:145px;height:auto;border:0;margin:0 auto 16px;" />
                <hr style="border:none;border-top:1px solid #f3f4f6;margin:0 0 28px;" />

                <h1 style="margin:0 0 12px;font-size:20px;line-height:1.4;font-weight:700;color:#111827;">Your tenant application has been accepted</h1>

                <p style="margin:0 0 24px;font-size:15px;line-height:1.65;color:#374151;">
                  Hello <strong>{safeName}</strong>,<br /><br />
                  Great news &mdash; your application for <strong>{safeTenantName}</strong> has been accepted.
                  We are now provisioning your LegalSynq tenant. Set your password to activate your tenant
                  administrator account while setup continues. This secure link expires
                  in&nbsp;{expiryHours}&nbsp;hours.
                </p>

                <table role="presentation" cellpadding="0" cellspacing="0" style="margin-bottom:28px;">
                  <tr>
                    <td style="border-radius:8px;background:#f97316;">
                      <a href="{safeLink}"
                         style="display:inline-block;padding:13px 28px;font-size:15px;font-weight:600;color:#ffffff;text-decoration:none;border-radius:8px;">
                        Set up your account
                      </a>
                    </td>
                  </tr>
                </table>

                <hr style="border:none;border-top:1px solid #f3f4f6;margin:0 0 20px;" />
                <p style="margin:0;font-size:13px;line-height:1.5;color:#9ca3af;">
                  If you did not submit this tenant application, please contact LegalSynq support.
                </p>

              </td>
            </tr>
          </table>
        </body>
        </html>
        """;
    }

    private static string BuildCareConnectLawFirmInviteHtmlBody(string name, string link)
    {
        var safeName = HtmlEncode(name);
        var safeLink = HtmlEncode(link);

        return $"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1.0" />
          <title>You've been invited to CareConnect</title>
        </head>
        <body style="margin:0;padding:32px 16px;background:#f9fafb;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;">
          <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:520px;margin:0 auto;">
            <tr>
              <td style="background:#ffffff;border-radius:12px;padding:40px;border:1px solid #e5e7eb;">

                <img src="{LegalSynqEmailBranding.LogoSource}" width="145" alt="LegalSynq" style="display:block;width:145px;height:auto;border:0;margin:0 auto 16px;" />
                <hr style="border:none;border-top:1px solid #f3f4f6;margin:0 0 28px;" />

                <h1 style="margin:0 0 12px;font-size:20px;line-height:1.4;font-weight:700;color:#111827;">You've been invited to CareConnect</h1>

                <p style="margin:0 0 24px;font-size:15px;line-height:1.65;color:#374151;">
                  Hello <strong>{safeName}</strong>,<br /><br />
                  Your law firm administrator has invited you to <strong>LegalSynq CareConnect</strong>.
                  Set your password to activate your account and start managing referrals. This secure
                  link expires in&nbsp;72&nbsp;hours.
                </p>

                <table role="presentation" cellpadding="0" cellspacing="0" style="margin-bottom:28px;">
                  <tr>
                    <td style="border-radius:8px;background:#f97316;">
                      <a href="{safeLink}"
                         style="display:inline-block;padding:13px 28px;font-size:15px;font-weight:600;color:#ffffff;text-decoration:none;border-radius:8px;">
                        Activate CareConnect account
                      </a>
                    </td>
                  </tr>
                </table>

                <hr style="border:none;border-top:1px solid #f3f4f6;margin:0 0 20px;" />
                <p style="margin:0;font-size:13px;line-height:1.5;color:#9ca3af;">
                  If you weren&rsquo;t expecting this invitation, you can safely ignore this email.
                  No account will be activated unless you follow the link above.
                </p>

              </td>
            </tr>
          </table>
        </body>
        </html>
        """;
    }

    private static string HtmlEncode(string value) =>
        value
            .Replace("&",  "&amp;")
            .Replace("<",  "&lt;")
            .Replace(">",  "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'",  "&#39;");

    private static string BuildTenantAccessGrantedHtmlBody(string name, string tenantName, string portalUrl)
    {
        var safeName       = HtmlEncode(name);
        var safeTenantName = HtmlEncode(tenantName);
        var safePortalUrl  = HtmlEncode(portalUrl);

        return $"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1.0" />
          <title>You now have access to a new LegalSynq network</title>
        </head>
        <body style="margin:0;padding:32px 0;background:#f9fafb;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;">
          <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:520px;margin:0 auto;">
            <tr>
              <td style="background:#ffffff;border-radius:12px;padding:40px;border:1px solid #e5e7eb;">

                <!-- Wordmark -->
                <p style="margin:0 0 4px;font-size:22px;font-weight:700;color:#111827;letter-spacing:-0.3px;">LegalSynq</p>
                <hr style="border:none;border-top:1px solid #f3f4f6;margin:16px 0 28px;" />

                <!-- Heading -->
                <h1 style="margin:0 0 12px;font-size:20px;font-weight:700;color:#111827;">New network access granted</h1>

                <!-- Body text -->
                <p style="margin:0 0 24px;font-size:15px;line-height:1.65;color:#374151;">
                  Hello <strong>{safeName}</strong>,<br /><br />
                  You have been granted access to <strong>{safeTenantName}</strong> on
                  <strong>LegalSynq</strong>. Sign in with your existing credentials to get started.
                </p>

                <!-- CTA button -->
                <table role="presentation" cellpadding="0" cellspacing="0" style="margin-bottom:28px;">
                  <tr>
                    <td style="border-radius:8px;background:#f97316;">
                      <a href="{safePortalUrl}"
                         style="display:inline-block;padding:13px 28px;font-size:15px;font-weight:600;
                                color:#ffffff;text-decoration:none;border-radius:8px;">
                        Sign in to {safeTenantName}
                      </a>
                    </td>
                  </tr>
                </table>

                <!-- Plain-text link fallback -->
                <p style="margin:0 0 4px;font-size:13px;color:#6b7280;">Or copy and paste this link into your browser:</p>
                <p style="margin:0 0 28px;font-size:13px;color:#f97316;word-break:break-all;">
                  <a href="{safePortalUrl}" style="color:#f97316;">{safePortalUrl}</a>
                </p>

                <hr style="border:none;border-top:1px solid #f3f4f6;margin:0 0 20px;" />

                <!-- Footer -->
                <p style="margin:0;font-size:13px;line-height:1.5;color:#9ca3af;">
                  If you weren&rsquo;t expecting this access grant, please contact
                  your administrator.
                </p>

              </td>
            </tr>
          </table>
        </body>
        </html>
        """;
    }
}
