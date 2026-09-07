using Microsoft.EntityFrameworkCore;
using Notifications.Application.DTOs;
using Notifications.Domain;
using Notifications.Infrastructure.Data;
using Notifications.Infrastructure.Services;
using Xunit;

namespace Notifications.Tests;

public sealed class UserInboxServiceTests
{
    [Fact]
    public async Task ListAsync_ScopesItemsAndCountsToTenantAndUser()
    {
        await using var db = CreateDb();
        var service = new UserInboxService(db);
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var occurredAt = DateTime.UtcNow.AddMinutes(-1);

        await AddAsync(db, service, tenantId, userId, "lien", occurredAt);
        await AddAsync(db, service, tenantId, userId, "message", occurredAt.AddSeconds(1));
        await AddAsync(db, service, tenantId, Guid.NewGuid(), "lien", occurredAt.AddSeconds(2));
        await AddAsync(db, service, Guid.NewGuid(), userId, "lien", occurredAt.AddSeconds(3));

        var result = await service.ListAsync(
            tenantId, userId, "lien", "all", 1, 10, DateTime.UtcNow);

        Assert.Single(result.Items);
        Assert.Equal(2, result.Counts.All);
        Assert.Equal(2, result.Counts.Unread);
        Assert.Equal(1, result.Counts.Liens);
        Assert.Equal(1, result.Counts.Messages);
    }

    [Fact]
    public async Task ReadAndDismiss_AreIdempotentAndOwnerScoped()
    {
        await using var db = CreateDb();
        var service = new UserInboxService(db);
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var itemId = await AddAsync(db, service, tenantId, userId, "lien", DateTime.UtcNow);

        var firstRead = await service.MarkReadAsync(tenantId, userId, itemId);
        var secondRead = await service.MarkReadAsync(tenantId, userId, itemId);

        Assert.NotNull(firstRead);
        Assert.Equal(firstRead!.ReadAtUtc, secondRead!.ReadAtUtc);
        Assert.Null(await service.MarkReadAsync(tenantId, Guid.NewGuid(), itemId));
        Assert.True(await service.DismissAsync(tenantId, userId, itemId));
        Assert.True(await service.DismissAsync(tenantId, userId, itemId));
        Assert.Null(await service.MarkReadAsync(tenantId, userId, itemId));
        Assert.False(await service.DismissAsync(tenantId, Guid.NewGuid(), itemId));
    }

    [Fact]
    public async Task MarkAllRead_StopsAtRequestedSnapshot()
    {
        await using var db = CreateDb();
        var service = new UserInboxService(db);
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var through = DateTime.UtcNow;
        await AddAsync(db, service, tenantId, userId, "lien", through.AddMinutes(-1));
        await AddAsync(db, service, tenantId, userId, "message", through.AddMinutes(1));

        var result = await service.MarkAllReadAsync(tenantId, userId, through);

        Assert.Equal(1, result.AffectedCount);
        Assert.Equal(1, result.UnreadCount);
    }

    [Fact]
    public async Task ListAsync_SnapshotExcludesItemsMaterializedAfterSnapshot()
    {
        await using var db = CreateDb();
        var service = new UserInboxService(db);
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var snapshot = DateTime.UtcNow.AddSeconds(-1);

        await AddAsync(db, service, tenantId, userId, "lien", snapshot.AddMinutes(-1));

        var result = await service.ListAsync(tenantId, userId, "all", "all", 1, 10, snapshot);

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
    }

    private static NotificationsDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new NotificationsDbContext(options);
    }

    private static async Task<Guid> AddAsync(
        NotificationsDbContext db,
        UserInboxService service,
        Guid tenantId,
        Guid userId,
        string category,
        DateTime occurredAt)
    {
        var notification = new Notification
        {
            TenantId = tenantId,
            Channel = "in_app",
            Status = "sent",
            RecipientJson = "{}",
            MessageJson = "{}",
            IdempotencyKey = Guid.NewGuid().ToString("N"),
        };
        await service.CreateWithNotificationAsync(
            notification,
            userId,
            "liens",
            category == "message" ? "lien.offer.message.created" : "lien.offer.submitted",
            new InboxPresentationDto
            {
                Category = category,
                Title = "Test notification",
                Description = "Generic test description.",
                SourceDisplayName = "Synq Selling",
                SourceInitials = "SS",
                OccurredAtUtc = occurredAt,
            });
        return await db.UserInboxItems.Where(item => item.NotificationId == notification.Id).Select(item => item.Id).SingleAsync();
    }
}
