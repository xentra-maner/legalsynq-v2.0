using BuildingBlocks.Domain;

namespace Liens.Domain.Entities;

public sealed class SellingNotificationOutboxItem : AuditableEntity
{
    public const int MaximumAttempts = 10;

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid RecipientUserId { get; private set; }
    public string EventKey { get; private set; } = string.Empty;
    public string Category { get; private set; } = string.Empty;
    public string Title { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public string SourceDisplayName { get; private set; } = string.Empty;
    public string SourceInitials { get; private set; } = string.Empty;
    public DateTime OccurredAtUtc { get; private set; }
    public string IdempotencyKey { get; private set; } = string.Empty;
    public int AttemptCount { get; private set; }
    public DateTime NextAttemptAtUtc { get; private set; }
    public DateTime? LeaseUntilUtc { get; private set; }
    public string? LeaseOwner { get; private set; }
    public DateTime? ProcessedAtUtc { get; private set; }
    public DateTime? DeadLetteredAtUtc { get; private set; }
    public string? LastError { get; private set; }

    private SellingNotificationOutboxItem() { }

    public static SellingNotificationOutboxItem Create(
        Guid tenantId,
        Guid recipientUserId,
        string eventKey,
        string category,
        string title,
        string description,
        string sourceDisplayName,
        string sourceInitials,
        DateTime occurredAtUtc,
        string idempotencyKey)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("TenantId is required.", nameof(tenantId));
        if (recipientUserId == Guid.Empty) throw new ArgumentException("RecipientUserId is required.", nameof(recipientUserId));
        if (category is not ("lien" or "message")) throw new ArgumentException("Category must be lien or message.", nameof(category));
        ArgumentException.ThrowIfNullOrWhiteSpace(eventKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceInitials);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        if (eventKey.Trim().Length > 128) throw new ArgumentException("EventKey cannot exceed 128 characters.", nameof(eventKey));
        if (idempotencyKey.Trim().Length > 255) throw new ArgumentException("IdempotencyKey cannot exceed 255 characters.", nameof(idempotencyKey));

        var now = DateTime.UtcNow;
        return new SellingNotificationOutboxItem
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            RecipientUserId = recipientUserId,
            EventKey = eventKey.Trim(),
            Category = category,
            Title = TrimToLength(title, 160),
            Description = TrimToLength(description, 500),
            SourceDisplayName = TrimToLength(sourceDisplayName, 160),
            SourceInitials = TrimToLength(sourceInitials, 8),
            OccurredAtUtc = occurredAtUtc.Kind switch
            {
                DateTimeKind.Utc => occurredAtUtc,
                DateTimeKind.Local => occurredAtUtc.ToUniversalTime(),
                _ => DateTime.SpecifyKind(occurredAtUtc, DateTimeKind.Utc),
            },
            IdempotencyKey = idempotencyKey.Trim(),
            NextAttemptAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
    }

    public void MarkProcessed(DateTime nowUtc)
    {
        ProcessedAtUtc = nowUtc;
        LeaseUntilUtc = null;
        LeaseOwner = null;
        LastError = null;
        UpdatedAtUtc = nowUtc;
    }

    public bool TryLease(string workerId, DateTime nowUtc, DateTime leaseUntilUtc)
    {
        if (ProcessedAtUtc.HasValue || DeadLetteredAtUtc.HasValue ||
            (LeaseUntilUtc.HasValue && LeaseUntilUtc.Value >= nowUtc))
            return false;

        LeaseOwner = workerId;
        LeaseUntilUtc = leaseUntilUtc;
        UpdatedAtUtc = nowUtc;
        return true;
    }

    public void RecordFailure(string error, bool retryable, DateTime nowUtc)
    {
        AttemptCount++;
        LastError = TrimToLength(
            string.IsNullOrWhiteSpace(error) ? "Notification submission failed." : error,
            1000);
        LeaseUntilUtc = null;
        LeaseOwner = null;
        UpdatedAtUtc = nowUtc;

        if (!retryable || AttemptCount >= MaximumAttempts)
        {
            DeadLetteredAtUtc = nowUtc;
            return;
        }

        var delayMinutes = Math.Min(60, Math.Pow(2, Math.Min(AttemptCount - 1, 6)));
        NextAttemptAtUtc = nowUtc.AddMinutes(delayMinutes);
    }

    private static string TrimToLength(string value, int maximumLength)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= maximumLength ? trimmed : trimmed[..maximumLength];
    }
}
