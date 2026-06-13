using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AdoptPerformanceIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Notifications_UserId_CreatedAtUtc",
                table: "Notifications",
                columns: new[] { "UserId", "CreatedAtUtc" },
                descending: new[] { false, true });

            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_Habits_Reminder"
                ON "Habits" ("ReminderEnabled", "IsCompleted", "IsGeneral")
                WHERE "ReminderEnabled" = true AND "IsCompleted" = false;
                """);

            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_Habits_DueDateAdvancement"
                ON "Habits" ("DueDate", "IsCompleted", "FrequencyUnit")
                WHERE "IsCompleted" = false AND "FrequencyUnit" IS NOT NULL;
                """);

            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_Habits_SlipAlert"
                ON "Habits" ("IsBadHabit", "SlipAlertEnabled", "IsCompleted")
                WHERE "IsBadHabit" = true AND "SlipAlertEnabled" = true AND "IsCompleted" = false;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Habits_SlipAlert";""");
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Habits_DueDateAdvancement";""");
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Habits_Reminder";""");

            migrationBuilder.DropIndex(
                name: "IX_Notifications_UserId_CreatedAtUtc",
                table: "Notifications");
        }
    }
}
