using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAiChangePlanFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");
            migrationBuilder.InsertData(
                table: "AppFeatureFlags",
                columns: new[] { "Key", "Description", "Enabled", "PlanRequirement", "UpdatedAtUtc" },
                values: new object[] { "ai_change_plan", "The model is the only owner of the act-or-ask decision on the short-prompt chat path", false, null, new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc) });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");
            migrationBuilder.DeleteData(
                table: "AppFeatureFlags",
                keyColumn: "Key",
                keyValue: "ai_change_plan");
        }
    }
}
