using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSoftDeleteToSyncedEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_HabitLogs_HabitId_Date_Completed",
                table: "HabitLogs");

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAtUtc",
                table: "Notifications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "Notifications",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAtUtc",
                table: "HabitLogs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "HabitLogs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAtUtc",
                table: "GoalProgressLogs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "GoalProgressLogs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAtUtc",
                table: "ChecklistTemplates",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "ChecklistTemplates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_UserId_IsDeleted",
                table: "Notifications",
                columns: new[] { "UserId", "IsDeleted" });

            migrationBuilder.CreateIndex(
                name: "IX_HabitLogs_HabitId_Date_Completed",
                table: "HabitLogs",
                columns: new[] { "HabitId", "Date" },
                unique: true,
                filter: "\"Value\" > 0 AND NOT \"IsDeleted\"");

            migrationBuilder.CreateIndex(
                name: "IX_GoalProgressLogs_GoalId_IsDeleted",
                table: "GoalProgressLogs",
                columns: new[] { "GoalId", "IsDeleted" });

            migrationBuilder.CreateIndex(
                name: "IX_ChecklistTemplates_UserId_IsDeleted",
                table: "ChecklistTemplates",
                columns: new[] { "UserId", "IsDeleted" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Notifications_UserId_IsDeleted",
                table: "Notifications");

            migrationBuilder.DropIndex(
                name: "IX_HabitLogs_HabitId_Date_Completed",
                table: "HabitLogs");

            migrationBuilder.DropIndex(
                name: "IX_GoalProgressLogs_GoalId_IsDeleted",
                table: "GoalProgressLogs");

            migrationBuilder.DropIndex(
                name: "IX_ChecklistTemplates_UserId_IsDeleted",
                table: "ChecklistTemplates");

            migrationBuilder.DropColumn(
                name: "DeletedAtUtc",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "DeletedAtUtc",
                table: "HabitLogs");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "HabitLogs");

            migrationBuilder.DropColumn(
                name: "DeletedAtUtc",
                table: "GoalProgressLogs");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "GoalProgressLogs");

            migrationBuilder.DropColumn(
                name: "DeletedAtUtc",
                table: "ChecklistTemplates");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "ChecklistTemplates");

            migrationBuilder.CreateIndex(
                name: "IX_HabitLogs_HabitId_Date_Completed",
                table: "HabitLogs",
                columns: new[] { "HabitId", "Date" },
                unique: true,
                filter: "\"Value\" > 0");
        }
    }
}
