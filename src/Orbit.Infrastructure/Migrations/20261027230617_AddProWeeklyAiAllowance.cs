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

            migrationBuilder.UpdateData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "ProAiCallsPerDay",
                column: "Description",
                value: "Weekly AI call safety budget for Pro users");

            migrationBuilder.UpdateData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "ProAiMicroDollarsPerDay",
                column: "Description",
                value: "Weekly AI safety budget for Pro users in USD micro-dollars");

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

            migrationBuilder.UpdateData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "ProAiCallsPerDay",
                column: "Description",
                value: "Daily AI call safety budget for Pro users");

            migrationBuilder.UpdateData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "ProAiMicroDollarsPerDay",
                column: "Description",
                value: "Daily AI safety budget for Pro users in USD micro-dollars");
        }
    }
}
