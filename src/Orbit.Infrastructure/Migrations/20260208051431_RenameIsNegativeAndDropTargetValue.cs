using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RenameIsNegativeAndDropTargetValue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TargetValue",
                table: "Habits");

            migrationBuilder.RenameColumn(
                name: "IsNegative",
                table: "Habits",
                newName: "IsBadHabit");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "IsBadHabit",
                table: "Habits",
                newName: "IsNegative");

            migrationBuilder.AddColumn<decimal>(
                name: "TargetValue",
                table: "Habits",
                type: "numeric",
                nullable: true);
        }
    }
}
