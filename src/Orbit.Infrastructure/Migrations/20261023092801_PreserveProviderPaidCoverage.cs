using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PreserveProviderPaidCoverage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AppStoreCoverageExpiresAtUtc",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AppStoreCoverageInterval",
                table: "Users",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AppStoreCoverageLapseReason",
                table: "Users",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PlayCoverageExpiresAtUtc",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PlayCoverageInterval",
                table: "Users",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlayCoverageLapseReason",
                table: "Users",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StripeCoverageExpiresAtUtc",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StripeCoverageInterval",
                table: "Users",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StripeCoverageLapseReason",
                table: "Users",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);
            migrationBuilder.Sql("""
                UPDATE "Users"
                SET "StripeCoverageExpiresAtUtc" = "PlanExpiresAt",
                    "StripeCoverageInterval" = "SubscriptionInterval",
                    "StripeCoverageLapseReason" = "SubscriptionLapseReason"
                WHERE "SubscriptionSource" = 0 AND "Plan" = 1
                  AND "StripeCoverageExpiresAtUtc" IS NULL;
                """);

            migrationBuilder.Sql("""
                UPDATE "Users"
                SET "PlayCoverageExpiresAtUtc" = "PlanExpiresAt",
                    "PlayCoverageInterval" = "SubscriptionInterval",
                    "PlayCoverageLapseReason" = "SubscriptionLapseReason"
                WHERE "SubscriptionSource" = 1 AND "Plan" = 1
                  AND "PlayCoverageExpiresAtUtc" IS NULL;
                """);

            migrationBuilder.Sql("""
                UPDATE "Users"
                SET "AppStoreCoverageExpiresAtUtc" = "PlanExpiresAt",
                    "AppStoreCoverageInterval" = "SubscriptionInterval",
                    "AppStoreCoverageLapseReason" = "SubscriptionLapseReason"
                WHERE "SubscriptionSource" = 2 AND "Plan" = 1
                  AND "AppStoreCoverageExpiresAtUtc" IS NULL;
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AppStoreCoverageExpiresAtUtc",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "AppStoreCoverageInterval",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "AppStoreCoverageLapseReason",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PlayCoverageExpiresAtUtc",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PlayCoverageInterval",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PlayCoverageLapseReason",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "StripeCoverageExpiresAtUtc",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "StripeCoverageInterval",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "StripeCoverageLapseReason",
                table: "Users");
        }
    }
}
