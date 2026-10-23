using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAppStoreSubscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AppStoreEnvironment",
                table: "Users",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AppStoreOriginalTransactionId",
                table: "Users",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AppStoreStateSignedAtUtc",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProcessedAppStoreNotifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    NotificationUuid = table.Column<Guid>(type: "uuid", nullable: false),
                    Environment = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ProcessedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessedAppStoreNotifications", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Users_AppStoreOriginalTransactionId",
                table: "Users",
                column: "AppStoreOriginalTransactionId",
                unique: true,
                filter: "\"AppStoreOriginalTransactionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessedAppStoreNotifications_NotificationUuid",
                table: "ProcessedAppStoreNotifications",
                column: "NotificationUuid",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProcessedAppStoreNotifications");

            migrationBuilder.DropIndex(
                name: "IX_Users_AppStoreOriginalTransactionId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "AppStoreEnvironment",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "AppStoreOriginalTransactionId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "AppStoreStateSignedAtUtc",
                table: "Users");
        }
    }
}
