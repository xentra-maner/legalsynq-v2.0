using Notifications.Application.DTOs;
using Notifications.Domain;

namespace Notifications.Application.Interfaces;

public interface IUserInboxService
{
    Task CreateWithNotificationAsync(
        Notification notification,
        Guid recipientUserId,
        string productKey,
        string eventKey,
        InboxPresentationDto presentation,
        CancellationToken ct = default);

    Task<UserInboxPageDto> ListAsync(
        Guid tenantId,
        Guid userId,
        string? category,
        string? readState,
        int page,
        int pageSize,
        DateTime? asOfUtc,
        CancellationToken ct = default);

    Task<UserInboxSummaryDto> GetSummaryAsync(
        Guid tenantId,
        Guid userId,
        int limit,
        CancellationToken ct = default);

    Task<UserInboxReadResultDto?> MarkReadAsync(
        Guid tenantId,
        Guid userId,
        Guid itemId,
        CancellationToken ct = default);

    Task<MarkAllInboxReadResultDto> MarkAllReadAsync(
        Guid tenantId,
        Guid userId,
        DateTime throughUtc,
        CancellationToken ct = default);

    Task<bool> DismissAsync(
        Guid tenantId,
        Guid userId,
        Guid itemId,
        CancellationToken ct = default);
}
