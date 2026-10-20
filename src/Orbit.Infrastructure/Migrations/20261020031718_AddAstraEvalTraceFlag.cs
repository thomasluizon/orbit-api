using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAstraEvalTraceFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "AppFeatureFlags",
                columns: new[] { "Key", "Description", "Enabled", "PlanRequirement", "UpdatedAtUtc" },
                values: new object[] { "astra_eval_trace", "Opt-in tool-call traces for Astra evaluation accounts", false, null, new DateTime(2026, 4, 1, 0, 0, 0, 0, DateTimeKind.Utc) });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "AppFeatureFlags",
                keyColumn: "Key",
                keyValue: "astra_eval_trace");
        }
    }
}
