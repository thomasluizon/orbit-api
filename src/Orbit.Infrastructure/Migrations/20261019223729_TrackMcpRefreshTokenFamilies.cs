using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TrackMcpRefreshTokenFamilies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ConsumedAtUtc",
                table: "OAuthStates",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OAuthFamilyId",
                table: "ApiKeys",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_OAuthFamilyId",
                table: "ApiKeys",
                column: "OAuthFamilyId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ApiKeys_OAuthFamilyId",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "ConsumedAtUtc",
                table: "OAuthStates");

            migrationBuilder.DropColumn(
                name: "OAuthFamilyId",
                table: "ApiKeys");
        }
    }
}
