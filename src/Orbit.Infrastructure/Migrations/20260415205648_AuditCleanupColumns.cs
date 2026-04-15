using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AuditCleanupColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // User: track the user-LOCAL date of the most recent ad reward so the daily cap
            // rolls over at the user's midnight, not UTC midnight.
            migrationBuilder.AddColumn<DateOnly>(
                name: "LastAdRewardLocalDate",
                table: "Users",
                type: "date",
                nullable: true);

            // Habit: persist the original day-of-month for monthly/yearly habits so subsequent
            // DueDate advances re-anchor through end-of-month clamps without permanently
            // drifting (e.g., Jan 31 -> Feb 28 was previously sticking at day 28 forever).
            migrationBuilder.AddColumn<int>(
                name: "OriginalDayOfMonth",
                table: "Habits",
                type: "integer",
                nullable: true);

            // Backfill: for existing monthly/yearly habits, seed OriginalDayOfMonth from
            // the current DueDate.Day. Habits that have already drifted will keep their
            // current (possibly wrong) anchor; this is intentional -- correcting historical
            // drift would skip user-visible cycles. New advances onward use the persisted day.
            migrationBuilder.Sql(
                "UPDATE \"Habits\" SET \"OriginalDayOfMonth\" = EXTRACT(DAY FROM \"DueDate\")::int " +
                "WHERE \"FrequencyUnit\" IN (2, 3) AND \"OriginalDayOfMonth\" IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastAdRewardLocalDate",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "OriginalDayOfMonth",
                table: "Habits");
        }
    }
}
