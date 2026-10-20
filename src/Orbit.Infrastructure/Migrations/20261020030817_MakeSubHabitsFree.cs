using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MakeSubHabitsFree : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "AppFeatureFlags",
                keyColumn: "Key",
                keyValue: "sub_habits",
                column: "PlanRequirement",
                value: null);

            migrationBuilder.DeleteData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "SubHabitsProOnly");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "AppFeatureFlags",
                keyColumn: "Key",
                keyValue: "sub_habits",
                column: "PlanRequirement",
                value: "Pro");

            migrationBuilder.InsertData(
                table: "AppConfigs",
                columns: new[] { "Key", "Description", "Value" },
                values: new object[] { "SubHabitsProOnly", "Whether sub-habit creation is restricted to Pro users", "true" });
        }
    }
}
