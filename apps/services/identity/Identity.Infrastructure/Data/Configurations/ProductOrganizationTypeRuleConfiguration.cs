using Identity.Domain;
using Identity.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Identity.Infrastructure.Data.Configurations;

public class ProductOrganizationTypeRuleConfiguration : IEntityTypeConfiguration<ProductOrganizationTypeRule>
{
    public void Configure(EntityTypeBuilder<ProductOrganizationTypeRule> builder)
    {
        builder.ToTable("idt_ProductOrganizationTypeRules");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.ProductId).IsRequired();
        builder.Property(r => r.ProductRoleId).IsRequired();
        builder.Property(r => r.OrganizationTypeId).IsRequired();
        builder.Property(r => r.IsActive).IsRequired();
        builder.Property(r => r.CreatedAtUtc).IsRequired();

        builder.HasIndex(r => new { r.ProductRoleId, r.OrganizationTypeId }).IsUnique();
        builder.HasIndex(r => r.ProductId);
        builder.HasIndex(r => r.OrganizationTypeId);

        builder.HasOne(r => r.Product)
            .WithMany()
            .HasForeignKey(r => r.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.ProductRole)
            .WithMany(pr => pr.OrgTypeRules)
            .HasForeignKey(r => r.ProductRoleId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.OrganizationType)
            .WithMany()
            .HasForeignKey(r => r.OrganizationTypeId)
            .OnDelete(DeleteBehavior.Restrict);

        // Backfill from ProductRole.EligibleOrgType seed values
        builder.HasData(
            new { Id = SeedIds.PrOrgTypeRuleCareConnectReferrerLawFirm,   ProductId = SeedIds.ProductSynqCareConnect, ProductRoleId = SeedIds.PrCareConnectReferrer,     OrganizationTypeId = SeedIds.OrgTypeLawFirm,   IsActive = true, CreatedAtUtc = SeedIds.SeededAt },
            new { Id = SeedIds.PrOrgTypeRuleCareConnectReceiverProvider,  ProductId = SeedIds.ProductSynqCareConnect, ProductRoleId = SeedIds.PrCareConnectReceiver,     OrganizationTypeId = SeedIds.OrgTypeProvider,  IsActive = true, CreatedAtUtc = SeedIds.SeededAt },
            new { Id = SeedIds.PrOrgTypeRuleCareConnectNetworkManagerLawFirm, ProductId = SeedIds.ProductSynqCareConnect, ProductRoleId = SeedIds.PrCareConnectNetworkManager, OrganizationTypeId = SeedIds.OrgTypeLawFirm, IsActive = true, CreatedAtUtc = SeedIds.SeededAt },
            new { Id = SeedIds.PrOrgTypeRuleCareConnectNetworkManagerLienOwner, ProductId = SeedIds.ProductSynqCareConnect, ProductRoleId = SeedIds.PrCareConnectNetworkManager, OrganizationTypeId = SeedIds.OrgTypeLienOwner, IsActive = true, CreatedAtUtc = SeedIds.SeededAt },
            new { Id = SeedIds.PrOrgTypeRuleSynqLienSellerLawFirm,        ProductId = SeedIds.ProductSynqLiens,       ProductRoleId = SeedIds.PrSynqLienSeller,          OrganizationTypeId = SeedIds.OrgTypeLawFirm,   IsActive = true, CreatedAtUtc = SeedIds.SeededAt },
            new { Id = SeedIds.PrOrgTypeRuleSynqLienBuyerLienOwner,       ProductId = SeedIds.ProductSynqLiens,       ProductRoleId = SeedIds.PrSynqLienBuyer,           OrganizationTypeId = SeedIds.OrgTypeLienOwner, IsActive = true, CreatedAtUtc = SeedIds.SeededAt },
            new { Id = SeedIds.PrOrgTypeRuleSynqLienHolderLienOwner,      ProductId = SeedIds.ProductSynqLiens,       ProductRoleId = SeedIds.PrSynqLienHolder,          OrganizationTypeId = SeedIds.OrgTypeLienOwner, IsActive = true, CreatedAtUtc = SeedIds.SeededAt },
            new { Id = SeedIds.PrOrgTypeRuleSynqFundReferrerLawFirm,      ProductId = SeedIds.ProductSynqFund,        ProductRoleId = SeedIds.PrSynqFundReferrer,        OrganizationTypeId = SeedIds.OrgTypeLawFirm,   IsActive = true, CreatedAtUtc = SeedIds.SeededAt },
            new { Id = SeedIds.PrOrgTypeRuleSynqFundFunderFunder,         ProductId = SeedIds.ProductSynqFund,        ProductRoleId = SeedIds.PrSynqFundFunder,          OrganizationTypeId = SeedIds.OrgTypeFunder,    IsActive = true, CreatedAtUtc = SeedIds.SeededAt },
            new { Id = SeedIds.PrOrgTypeRuleXeniaUserLawFirm,             ProductId = SeedIds.ProductSynqAI,          ProductRoleId = SeedIds.PrXeniaUser,               OrganizationTypeId = SeedIds.OrgTypeLawFirm,   IsActive = true, CreatedAtUtc = SeedIds.SeededAt },
            new { Id = SeedIds.PrOrgTypeRuleXeniaUserProvider,            ProductId = SeedIds.ProductSynqAI,          ProductRoleId = SeedIds.PrXeniaUser,               OrganizationTypeId = SeedIds.OrgTypeProvider,  IsActive = true, CreatedAtUtc = SeedIds.SeededAt },
            new { Id = SeedIds.PrOrgTypeRuleXeniaUserFunder,              ProductId = SeedIds.ProductSynqAI,          ProductRoleId = SeedIds.PrXeniaUser,               OrganizationTypeId = SeedIds.OrgTypeFunder,    IsActive = true, CreatedAtUtc = SeedIds.SeededAt },
            new { Id = SeedIds.PrOrgTypeRuleXeniaUserLienOwner,           ProductId = SeedIds.ProductSynqAI,          ProductRoleId = SeedIds.PrXeniaUser,               OrganizationTypeId = SeedIds.OrgTypeLienOwner, IsActive = true, CreatedAtUtc = SeedIds.SeededAt },
            new { Id = SeedIds.PrOrgTypeRuleXeniaAdminInternal,           ProductId = SeedIds.ProductSynqAI,          ProductRoleId = SeedIds.PrXeniaAdmin,              OrganizationTypeId = SeedIds.OrgTypeInternal,  IsActive = true, CreatedAtUtc = SeedIds.SeededAt }
        );
    }
}
