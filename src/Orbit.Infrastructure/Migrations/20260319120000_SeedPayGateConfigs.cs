using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SeedPayGateConfigs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "AppConfigs",
                columns: new[] { "Key", "Description", "Value" },
                values: new object[] { "FreeMaxHabits", "Maximum number of active habits for free plan users", "10" });

            migrationBuilder.InsertData(
                table: "AppConfigs",
                columns: new[] { "Key", "Description", "Value" },
                values: new object[] { "FreeAiMessagesPerMonth", "Monthly AI message limit for free plan users", "20" });

            migrationBuilder.InsertData(
                table: "AppConfigs",
                columns: new[] { "Key", "Description", "Value" },
                values: new object[] { "ProAiMessagesPerMonth", "Monthly AI message limit for Pro plan users", "500" });

            migrationBuilder.InsertData(
                table: "AppConfigs",
                columns: new[] { "Key", "Description", "Value" },
                values: new object[] { "SubHabitsProOnly", "Whether sub-habit creation is restricted to Pro users", "true" });

            migrationBuilder.InsertData(
                table: "AppConfigs",
                columns: new[] { "Key", "Description", "Value" },
                values: new object[] { "DailySummaryProOnly", "Whether daily AI summary is restricted to Pro users", "true" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(table: "AppConfigs", keyColumn: "Key", keyValue: "FreeMaxHabits");
            migrationBuilder.DeleteData(table: "AppConfigs", keyColumn: "Key", keyValue: "FreeAiMessagesPerMonth");
            migrationBuilder.DeleteData(table: "AppConfigs", keyColumn: "Key", keyValue: "ProAiMessagesPerMonth");
            migrationBuilder.DeleteData(table: "AppConfigs", keyColumn: "Key", keyValue: "SubHabitsProOnly");
            migrationBuilder.DeleteData(table: "AppConfigs", keyColumn: "Key", keyValue: "DailySummaryProOnly");
        }
    }
}
