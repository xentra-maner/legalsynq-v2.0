using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Notifications.Infrastructure.Data;

#nullable disable

namespace Notifications.Infrastructure.Data.Migrations;

[DbContext(typeof(NotificationsDbContext))]
[Migration("20260905010000_AddUserInbox")]
public partial class AddUserInbox : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ntf_UserInboxItems",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                NotificationId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                TenantId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                RecipientUserId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                ProductKey = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                EventKey = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                Category = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                Title = table.Column<string>(type: "varchar(160)", maxLength: 160, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                Description = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                SourceDisplayName = table.Column<string>(type: "varchar(160)", maxLength: 160, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                SourceInitials = table.Column<string>(type: "varchar(8)", maxLength: 8, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                OccurredAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                ReadAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                DismissedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_ntf_UserInboxItems", x => x.Id))
            .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.CreateIndex(
            name: "UX_UserInboxItems_NotificationId",
            table: "ntf_UserInboxItems",
            column: "NotificationId",
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_UserInboxItems_User_Chronology",
            table: "ntf_UserInboxItems",
            columns: new[] { "TenantId", "RecipientUserId", "DismissedAtUtc", "OccurredAtUtc", "Id" });
        migrationBuilder.CreateIndex(
            name: "IX_UserInboxItems_User_Category",
            table: "ntf_UserInboxItems",
            columns: new[] { "TenantId", "RecipientUserId", "Category", "DismissedAtUtc", "OccurredAtUtc" });
        migrationBuilder.CreateIndex(
            name: "IX_UserInboxItems_User_ReadState",
            table: "ntf_UserInboxItems",
            columns: new[] { "TenantId", "RecipientUserId", "ReadAtUtc", "DismissedAtUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropTable(name: "ntf_UserInboxItems");
}
