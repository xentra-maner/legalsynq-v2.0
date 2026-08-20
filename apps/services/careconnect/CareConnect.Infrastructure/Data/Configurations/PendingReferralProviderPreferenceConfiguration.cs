using CareConnect.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CareConnect.Infrastructure.Data.Configurations;

public class PendingReferralProviderPreferenceConfiguration : IEntityTypeConfiguration<PendingReferralProviderPreference>
{
    public void Configure(EntityTypeBuilder<PendingReferralProviderPreference> builder)
    {
        builder.ToTable("cc_PendingReferralProviderPreferences");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).IsRequired();
        builder.Property(p => p.PendingReferralRequestId).IsRequired();
        builder.Property(p => p.ProviderId).IsRequired();
        builder.Property(p => p.FacilityId);
        builder.Property(p => p.ProviderName).IsRequired().HasMaxLength(250);
        builder.Property(p => p.FacilityName).HasMaxLength(250);
        builder.Property(p => p.DisplayOrder).IsRequired();

        builder.HasIndex(p => p.PendingReferralRequestId)
            .HasDatabaseName("IX_PendingReferralProviderPreferences_Request");

        builder.HasIndex(p => new { p.PendingReferralRequestId, p.DisplayOrder })
            .HasDatabaseName("IX_PendingReferralProviderPreferences_Request_Order");

        builder.HasOne(p => p.PendingReferralRequest)
            .WithMany(r => r.ProviderPreferences)
            .HasForeignKey(p => p.PendingReferralRequestId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
