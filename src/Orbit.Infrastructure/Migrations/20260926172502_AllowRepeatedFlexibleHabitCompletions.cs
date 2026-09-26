using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AllowRepeatedFlexibleHabitCompletions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_HabitLogs_HabitId_Date_Completed",
                table: "HabitLogs");

            migrationBuilder.AddColumn<int>(
                name: "CompletionOrdinal",
                table: "HabitLogs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_HabitLogs_HabitId_Date_Completed",
                table: "HabitLogs",
                columns: new[] { "HabitId", "Date", "CompletionOrdinal" },
                unique: true,
                filter: "\"Value\" > 0 AND NOT \"IsDeleted\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_HabitLogs_HabitId_Date_Completed",
                table: "HabitLogs");

            migrationBuilder.DropColumn(
                name: "CompletionOrdinal",
                table: "HabitLogs");

            migrationBuilder.CreateIndex(
                name: "IX_HabitLogs_HabitId_Date_Completed",
                table: "HabitLogs",
                columns: new[] { "HabitId", "Date" },
                unique: true,
                filter: "\"Value\" > 0 AND NOT \"IsDeleted\"");
        }
    }
}
