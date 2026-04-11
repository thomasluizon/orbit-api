using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPerformanceIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 2.4: Partial index for ReminderSchedulerService (runs every minute)
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_Habits_Reminder"
                ON "Habits" ("ReminderEnabled", "IsCompleted", "IsGeneral")
                WHERE "ReminderEnabled" = true AND "IsCompleted" = false;
                """);

            // 2.5: Partial index for HabitDueDateAdvancementService (runs every 30 min)
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_Habits_DueDateAdvancement"
                ON "Habits" ("DueDate", "IsCompleted", "FrequencyUnit")
                WHERE "IsCompleted" = false AND "FrequencyUnit" IS NOT NULL;
                """);

            // 7.1: Partial index for SlipAlertSchedulerService (runs every 5 min)
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_Habits_SlipAlert"
                ON "Habits" ("IsBadHabit", "SlipAlertEnabled", "IsCompleted")
                WHERE "IsBadHabit" = true AND "SlipAlertEnabled" = true AND "IsCompleted" = false;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Habits_Reminder";""");
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Habits_DueDateAdvancement";""");
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Habits_SlipAlert";""");
        }
    }
}
