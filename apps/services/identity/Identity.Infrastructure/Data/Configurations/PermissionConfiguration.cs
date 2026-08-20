using Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Identity.Infrastructure.Data.Configurations;

public class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> builder)
    {
        builder.ToTable("idt_Capabilities");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.ProductId).IsRequired();

        builder.Property(c => c.Code)
            .IsRequired()
            .HasMaxLength(150);

        builder.Property(c => c.Name)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(c => c.Description)
            .HasMaxLength(1000);

        builder.Property(c => c.Category)
            .HasMaxLength(100);

        builder.Property(c => c.IsActive).IsRequired();
        builder.Property(c => c.CreatedAtUtc).IsRequired();
        builder.Property(c => c.UpdatedAtUtc);
        builder.Property(c => c.CreatedBy);
        builder.Property(c => c.UpdatedBy);

        builder.HasIndex(c => c.Code).IsUnique();
        builder.HasIndex(c => c.ProductId);

        builder.HasOne(c => c.Product)
            .WithMany(p => p.Permissions)
            .HasForeignKey(c => c.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        var cc  = SeedIds.ProductSynqCareConnect;
        var sl  = SeedIds.ProductSynqLiens;
        var sf  = SeedIds.ProductSynqFund;
        var ai  = SeedIds.ProductSynqAI;
        var at  = SeedIds.SeededAt;

        builder.HasData(
            // CareConnect — Referral
            new { Id = SeedIds.PermReferralCreate,        ProductId = cc, Code = "SYNQ_CARECONNECT.referral:create",         Name = "Create Referral",              Description = (string?)"Create a new referral",                          Category = (string?)"Referral",   IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },
            new { Id = SeedIds.PermReferralReadOwn,       ProductId = cc, Code = "SYNQ_CARECONNECT.referral:read:own",       Name = "Read Own Referrals",           Description = (string?)"View referrals you initiated",                   Category = (string?)"Referral",   IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },
            new { Id = SeedIds.PermReferralCancel,        ProductId = cc, Code = "SYNQ_CARECONNECT.referral:cancel",         Name = "Cancel Referral",              Description = (string?)"Cancel a referral you initiated",                Category = (string?)"Referral",   IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },
            new { Id = SeedIds.PermReferralReadAddressed, ProductId = cc, Code = "SYNQ_CARECONNECT.referral:read:addressed", Name = "Read Addressed Referrals",     Description = (string?)"View referrals addressed to your organization",  Category = (string?)"Referral",   IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },
            new { Id = SeedIds.PermReferralAccept,        ProductId = cc, Code = "SYNQ_CARECONNECT.referral:accept",         Name = "Accept Referral",              Description = (string?)"Accept an incoming referral",                    Category = (string?)"Referral",   IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },
            new { Id = SeedIds.PermReferralDecline,       ProductId = cc, Code = "SYNQ_CARECONNECT.referral:decline",        Name = "Decline Referral",             Description = (string?)"Decline an incoming referral",                   Category = (string?)"Referral",   IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },
            // CareConnect — Provider
            new { Id = SeedIds.PermProviderSearch,        ProductId = cc, Code = "SYNQ_CARECONNECT.provider:search",         Name = "Search Providers",             Description = (string?)"Search for providers by criteria",               Category = (string?)"Provider",   IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },
            new { Id = SeedIds.PermProviderMap,           ProductId = cc, Code = "SYNQ_CARECONNECT.provider:map",            Name = "View Provider Map",            Description = (string?)"View providers on a geographic map",             Category = (string?)"Provider",   IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },
            new { Id = SeedIds.PermProviderManage,        ProductId = cc, Code = "SYNQ_CARECONNECT.provider:manage",         Name = "Manage Providers",             Description = (string?)"Manage organization provider network entries",    Category = (string?)"Provider",   IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },
            // CareConnect — Appointment
            new { Id = SeedIds.PermAppointmentCreate,     ProductId = cc, Code = "SYNQ_CARECONNECT.appointment:create",      Name = "Create Appointment",           Description = (string?)"Schedule an appointment",                        Category = (string?)"Appointment", IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },
            new { Id = SeedIds.PermAppointmentUpdate,     ProductId = cc, Code = "SYNQ_CARECONNECT.appointment:update",      Name = "Update Appointment",           Description = (string?)"Modify an existing appointment",                 Category = (string?)"Appointment", IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },
            new { Id = SeedIds.PermAppointmentReadOwn,    ProductId = cc, Code = "SYNQ_CARECONNECT.appointment:read:own",    Name = "Read Own Appointments",        Description = (string?)"View your organization's appointments",          Category = (string?)"Appointment", IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },
            // SynqLien
            new { Id = SeedIds.PermLienCreate,   ProductId = sl, Code = "SYNQ_LIENS.lien:create",    Name = "Create Lien",           Description = (string?)"Create a new lien record",          Category = (string?)"Lien", IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },
            new { Id = SeedIds.PermLienOffer,    ProductId = sl, Code = "SYNQ_LIENS.lien:offer",     Name = "Offer Lien",            Description = (string?)"Offer a lien for sale",              Category = (string?)"Lien", IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },
            new { Id = SeedIds.PermLienReadOwn,  ProductId = sl, Code = "SYNQ_LIENS.lien:read:own",  Name = "Read Own Liens",        Description = (string?)"View liens you created",             Category = (string?)"Lien", IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },
            new { Id = SeedIds.PermLienBrowse,   ProductId = sl, Code = "SYNQ_LIENS.lien:browse",    Name = "Browse Liens",          Description = (string?)"Browse available liens for purchase", Category = (string?)"Lien", IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },
            new { Id = SeedIds.PermLienPurchase, ProductId = sl, Code = "SYNQ_LIENS.lien:purchase",  Name = "Purchase Lien",         Description = (string?)"Purchase a lien",                    Category = (string?)"Lien", IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },
            new { Id = SeedIds.PermLienReadHeld, ProductId = sl, Code = "SYNQ_LIENS.lien:read:held", Name = "Read Held Liens",       Description = (string?)"View liens you hold",                Category = (string?)"Lien", IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },
            new { Id = SeedIds.PermLienService,  ProductId = sl, Code = "SYNQ_LIENS.lien:service",   Name = "Service Lien",          Description = (string?)"Service an active lien",             Category = (string?)"Lien", IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },
            new { Id = SeedIds.PermLienSettle,   ProductId = sl, Code = "SYNQ_LIENS.lien:settle",    Name = "Settle Lien",           Description = (string?)"Settle and close a lien",            Category = (string?)"Lien", IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },
            // SynqLien — Task
            new { Id = SeedIds.PermTaskRead,     ProductId = sl, Code = "SYNQ_LIENS.task:read",       Name = "Read Tasks",            Description = (string?)"View tasks within a lien or case",  Category = (string?)"Task", IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },
            new { Id = SeedIds.PermTaskCreate,   ProductId = sl, Code = "SYNQ_LIENS.task:create",     Name = "Create Task",           Description = (string?)"Create a new task",                  Category = (string?)"Task", IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },
            new { Id = SeedIds.PermTaskEditOwn,  ProductId = sl, Code = "SYNQ_LIENS.task:edit:own",   Name = "Edit Own Tasks",        Description = (string?)"Update status and details of tasks assigned to or created by you", Category = (string?)"Task", IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },
            new { Id = SeedIds.PermTaskEditAll,  ProductId = sl, Code = "SYNQ_LIENS.task:edit:all",   Name = "Edit All Tasks",        Description = (string?)"Update any task regardless of ownership", Category = (string?)"Task", IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },
            new { Id = SeedIds.PermTaskAssign,   ProductId = sl, Code = "SYNQ_LIENS.task:assign",     Name = "Assign Task",           Description = (string?)"Assign a task to a user or team",   Category = (string?)"Task", IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },
            new { Id = SeedIds.PermTaskComplete, ProductId = sl, Code = "SYNQ_LIENS.task:complete",   Name = "Complete Task",         Description = (string?)"Mark a task as completed",           Category = (string?)"Task", IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },
            new { Id = SeedIds.PermTaskCancel,   ProductId = sl, Code = "SYNQ_LIENS.task:cancel",     Name = "Cancel Task",           Description = (string?)"Cancel a task",                      Category = (string?)"Task", IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },
            // SynqFund — Application
            new { Id = SeedIds.PermApplicationCreate,        ProductId = sf, Code = "SYNQ_FUND.application:create",         Name = "Create Application",           Description = (string?)"Submit a new fund application",                     Category = (string?)"Application", IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },
            new { Id = SeedIds.PermApplicationReadOwn,       ProductId = sf, Code = "SYNQ_FUND.application:read:own",       Name = "Read Own Applications",        Description = (string?)"View applications you submitted",                    Category = (string?)"Application", IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },
            new { Id = SeedIds.PermApplicationCancel,        ProductId = sf, Code = "SYNQ_FUND.application:cancel",         Name = "Cancel Application",           Description = (string?)"Cancel a pending application",                      Category = (string?)"Application", IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },
            new { Id = SeedIds.PermApplicationReadAddressed, ProductId = sf, Code = "SYNQ_FUND.application:read:addressed", Name = "Read Addressed Applications",  Description = (string?)"View applications addressed to your organization",   Category = (string?)"Application", IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },
            new { Id = SeedIds.PermApplicationEvaluate,      ProductId = sf, Code = "SYNQ_FUND.application:evaluate",       Name = "Evaluate Application",         Description = (string?)"Perform underwriting evaluation of an application",  Category = (string?)"Application", IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },
            new { Id = SeedIds.PermApplicationApprove,       ProductId = sf, Code = "SYNQ_FUND.application:approve",        Name = "Approve Application",          Description = (string?)"Approve and fund an application",                   Category = (string?)"Application", IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },
            new { Id = SeedIds.PermApplicationDecline,       ProductId = sf, Code = "SYNQ_FUND.application:decline",        Name = "Decline Application",          Description = (string?)"Decline a fund application",                        Category = (string?)"Application", IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },
            // SynqFund — Party
            new { Id = SeedIds.PermPartyCreate,              ProductId = sf, Code = "SYNQ_FUND.party:create",               Name = "Create Party",                 Description = (string?)"Create a party profile for a client",               Category = (string?)"Party",       IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },
            new { Id = SeedIds.PermPartyReadOwn,             ProductId = sf, Code = "SYNQ_FUND.party:read:own",             Name = "Read Own Party",               Description = (string?)"View party profiles you created",                   Category = (string?)"Party",       IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },
            new { Id = SeedIds.PermApplicationStatusView,    ProductId = sf, Code = "SYNQ_FUND.application:status:view",    Name = "View Application Status",      Description = (string?)"View the status of a fund application (party portal)", Category = (string?)"Application", IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },

            // LS-ID-TNT-011: Tenant-level permission catalog (SYNQ_PLATFORM pseudo-product).
            // Resolved via system Role → RolePermissionAssignment, not via ProductRole.
            // These permissions govern platform-within-tenant administrative operations.
            new { Id = SeedIds.PermTenantUsersView,         ProductId = SeedIds.ProductSynqPlatform, Code = "TENANT.users:view",         Name = "View Tenant Users",         Description = (string?)"View the list of users in the tenant",                    Category = (string?)"Users",        IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },
            new { Id = SeedIds.PermTenantUsersManage,       ProductId = SeedIds.ProductSynqPlatform, Code = "TENANT.users:manage",       Name = "Manage Tenant Users",       Description = (string?)"Create, edit, and deactivate users in the tenant",      Category = (string?)"Users",        IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },
            new { Id = SeedIds.PermTenantGroupsManage,      ProductId = SeedIds.ProductSynqPlatform, Code = "TENANT.groups:manage",      Name = "Manage Access Groups",      Description = (string?)"Create, edit, and delete tenant access groups",         Category = (string?)"Groups",       IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },
            new { Id = SeedIds.PermTenantRolesAssign,       ProductId = SeedIds.ProductSynqPlatform, Code = "TENANT.roles:assign",       Name = "Assign Roles",              Description = (string?)"Assign or revoke roles for tenant users",               Category = (string?)"Roles",        IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },
            new { Id = SeedIds.PermTenantProductsAssign,    ProductId = SeedIds.ProductSynqPlatform, Code = "TENANT.products:assign",    Name = "Assign Product Access",     Description = (string?)"Assign or revoke product access for tenant users",      Category = (string?)"Products",     IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },
            new { Id = SeedIds.PermTenantSettingsManage,    ProductId = SeedIds.ProductSynqPlatform, Code = "TENANT.settings:manage",    Name = "Manage Tenant Settings",    Description = (string?)"Update tenant configuration and preferences",           Category = (string?)"Settings",     IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },
            new { Id = SeedIds.PermTenantAuditView,         ProductId = SeedIds.ProductSynqPlatform, Code = "TENANT.audit:view",         Name = "View Audit Logs",           Description = (string?)"View identity and access audit events for the tenant",  Category = (string?)"Audit",        IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },
            new { Id = SeedIds.PermTenantInvitationsManage, ProductId = SeedIds.ProductSynqPlatform, Code = "TENANT.invitations:manage", Name = "Manage User Invitations",   Description = (string?)"Send, resend, and revoke user invitations",             Category = (string?)"Invitations",  IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },

            // Xenia / SynqAI
            new { Id = SeedIds.PermXeniaAssistantUse,    ProductId = ai, Code = "SYNQ_AI.assistant:use",    Name = "Use Xenia Assistant",    Description = (string?)"Create and use Xenia assistant conversations",          Category = (string?)"Assistant", IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },
            new { Id = SeedIds.PermXeniaAssistantManage, ProductId = ai, Code = "SYNQ_AI.assistant:manage", Name = "Manage Xenia Assistant", Description = (string?)"Configure Xenia assistant providers, agents, and quotas", Category = (string?)"Assistant", IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null },
            new { Id = SeedIds.PermXeniaUsageRead,       ProductId = ai, Code = "SYNQ_AI.usage:read",       Name = "View Xenia Usage",       Description = (string?)"View Xenia assistant usage and cost telemetry",          Category = (string?)"Usage",     IsActive = true, CreatedAtUtc = at, UpdatedAtUtc = (DateTime?)null, CreatedBy = (Guid?)null, UpdatedBy = (Guid?)null }
        );
    }
}
