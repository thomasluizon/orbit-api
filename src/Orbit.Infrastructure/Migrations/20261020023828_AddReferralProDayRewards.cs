using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddReferralProDayRewards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "BonusProExpiresAtUtc",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReferredReward",
                table: "Referrals",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReferredRewardDeliveredAtUtc",
                table: "Referrals",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReferrerReward",
                table: "Referrals",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReferrerRewardDeliveredAtUtc",
                table: "Referrals",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "Referrals"
                SET "ReferrerReward" = 2,
                    "ReferredReward" = 2,
                    "ReferrerRewardDeliveredAtUtc" = "RewardGrantedAtUtc",
                    "ReferredRewardDeliveredAtUtc" = "RewardGrantedAtUtc"
                WHERE "RewardGrantedAtUtc" IS NOT NULL
                  AND "ReferrerReward" = 0
                  AND "ReferredReward" = 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BonusProExpiresAtUtc",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ReferredReward",
                table: "Referrals");

            migrationBuilder.DropColumn(
                name: "ReferredRewardDeliveredAtUtc",
                table: "Referrals");

            migrationBuilder.DropColumn(
                name: "ReferrerReward",
                table: "Referrals");

            migrationBuilder.DropColumn(
                name: "ReferrerRewardDeliveredAtUtc",
                table: "Referrals");
        }
    }
}
