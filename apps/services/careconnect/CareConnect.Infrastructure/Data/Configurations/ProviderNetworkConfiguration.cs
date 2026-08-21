using CareConnect.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CareConnect.Infrastructure.Data.Configurations;

// CC2-INT-B06
public class ProviderNetworkConfiguration : IEntityTypeConfiguration<ProviderNetwork>
{
    public void Configure(EntityTypeBuilder<ProviderNetwork> builder)
    {
        builder.ToTable("cc_ProviderNetworks");

        builder.HasKey(n => n.Id);

        builder.Property(n => n.Id).IsRequired();
        builder.Property(n => n.TenantId).IsRequired();
        builder.Property(n => n.Name).IsRequired().HasMaxLength(200);
        builder.Property(n => n.Description).HasMaxLength(1000);
        builder.Property(n => n.IsDeleted).IsRequired();
        builder.Property(n => n.OwningOrganizationId);
        builder.Property(n => n.CreatedAtUtc).IsRequired();
        builder.Property(n => n.UpdatedAtUtc).IsRequired();
        builder.Property(n => n.CreatedByUserId);
        builder.Property(n => n.UpdatedByUserId);

        // MySQL 8.0 does not support partial/filtered indexes.
        // Uniqueness by (TenantId, Name) for active networks is enforced at the
        // application level in NetworkService.NameExistsAsync().
        builder.HasIndex(n => new { n.TenantId, n.Name });

        builder.HasMany(n => n.NetworkProviders)
               .WithOne(np => np.Network)
               .HasForeignKey(np => np.ProviderNetworkId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}
