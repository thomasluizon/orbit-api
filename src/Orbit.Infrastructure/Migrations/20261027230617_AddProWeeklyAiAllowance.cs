using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProWeeklyAiAllowance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AiMessagesUsedThisWeek",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateOnly>(
                name: "AiMessagesWeekStart",
                table: "Users",
                type: "date",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "Users"
                SET "AiMessagesUsedThisWeek" = "AiMessagesUsedToday",
                    "AiMessagesWeekStart" = "AiMessagesLocalDate" -
                        ((EXTRACT(DOW FROM "AiMessagesLocalDate")::integer - "WeekStartDay" + 7) % 7)
                WHERE "AiMessagesLocalDate" IS NOT NULL;
                """);

            migrationBuilder.DeleteData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "ProAiMessagesPerDay");

            migrationBuilder.DeleteData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "ProAiCallsPerDay");

            migrationBuilder.DeleteData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "ProAiMicroDollarsPerDay");

            migrationBuilder.InsertData(
                table: "AppConfigs",
                columns: new[] { "Key", "Description", "Value" },
                values: new object[] { "ProAiCallsPerWeek", "Weekly AI call safety budget for Pro users", "2250" });

            migrationBuilder.InsertData(
                table: "AppConfigs",
                columns: new[] { "Key", "Description", "Value" },
                values: new object[] { "ProAiMicroDollarsPerWeek", "Weekly AI safety budget for Pro users in USD micro-dollars", "1750000" });

            migrationBuilder.InsertData(
                table: "AppConfigs",
                columns: new[] { "Key", "Description", "Value" },
                values: new object[] { "ProAiMessagesPerWeek", "Weekly AI message limit for Pro users", "250" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "AppConfigs",
                columns: new[] { "Key", "Description", "Value" },
                values: new object[] { "ProAiMessagesPerDay", "Daily AI message limit for Pro plan users", "50" });

            migrationBuilder.DeleteData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "ProAiMessagesPerWeek");

            migrationBuilder.DropColumn(
                name: "AiMessagesUsedThisWeek",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "AiMessagesWeekStart",
                table: "Users");

            migrationBuilder.DeleteData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "ProAiCallsPerWeek");

            migrationBuilder.DeleteData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "ProAiMicroDollarsPerWeek");

            migrationBuilder.InsertData(
                table: "AppConfigs",
                columns: new[] { "Key", "Description", "Value" },
                values: new object[] { "ProAiCallsPerDay", "Daily AI call safety budget for Pro users", "450" });

            migrationBuilder.InsertData(
                table: "AppConfigs",
                columns: new[] { "Key", "Description", "Value" },
                values: new object[] { "ProAiMicroDollarsPerDay", "Daily AI safety budget for Pro users in USD micro-dollars", "120000" });
        }
    }
}
