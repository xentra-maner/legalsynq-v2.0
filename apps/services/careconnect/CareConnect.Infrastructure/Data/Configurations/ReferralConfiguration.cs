using CareConnect.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CareConnect.Infrastructure.Data.Configurations;

public class ReferralConfiguration : IEntityTypeConfiguration<Referral>
{
    public void Configure(EntityTypeBuilder<Referral> builder)
    {
        builder.ToTable("cc_Referrals");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Id).IsRequired();
        builder.Property(r => r.TenantId).IsRequired();

        // Multi-org participant columns (nullable during migration window)
        builder.Property(r => r.ReferringOrganizationId);
        builder.Property(r => r.ReceivingOrganizationId);
        builder.Property(r => r.SubjectPartyId);
        builder.Property(r => r.SubjectNameSnapshot).HasMaxLength(250);
        builder.Property(r => r.SubjectDobSnapshot).HasColumnType("date");

        // Provider routing
        builder.Property(r => r.ProviderId).IsRequired();
        builder.Property(r => r.FacilityId);

        // Phase 5: organization relationship context (nullable; set when both orgs are linked)
        builder.Property(r => r.OrganizationRelationshipId);
        builder.HasIndex(r => r.OrganizationRelationshipId)
            .HasDatabaseName("IX_Referrals_OrganizationRelationshipId");

        // Legacy inline client fields (kept during migration window)
        builder.Property(r => r.ClientFirstName).IsRequired().HasMaxLength(100);
        builder.Property(r => r.ClientLastName).IsRequired().HasMaxLength(100);
        builder.Property(r => r.ClientDob);
        builder.Property(r => r.ClientPhone).IsRequired().HasMaxLength(50);
        builder.Property(r => r.ClientEmail).IsRequired().HasMaxLength(320);

        // Referral detail
        builder.Property(r => r.CaseNumber).HasMaxLength(100);
        builder.Property(r => r.RequestedService).HasMaxLength(500);
        builder.Property(r => r.DateOfAccident).HasColumnType("date");
        builder.Property(r => r.TreatmentTypeId);
        builder.Property(r => r.Urgency).IsRequired().HasMaxLength(20);
        builder.Property(r => r.Status).IsRequired().HasMaxLength(20);
        builder.Property(r => r.Notes).HasMaxLength(2000);
        builder.Property(r => r.DeclineNotes).HasMaxLength(2000);
        builder.Property(r => r.Origin).IsRequired().HasMaxLength(40).HasDefaultValue(ReferralOrigin.LawFirm);
        builder.Property(r => r.LienCompanyName).HasMaxLength(250);
        builder.Property(r => r.LienCompanyEmail).HasMaxLength(320);
        builder.Property(r => r.ReferrerFirmName).HasMaxLength(250);
        builder.Property(r => r.ReferrerPhone).HasMaxLength(50);
        builder.Property(r => r.CreatedAtUtc).IsRequired();
        builder.Property(r => r.UpdatedAtUtc).IsRequired();
        builder.Property(r => r.CreatedByUserId);
        builder.Property(r => r.UpdatedByUserId);

        // Referral Origination (optional)
        builder.Property(r => r.ReferralAttributionId);

        // Indexes
        builder.HasIndex(r => new { r.TenantId, r.Status })
            .HasDatabaseName("IX_Referrals_TenantId_Status");
        builder.HasIndex(r => new { r.TenantId, r.ProviderId })
            .HasDatabaseName("IX_Referrals_TenantId_ProviderId");
        builder.HasIndex(r => new { r.TenantId, r.FacilityId })
            .HasDatabaseName("IX_Referrals_TenantId_FacilityId");
        builder.HasIndex(r => new { r.TenantId, r.CreatedAtUtc })
            .HasDatabaseName("IX_Referrals_TenantId_CreatedAtUtc");
        // BLK-PERF-01: Composite for admin dashboard / analytics queries that filter on
        // TenantId + Status + CreatedAtUtc simultaneously (e.g. status counts within date window).
        builder.HasIndex(r => new { r.TenantId, r.Status, r.CreatedAtUtc })
            .HasDatabaseName("IX_Referrals_TenantId_Status_CreatedAtUtc");
        builder.HasIndex(r => new { r.ReferringOrganizationId, r.Status })
            .HasDatabaseName("IX_Referrals_ReferringOrgId_Status");
        builder.HasIndex(r => new { r.ReceivingOrganizationId, r.Status })
            .HasDatabaseName("IX_Referrals_ReceivingOrgId_Status");
        builder.HasIndex(r => r.SubjectPartyId)
            .HasDatabaseName("IX_Referrals_SubjectPartyId");

        // Referral Origination — representative visibility scope hot paths.
        builder.HasIndex(r => r.ReferralAttributionId)
            .HasDatabaseName("IX_Referrals_ReferralAttributionId");
        builder.HasIndex(r => new { r.TenantId, r.ReferralAttributionId })
            .HasDatabaseName("IX_Referrals_TenantId_ReferralAttributionId");
        builder.HasIndex(r => new { r.TenantId, r.ReferralAttributionId, r.Status })
            .HasDatabaseName("IX_Referrals_TenantId_ReferralAttributionId_Status");
        builder.HasIndex(r => new { r.TenantId, r.ReferralAttributionId, r.CreatedAtUtc })
            .HasDatabaseName("IX_Referrals_TenantId_ReferralAttributionId_CreatedAtUtc");

        // Relationships
        builder.HasOne(r => r.Provider)
               .WithMany()
               .HasForeignKey(r => r.ProviderId)
               .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.Facility)
               .WithMany()
               .HasForeignKey(r => r.FacilityId)
               .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.SubjectParty)
               .WithMany()
               .HasForeignKey(r => r.SubjectPartyId)
               .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.ReferralAttribution)
               .WithMany()
               .HasForeignKey(r => r.ReferralAttributionId)
               .OnDelete(DeleteBehavior.Restrict);
    }
}
