using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCareConnectNetworkManagerRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "idt_Capabilities",
                columns: new[] { "Id", "Category", "Code", "CreatedAtUtc", "CreatedBy", "Description", "IsActive", "Name", "ProductId", "UpdatedAtUtc", "UpdatedBy" },
                values: new object[] { new Guid("60000000-0000-0000-0000-000000000072"), "Provider", "SYNQ_CARECONNECT.provider:manage", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "Manage organization provider network entries", true, "Manage Providers", new Guid("10000000-0000-0000-0000-000000000003"), null, null });

            migrationBuilder.InsertData(
                table: "idt_ProductRoles",
                columns: new[] { "Id", "Code", "CreatedAtUtc", "Description", "IsActive", "Name", "ProductId" },
                values: new object[] { new Guid("50000000-0000-0000-0000-000000000011"), "CARECONNECT_NETWORK_MANAGER", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "Organization-scoped manager for CareConnect provider networks", true, "CareConnect Network Manager", new Guid("10000000-0000-0000-0000-000000000003") });

            migrationBuilder.InsertData(
                table: "idt_ProductOrganizationTypeRules",
                columns: new[] { "Id", "CreatedAtUtc", "IsActive", "OrganizationTypeId", "ProductId", "ProductRoleId" },
                values: new object[,]
                {
                    { new Guid("90000000-0000-0000-0000-000000000013"), new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), true, new Guid("70000000-0000-0000-0000-000000000002"), new Guid("10000000-0000-0000-0000-000000000003"), new Guid("50000000-0000-0000-0000-000000000011") },
                    { new Guid("90000000-0000-0000-0000-000000000014"), new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), true, new Guid("70000000-0000-0000-0000-000000000005"), new Guid("10000000-0000-0000-0000-000000000003"), new Guid("50000000-0000-0000-0000-000000000011") }
                });

            migrationBuilder.InsertData(
                table: "idt_RoleCapabilities",
                columns: new[] { "CapabilityId", "ProductRoleId" },
                values: new object[,]
                {
                    { new Guid("60000000-0000-0000-0000-000000000007"), new Guid("50000000-0000-0000-0000-000000000011") },
                    { new Guid("60000000-0000-0000-0000-000000000008"), new Guid("50000000-0000-0000-0000-000000000011") },
                    { new Guid("60000000-0000-0000-0000-000000000072"), new Guid("50000000-0000-0000-0000-000000000011") }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "idt_ProductOrganizationTypeRules",
                keyColumn: "Id",
                keyValue: new Guid("90000000-0000-0000-0000-000000000013"));

            migrationBuilder.DeleteData(
                table: "idt_ProductOrganizationTypeRules",
                keyColumn: "Id",
                keyValue: new Guid("90000000-0000-0000-0000-000000000014"));

            migrationBuilder.DeleteData(
                table: "idt_RoleCapabilities",
                keyColumns: new[] { "CapabilityId", "ProductRoleId" },
                keyValues: new object[] { new Guid("60000000-0000-0000-0000-000000000007"), new Guid("50000000-0000-0000-0000-000000000011") });

            migrationBuilder.DeleteData(
                table: "idt_RoleCapabilities",
                keyColumns: new[] { "CapabilityId", "ProductRoleId" },
                keyValues: new object[] { new Guid("60000000-0000-0000-0000-000000000008"), new Guid("50000000-0000-0000-0000-000000000011") });

            migrationBuilder.DeleteData(
                table: "idt_RoleCapabilities",
                keyColumns: new[] { "CapabilityId", "ProductRoleId" },
                keyValues: new object[] { new Guid("60000000-0000-0000-0000-000000000072"), new Guid("50000000-0000-0000-0000-000000000011") });

            migrationBuilder.DeleteData(
                table: "idt_Capabilities",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-000000000072"));

            migrationBuilder.DeleteData(
                table: "idt_ProductRoles",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000011"));
        }
    }
}
