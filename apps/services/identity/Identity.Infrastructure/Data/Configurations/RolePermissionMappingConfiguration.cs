using Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Identity.Infrastructure.Data.Configurations;

public class RolePermissionMappingConfiguration : IEntityTypeConfiguration<RolePermissionMapping>
{
    public void Configure(EntityTypeBuilder<RolePermissionMapping> builder)
    {
        builder.ToTable("idt_RoleCapabilities");

        builder.HasKey(rc => new { rc.ProductRoleId, rc.PermissionId });

        builder.Property(rc => rc.PermissionId).HasColumnName("CapabilityId");

        builder.HasIndex(rc => rc.PermissionId);

        builder.HasOne(rc => rc.ProductRole)
            .WithMany(pr => pr.RolePermissionMappings)
            .HasForeignKey(rc => rc.ProductRoleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(rc => rc.Permission)
            .WithMany(c => c.RolePermissionMappings)
            .HasForeignKey(rc => rc.PermissionId)
            .OnDelete(DeleteBehavior.Cascade);

        var referrer  = SeedIds.PrCareConnectReferrer;
        var receiver  = SeedIds.PrCareConnectReceiver;
        var networkManager = SeedIds.PrCareConnectNetworkManager;
        var seller    = SeedIds.PrSynqLienSeller;
        var buyer     = SeedIds.PrSynqLienBuyer;
        var holder    = SeedIds.PrSynqLienHolder;
        var fReferrer = SeedIds.PrSynqFundReferrer;
        var funder    = SeedIds.PrSynqFundFunder;
        var portal    = SeedIds.PrSynqFundApplicantPortal;
        var xeniaUser = SeedIds.PrXeniaUser;
        var xeniaAdmin = SeedIds.PrXeniaAdmin;

        builder.HasData(
            new { ProductRoleId = referrer, PermissionId = SeedIds.PermReferralCreate },
            new { ProductRoleId = referrer, PermissionId = SeedIds.PermReferralReadOwn },
            new { ProductRoleId = referrer, PermissionId = SeedIds.PermReferralCancel },
            new { ProductRoleId = referrer, PermissionId = SeedIds.PermProviderSearch },
            new { ProductRoleId = referrer, PermissionId = SeedIds.PermProviderMap },
            new { ProductRoleId = referrer, PermissionId = SeedIds.PermAppointmentReadOwn },

            new { ProductRoleId = networkManager, PermissionId = SeedIds.PermProviderSearch },
            new { ProductRoleId = networkManager, PermissionId = SeedIds.PermProviderMap },
            new { ProductRoleId = networkManager, PermissionId = SeedIds.PermProviderManage },

            new { ProductRoleId = receiver, PermissionId = SeedIds.PermReferralReadAddressed },
            new { ProductRoleId = receiver, PermissionId = SeedIds.PermReferralAccept },
            new { ProductRoleId = receiver, PermissionId = SeedIds.PermReferralDecline },
            new { ProductRoleId = receiver, PermissionId = SeedIds.PermAppointmentCreate },
            new { ProductRoleId = receiver, PermissionId = SeedIds.PermAppointmentUpdate },
            new { ProductRoleId = receiver, PermissionId = SeedIds.PermAppointmentReadOwn },

            new { ProductRoleId = seller, PermissionId = SeedIds.PermLienCreate },
            new { ProductRoleId = seller, PermissionId = SeedIds.PermLienOffer },
            new { ProductRoleId = seller, PermissionId = SeedIds.PermLienReadOwn },

            new { ProductRoleId = buyer, PermissionId = SeedIds.PermLienBrowse },
            new { ProductRoleId = buyer, PermissionId = SeedIds.PermLienPurchase },
            new { ProductRoleId = buyer, PermissionId = SeedIds.PermLienReadHeld },

            new { ProductRoleId = holder, PermissionId = SeedIds.PermLienReadHeld },
            new { ProductRoleId = holder, PermissionId = SeedIds.PermLienService },
            new { ProductRoleId = holder, PermissionId = SeedIds.PermLienSettle },

            // SynqLien — Task permissions
            // Seller (law firm): creates and manages their own tasks
            new { ProductRoleId = seller, PermissionId = SeedIds.PermTaskRead },
            new { ProductRoleId = seller, PermissionId = SeedIds.PermTaskCreate },
            new { ProductRoleId = seller, PermissionId = SeedIds.PermTaskEditOwn },
            new { ProductRoleId = seller, PermissionId = SeedIds.PermTaskComplete },
            new { ProductRoleId = seller, PermissionId = SeedIds.PermTaskCancel },

            // Buyer: read-only view of tasks on their acquired liens
            new { ProductRoleId = buyer, PermissionId = SeedIds.PermTaskRead },

            // Holder (services liens): full task management capability
            new { ProductRoleId = holder, PermissionId = SeedIds.PermTaskRead },
            new { ProductRoleId = holder, PermissionId = SeedIds.PermTaskCreate },
            new { ProductRoleId = holder, PermissionId = SeedIds.PermTaskEditOwn },
            new { ProductRoleId = holder, PermissionId = SeedIds.PermTaskEditAll },
            new { ProductRoleId = holder, PermissionId = SeedIds.PermTaskAssign },
            new { ProductRoleId = holder, PermissionId = SeedIds.PermTaskComplete },
            new { ProductRoleId = holder, PermissionId = SeedIds.PermTaskCancel },

            new { ProductRoleId = fReferrer, PermissionId = SeedIds.PermApplicationCreate },
            new { ProductRoleId = fReferrer, PermissionId = SeedIds.PermApplicationReadOwn },
            new { ProductRoleId = fReferrer, PermissionId = SeedIds.PermApplicationCancel },
            new { ProductRoleId = fReferrer, PermissionId = SeedIds.PermPartyCreate },
            new { ProductRoleId = fReferrer, PermissionId = SeedIds.PermPartyReadOwn },

            new { ProductRoleId = funder, PermissionId = SeedIds.PermApplicationReadAddressed },
            new { ProductRoleId = funder, PermissionId = SeedIds.PermApplicationEvaluate },
            new { ProductRoleId = funder, PermissionId = SeedIds.PermApplicationApprove },
            new { ProductRoleId = funder, PermissionId = SeedIds.PermApplicationDecline },

            new { ProductRoleId = portal, PermissionId = SeedIds.PermApplicationStatusView },
            new { ProductRoleId = portal, PermissionId = SeedIds.PermPartyReadOwn },

            new { ProductRoleId = xeniaUser, PermissionId = SeedIds.PermXeniaAssistantUse },
            new { ProductRoleId = xeniaAdmin, PermissionId = SeedIds.PermXeniaAssistantUse },
            new { ProductRoleId = xeniaAdmin, PermissionId = SeedIds.PermXeniaAssistantManage },
            new { ProductRoleId = xeniaAdmin, PermissionId = SeedIds.PermXeniaUsageRead }
        );
    }
}
