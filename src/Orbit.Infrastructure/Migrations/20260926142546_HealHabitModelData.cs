using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class HealHabitModelData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "Habits"
                SET "EndDate" = NULL
                WHERE "IsGeneral" AND "EndDate" IS NOT NULL;
                """);
        }

        /// <summary>
        /// Cleared end dates cannot be recovered.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
