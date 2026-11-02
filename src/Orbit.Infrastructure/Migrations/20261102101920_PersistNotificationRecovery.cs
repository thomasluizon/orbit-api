using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PersistNotificationRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ProcessingLeaseExpiresAtUtc",
                table: "ProcessedPlayNotifications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProcessingLeaseId",
                table: "ProcessedPlayNotifications",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ConversationObjectCleanups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationObjectCleanups", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationObjectCleanups_UserId_ConversationId",
                table: "ConversationObjectCleanups",
                columns: new[] { "UserId", "ConversationId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConversationObjectCleanups");

            migrationBuilder.DropColumn(
                name: "ProcessingLeaseExpiresAtUtc",
                table: "ProcessedPlayNotifications");

            migrationBuilder.DropColumn(
                name: "ProcessingLeaseId",
                table: "ProcessedPlayNotifications");
        }
    }
}
