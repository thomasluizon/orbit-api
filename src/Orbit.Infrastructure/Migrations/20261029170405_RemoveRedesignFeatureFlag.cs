using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveRedesignFeatureFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DELETE FROM "AppFeatureFlagAccounts" WHERE "FlagKey" = 'redesign';""");

            migrationBuilder.DeleteData(
                table: "AppFeatureFlags",
                keyColumn: "Key",
                keyValue: "redesign");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "AppFeatureFlags",
                columns: new[] { "Key", "Description", "Enabled", "PlanRequirement", "UpdatedAtUtc" },
                values: new object[] { "redesign", "Serve the redesigned web and Android builds to this account", false, null, new DateTime(2026, 4, 1, 0, 0, 0, 0, DateTimeKind.Utc) });
        }
    }
}
