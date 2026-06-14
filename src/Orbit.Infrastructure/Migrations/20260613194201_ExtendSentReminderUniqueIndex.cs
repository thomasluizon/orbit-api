using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ExtendSentReminderUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SentReminders_HabitId_Date_MinutesBefore",
                table: "SentReminders");

            migrationBuilder.CreateIndex(
                name: "IX_SentReminders_HabitId_Date_MinutesBefore_ReminderTimeUtc",
                table: "SentReminders",
                columns: new[] { "HabitId", "Date", "MinutesBefore", "ReminderTimeUtc" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SentReminders_HabitId_Date_MinutesBefore_ReminderTimeUtc",
                table: "SentReminders");

            migrationBuilder.CreateIndex(
                name: "IX_SentReminders_HabitId_Date_MinutesBefore",
                table: "SentReminders",
                columns: new[] { "HabitId", "Date", "MinutesBefore" },
                unique: true);
        }
    }
}
