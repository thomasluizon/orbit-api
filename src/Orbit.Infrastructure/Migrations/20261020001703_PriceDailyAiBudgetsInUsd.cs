using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PriceDailyAiBudgetsInUsd : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "FreeAiTokensPerDay");

            migrationBuilder.DeleteData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "ProAiTokensPerDay");

            migrationBuilder.AddColumn<long>(
                name: "ChargedMicroDollars",
                table: "AiBudgetCalls",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.InsertData(
                table: "AppConfigs",
                columns: new[] { "Key", "Description", "Value" },
                values: new object[,]
                {
                    { "FreeAiMicroDollarsPerDay", "Daily AI safety budget for Free users in USD micro-dollars", "20000" },
                    { "ProAiMicroDollarsPerDay", "Daily AI safety budget for Pro users in USD micro-dollars", "120000" }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "FreeAiMicroDollarsPerDay");

            migrationBuilder.DeleteData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "ProAiMicroDollarsPerDay");

            migrationBuilder.DropColumn(
                name: "ChargedMicroDollars",
                table: "AiBudgetCalls");

            migrationBuilder.InsertData(
                table: "AppConfigs",
                columns: new[] { "Key", "Description", "Value" },
                values: new object[,]
                {
                    { "FreeAiTokensPerDay", "Daily AI token safety budget for Free users", "40000" },
                    { "ProAiTokensPerDay", "Daily AI token safety budget for Pro users", "240000" }
                });
        }
    }
}
