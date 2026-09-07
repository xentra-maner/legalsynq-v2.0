namespace Notifications.Application.DTOs;

public sealed class UserInboxItemDto
{
    public Guid Id { get; set; }
    public string Category { get; set; } = string.Empty;
    public string EventKey { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SourceDisplayName { get; set; } = string.Empty;
    public string SourceInitials { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; }
    public bool IsRead { get; set; }
    public DateTime? ReadAtUtc { get; set; }
}

public sealed class UserInboxCountsDto
{
    public int All { get; set; }
    public int Unread { get; set; }
    public int Liens { get; set; }
    public int Messages { get; set; }
}

public sealed class UserInboxPageDto
{
    public List<UserInboxItemDto> Items { get; set; } = [];
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages { get; set; }
    public DateTime AsOfUtc { get; set; }
    public UserInboxCountsDto Counts { get; set; } = new();
}

public sealed class UserInboxSummaryDto
{
    public int UnreadCount { get; set; }
    public DateTime AsOfUtc { get; set; }
    public List<UserInboxItemDto> Items { get; set; } = [];
}

public sealed class UserInboxReadResultDto
{
    public Guid Id { get; set; }
    public bool IsRead { get; set; }
    public DateTime ReadAtUtc { get; set; }
}

public sealed class MarkAllInboxReadRequest
{
    public DateTime ThroughUtc { get; set; }
}

public sealed class MarkAllInboxReadResultDto
{
    public int AffectedCount { get; set; }
    public int UnreadCount { get; set; }
    public DateTime CompletedAtUtc { get; set; }
}
