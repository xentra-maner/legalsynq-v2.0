using CareConnect.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CareConnect.Infrastructure.Data.Configurations;

public class PendingReferralRequestConfiguration : IEntityTypeConfiguration<PendingReferralRequest>
{
    public void Configure(EntityTypeBuilder<PendingReferralRequest> builder)
    {
        builder.ToTable("cc_PendingReferralRequests");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).IsRequired();
        builder.Property(r => r.TenantId).IsRequired();
        builder.Property(r => r.LawFirmOrganizationId).IsRequired();
        builder.Property(r => r.ReferralAttributionId).IsRequired();
        builder.Property(r => r.Origin).IsRequired().HasMaxLength(40);
        builder.Property(r => r.ClientFirstName).IsRequired().HasMaxLength(100);
        builder.Property(r => r.ClientLastName).IsRequired().HasMaxLength(100);
        builder.Property(r => r.ClientDob);
        builder.Property(r => r.ClientPhone).IsRequired().HasMaxLength(50);
        builder.Property(r => r.ClientEmail).IsRequired().HasMaxLength(320);
        builder.Property(r => r.CaseNumber).HasMaxLength(100);
        builder.Property(r => r.RequestedService).HasMaxLength(500);
        builder.Property(r => r.Urgency).IsRequired().HasMaxLength(20);
        builder.Property(r => r.TreatmentTypeId);
        builder.Property(r => r.DateOfAccident).HasColumnType("date");
        builder.Property(r => r.RecommendedProviderId);
        builder.Property(r => r.RecommendedFacilityId);
        builder.Property(r => r.RecommendedProviderName).HasMaxLength(250);
        builder.Property(r => r.RecommendedFacilityName).HasMaxLength(250);
        builder.Property(r => r.Notes).HasMaxLength(2000);
        builder.Property(r => r.LienCompanyName).HasMaxLength(250);
        builder.Property(r => r.LienCompanyEmail).HasMaxLength(320);
        builder.Property(r => r.Status).IsRequired().HasMaxLength(40);
        builder.Property(r => r.ConvertedReferralId);
        builder.Property(r => r.ConvertedAtUtc);
        builder.Property(r => r.ConvertedByUserId);
        builder.Property(r => r.CreatedAtUtc).IsRequired();
        builder.Property(r => r.UpdatedAtUtc).IsRequired();
        builder.Property(r => r.CreatedByUserId);
        builder.Property(r => r.UpdatedByUserId);

        builder.HasIndex(r => new { r.TenantId, r.LawFirmOrganizationId, r.Status })
            .HasDatabaseName("IX_PendingReferralRequests_Tenant_LawFirm_Status");
        builder.HasIndex(r => new { r.TenantId, r.ReferralAttributionId, r.CreatedAtUtc })
            .HasDatabaseName("IX_PendingReferralRequests_Tenant_Attribution_Created");
        builder.HasIndex(r => r.ConvertedReferralId)
            .HasDatabaseName("IX_PendingReferralRequests_ConvertedReferralId");

        builder.HasOne(r => r.ReferralAttribution)
            .WithMany()
            .HasForeignKey(r => r.ReferralAttributionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.ConvertedReferral)
            .WithMany()
            .HasForeignKey(r => r.ConvertedReferralId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
