using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Liens.Infrastructure.Persistence.Migrations;

[DbContext(typeof(LiensDbContext))]
[Migration("20260906010000_AddSellingNotificationInboxOutbox")]
public partial class AddSellingNotificationInboxOutbox : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "SubmittedByPlatformUserId",
            table: "liens_LienOffers",
            type: "char(36)",
            nullable: true,
            collation: "ascii_general_ci");

        migrationBuilder.CreateTable(
            name: "liens_SellingNotificationOutbox",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                TenantId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                RecipientUserId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
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
                IdempotencyKey = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                AttemptCount = table.Column<int>(type: "int", nullable: false),
                NextAttemptAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                LeaseUntilUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                LeaseOwner = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                ProcessedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                DeadLetteredAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                LastError = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                CreatedByUserId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                UpdatedByUserId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci")
            },
            constraints: table => table.PrimaryKey("PK_liens_SellingNotificationOutbox", x => x.Id))
            .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.CreateIndex(
            name: "IX_SellingNotificationOutbox_Dispatch",
            table: "liens_SellingNotificationOutbox",
            columns: new[] { "ProcessedAtUtc", "DeadLetteredAtUtc", "NextAttemptAtUtc", "LeaseUntilUtc" });

        migrationBuilder.CreateIndex(
            name: "UX_SellingNotificationOutbox_Tenant_Idempotency",
            table: "liens_SellingNotificationOutbox",
            columns: new[] { "TenantId", "IdempotencyKey" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "liens_SellingNotificationOutbox");

        migrationBuilder.DropColumn(
            name: "SubmittedByPlatformUserId",
            table: "liens_LienOffers");
    }
}
