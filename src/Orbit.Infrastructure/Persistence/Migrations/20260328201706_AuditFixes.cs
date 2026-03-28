using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuditFixes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Habits_UserId",
                table: "Habits",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_PushSubscriptions_Users_UserId",
                table: "PushSubscriptions",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PushSubscriptions_Users_UserId",
                table: "PushSubscriptions");

            migrationBuilder.DropIndex(
                name: "IX_Habits_UserId",
                table: "Habits");
        }
    }
}
