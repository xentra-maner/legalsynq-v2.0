namespace Liens.Application.Interfaces;

public interface INotificationPublisher
{
    Task PublishAsync(
        string notificationType,
        Guid tenantId,
        Dictionary<string, string> data,
        CancellationToken ct = default);

    Task<NotificationInboxSendResult> SubmitInboxAsync(
        NotificationInboxSendRequest request,
        CancellationToken ct = default);

    Task<NotificationEmailSendResult> SendEmailAsync(
        string notificationType,
        Guid tenantId,
        string recipientEmail,
        string subject,
        string body,
        Dictionary<string, string> metadata,
        CancellationToken ct = default,
        NotificationEmailSendOptions? options = null);
}

public sealed record NotificationInboxSendRequest(
    Guid TenantId,
    Guid RecipientUserId,
    string EventKey,
    string Category,
    string Title,
    string Description,
    string SourceDisplayName,
    string SourceInitials,
    DateTime OccurredAtUtc,
    string IdempotencyKey);

public sealed record NotificationInboxSendResult(
    bool Succeeded,
    bool Retryable,
    Guid? NotificationId,
    string? Error);

public sealed record NotificationEmailSendOptions(
    string? IdempotencyKey = null,
    string? TemplateKey = null,
    Dictionary<string, string>? TemplateData = null,
    string? RequestedBy = null,
    bool? BrandedRendering = null,
    string? HtmlBody = null,
    string? TextBody = null,
    IReadOnlyList<NotificationEmailInlineAttachment>? InlineAttachments = null,
    bool DisableClickTracking = false);

public sealed record NotificationEmailInlineAttachment(
    string ContentId,
    string FileName,
    string ContentType,
    string Base64Content);

public sealed record NotificationEmailSendResult(
    Guid? NotificationId,
    string Status,
    bool BlockedByPolicy,
    string? BlockedReasonCode,
    string? FailureCategory,
    string? LastErrorMessage)
{
    public bool Succeeded => string.Equals(Status, "sent", StringComparison.OrdinalIgnoreCase);
}
