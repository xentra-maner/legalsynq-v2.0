using Identity.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(IdentityDbContext))]
    [Migration("20260824120000_MigrateCareConnectLawFirmReferrerToAdmin")]
    public partial class MigrateCareConnectLawFirmReferrerToAdmin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Law-firm self-enrollment previously stored CARECONNECT_REFERRER.
            // CARECONNECT_REFERRER_ADMIN now owns law-firm account administration and
            // implies referrer access through EffectiveAccessService.
            migrationBuilder.Sql(@"
UPDATE `idt_UserRoleAssignments` referrer
INNER JOIN `idt_Organizations` org
        ON org.`Id` = referrer.`OrganizationId`
LEFT JOIN `idt_UserRoleAssignments` admin_role
       ON admin_role.`TenantId` = referrer.`TenantId`
      AND admin_role.`UserId` = referrer.`UserId`
      AND admin_role.`ProductCode` = 'SYNQ_CARECONNECT'
      AND admin_role.`RoleCode` = 'CARECONNECT_REFERRER_ADMIN'
      AND admin_role.`AssignmentStatus` = 'Active'
SET referrer.`RoleCode` = 'CARECONNECT_REFERRER_ADMIN',
    referrer.`UpdatedAtUtc` = UTC_TIMESTAMP()
WHERE referrer.`ProductCode` = 'SYNQ_CARECONNECT'
  AND referrer.`RoleCode` = 'CARECONNECT_REFERRER'
  AND referrer.`AssignmentStatus` = 'Active'
  AND (org.`OrgType` = 'LAW_FIRM'
       OR org.`OrganizationTypeId` = '70000000-0000-0000-0000-000000000002')
  AND admin_role.`Id` IS NULL;");

            migrationBuilder.Sql(@"
UPDATE `idt_UserRoleAssignments` referrer
INNER JOIN `idt_Organizations` org
        ON org.`Id` = referrer.`OrganizationId`
INNER JOIN `idt_UserRoleAssignments` admin_role
        ON admin_role.`TenantId` = referrer.`TenantId`
       AND admin_role.`UserId` = referrer.`UserId`
       AND admin_role.`ProductCode` = 'SYNQ_CARECONNECT'
       AND admin_role.`RoleCode` = 'CARECONNECT_REFERRER_ADMIN'
       AND admin_role.`AssignmentStatus` = 'Active'
       AND (admin_role.`OrganizationId` = referrer.`OrganizationId`
            OR (admin_role.`OrganizationId` IS NULL AND referrer.`OrganizationId` IS NULL))
SET referrer.`AssignmentStatus` = 'Removed',
    referrer.`RemovedAtUtc` = UTC_TIMESTAMP(),
    referrer.`UpdatedAtUtc` = UTC_TIMESTAMP()
WHERE referrer.`ProductCode` = 'SYNQ_CARECONNECT'
  AND referrer.`RoleCode` = 'CARECONNECT_REFERRER'
  AND referrer.`AssignmentStatus` = 'Active'
  AND (org.`OrgType` = 'LAW_FIRM'
       OR org.`OrganizationTypeId` = '70000000-0000-0000-0000-000000000002');");

            migrationBuilder.Sql(@"
UPDATE `idt_Users` u
INNER JOIN (
    SELECT DISTINCT admin_role.`UserId`
    FROM `idt_UserRoleAssignments` admin_role
    INNER JOIN `idt_Organizations` org
            ON org.`Id` = admin_role.`OrganizationId`
    WHERE admin_role.`ProductCode` = 'SYNQ_CARECONNECT'
      AND admin_role.`RoleCode` = 'CARECONNECT_REFERRER_ADMIN'
      AND admin_role.`AssignmentStatus` = 'Active'
      AND (org.`OrgType` = 'LAW_FIRM'
           OR org.`OrganizationTypeId` = '70000000-0000-0000-0000-000000000002')
) affected ON affected.`UserId` = u.`Id`
SET u.`AccessVersion` = u.`AccessVersion` + 1,
    u.`UpdatedAtUtc` = UTC_TIMESTAMP();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
UPDATE `idt_UserRoleAssignments` admin_role
INNER JOIN `idt_Organizations` org
        ON org.`Id` = admin_role.`OrganizationId`
LEFT JOIN `idt_UserRoleAssignments` referrer
       ON referrer.`TenantId` = admin_role.`TenantId`
      AND referrer.`UserId` = admin_role.`UserId`
      AND referrer.`ProductCode` = 'SYNQ_CARECONNECT'
      AND referrer.`RoleCode` = 'CARECONNECT_REFERRER'
      AND referrer.`AssignmentStatus` = 'Active'
SET admin_role.`RoleCode` = 'CARECONNECT_REFERRER',
    admin_role.`UpdatedAtUtc` = UTC_TIMESTAMP()
WHERE admin_role.`ProductCode` = 'SYNQ_CARECONNECT'
  AND admin_role.`RoleCode` = 'CARECONNECT_REFERRER_ADMIN'
  AND admin_role.`AssignmentStatus` = 'Active'
  AND (org.`OrgType` = 'LAW_FIRM'
       OR org.`OrganizationTypeId` = '70000000-0000-0000-0000-000000000002')
  AND referrer.`Id` IS NULL;");

            migrationBuilder.Sql(@"
UPDATE `idt_Users` u
INNER JOIN (
    SELECT DISTINCT referrer.`UserId`
    FROM `idt_UserRoleAssignments` referrer
    INNER JOIN `idt_Organizations` org
            ON org.`Id` = referrer.`OrganizationId`
    WHERE referrer.`ProductCode` = 'SYNQ_CARECONNECT'
      AND referrer.`RoleCode` = 'CARECONNECT_REFERRER'
      AND referrer.`AssignmentStatus` = 'Active'
      AND (org.`OrgType` = 'LAW_FIRM'
           OR org.`OrganizationTypeId` = '70000000-0000-0000-0000-000000000002')
) affected ON affected.`UserId` = u.`Id`
SET u.`AccessVersion` = u.`AccessVersion` + 1,
    u.`UpdatedAtUtc` = UTC_TIMESTAMP();");
        }
    }
}
