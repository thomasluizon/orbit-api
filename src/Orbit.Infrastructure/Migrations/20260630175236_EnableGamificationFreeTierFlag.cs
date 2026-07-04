using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EnableGamificationFreeTierFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "AppFeatureFlags",
                keyColumn: "Key",
                keyValue: "gamification_free_tier",
                columns: new[] { "Enabled", "UpdatedAtUtc" },
                values: new object[] { true, new DateTime(2026, 6, 30, 0, 0, 0, 0, DateTimeKind.Utc) });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "AppFeatureFlags",
                keyColumn: "Key",
                keyValue: "gamification_free_tier",
                columns: new[] { "Enabled", "UpdatedAtUtc" },
                values: new object[] { false, new DateTime(2026, 6, 27, 0, 0, 0, 0, DateTimeKind.Utc) });
        }
    }
}
