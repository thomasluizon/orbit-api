using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RecordHabitLogSlip : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSlip",
                table: "HabitLogs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("""
                UPDATE "HabitLogs" AS logs
                SET "IsSlip" = TRUE
                FROM "Habits" AS habits
                WHERE logs."HabitId" = habits."Id"
                  AND logs."Value" > 0
                  AND habits."IsBadHabit" = TRUE;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsSlip",
                table: "HabitLogs");
        }
    }
}
