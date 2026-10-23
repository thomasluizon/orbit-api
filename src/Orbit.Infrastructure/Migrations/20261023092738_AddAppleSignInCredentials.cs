using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAppleSignInCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AppleRefreshToken",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AppleSubject",
                table: "Users",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AppleTokenClientId",
                table: "Users",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_AppleSubject",
                table: "Users",
                column: "AppleSubject",
                unique: true,
                filter: "\"AppleSubject\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_AppleSubject",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "AppleRefreshToken",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "AppleSubject",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "AppleTokenClientId",
                table: "Users");
        }
    }
}
