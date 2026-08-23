using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptionLapseReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "SubscriptionEndedAtUtc",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubscriptionLapseReason",
                table: "Users",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SubscriptionEndedAtUtc",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "SubscriptionLapseReason",
                table: "Users");
        }
    }
}
