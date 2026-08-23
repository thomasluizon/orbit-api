using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DailyAiQuotaByLocalDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AiMessagesResetAt",
                table: "Users");

            migrationBuilder.RenameColumn(
                name: "AiMessagesUsedThisMonth",
                table: "Users",
                newName: "AiMessagesUsedToday");

            migrationBuilder.AddColumn<DateOnly>(
                name: "AiMessagesLocalDate",
                table: "Users",
                type: "date",
                nullable: true);

            migrationBuilder.Sql("UPDATE \"Users\" SET \"AiMessagesUsedToday\" = 0;");

            migrationBuilder.DeleteData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "FreeAiMessagesPerMonth");

            migrationBuilder.DeleteData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "ProAiMessagesPerMonth");

            migrationBuilder.InsertData(
                table: "AppConfigs",
                columns: new[] { "Key", "Description", "Value" },
                values: new object[] { "FreeAiMessagesPerDay", "Daily AI message limit for free plan users", "5" });

            migrationBuilder.InsertData(
                table: "AppConfigs",
                columns: new[] { "Key", "Description", "Value" },
                values: new object[] { "ProAiMessagesPerDay", "Daily AI message limit for Pro plan users", "50" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AiMessagesLocalDate",
                table: "Users");

            migrationBuilder.RenameColumn(
                name: "AiMessagesUsedToday",
                table: "Users",
                newName: "AiMessagesUsedThisMonth");

            migrationBuilder.AddColumn<DateTime>(
                name: "AiMessagesResetAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.DeleteData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "FreeAiMessagesPerDay");

            migrationBuilder.DeleteData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "ProAiMessagesPerDay");

            migrationBuilder.InsertData(
                table: "AppConfigs",
                columns: new[] { "Key", "Description", "Value" },
                values: new object[] { "FreeAiMessagesPerMonth", "Monthly AI message limit for free plan users", "20" });

            migrationBuilder.InsertData(
                table: "AppConfigs",
                columns: new[] { "Key", "Description", "Value" },
                values: new object[] { "ProAiMessagesPerMonth", "Monthly AI message limit for Pro plan users", "500" });
        }
    }
}
