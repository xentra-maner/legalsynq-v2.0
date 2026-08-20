using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CareConnect.Infrastructure.Data.Migrations
{
    [DbContext(typeof(CareConnectDbContext))]
    [Migration("20260819090000_AddReferralPortalPendingRequests")]
    public partial class AddReferralPortalPendingRequests : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Origin",
                table: "cc_Referrals",
                type: "varchar(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "LawFirm")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "LienCompanyName",
                table: "cc_Referrals",
                type: "varchar(250)",
                maxLength: 250,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "LienCompanyEmail",
                table: "cc_Referrals",
                type: "varchar(320)",
                maxLength: 320,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<Guid>(
                name: "OwningOrganizationId",
                table: "cc_NetworkProviders",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<string>(
                name: "Visibility",
                table: "cc_NetworkProviders",
                type: "varchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Public")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "cc_PendingReferralRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    LawFirmOrganizationId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ReferralAttributionId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Origin = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ClientFirstName = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ClientLastName = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ClientDob = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ClientPhone = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ClientEmail = table.Column<string>(type: "varchar(320)", maxLength: 320, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CaseNumber = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RequestedService = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Urgency = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    TreatmentTypeId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    DateOfAccident = table.Column<DateOnly>(type: "date", nullable: true),
                    Notes = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    LienCompanyName = table.Column<string>(type: "varchar(250)", maxLength: 250, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    LienCompanyEmail = table.Column<string>(type: "varchar(320)", maxLength: 320, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Status = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ConvertedReferralId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    ConvertedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ConvertedByUserId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    UpdatedByUserId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cc_PendingReferralRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cc_PendingReferralRequests_cc_ReferralAttributions_ReferralAttributionId",
                        column: x => x.ReferralAttributionId,
                        principalTable: "cc_ReferralAttributions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cc_PendingReferralRequests_cc_Referrals_ConvertedReferralId",
                        column: x => x.ConvertedReferralId,
                        principalTable: "cc_Referrals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_NetworkProviders_Tenant_OwningOrg_Visibility",
                table: "cc_NetworkProviders",
                columns: new[] { "TenantId", "OwningOrganizationId", "Visibility" });

            migrationBuilder.CreateIndex(
                name: "IX_PendingReferralRequests_ConvertedReferralId",
                table: "cc_PendingReferralRequests",
                column: "ConvertedReferralId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingReferralRequests_Tenant_Attribution_Created",
                table: "cc_PendingReferralRequests",
                columns: new[] { "TenantId", "ReferralAttributionId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PendingReferralRequests_Tenant_LawFirm_Status",
                table: "cc_PendingReferralRequests",
                columns: new[] { "TenantId", "LawFirmOrganizationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_PendingReferralRequests_ReferralAttributionId",
                table: "cc_PendingReferralRequests",
                column: "ReferralAttributionId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "cc_PendingReferralRequests");
            migrationBuilder.DropIndex(name: "IX_NetworkProviders_Tenant_OwningOrg_Visibility", table: "cc_NetworkProviders");
            migrationBuilder.DropColumn(name: "OwningOrganizationId", table: "cc_NetworkProviders");
            migrationBuilder.DropColumn(name: "Visibility", table: "cc_NetworkProviders");
            migrationBuilder.DropColumn(name: "Origin", table: "cc_Referrals");
            migrationBuilder.DropColumn(name: "LienCompanyName", table: "cc_Referrals");
            migrationBuilder.DropColumn(name: "LienCompanyEmail", table: "cc_Referrals");
        }
    }
}
