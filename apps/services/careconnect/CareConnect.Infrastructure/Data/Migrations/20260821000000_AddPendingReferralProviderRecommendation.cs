using System;
using CareConnect.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CareConnect.Infrastructure.Data.Migrations
{
    [DbContext(typeof(CareConnectDbContext))]
    [Migration("20260821000000_AddPendingReferralProviderRecommendation")]
    public partial class AddPendingReferralProviderRecommendation : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RecommendedProviderId",
                table: "cc_PendingReferralRequests",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<Guid>(
                name: "RecommendedFacilityId",
                table: "cc_PendingReferralRequests",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<string>(
                name: "RecommendedProviderName",
                table: "cc_PendingReferralRequests",
                type: "varchar(250)",
                maxLength: 250,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "RecommendedFacilityName",
                table: "cc_PendingReferralRequests",
                type: "varchar(250)",
                maxLength: 250,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "cc_PendingReferralProviderPreferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    PendingReferralRequestId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ProviderId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    FacilityId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    ProviderName = table.Column<string>(type: "varchar(250)", maxLength: 250, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    FacilityName = table.Column<string>(type: "varchar(250)", maxLength: 250, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cc_PendingReferralProviderPreferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cc_PendingReferralProviderPreferences_cc_PendingReferralRequests_PendingReferralRequestId",
                        column: x => x.PendingReferralRequestId,
                        principalTable: "cc_PendingReferralRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_PendingReferralProviderPreferences_Request",
                table: "cc_PendingReferralProviderPreferences",
                column: "PendingReferralRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingReferralProviderPreferences_Request_Order",
                table: "cc_PendingReferralProviderPreferences",
                columns: new[] { "PendingReferralRequestId", "DisplayOrder" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cc_PendingReferralProviderPreferences");

            migrationBuilder.DropColumn(
                name: "RecommendedProviderId",
                table: "cc_PendingReferralRequests");

            migrationBuilder.DropColumn(
                name: "RecommendedFacilityId",
                table: "cc_PendingReferralRequests");

            migrationBuilder.DropColumn(
                name: "RecommendedProviderName",
                table: "cc_PendingReferralRequests");

            migrationBuilder.DropColumn(
                name: "RecommendedFacilityName",
                table: "cc_PendingReferralRequests");
        }
    }
}
