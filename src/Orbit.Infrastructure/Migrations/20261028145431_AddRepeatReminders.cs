using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRepeatReminders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SentReminders_HabitId_Date_MinutesBefore_ReminderTimeUtc_Wh~",
                table: "SentReminders");

            migrationBuilder.AddColumn<DateTime>(
                name: "ConsumedAtUtc",
                table: "SentReminders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DoseAnchorAtUtc",
                table: "SentReminders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExpiresAtUtc",
                table: "SentReminders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ParentReminderId",
                table: "SentReminders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReminderAtUtc",
                table: "SentReminders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "RepeatActiveEnd",
                table: "Habits",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "RepeatActiveStart",
                table: "Habits",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RepeatIntervalMinutes",
                table: "Habits",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RepeatStartAtUtc",
                table: "Habits",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReminderAtUtc",
                table: "HabitLogs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TakenAtUtc",
                table: "HabitLogs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SentReminders_HabitId_Date_MinutesBefore_ReminderTimeUtc_Wh~",
                table: "SentReminders",
                columns: new[] { "HabitId", "Date", "MinutesBefore", "ReminderTimeUtc", "When" },
                unique: true,
                filter: "\"ReminderAtUtc\" IS NULL")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_SentReminders_HabitId_ReminderAtUtc",
                table: "SentReminders",
                columns: new[] { "HabitId", "ReminderAtUtc" },
                unique: true,
                filter: "\"ReminderAtUtc\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SentReminders_HabitId_Date_MinutesBefore_ReminderTimeUtc_Wh~",
                table: "SentReminders");

            migrationBuilder.DropIndex(
                name: "IX_SentReminders_HabitId_ReminderAtUtc",
                table: "SentReminders");

            migrationBuilder.DropColumn(
                name: "ConsumedAtUtc",
                table: "SentReminders");

            migrationBuilder.DropColumn(
                name: "DoseAnchorAtUtc",
                table: "SentReminders");

            migrationBuilder.DropColumn(
                name: "ExpiresAtUtc",
                table: "SentReminders");

            migrationBuilder.DropColumn(
                name: "ParentReminderId",
                table: "SentReminders");

            migrationBuilder.DropColumn(
                name: "ReminderAtUtc",
                table: "SentReminders");

            migrationBuilder.DropColumn(
                name: "RepeatActiveEnd",
                table: "Habits");

            migrationBuilder.DropColumn(
                name: "RepeatActiveStart",
                table: "Habits");

            migrationBuilder.DropColumn(
                name: "RepeatIntervalMinutes",
                table: "Habits");

            migrationBuilder.DropColumn(
                name: "RepeatStartAtUtc",
                table: "Habits");

            migrationBuilder.DropColumn(
                name: "ReminderAtUtc",
                table: "HabitLogs");

            migrationBuilder.DropColumn(
                name: "TakenAtUtc",
                table: "HabitLogs");

            migrationBuilder.CreateIndex(
                name: "IX_SentReminders_HabitId_Date_MinutesBefore_ReminderTimeUtc_Wh~",
                table: "SentReminders",
                columns: new[] { "HabitId", "Date", "MinutesBefore", "ReminderTimeUtc", "When" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }
    }
}
