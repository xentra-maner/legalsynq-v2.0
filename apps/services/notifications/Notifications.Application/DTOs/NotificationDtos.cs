namespace Notifications.Application.DTOs;

public class SubmitNotificationDto
{
    // ── Required ─────────────────────────────────────────────────────────────

    public string Channel { get; set; } = string.Empty;
    public object Recipient { get; set; } = new();
    public object Message { get; set; } = new();

    // ── Canonical producer identity (LS-NOTIF-CORE-020) ──────────────────────

    /// <summary>
    /// Canonical product identifier — stable, lowercase kebab-case.
    /// Preferred over <see cref="ProductType"/> for new producers.
    /// Examples: "liens", "comms", "reports", "identity", "flow".
    /// </summary>
    public string? ProductKey { get; set; }

    /// <summary>
    /// Stable business event identifier — lowercase, dot-separated.
    /// Not channel- or template-specific.
    /// Examples: "lien.offer.submitted", "report.delivery", "flow.task.assigned".
    /// Stored in MetadataJson for observability.
    /// </summary>
    public string? EventKey { get; set; }

    /// <summary>
    /// Originating service name. Examples: "liens-service", "comms-service".
    /// Stored in MetadataJson.
    /// </summary>
    public string? SourceSystem { get; set; }

    /// <summary>Cross-service trace/correlation identifier. Stored in MetadataJson.</summary>
    public string? CorrelationId { get; set; }

    /// <summary>Identity of the actor who triggered this notification. Stored in MetadataJson.</summary>
    public string? RequestedBy { get; set; }

    /// <summary>Optional delivery priority hint: "low", "normal", "high", "critical".</summary>
    public string? Priority { get; set; }

    // ── Template ─────────────────────────────────────────────────────────────

    public string? IdempotencyKey { get; set; }
    public string? TemplateKey { get; set; }
    public Dictionary<string, string>? TemplateData { get; set; }
    public InboxPresentationDto? InboxPresentation { get; set; }

    /// <summary>
    /// Email subject line. When set, takes precedence over a <c>subject</c> field
    /// embedded inside <see cref="Message"/>. Ignored for non-email channels.
    /// </summary>
    public string? Subject { get; set; }

    // ── Legacy / backward compat ─────────────────────────────────────────────

    /// <summary>
    /// Legacy product identifier — kept for backward compatibility.
    /// New producers should use <see cref="ProductKey"/> instead.
    /// At ingest, <c>ProductKey ?? ProductType</c> is used for template resolution.
    /// </summary>
    public string? ProductType { get; set; }

    // ── Overrides / classification ────────────────────────────────────────────

    public object? Metadata { get; set; }
    public bool? BrandedRendering { get; set; }
    public bool? OverrideSuppression { get; set; }
    public string? OverrideReason { get; set; }
    public string? Severity { get; set; }
    public string? Category { get; set; }
}

public sealed class InboxPresentationDto
{
    public string Category { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; }
    public string SourceDisplayName { get; set; } = string.Empty;
    public string SourceInitials { get; set; } = string.Empty;
}

public class NotificationResultDto
{
    public Guid Id { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? ProviderUsed { get; set; }
    public bool PlatformFallbackUsed { get; set; }
    public bool BlockedByPolicy { get; set; }
    public string? BlockedReasonCode { get; set; }
    public bool OverrideUsed { get; set; }
    public string? FailureCategory { get; set; }
    public string? LastErrorMessage { get; set; }
}

public class NotificationDto
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public string Channel { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string RecipientJson { get; set; } = string.Empty;
    public string MessageJson { get; set; } = string.Empty;
    public string? MetadataJson { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? ProviderUsed { get; set; }
    public string? FailureCategory { get; set; }
    public string? LastErrorMessage { get; set; }
    public Guid? TemplateId { get; set; }
    public Guid? TemplateVersionId { get; set; }
    public string? TemplateKey { get; set; }
    public string? RenderedSubject { get; set; }
    public string? RenderedBody { get; set; }
    public string? RenderedText { get; set; }
    public string? ProviderOwnershipMode { get; set; }
    public Guid? ProviderConfigId { get; set; }
    public bool PlatformFallbackUsed { get; set; }
    public bool BlockedByPolicy { get; set; }
    public string? BlockedReasonCode { get; set; }
    public bool OverrideUsed { get; set; }
    public string? Severity { get; set; }
    public string? Category { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
