using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CareConnect.Infrastructure.Data.Migrations;

[DbContext(typeof(CareConnectDbContext))]
[Migration("20260820000000_AddOtherSpecialty")]
public partial class AddOtherSpecialty : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            INSERT INTO `cc_Specialties`
                (`Id`, `Name`, `Code`, `Description`, `IsActive`, `CreatedAtUtc`, `UpdatedAtUtc`)
            VALUES
                ('41000000-0000-0000-0000-000000000010', 'Other', 'OTHER', NULL, 1, '2024-01-01 00:00:00', '2024-01-01 00:00:00')
            ON DUPLICATE KEY UPDATE
                `Name` = VALUES(`Name`),
                `Description` = VALUES(`Description`),
                `IsActive` = VALUES(`IsActive`),
                `UpdatedAtUtc` = VALUES(`UpdatedAtUtc`);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DELETE FROM `cc_Specialties` WHERE `Id` = '41000000-0000-0000-0000-000000000010';");
    }
}
