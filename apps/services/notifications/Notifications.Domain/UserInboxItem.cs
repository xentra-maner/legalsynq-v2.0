namespace Notifications.Domain;

public sealed class UserInboxItem
{
    public Guid Id { get; set; }
    public Guid NotificationId { get; set; }
    public Guid TenantId { get; set; }
    public Guid RecipientUserId { get; set; }
    public string ProductKey { get; set; } = string.Empty;
    public string EventKey { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SourceDisplayName { get; set; } = string.Empty;
    public string SourceInitials { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; }
    public DateTime? ReadAtUtc { get; set; }
    public DateTime? DismissedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
