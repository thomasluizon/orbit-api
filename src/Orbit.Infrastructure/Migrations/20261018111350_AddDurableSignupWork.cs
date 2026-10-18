using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableSignupWork : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "SignupAnalyticsCompletedAtUtc",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SignupCompletedAtUtc",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SignupLanguage",
                table: "Users",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SignupReferralCode",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SignupReferralCompletedAtUtc",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SignupWelcomeCompletedAtUtc",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SignupAnalyticsCompletedAtUtc",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "SignupCompletedAtUtc",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "SignupLanguage",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "SignupReferralCode",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "SignupReferralCompletedAtUtc",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "SignupWelcomeCompletedAtUtc",
                table: "Users");
        }
    }
}
