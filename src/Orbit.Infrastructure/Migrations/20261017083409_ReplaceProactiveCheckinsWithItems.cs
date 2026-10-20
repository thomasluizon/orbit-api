using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceProactiveCheckinsWithItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<bool>(
                name: "ProactiveAstraEnabled",
                table: "Users",
                type: "boolean",
                nullable: true,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldDefaultValue: false);

            migrationBuilder.CreateTable(
                name: "ProactiveCalendarDays",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocalDate = table.Column<DateOnly>(type: "date", nullable: false),
                    TimedEventCount = table.Column<int>(type: "integer", nullable: true),
                    TimedMinutes = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProactiveCalendarDays", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProactiveCalendarDays_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProactiveItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Trigger = table.Column<int>(type: "integer", nullable: false),
                    LocalDate = table.Column<DateOnly>(type: "date", nullable: false),
                    FactsJson = table.Column<string>(type: "text", nullable: false),
                    GoalId = table.Column<Guid>(type: "uuid", nullable: true),
                    Text = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: false),
                    ActionLabel = table.Column<string>(type: "text", nullable: false),
                    ActionPrompt = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ShownAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PushedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OpenedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ActedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndReason = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProactiveItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProactiveItems_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProactiveCalendarDays_UserId_LocalDate",
                table: "ProactiveCalendarDays",
                columns: new[] { "UserId", "LocalDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProactiveItems_UserId_LocalDate",
                table: "ProactiveItems",
                columns: new[] { "UserId", "LocalDate" },
                unique: true,
                filter: "\"PushedAtUtc\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ProactiveItems_UserId_Trigger_EndedAtUtc",
                table: "ProactiveItems",
                columns: new[] { "UserId", "Trigger", "EndedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ProactiveItems_UserId_Trigger_LocalDate",
                table: "ProactiveItems",
                columns: new[] { "UserId", "Trigger", "LocalDate" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProactiveCalendarDays");

            migrationBuilder.DropTable(
                name: "ProactiveItems");

            migrationBuilder.AlterColumn<bool>(
                name: "ProactiveAstraEnabled",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldNullable: true);
        }
    }
}
