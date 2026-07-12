using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncChangesV2Indexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_Tags_UserId_UpdatedAtUtc"
                ON "Tags" ("UserId", "UpdatedAtUtc");
                """);

            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_Notifications_UserId_UpdatedAtUtc"
                ON "Notifications" ("UserId", "UpdatedAtUtc");
                """);

            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_Habits_UserId_UpdatedAtUtc"
                ON "Habits" ("UserId", "UpdatedAtUtc");
                """);

            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_HabitLogs_HabitId_UpdatedAtUtc"
                ON "HabitLogs" ("HabitId", "UpdatedAtUtc");
                """);

            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_Goals_UserId_UpdatedAtUtc"
                ON "Goals" ("UserId", "UpdatedAtUtc");
                """);

            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_GoalProgressLogs_GoalId_UpdatedAtUtc"
                ON "GoalProgressLogs" ("GoalId", "UpdatedAtUtc");
                """);

            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_ChecklistTemplates_UserId_UpdatedAtUtc"
                ON "ChecklistTemplates" ("UserId", "UpdatedAtUtc");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_ChecklistTemplates_UserId_UpdatedAtUtc";""");
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_GoalProgressLogs_GoalId_UpdatedAtUtc";""");
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Goals_UserId_UpdatedAtUtc";""");
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_HabitLogs_HabitId_UpdatedAtUtc";""");
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Habits_UserId_UpdatedAtUtc";""");
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Notifications_UserId_UpdatedAtUtc";""");
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Tags_UserId_UpdatedAtUtc";""");
        }
    }
}
