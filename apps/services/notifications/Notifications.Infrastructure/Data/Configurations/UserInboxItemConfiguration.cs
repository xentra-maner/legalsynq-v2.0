using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Notifications.Domain;

namespace Notifications.Infrastructure.Data.Configurations;

public sealed class UserInboxItemConfiguration : IEntityTypeConfiguration<UserInboxItem>
{
    public void Configure(EntityTypeBuilder<UserInboxItem> builder)
    {
        builder.ToTable("ntf_UserInboxItems");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.ProductKey).IsRequired().HasMaxLength(50);
        builder.Property(item => item.EventKey).IsRequired().HasMaxLength(128);
        builder.Property(item => item.Category).IsRequired().HasMaxLength(20);
        builder.Property(item => item.Title).IsRequired().HasMaxLength(160);
        builder.Property(item => item.Description).IsRequired().HasMaxLength(500);
        builder.Property(item => item.SourceDisplayName).IsRequired().HasMaxLength(160);
        builder.Property(item => item.SourceInitials).IsRequired().HasMaxLength(8);

        builder.HasIndex(item => item.NotificationId)
            .IsUnique()
            .HasDatabaseName("UX_UserInboxItems_NotificationId");
        builder.HasIndex(item => new { item.TenantId, item.RecipientUserId, item.DismissedAtUtc, item.OccurredAtUtc, item.Id })
            .HasDatabaseName("IX_UserInboxItems_User_Chronology");
        builder.HasIndex(item => new { item.TenantId, item.RecipientUserId, item.Category, item.DismissedAtUtc, item.OccurredAtUtc })
            .HasDatabaseName("IX_UserInboxItems_User_Category");
        builder.HasIndex(item => new { item.TenantId, item.RecipientUserId, item.ReadAtUtc, item.DismissedAtUtc })
            .HasDatabaseName("IX_UserInboxItems_User_ReadState");
    }
}
