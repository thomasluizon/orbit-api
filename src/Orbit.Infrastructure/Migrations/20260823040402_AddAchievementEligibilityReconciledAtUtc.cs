using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAchievementEligibilityReconciledAtUtc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AchievementEligibilityReconciledAtUtc",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_AchievementEligibilityReconciledAtUtc",
                table: "Users",
                column: "AchievementEligibilityReconciledAtUtc",
                filter: "\"AchievementEligibilityReconciledAtUtc\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_AchievementEligibilityReconciledAtUtc",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "AchievementEligibilityReconciledAtUtc",
                table: "Users");
        }
    }
}
