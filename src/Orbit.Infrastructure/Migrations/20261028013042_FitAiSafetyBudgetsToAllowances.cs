using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FitAiSafetyBudgetsToAllowances : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "FreeAiCallsPerDay",
                column: "Value",
                value: "110");

            migrationBuilder.UpdateData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "FreeAiMicroDollarsPerDay",
                column: "Value",
                value: "1000000");

            migrationBuilder.UpdateData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "ProAiCallsPerWeek",
                column: "Value",
                value: "5500");

            migrationBuilder.UpdateData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "ProAiMicroDollarsPerWeek",
                column: "Value",
                value: "50000000");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "FreeAiCallsPerDay",
                column: "Value",
                value: "45");

            migrationBuilder.UpdateData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "FreeAiMicroDollarsPerDay",
                column: "Value",
                value: "20000");

            migrationBuilder.UpdateData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "ProAiCallsPerWeek",
                column: "Value",
                value: "2250");

            migrationBuilder.UpdateData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "ProAiMicroDollarsPerWeek",
                column: "Value",
                value: "1750000");
        }
    }
}
