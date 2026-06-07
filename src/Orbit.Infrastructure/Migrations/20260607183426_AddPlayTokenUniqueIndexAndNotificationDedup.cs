using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPlayTokenUniqueIndexAndNotificationDedup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProcessedPlayNotifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ProcessedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessedPlayNotifications", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Users_PlayPurchaseToken",
                table: "Users",
                column: "PlayPurchaseToken",
                unique: true,
                filter: "\"PlayPurchaseToken\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessedPlayNotifications_MessageId",
                table: "ProcessedPlayNotifications",
                column: "MessageId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProcessedPlayNotifications");

            migrationBuilder.DropIndex(
                name: "IX_Users_PlayPurchaseToken",
                table: "Users");
        }
    }
}
