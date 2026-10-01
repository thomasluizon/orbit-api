using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddHabitSkipUndo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HabitSkipUndos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    HabitId = table.Column<Guid>(type: "uuid", nullable: false),
                    PreviousDueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    PreviousScheduledStartDate = table.Column<DateOnly>(type: "date", nullable: true),
                    PreviousIsCompleted = table.Column<bool>(type: "boolean", nullable: false),
                    SkipLogId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExpectedUpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpectedLogState = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IsUndone = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HabitSkipUndos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HabitSkipUndos_Habits_HabitId",
                        column: x => x.HabitId,
                        principalTable: "Habits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HabitSkipUndos_HabitId",
                table: "HabitSkipUndos",
                column: "HabitId");

            migrationBuilder.CreateIndex(
                name: "IX_HabitSkipUndos_UserId_HabitId",
                table: "HabitSkipUndos",
                columns: new[] { "UserId", "HabitId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HabitSkipUndos");
        }
    }
}
