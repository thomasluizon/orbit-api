using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPublicProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PublicProfileShowAchievements",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "PublicProfileShowLevel",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "PublicProfileShowStreak",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "PublicProfileShowTopHabits",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PublicProfileSlug",
                table: "Users",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_PublicProfileSlug",
                table: "Users",
                column: "PublicProfileSlug",
                unique: true,
                filter: "\"PublicProfileSlug\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_PublicProfileSlug",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PublicProfileShowAchievements",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PublicProfileShowLevel",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PublicProfileShowStreak",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PublicProfileShowTopHabits",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PublicProfileSlug",
                table: "Users");
        }
    }
}
