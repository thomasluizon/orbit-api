using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDailyAiSafetyBudget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiBudgetCalls",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocalDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ChargedTokens = table.Column<long>(type: "bigint", nullable: false),
                    IsSettled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiBudgetCalls", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiBudgetCalls_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "AppConfigs",
                columns: new[] { "Key", "Description", "Value" },
                values: new object[,]
                {
                    { "FreeAiCallsPerDay", "Daily AI call safety budget for Free users", "45" },
                    { "FreeAiTokensPerDay", "Daily AI token safety budget for Free users", "900000" },
                    { "ProAiCallsPerDay", "Daily AI call safety budget for Pro users", "450" },
                    { "ProAiTokensPerDay", "Daily AI token safety budget for Pro users", "9000000" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiBudgetCalls_UserId_LocalDate",
                table: "AiBudgetCalls",
                columns: new[] { "UserId", "LocalDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiBudgetCalls");

            migrationBuilder.DeleteData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "FreeAiCallsPerDay");

            migrationBuilder.DeleteData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "FreeAiTokensPerDay");

            migrationBuilder.DeleteData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "ProAiCallsPerDay");

            migrationBuilder.DeleteData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "ProAiTokensPerDay");
        }
    }
}
