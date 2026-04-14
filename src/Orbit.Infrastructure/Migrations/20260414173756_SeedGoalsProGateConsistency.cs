using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SeedGoalsProGateConsistency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "AppConfigs",
                columns: new[] { "Key", "Description", "Value" },
                values: new object[] { "GoalsProOnly", "Whether goal access is restricted to Pro users", "true" });

            migrationBuilder.UpdateData(
                table: "AppFeatureFlags",
                keyColumn: "Key",
                keyValue: "goal_tracking",
                column: "PlanRequirement",
                value: "Pro");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "GoalsProOnly");

            migrationBuilder.UpdateData(
                table: "AppFeatureFlags",
                keyColumn: "Key",
                keyValue: "goal_tracking",
                column: "PlanRequirement",
                value: null);
        }
    }
}
