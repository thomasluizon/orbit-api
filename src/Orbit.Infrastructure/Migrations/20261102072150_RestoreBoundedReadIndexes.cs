using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RestoreBoundedReadIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Habits_Reminder",
                table: "Habits",
                columns: new[] { "ReminderEnabled", "IsCompleted" },
                filter: "\"ReminderEnabled\" AND NOT \"IsCompleted\"");

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageDaily_UserId",
                table: "AiUsageDaily",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Habits_Reminder",
                table: "Habits");

            migrationBuilder.DropIndex(
                name: "IX_AiUsageDaily_UserId",
                table: "AiUsageDaily");
        }
    }
}
