using Identity.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(IdentityDbContext))]
    [Migration("20260823184013_BackfillOrganizationOwnerUserId")]
    public partial class BackfillOrganizationOwnerUserId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // LSV3-1083: backfill OwnerUserId for all existing organizations.
            //
            // Strategy: for each organization, pick the user holding the earliest ADMIN
            // UserOrganizationMembership (by JoinedAtUtc) — the founding member. This
            // matches the convention established for organizations created going forward
            // (AdminEndpoints/TenantProvisioningEndpoints wire SetOwner on the admin user
            // created alongside the org), mirroring BackfillTenantOwnerUserId's approach.
            //
            // Organizations with no active ADMIN membership are left NULL (e.g. globally
            // scoped provider/law-firm orgs auto-provisioned without a specific user, and
            // the LegalSynq internal seed org). This is safe — nothing currently requires
            // OwnerUserId to be non-null.
            migrationBuilder.Sql(@"
UPDATE `idt_Organizations` o
INNER JOIN (
    SELECT m.`OrganizationId`, m.`UserId`
    FROM `idt_UserOrganizationMemberships` m
    WHERE m.`MemberRole` = 'ADMIN'
      AND m.`IsActive`   = 1
) first_admin ON first_admin.`OrganizationId` = o.`Id`
INNER JOIN (
    SELECT m2.`OrganizationId`, MIN(m2.`JoinedAtUtc`) AS `EarliestAt`
    FROM `idt_UserOrganizationMemberships` m2
    WHERE m2.`MemberRole` = 'ADMIN'
      AND m2.`IsActive`   = 1
    GROUP BY m2.`OrganizationId`
) earliest ON earliest.`OrganizationId` = first_admin.`OrganizationId`
INNER JOIN `idt_UserOrganizationMemberships` m3
       ON  m3.`OrganizationId` = earliest.`OrganizationId`
       AND m3.`JoinedAtUtc`    = earliest.`EarliestAt`
       AND m3.`MemberRole`     = 'ADMIN'
       AND m3.`IsActive`       = 1
SET o.`OwnerUserId` = m3.`UserId`
WHERE o.`OwnerUserId` IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Clear backfilled values — only clears rows that were set by this migration
            // (orgs that had an ADMIN membership). Manually-set values are not
            // distinguishable, so Down wipes all OwnerUserId values for safety, matching
            // BackfillTenantOwnerUserId's Down behavior.
            migrationBuilder.Sql(@"
UPDATE `idt_Organizations`
SET `OwnerUserId` = NULL
WHERE `OwnerUserId` IS NOT NULL;");
        }
    }
}
