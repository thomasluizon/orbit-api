using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orbit.Infrastructure.Persistence;

#nullable disable

namespace Orbit.Infrastructure.Migrations;

[DbContext(typeof(OrbitDbContext))]
[Migration("20260806120000_ClearGeneralHabitCompletionFlags")]
public class ClearGeneralHabitCompletionFlags : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            UPDATE "Habits"
            SET "IsCompleted" = false
            WHERE "IsGeneral" = true
              AND "IsCompleted" = true;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            UPDATE "Habits" h
            SET "IsCompleted" = true
            WHERE h."IsGeneral" = true
              AND h."IsCompleted" = false
              AND EXISTS (
                  SELECT 1
                  FROM "HabitLogs" l
                  WHERE l."HabitId" = h."Id"
                    AND l."Value" > 0
                    AND l."IsDeleted" = false
              );
            """);
    }
}
