using Identity.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Identity.Infrastructure.Persistence.Migrations;

/// <summary>
/// Seeds the tenant-portal Role Management permission codes
/// (<c>TENANT.roles:view</c> / <c>TENANT.roles:manage</c>) into the
/// SYNQ_PLATFORM capability catalog and grants them to the TenantAdmin
/// system role. Data-only — no schema/model change, so no model snapshot update.
/// </summary>
[DbContext(typeof(IdentityDbContext))]
[Migration("20260907000000_AddTenantRoleManagementPermissions")]
public partial class AddTenantRoleManagementPermissions : Migration
{
    // SeedIds.ProductSynqPlatform
    private const string ProductSynqPlatform = "10000000-0000-0000-0000-000000000006";
    // SeedIds.RoleTenantAdmin
    private const string RoleTenantAdmin = "30000000-0000-0000-0000-000000000002";
    private const string CapRolesView   = "6c000000-0000-0000-0000-000000000001";
    private const string CapRolesManage = "6c000000-0000-0000-0000-000000000002";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql($$"""
            INSERT IGNORE INTO `idt_Capabilities`
                (`Id`,`ProductId`,`Code`,`Name`,`Description`,`Category`,`IsActive`,`CreatedAtUtc`,`UpdatedAtUtc`,`CreatedBy`,`UpdatedBy`)
            VALUES
                ('{{CapRolesView}}','{{ProductSynqPlatform}}','TENANT.roles:view','View Roles','View tenant custom roles and their permissions','Roles',1,UTC_TIMESTAMP(6),NULL,NULL,NULL),
                ('{{CapRolesManage}}','{{ProductSynqPlatform}}','TENANT.roles:manage','Manage Roles','Create, edit, and delete tenant custom roles','Roles',1,UTC_TIMESTAMP(6),NULL,NULL,NULL);

            INSERT IGNORE INTO `idt_RoleCapabilityAssignments`
                (`RoleId`,`CapabilityId`,`AssignedAtUtc`,`AssignedByUserId`)
            VALUES
                ('{{RoleTenantAdmin}}','{{CapRolesView}}',UTC_TIMESTAMP(6),NULL),
                ('{{RoleTenantAdmin}}','{{CapRolesManage}}',UTC_TIMESTAMP(6),NULL);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql($$"""
            DELETE FROM `idt_RoleCapabilityAssignments`
             WHERE `CapabilityId` IN ('{{CapRolesView}}','{{CapRolesManage}}');
            DELETE FROM `idt_Capabilities`
             WHERE `Id` IN ('{{CapRolesView}}','{{CapRolesManage}}');
            """);
    }
}
