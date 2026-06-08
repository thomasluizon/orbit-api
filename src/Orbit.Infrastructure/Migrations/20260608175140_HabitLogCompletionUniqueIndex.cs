using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class HabitLogCompletionUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DELETE FROM ""HabitLogs"" a
                USING ""HabitLogs"" b
                WHERE a.""Id"" < b.""Id""
                  AND a.""HabitId"" = b.""HabitId""
                  AND a.""Date""    = b.""Date""
                  AND a.""Value"" > 0
                  AND b.""Value"" > 0;");

            migrationBuilder.CreateIndex(
                name: "IX_HabitLogs_HabitId_Date_Completed",
                table: "HabitLogs",
                columns: new[] { "HabitId", "Date" },
                unique: true,
                filter: "\"Value\" > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_HabitLogs_HabitId_Date_Completed",
                table: "HabitLogs");
        }
    }
}
