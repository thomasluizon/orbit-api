using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FilterDeletedHabitGoogleEventIdIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Habits_UserId_GoogleEventId",
                table: "Habits");

            migrationBuilder.CreateIndex(
                name: "IX_Habits_UserId_GoogleEventId",
                table: "Habits",
                columns: new[] { "UserId", "GoogleEventId" },
                unique: true,
                filter: "\"GoogleEventId\" IS NOT NULL AND \"IsDeleted\" = FALSE");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Habits_UserId_GoogleEventId",
                table: "Habits");

            migrationBuilder.CreateIndex(
                name: "IX_Habits_UserId_GoogleEventId",
                table: "Habits",
                columns: new[] { "UserId", "GoogleEventId" },
                unique: true,
                filter: "\"GoogleEventId\" IS NOT NULL");
        }
    }
}
