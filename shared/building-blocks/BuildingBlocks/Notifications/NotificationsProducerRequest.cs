using System.Text.Json.Serialization;

namespace BuildingBlocks.Notifications;

/// <summary>
/// Canonical producer request for <c>POST /v1/notifications</c>.
///
/// Use this type when submitting notifications to the platform Notifications service.
/// It maps directly to <c>SubmitNotificationDto</c> on the server side.
///
/// All producer services in the LegalSynq platform (and any future platform) should
/// use this type to reduce drift and ensure the canonical contract is honoured.
/// </summary>
public sealed class NotificationsProducerRequest
{
    // ── Required ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Delivery channel.
    /// Valid values: <c>email</c>, <c>sms</c>, <c>in_app</c>, <c>push</c>,
    /// <c>event</c>, <c>internal</c>.
    /// </summary>
    [JsonPropertyName("channel")]
    public string Channel { get; set; } = string.Empty;

    /// <summary>
    /// Recipient descriptor. Use <see cref="NotificationsRecipient"/> for type safety,
    /// or pass an anonymous object for fan-out modes (role, org, batch).
    /// </summary>
    [JsonPropertyName("recipient")]
    public object Recipient { get; set; } = new();

    // ── Canonical producer identity ──────────────────────────────────────────

    /// <summary>
    /// Stable, lowercase kebab-case product/module identifier.
    /// Examples: <c>liens</c>, <c>comms</c>, <c>reports</c>, <c>identity</c>,
    /// <c>flow</c>, <c>care-connect</c>.
    ///
    /// Used for template resolution and branding token injection.
    /// Must not contain UI labels or assumptions about the platform.
    /// </summary>
    [JsonPropertyName("productKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProductKey { get; set; }

    /// <summary>
    /// Stable, lowercase dot-separated business event identifier.
    /// Not channel-specific. Not template-specific.
    /// Examples: <c>lien.offer.submitted</c>, <c>comms.sla_alert.breached</c>,
    /// <c>report.delivery</c>, <c>flow.task.assigned</c>.
    /// </summary>
    [JsonPropertyName("eventKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EventKey { get; set; }

    /// <summary>
    /// Originating service identifier. Stored in notification metadata.
    /// Examples: <c>liens-service</c>, <c>comms-service</c>, <c>reports-service</c>.
    /// </summary>
    [JsonPropertyName("sourceSystem")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceSystem { get; set; }

    /// <summary>
    /// Cross-service trace/correlation identifier.
    /// Stored in notification metadata for observability.
    /// </summary>
    [JsonPropertyName("correlationId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CorrelationId { get; set; }

    /// <summary>
    /// Identity of the actor who triggered this notification.
    /// Use a userId GUID string or a service-identity slug.
    /// </summary>
    [JsonPropertyName("requestedBy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestedBy { get; set; }

    // ── Template ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Explicit template identifier. Separate concern from <see cref="EventKey"/>.
    /// When absent, the server resolves a template by <c>eventKey</c> + <c>productKey</c> + <c>channel</c>.
    /// </summary>
    [JsonPropertyName("templateKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TemplateKey { get; set; }

    /// <summary>
    /// Template render data. Concise and deliberate — not a full domain object dump.
    /// Values are used to interpolate template variables.
    /// </summary>
    [JsonPropertyName("templateData")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? TemplateData { get; set; }

    // ── Content / routing ────────────────────────────────────────────────────

    /// <summary>
    /// Email subject line. When set, takes precedence over a <c>subject</c> field
    /// embedded inside <see cref="Message"/>. Ignored for non-email channels.
    /// </summary>
    [JsonPropertyName("subject")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Subject { get; set; }

    /// <summary>
    /// Raw message content when not using templates.
    /// Use <c>new { type = "...", subject = "...", body = "..." }</c> or
    /// a typed object.
    /// </summary>
    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Message { get; set; }

    /// <summary>
    /// Operational / context metadata. Not for secrets. Not the main rendering model.
    /// Use a <c>Dictionary&lt;string, string&gt;</c> or typed object.
    /// </summary>
    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Metadata { get; set; }

    /// <summary>
    /// Idempotency key. Prevents duplicate deliveries when a producer retries.
    /// The server deduplicates per (tenantId, idempotencyKey).
    /// </summary>
    [JsonPropertyName("idempotencyKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IdempotencyKey { get; set; }

    /// <summary>
    /// Immutable presentation snapshot for a personal in-app inbox item.
    /// Required when <see cref="Channel"/> is <c>in_app</c>.
    /// </summary>
    [JsonPropertyName("inboxPresentation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public InboxPresentation? InboxPresentation { get; set; }

    // ── Optional overrides ───────────────────────────────────────────────────

    /// <summary>
    /// Delivery priority hint: <c>low</c>, <c>normal</c>, <c>high</c>, <c>critical</c>.
    /// </summary>
    [JsonPropertyName("priority")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Priority { get; set; }

    /// <summary>Severity label stored on the notification record.</summary>
    [JsonPropertyName("severity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Severity { get; set; }

    /// <summary>Classification tag stored on the notification record.</summary>
    [JsonPropertyName("category")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Category { get; set; }

    /// <summary>When <c>true</c>, branding tokens from the tenant's branding config are injected into the template.</summary>
    [JsonPropertyName("brandedRendering")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? BrandedRendering { get; set; }

    /// <summary>When <c>true</c>, bypasses the contact suppression list for this send.</summary>
    [JsonPropertyName("overrideSuppression")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? OverrideSuppression { get; set; }

    /// <summary>Human-readable justification required when <see cref="OverrideSuppression"/> is <c>true</c>.</summary>
    [JsonPropertyName("overrideReason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OverrideReason { get; set; }
}

public sealed class InboxPresentation
{
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("occurredAtUtc")]
    public DateTime OccurredAtUtc { get; set; }

    [JsonPropertyName("sourceDisplayName")]
    public string SourceDisplayName { get; set; } = string.Empty;

    [JsonPropertyName("sourceInitials")]
    public string SourceInitials { get; set; } = string.Empty;
}

/// <summary>
/// Generic recipient model for the platform Notifications service.
///
/// Set only the fields relevant to the delivery channel and recipient mode:
/// <list type="bullet">
///   <item><c>email</c> channel → set <see cref="Email"/> (+ optionally <see cref="Cc"/>, <see cref="Bcc"/>)</item>
///   <item><c>sms</c> channel → set <see cref="Phone"/></item>
///   <item>User-targeted → set <see cref="UserId"/></item>
///   <item>Fan-out by role → set <see cref="Mode"/> = <c>"Role"</c> + <see cref="RoleKey"/></item>
///   <item>Fan-out by org → set <see cref="Mode"/> = <c>"Org"</c> + <see cref="OrgId"/></item>
///   <item>Tenant-scoped event → set <see cref="TenantId"/> (no specific user)</item>
/// </list>
/// </summary>
public sealed class NotificationsRecipient
{
    [JsonPropertyName("email")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Email { get; set; }

    [JsonPropertyName("phone")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Phone { get; set; }

    [JsonPropertyName("userId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UserId { get; set; }

    [JsonPropertyName("tenantId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TenantId { get; set; }

    [JsonPropertyName("roleKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RoleKey { get; set; }

    [JsonPropertyName("orgId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OrgId { get; set; }

    /// <summary>
    /// Fan-out mode: <c>UserId</c>, <c>Email</c>, <c>Role</c>, <c>Org</c>.
    /// Only required when using role/org fan-out.
    /// </summary>
    [JsonPropertyName("mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mode { get; set; }

    [JsonPropertyName("cc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Cc { get; set; }

    [JsonPropertyName("bcc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Bcc { get; set; }
}
