using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SimplifyHabitModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TargetValue",
                table: "Habits");

            migrationBuilder.DropColumn(
                name: "Type",
                table: "Habits");

            migrationBuilder.DropColumn(
                name: "Unit",
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

            migrationBuilder.AddColumn<int>(
                name: "Type",
                table: "Habits",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Unit",
                table: "Habits",
                type: "text",
                nullable: true);
        }
    }
}
