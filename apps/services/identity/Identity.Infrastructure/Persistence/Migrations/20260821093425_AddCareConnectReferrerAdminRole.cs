using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCareConnectReferrerAdminRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // LSV3-1084: Law-firm-scoped network self-management. Reuses the same
            // capabilities as CARECONNECT_NETWORK_MANAGER (read-network + provider:manage),
            // but is restricted to LAW_FIRM orgs only (unlike NetworkManager, which is
            // also assignable to LIEN_OWNER orgs).
            migrationBuilder.InsertData(
                table: "idt_ProductRoles",
                columns: new[] { "Id", "Code", "CreatedAtUtc", "Description", "IsActive", "Name", "ProductId" },
                values: new object[] { new Guid("50000000-0000-0000-0000-000000000012"), "CARECONNECT_REFERRER_ADMIN", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "Law-firm-scoped manager for the firm's own CareConnect provider network", true, "CareConnect Referrer Admin", new Guid("10000000-0000-0000-0000-000000000003") });

            migrationBuilder.InsertData(
                table: "idt_ProductOrganizationTypeRules",
                columns: new[] { "Id", "CreatedAtUtc", "IsActive", "OrganizationTypeId", "ProductId", "ProductRoleId" },
                values: new object[] { new Guid("90000000-0000-0000-0000-000000000015"), new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), true, new Guid("70000000-0000-0000-0000-000000000002"), new Guid("10000000-0000-0000-0000-000000000003"), new Guid("50000000-0000-0000-0000-000000000012") });

            migrationBuilder.InsertData(
                table: "idt_RoleCapabilities",
                columns: new[] { "CapabilityId", "ProductRoleId" },
                values: new object[,]
                {
                    { new Guid("60000000-0000-0000-0000-000000000007"), new Guid("50000000-0000-0000-0000-000000000012") },
                    { new Guid("60000000-0000-0000-0000-000000000008"), new Guid("50000000-0000-0000-0000-000000000012") },
                    { new Guid("60000000-0000-0000-0000-000000000072"), new Guid("50000000-0000-0000-0000-000000000012") }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "idt_ProductOrganizationTypeRules",
                keyColumn: "Id",
                keyValue: new Guid("90000000-0000-0000-0000-000000000015"));

            migrationBuilder.DeleteData(
                table: "idt_RoleCapabilities",
                keyColumns: new[] { "CapabilityId", "ProductRoleId" },
                keyValues: new object[] { new Guid("60000000-0000-0000-0000-000000000007"), new Guid("50000000-0000-0000-0000-000000000012") });

            migrationBuilder.DeleteData(
                table: "idt_RoleCapabilities",
                keyColumns: new[] { "CapabilityId", "ProductRoleId" },
                keyValues: new object[] { new Guid("60000000-0000-0000-0000-000000000008"), new Guid("50000000-0000-0000-0000-000000000012") });

            migrationBuilder.DeleteData(
                table: "idt_RoleCapabilities",
                keyColumns: new[] { "CapabilityId", "ProductRoleId" },
                keyValues: new object[] { new Guid("60000000-0000-0000-0000-000000000072"), new Guid("50000000-0000-0000-0000-000000000012") });

            migrationBuilder.DeleteData(
                table: "idt_ProductRoles",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000012"));
        }
    }
}
