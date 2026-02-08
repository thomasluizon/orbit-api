using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceSubHabitsWithParentChild : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SubHabitLogs");

            migrationBuilder.DropTable(
                name: "SubHabits");

            migrationBuilder.AddColumn<Guid>(
                name: "ParentHabitId",
                table: "Habits",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Habits_ParentHabitId",
                table: "Habits",
                column: "ParentHabitId");

            migrationBuilder.AddForeignKey(
                name: "FK_Habits_Habits_ParentHabitId",
                table: "Habits",
                column: "ParentHabitId",
                principalTable: "Habits",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Habits_Habits_ParentHabitId",
                table: "Habits");

            migrationBuilder.DropIndex(
                name: "IX_Habits_ParentHabitId",
                table: "Habits");

            migrationBuilder.DropColumn(
                name: "ParentHabitId",
                table: "Habits");

            migrationBuilder.CreateTable(
                name: "SubHabits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    HabitId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubHabits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubHabits_Habits_HabitId",
                        column: x => x.HabitId,
                        principalTable: "Habits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SubHabitLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    IsCompleted = table.Column<bool>(type: "boolean", nullable: false),
                    SubHabitId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubHabitLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubHabitLogs_SubHabits_SubHabitId",
                        column: x => x.SubHabitId,
                        principalTable: "SubHabits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SubHabitLogs_SubHabitId_Date",
                table: "SubHabitLogs",
                columns: new[] { "SubHabitId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_SubHabits_HabitId_SortOrder",
                table: "SubHabits",
                columns: new[] { "HabitId", "SortOrder" });
        }
    }
}
