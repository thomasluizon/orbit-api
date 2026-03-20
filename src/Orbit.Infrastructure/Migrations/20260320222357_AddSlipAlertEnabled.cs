using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSlipAlertEnabled : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SlipAlertEnabled",
                table: "Habits",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "SentSlipAlerts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HabitId = table.Column<Guid>(type: "uuid", nullable: false),
                    WeekStart = table.Column<DateOnly>(type: "date", nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SentSlipAlerts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SentSlipAlerts_HabitId_WeekStart",
                table: "SentSlipAlerts",
                columns: new[] { "HabitId", "WeekStart" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SentSlipAlerts");

            migrationBuilder.DropColumn(
                name: "SlipAlertEnabled",
                table: "Habits");
        }
    }
}
