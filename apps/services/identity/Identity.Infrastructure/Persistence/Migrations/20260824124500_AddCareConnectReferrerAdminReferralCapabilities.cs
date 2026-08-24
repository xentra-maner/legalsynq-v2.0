using System;
using Identity.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Identity.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
[DbContext(typeof(IdentityDbContext))]
[Migration("20260824124500_AddCareConnectReferrerAdminReferralCapabilities")]
public partial class AddCareConnectReferrerAdminReferralCapabilities : Migration
{
    private const string CareConnectReferrerAdminRoleId = "50000000-0000-0000-0000-000000000012";

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql($"""
            INSERT IGNORE INTO `idt_RoleCapabilities` (`ProductRoleId`, `CapabilityId`)
            VALUES
                ('{CareConnectReferrerAdminRoleId}', '60000000-0000-0000-0000-000000000001'),
                ('{CareConnectReferrerAdminRoleId}', '60000000-0000-0000-0000-000000000002'),
                ('{CareConnectReferrerAdminRoleId}', '60000000-0000-0000-0000-000000000003'),
                ('{CareConnectReferrerAdminRoleId}', '60000000-0000-0000-0000-000000000011');
            """);

        migrationBuilder.Sql($"""
            UPDATE `idt_Users` u
            SET u.`AccessVersion` = u.`AccessVersion` + 1,
                u.`UpdatedAtUtc` = UTC_TIMESTAMP(6)
            WHERE EXISTS (
                SELECT 1
                FROM `idt_UserRoleAssignments` ura
                WHERE ura.`UserId` = u.`Id`
                  AND ura.`ProductCode` = 'SYNQ_CARECONNECT'
                  AND ura.`RoleCode` = 'CARECONNECT_REFERRER_ADMIN'
                  AND ura.`AssignmentStatus` = 'Active'
            );
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql($"""
            DELETE FROM `idt_RoleCapabilities`
            WHERE `ProductRoleId` = '{CareConnectReferrerAdminRoleId}'
              AND `CapabilityId` IN (
                  '60000000-0000-0000-0000-000000000001',
                  '60000000-0000-0000-0000-000000000002',
                  '60000000-0000-0000-0000-000000000003',
                  '60000000-0000-0000-0000-000000000011'
              );
            """);

        migrationBuilder.Sql($"""
            UPDATE `idt_Users` u
            SET u.`AccessVersion` = GREATEST(u.`AccessVersion` - 1, 0),
                u.`UpdatedAtUtc` = UTC_TIMESTAMP(6)
            WHERE EXISTS (
                SELECT 1
                FROM `idt_UserRoleAssignments` ura
                WHERE ura.`UserId` = u.`Id`
                  AND ura.`ProductCode` = 'SYNQ_CARECONNECT'
                  AND ura.`RoleCode` = 'CARECONNECT_REFERRER_ADMIN'
                  AND ura.`AssignmentStatus` = 'Active'
            );
            """);
    }
}
