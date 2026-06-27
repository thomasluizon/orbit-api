using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SeedGamificationFreeTierFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "AppFeatureFlags",
                columns: new[] { "Key", "Description", "Enabled", "PlanRequirement", "UpdatedAtUtc" },
                values: new object[]
                {
                    "gamification_free_tier",
                    "Unlocks the free gamification tier (streak, XP, level, streak-freeze auto-apply) for non-Pro users when enabled",
                    false,
                    null,
                    new DateTime(2026, 6, 27, 0, 0, 0, 0, DateTimeKind.Utc)
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "AppFeatureFlags",
                keyColumn: "Key",
                keyValue: "gamification_free_tier");
        }
    }
}
