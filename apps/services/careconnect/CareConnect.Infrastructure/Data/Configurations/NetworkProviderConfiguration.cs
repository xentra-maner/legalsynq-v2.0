using CareConnect.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CareConnect.Infrastructure.Data.Configurations;

// CC2-INT-B06
public class NetworkProviderConfiguration : IEntityTypeConfiguration<NetworkProvider>
{
    public void Configure(EntityTypeBuilder<NetworkProvider> builder)
    {
        builder.ToTable("cc_NetworkProviders");

        builder.HasKey(np => np.Id);

        builder.Property(np => np.Id).IsRequired();
        builder.Property(np => np.TenantId).IsRequired();
        builder.Property(np => np.ProviderNetworkId).IsRequired();
        builder.Property(np => np.ProviderId).IsRequired();
        builder.Property(np => np.FacilityId).IsRequired();
        builder.Property(np => np.IsActive).IsRequired();
        builder.Property(np => np.AcceptingReferrals).IsRequired();
        builder.Property(np => np.OwningOrganizationId);
        builder.Property(np => np.Visibility).IsRequired().HasMaxLength(20).HasDefaultValue(ProviderVisibility.Public);
        builder.Property(np => np.CreatedAtUtc).IsRequired();
        builder.Property(np => np.UpdatedAtUtc).IsRequired();
        builder.Property(np => np.CreatedByUserId);
        builder.Property(np => np.UpdatedByUserId);

        builder.HasIndex(np => new { np.ProviderNetworkId, np.ProviderId, np.FacilityId })
               .IsUnique()
               .HasDatabaseName("IX_NetworkProviders_ProviderNetworkId_ProviderId_FacilityId");
        builder.HasIndex(np => new { np.ProviderNetworkId, np.ProviderId })
               .HasDatabaseName("IX_NetworkProviders_ProviderNetworkId_ProviderId");
        builder.HasIndex(np => np.FacilityId)
               .HasDatabaseName("IX_NetworkProviders_FacilityId");

        // BLK-PERF-01: GetNetworkProvidersAsync filters on (TenantId, ProviderNetworkId).
        // The existing unique index on (ProviderNetworkId, ProviderId) does not lead on TenantId,
        // so tenant-scoped network provider list queries would scan all rows for that network.
        builder.HasIndex(np => new { np.TenantId, np.ProviderNetworkId })
               .HasDatabaseName("IX_NetworkProviders_TenantId_ProviderNetworkId");
        builder.HasIndex(np => new { np.TenantId, np.OwningOrganizationId, np.Visibility })
               .HasDatabaseName("IX_NetworkProviders_Tenant_OwningOrg_Visibility");

        builder.HasOne(np => np.Provider)
               .WithMany()
               .HasForeignKey(np => np.ProviderId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(np => np.Facility)
               .WithMany()
               .HasForeignKey(np => np.FacilityId)
               .OnDelete(DeleteBehavior.Restrict);
    }
}
