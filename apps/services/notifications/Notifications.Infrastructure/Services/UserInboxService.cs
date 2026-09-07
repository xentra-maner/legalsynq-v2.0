using Microsoft.EntityFrameworkCore;
using Notifications.Application.DTOs;
using Notifications.Application.Interfaces;
using Notifications.Domain;
using Notifications.Infrastructure.Data;

namespace Notifications.Infrastructure.Services;

public sealed class UserInboxService : IUserInboxService
{
    private readonly NotificationsDbContext _db;

    public UserInboxService(NotificationsDbContext db) => _db = db;

    public async Task CreateWithNotificationAsync(
        Notification notification,
        Guid recipientUserId,
        string productKey,
        string eventKey,
        InboxPresentationDto presentation,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        notification.Id = notification.Id == Guid.Empty ? Guid.CreateVersion7() : notification.Id;
        notification.CreatedAt = now;
        notification.UpdatedAt = now;

        _db.Notifications.Add(notification);
        _db.UserInboxItems.Add(new UserInboxItem
        {
            Id = Guid.CreateVersion7(),
            NotificationId = notification.Id,
            TenantId = notification.TenantId!.Value,
            RecipientUserId = recipientUserId,
            ProductKey = productKey,
            EventKey = eventKey,
            Category = presentation.Category,
            Title = presentation.Title,
            Description = presentation.Description,
            SourceDisplayName = presentation.SourceDisplayName,
            SourceInitials = presentation.SourceInitials,
            OccurredAtUtc = presentation.OccurredAtUtc,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });

        await _db.SaveChangesAsync(ct);
    }

    public async Task<UserInboxPageDto> ListAsync(
        Guid tenantId,
        Guid userId,
        string? category,
        string? readState,
        int page,
        int pageSize,
        DateTime? asOfUtc,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var requestedSnapshot = NormalizeUtc(asOfUtc ?? now);
        var snapshot = requestedSnapshot <= now ? requestedSnapshot : now;
        var baseQuery = _db.UserInboxItems.AsNoTracking().Where(item =>
            item.TenantId == tenantId &&
            item.RecipientUserId == userId &&
            item.DismissedAtUtc == null &&
            item.CreatedAtUtc <= snapshot &&
            item.OccurredAtUtc <= snapshot);

        var counts = new UserInboxCountsDto
        {
            All = await baseQuery.CountAsync(ct),
            Unread = await baseQuery.CountAsync(item => item.ReadAtUtc == null, ct),
            Liens = await baseQuery.CountAsync(item => item.Category == "lien", ct),
            Messages = await baseQuery.CountAsync(item => item.Category == "message", ct),
        };

        var filtered = baseQuery;
        if (!string.IsNullOrEmpty(category) && category != "all")
            filtered = filtered.Where(item => item.Category == category);
        if (readState == "unread")
            filtered = filtered.Where(item => item.ReadAtUtc == null);

        var total = await filtered.CountAsync(ct);
        var items = await filtered
            .OrderByDescending(item => item.OccurredAtUtc)
            .ThenByDescending(item => item.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(item => new UserInboxItemDto
            {
                Id = item.Id,
                Category = item.Category,
                EventKey = item.EventKey,
                Title = item.Title,
                Description = item.Description,
                SourceDisplayName = item.SourceDisplayName,
                SourceInitials = item.SourceInitials,
                OccurredAtUtc = item.OccurredAtUtc,
                IsRead = item.ReadAtUtc.HasValue,
                ReadAtUtc = item.ReadAtUtc,
            })
            .ToListAsync(ct);

        return new UserInboxPageDto
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = total,
            TotalPages = (int)Math.Ceiling(total / (double)pageSize),
            AsOfUtc = snapshot,
            Counts = counts,
        };
    }

    public async Task<UserInboxSummaryDto> GetSummaryAsync(
        Guid tenantId,
        Guid userId,
        int limit,
        CancellationToken ct = default)
    {
        var snapshot = DateTime.UtcNow;
        var query = _db.UserInboxItems.AsNoTracking().Where(item =>
            item.TenantId == tenantId &&
            item.RecipientUserId == userId &&
            item.DismissedAtUtc == null &&
            item.OccurredAtUtc <= snapshot);

        return new UserInboxSummaryDto
        {
            UnreadCount = await query.CountAsync(item => item.ReadAtUtc == null, ct),
            AsOfUtc = snapshot,
            Items = await query
                .OrderByDescending(item => item.OccurredAtUtc)
                .ThenByDescending(item => item.Id)
                .Take(limit)
                .Select(item => new UserInboxItemDto
                {
                    Id = item.Id,
                    Category = item.Category,
                    EventKey = item.EventKey,
                    Title = item.Title,
                    Description = item.Description,
                    SourceDisplayName = item.SourceDisplayName,
                    SourceInitials = item.SourceInitials,
                    OccurredAtUtc = item.OccurredAtUtc,
                    IsRead = item.ReadAtUtc.HasValue,
                    ReadAtUtc = item.ReadAtUtc,
                })
                .ToListAsync(ct),
        };
    }

    public async Task<UserInboxReadResultDto?> MarkReadAsync(
        Guid tenantId,
        Guid userId,
        Guid itemId,
        CancellationToken ct = default)
    {
        var item = await _db.UserInboxItems.FirstOrDefaultAsync(candidate =>
            candidate.Id == itemId &&
            candidate.TenantId == tenantId &&
            candidate.RecipientUserId == userId &&
            candidate.DismissedAtUtc == null, ct);
        if (item is null) return null;

        if (!item.ReadAtUtc.HasValue)
        {
            item.ReadAtUtc = DateTime.UtcNow;
            item.UpdatedAtUtc = item.ReadAtUtc.Value;
            await _db.SaveChangesAsync(ct);
        }

        return new UserInboxReadResultDto
        {
            Id = item.Id,
            IsRead = true,
            ReadAtUtc = item.ReadAtUtc.Value,
        };
    }

    public async Task<MarkAllInboxReadResultDto> MarkAllReadAsync(
        Guid tenantId,
        Guid userId,
        DateTime throughUtc,
        CancellationToken ct = default)
    {
        var through = NormalizeUtc(throughUtc);
        var now = DateTime.UtcNow;
        var unread = await _db.UserInboxItems.Where(item =>
            item.TenantId == tenantId &&
            item.RecipientUserId == userId &&
            item.DismissedAtUtc == null &&
            item.ReadAtUtc == null &&
            item.OccurredAtUtc <= through).ToListAsync(ct);

        foreach (var item in unread)
        {
            item.ReadAtUtc = now;
            item.UpdatedAtUtc = now;
        }
        if (unread.Count > 0) await _db.SaveChangesAsync(ct);

        var remaining = await _db.UserInboxItems.CountAsync(item =>
            item.TenantId == tenantId &&
            item.RecipientUserId == userId &&
            item.DismissedAtUtc == null &&
            item.ReadAtUtc == null, ct);

        return new MarkAllInboxReadResultDto
        {
            AffectedCount = unread.Count,
            UnreadCount = remaining,
            CompletedAtUtc = now,
        };
    }

    public async Task<bool> DismissAsync(
        Guid tenantId,
        Guid userId,
        Guid itemId,
        CancellationToken ct = default)
    {
        var item = await _db.UserInboxItems.FirstOrDefaultAsync(candidate =>
            candidate.Id == itemId &&
            candidate.TenantId == tenantId &&
            candidate.RecipientUserId == userId, ct);
        if (item is null) return false;

        if (!item.DismissedAtUtc.HasValue)
        {
            item.DismissedAtUtc = DateTime.UtcNow;
            item.UpdatedAtUtc = item.DismissedAtUtc.Value;
            await _db.SaveChangesAsync(ct);
        }
        return true;
    }

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

}
