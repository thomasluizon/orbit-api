using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddReferralSystem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReferralCode",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReferredByUserId",
                table: "Users",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Referrals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReferrerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReferredUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RewardGrantedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Referrals", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "AppConfigs",
                columns: new[] { "Key", "Description", "Value" },
                values: new object[,]
                {
                    { "MaxReferrals", "Maximum successful referrals per user", "10" },
                    { "ReferralRewardDays", "Days of Pro added per successful referral", "10" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Users_ReferralCode",
                table: "Users",
                column: "ReferralCode",
                unique: true,
                filter: "\"ReferralCode\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Referrals_ReferredUserId",
                table: "Referrals",
                column: "ReferredUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Referrals_ReferrerId",
                table: "Referrals",
                column: "ReferrerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Referrals");

            migrationBuilder.DropIndex(
                name: "IX_Users_ReferralCode",
                table: "Users");

            migrationBuilder.DeleteData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "MaxReferrals");

            migrationBuilder.DeleteData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "ReferralRewardDays");

            migrationBuilder.DropColumn(
                name: "ReferralCode",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ReferredByUserId",
                table: "Users");
        }
    }
}
