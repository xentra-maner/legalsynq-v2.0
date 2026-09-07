using Liens.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Liens.Infrastructure.Persistence.Configurations;

public sealed class SellingNotificationOutboxItemConfiguration : IEntityTypeConfiguration<SellingNotificationOutboxItem>
{
    public void Configure(EntityTypeBuilder<SellingNotificationOutboxItem> builder)
    {
        builder.ToTable("liens_SellingNotificationOutbox");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.EventKey).IsRequired().HasMaxLength(128);
        builder.Property(item => item.Category).IsRequired().HasMaxLength(20);
        builder.Property(item => item.Title).IsRequired().HasMaxLength(160);
        builder.Property(item => item.Description).IsRequired().HasMaxLength(500);
        builder.Property(item => item.SourceDisplayName).IsRequired().HasMaxLength(160);
        builder.Property(item => item.SourceInitials).IsRequired().HasMaxLength(8);
        builder.Property(item => item.IdempotencyKey).IsRequired().HasMaxLength(255);
        builder.Property(item => item.LeaseOwner).HasMaxLength(100);
        builder.Property(item => item.LastError).HasMaxLength(1000);
        builder.HasIndex(item => new { item.TenantId, item.IdempotencyKey })
            .IsUnique()
            .HasDatabaseName("UX_SellingNotificationOutbox_Tenant_Idempotency");
        builder.HasIndex(item => new { item.ProcessedAtUtc, item.DeadLetteredAtUtc, item.NextAttemptAtUtc, item.LeaseUntilUtc })
            .HasDatabaseName("IX_SellingNotificationOutbox_Dispatch");
    }
}
