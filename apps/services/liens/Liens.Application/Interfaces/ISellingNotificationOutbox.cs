namespace Liens.Application.Interfaces;

public interface ISellingNotificationOutbox
{
    void Enqueue(NotificationInboxSendRequest request);
}
