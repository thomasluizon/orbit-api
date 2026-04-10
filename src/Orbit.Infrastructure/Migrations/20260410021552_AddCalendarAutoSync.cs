using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCalendarAutoSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "GoogleCalendarAutoSyncEnabled",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "GoogleCalendarAutoSyncStatus",
                table: "Users",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GoogleCalendarLastSyncError",
                table: "Users",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "GoogleCalendarLastSyncedAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "GoogleCalendarSyncReconciledAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GoogleEventId",
                table: "Habits",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GoogleCalendarSyncSuggestions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    GoogleEventId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    StartDateUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RawEventJson = table.Column<string>(type: "text", nullable: false),
                    DiscoveredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DismissedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ImportedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ImportedHabitId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GoogleCalendarSyncSuggestions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GoogleCalendarSyncSuggestions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Users_GoogleCalendarAutoSyncEnabled_GoogleCalendarLastSynce~",
                table: "Users",
                columns: new[] { "GoogleCalendarAutoSyncEnabled", "GoogleCalendarLastSyncedAt" },
                filter: "\"GoogleCalendarAutoSyncEnabled\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_Habits_UserId_GoogleEventId",
                table: "Habits",
                columns: new[] { "UserId", "GoogleEventId" },
                unique: true,
                filter: "\"GoogleEventId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_GoogleCalendarSyncSuggestions_UserId",
                table: "GoogleCalendarSyncSuggestions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_GoogleCalendarSyncSuggestions_UserId_DismissedAtUtc_Importe~",
                table: "GoogleCalendarSyncSuggestions",
                columns: new[] { "UserId", "DismissedAtUtc", "ImportedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_GoogleCalendarSyncSuggestions_UserId_GoogleEventId",
                table: "GoogleCalendarSyncSuggestions",
                columns: new[] { "UserId", "GoogleEventId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GoogleCalendarSyncSuggestions");

            migrationBuilder.DropIndex(
                name: "IX_Users_GoogleCalendarAutoSyncEnabled_GoogleCalendarLastSynce~",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Habits_UserId_GoogleEventId",
                table: "Habits");

            migrationBuilder.DropColumn(
                name: "GoogleCalendarAutoSyncEnabled",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "GoogleCalendarAutoSyncStatus",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "GoogleCalendarLastSyncError",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "GoogleCalendarLastSyncedAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "GoogleCalendarSyncReconciledAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "GoogleEventId",
                table: "Habits");
        }
    }
}
