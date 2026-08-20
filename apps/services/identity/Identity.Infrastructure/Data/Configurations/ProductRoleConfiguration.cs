using Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Identity.Infrastructure.Data.Configurations;

public class ProductRoleConfiguration : IEntityTypeConfiguration<ProductRole>
{
    public void Configure(EntityTypeBuilder<ProductRole> builder)
    {
        builder.ToTable("idt_ProductRoles");

        builder.HasKey(pr => pr.Id);

        builder.Property(pr => pr.ProductId).IsRequired();

        builder.Property(pr => pr.Code)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(pr => pr.Name)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(pr => pr.Description)
            .HasMaxLength(1000);

        // EligibleOrgType column removed in migration 20260330200003_PhaseFRetirement.
        // Eligibility is now exclusively controlled by ProductOrganizationTypeRules.

        builder.Property(pr => pr.IsActive).IsRequired();
        builder.Property(pr => pr.CreatedAtUtc).IsRequired();

        builder.HasIndex(pr => pr.Code).IsUnique();

        builder.HasOne(pr => pr.Product)
            .WithMany(p => p.ProductRoles)
            .HasForeignKey(pr => pr.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasData(
            new { Id = SeedIds.PrCareConnectReferrer,     ProductId = SeedIds.ProductSynqCareConnect, Code = "CARECONNECT_REFERRER",      Name = "CareConnect Referrer",      Description = (string?)"Law firm that refers clients to providers",                    IsActive = true, CreatedAtUtc = SeedIds.SeededAt },
            new { Id = SeedIds.PrCareConnectReceiver,     ProductId = SeedIds.ProductSynqCareConnect, Code = "CARECONNECT_RECEIVER",      Name = "CareConnect Receiver",      Description = (string?)"Provider that receives referrals",                            IsActive = true, CreatedAtUtc = SeedIds.SeededAt },
            new { Id = SeedIds.PrCareConnectNetworkManager, ProductId = SeedIds.ProductSynqCareConnect, Code = "CARECONNECT_NETWORK_MANAGER", Name = "CareConnect Network Manager", Description = (string?)"Organization-scoped manager for CareConnect provider networks", IsActive = true, CreatedAtUtc = SeedIds.SeededAt },
            new { Id = SeedIds.PrSynqLienSeller,          ProductId = SeedIds.ProductSynqLiens,       Code = "SYNQLIEN_SELLER",           Name = "SynqLien Seller",           Description = (string?)"Law firm that creates and offers liens",                       IsActive = true, CreatedAtUtc = SeedIds.SeededAt },
            new { Id = SeedIds.PrSynqLienBuyer,           ProductId = SeedIds.ProductSynqLiens,       Code = "SYNQLIEN_BUYER",            Name = "SynqLien Buyer",            Description = (string?)"Lien owner that purchases liens",                             IsActive = true, CreatedAtUtc = SeedIds.SeededAt },
            new { Id = SeedIds.PrSynqLienHolder,          ProductId = SeedIds.ProductSynqLiens,       Code = "SYNQLIEN_HOLDER",           Name = "SynqLien Holder",           Description = (string?)"Lien owner that services and settles liens",                  IsActive = true, CreatedAtUtc = SeedIds.SeededAt },
            new { Id = SeedIds.PrSynqFundReferrer,        ProductId = SeedIds.ProductSynqFund,        Code = "SYNQFUND_REFERRER",         Name = "SynqFund Referrer",         Description = (string?)"Law firm that submits fund applications on behalf of clients", IsActive = true, CreatedAtUtc = SeedIds.SeededAt },
            new { Id = SeedIds.PrSynqFundFunder,          ProductId = SeedIds.ProductSynqFund,        Code = "SYNQFUND_FUNDER",           Name = "SynqFund Funder",           Description = (string?)"Funder that evaluates and funds applications",                IsActive = true, CreatedAtUtc = SeedIds.SeededAt },
            new { Id = SeedIds.PrSynqFundApplicantPortal, ProductId = SeedIds.ProductSynqFund,        Code = "SYNQFUND_APPLICANT_PORTAL", Name = "SynqFund Applicant Portal", Description = (string?)"Limited read-only portal access for fund applicants",         IsActive = true, CreatedAtUtc = SeedIds.SeededAt },
            new { Id = SeedIds.PrXeniaUser,               ProductId = SeedIds.ProductSynqAI,          Code = "XENIA_USER",                Name = "Xenia User",                Description = (string?)"User access to the Xenia assistant",                           IsActive = true, CreatedAtUtc = SeedIds.SeededAt },
            new { Id = SeedIds.PrXeniaAdmin,              ProductId = SeedIds.ProductSynqAI,          Code = "XENIA_ADMIN",               Name = "Xenia Admin",               Description = (string?)"Administrative access to Xenia assistant configuration",       IsActive = true, CreatedAtUtc = SeedIds.SeededAt }
        );
    }
}
