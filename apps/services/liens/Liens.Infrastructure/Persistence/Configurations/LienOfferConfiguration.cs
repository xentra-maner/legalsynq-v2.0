using Liens.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Liens.Infrastructure.Persistence.Configurations;

public class LienOfferConfiguration : IEntityTypeConfiguration<LienOffer>
{
    public void Configure(EntityTypeBuilder<LienOffer> builder)
    {
        builder.ToTable("liens_LienOffers");

        builder.HasKey(o => o.Id);

        builder.Property(o => o.Id).IsRequired();
        builder.Property(o => o.TenantId).IsRequired();
        builder.Property(o => o.LienId).IsRequired();

        builder.Property(o => o.BuyerOrgId).IsRequired();
        builder.Property(o => o.BuyerCompanyId);
        builder.Property(o => o.SellerOrgId).IsRequired();

        builder.Property(o => o.OfferAmount)
            .IsRequired()
            .HasColumnType("decimal(18,2)");

        builder.Property(o => o.Status)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(o => o.Notes)
            .HasMaxLength(4000);

        builder.Property(o => o.ResponseNotes)
            .HasMaxLength(4000);

        builder.Property(o => o.ExternalReference)
            .HasMaxLength(200);
        builder.Property(o => o.SubmittedByPlatformUserId);

        builder.Property(o => o.OfferedAtUtc).IsRequired();
        builder.Property(o => o.ExpiresAtUtc);
        builder.Property(o => o.RespondedAtUtc);
        builder.Property(o => o.WithdrawnAtUtc);

        builder.Property(o => o.CreatedByUserId).IsRequired();
        builder.Property(o => o.UpdatedByUserId);
        builder.Property(o => o.CreatedAtUtc).IsRequired();
        builder.Property(o => o.UpdatedAtUtc).IsRequired();

        builder.HasIndex(o => new { o.TenantId, o.LienId, o.Status })
            .HasDatabaseName("IX_LienOffers_TenantId_LienId_Status");

        builder.HasIndex(o => new { o.TenantId, o.BuyerOrgId, o.Status })
            .HasDatabaseName("IX_LienOffers_TenantId_BuyerOrgId_Status");

        builder.HasIndex(o => new { o.TenantId, o.SellerOrgId, o.Status })
            .HasDatabaseName("IX_LienOffers_TenantId_SellerOrgId_Status");

        builder.HasIndex(o => new { o.TenantId, o.SellerOrgId, o.OfferedAtUtc })
            .HasDatabaseName("IX_LienOffers_Tenant_Seller_OfferedAt");

        builder.HasIndex(o => new { o.TenantId, o.Status })
            .HasDatabaseName("IX_LienOffers_TenantId_Status");

        builder.HasOne<Lien>()
            .WithMany()
            .HasForeignKey(o => o.LienId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Company>().WithMany().HasForeignKey(o => o.BuyerCompanyId).OnDelete(DeleteBehavior.Restrict);
    }
}
