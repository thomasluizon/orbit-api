using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CollapseYearlyProIntoPro : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "AppFeatureFlags",
                keyColumn: "Key",
                keyValue: "ai_retrospective",
                column: "PlanRequirement",
                value: "Pro");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "AppFeatureFlags",
                keyColumn: "Key",
                keyValue: "ai_retrospective",
                column: "PlanRequirement",
                value: string.Concat("Yearly", "Pro"));
        }
    }
}
