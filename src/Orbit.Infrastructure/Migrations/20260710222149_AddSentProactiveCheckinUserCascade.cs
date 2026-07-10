using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSentProactiveCheckinUserCascade : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DELETE FROM \"SentProactiveCheckins\" WHERE \"UserId\" NOT IN (SELECT \"Id\" FROM \"Users\");");

            migrationBuilder.AddForeignKey(
                name: "FK_SentProactiveCheckins_Users_UserId",
                table: "SentProactiveCheckins",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SentProactiveCheckins_Users_UserId",
                table: "SentProactiveCheckins");
        }
    }
}
