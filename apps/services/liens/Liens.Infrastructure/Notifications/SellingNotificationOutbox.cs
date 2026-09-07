using Liens.Application.Interfaces;
using Liens.Domain.Entities;
using Liens.Infrastructure.Persistence;

namespace Liens.Infrastructure.Notifications;

public sealed class SellingNotificationOutbox : ISellingNotificationOutbox
{
    private readonly LiensDbContext _db;

    public SellingNotificationOutbox(LiensDbContext db) => _db = db;

    public void Enqueue(NotificationInboxSendRequest request)
    {
        _db.SellingNotificationOutboxItems.Add(SellingNotificationOutboxItem.Create(
            request.TenantId,
            request.RecipientUserId,
            request.EventKey,
            request.Category,
            request.Title,
            request.Description,
            request.SourceDisplayName,
            request.SourceInitials,
            request.OccurredAtUtc,
            request.IdempotencyKey));
    }
}
