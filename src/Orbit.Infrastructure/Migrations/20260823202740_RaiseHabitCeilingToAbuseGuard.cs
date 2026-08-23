using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RaiseHabitCeilingToAbuseGuard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "FreeMaxHabits",
                columns: new[] { "Description", "Value" },
                values: new object[] { "Maximum live top-level habits per user as an abuse guard", "1000" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "FreeMaxHabits",
                columns: new[] { "Description", "Value" },
                values: new object[] { "Maximum number of active habits for free plan users", "10" });
        }
    }
}
