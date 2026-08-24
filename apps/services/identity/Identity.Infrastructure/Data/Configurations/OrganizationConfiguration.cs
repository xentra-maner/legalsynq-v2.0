using Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Identity.Infrastructure.Data.Configurations;

public class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> builder)
    {
        builder.ToTable("idt_Organizations");

        builder.HasKey(o => o.Id);

        builder.Property(o => o.TenantId).IsRequired(false);

        builder.Property(o => o.Name)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(o => o.DisplayName)
            .HasMaxLength(300);

        builder.Property(o => o.OrgType)
            .IsRequired()
            .HasMaxLength(50);

        // Phase 1: typed org-type FK (nullable; backfilled in migration)
        builder.Property(o => o.OrganizationTypeId);

        builder.Property(o => o.ProviderMode)
            .IsRequired()
            .HasMaxLength(20)
            .HasDefaultValue("sell");

        builder.Property(o => o.IsActive).IsRequired();
        builder.Property(o => o.CreatedAtUtc).IsRequired();
        builder.Property(o => o.UpdatedAtUtc).IsRequired();
        builder.Property(o => o.CreatedByUserId);
        builder.Property(o => o.UpdatedByUserId);
        builder.Property(o => o.OwnerUserId);

        builder.HasIndex(o => new { o.TenantId, o.Name }).IsUnique();
        builder.HasIndex(o => new { o.OrgType, o.Name });
        builder.HasIndex(o => new { o.TenantId, o.OrgType });
        builder.HasIndex(o => o.OrganizationTypeId);
        builder.HasIndex(o => o.OwnerUserId);

        builder.HasOne(o => o.Tenant)
            .WithMany(t => t.Organizations)
            .HasForeignKey(o => o.TenantId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(o => o.OrganizationTypeRef)
            .WithMany(ot => ot.Organizations)
            .HasForeignKey(o => o.OrganizationTypeId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasData(new
        {
            Id = SeedIds.OrgLegalSynq,
            TenantId = SeedIds.TenantLegalSynq,
            Name = "LegalSynq Platform",
            DisplayName = (string?)"LegalSynq Internal",
            OrgType = Domain.OrgType.Internal,
            OrganizationTypeId = (Guid?)SeedIds.OrgTypeInternal,
            ProviderMode = Domain.ProviderModes.Sell,
            IsActive = true,
            CreatedAtUtc = SeedIds.SeededAt,
            UpdatedAtUtc = SeedIds.SeededAt,
            CreatedByUserId = (Guid?)null,
            UpdatedByUserId = (Guid?)null,
            OwnerUserId = (Guid?)null
        });
    }
}
