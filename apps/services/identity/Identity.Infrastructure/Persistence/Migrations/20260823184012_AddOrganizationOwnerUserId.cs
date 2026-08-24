using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationOwnerUserId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OwnerUserId",
                table: "idt_Organizations",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.UpdateData(
                table: "idt_Organizations",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000001"),
                column: "OwnerUserId",
                value: null);

            migrationBuilder.CreateIndex(
                name: "IX_idt_Organizations_OwnerUserId",
                table: "idt_Organizations",
                column: "OwnerUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_idt_Organizations_OwnerUserId",
                table: "idt_Organizations");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "idt_Organizations");
        }
    }
}
