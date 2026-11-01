using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CompleteGeneralTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE TABLE IF NOT EXISTS "GeneralTaskCompletionBackfill" (
                    "HabitId" uuid PRIMARY KEY
                );

                INSERT INTO "GeneralTaskCompletionBackfill" ("HabitId")
                SELECT h."Id"
                FROM "Habits" h
                WHERE h."Schedule" = 3
                  AND h."IsCompleted" = false
                  AND h."IsDeleted" = false
                  AND EXISTS (
                      SELECT 1
                      FROM "HabitLogs" l
                      WHERE l."HabitId" = h."Id"
                        AND l."Value" > 0
                        AND l."IsDeleted" = false
                        AND NOT EXISTS (
                            SELECT 1
                            FROM "HabitLogs" undone
                            WHERE undone."HabitId" = h."Id"
                              AND undone."Value" > 0
                              AND undone."IsDeleted" = true
                              AND (undone."DeletedAtUtc" IS NULL
                                   OR undone."DeletedAtUtc" >= l."CreatedAtUtc")
                        )
                  )
                ON CONFLICT ("HabitId") DO NOTHING;

                UPDATE "Habits"
                SET "IsCompleted" = true
                WHERE "Id" IN (SELECT "HabitId" FROM "GeneralTaskCompletionBackfill");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE TABLE IF NOT EXISTS "GeneralTaskCompletionBackfill" (
                    "HabitId" uuid PRIMARY KEY
                );

                UPDATE "Habits"
                SET "IsCompleted" = false
                WHERE "Id" IN (SELECT "HabitId" FROM "GeneralTaskCompletionBackfill");

                DROP TABLE IF EXISTS "GeneralTaskCompletionBackfill";
                """);
        }
    }
}
