using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSentReminderWhenDiscriminator : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SentReminders_HabitId_Date_MinutesBefore_ReminderTimeUtc",
                table: "SentReminders");

            migrationBuilder.AddColumn<int>(
                name: "When",
                table: "SentReminders",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SentReminders_HabitId_Date_MinutesBefore_ReminderTimeUtc_Wh~",
                table: "SentReminders",
                columns: new[] { "HabitId", "Date", "MinutesBefore", "ReminderTimeUtc", "When" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SentReminders_HabitId_Date_MinutesBefore_ReminderTimeUtc_Wh~",
                table: "SentReminders");

            migrationBuilder.DropColumn(
                name: "When",
                table: "SentReminders");

            migrationBuilder.CreateIndex(
                name: "IX_SentReminders_HabitId_Date_MinutesBefore_ReminderTimeUtc",
                table: "SentReminders",
                columns: new[] { "HabitId", "Date", "MinutesBefore", "ReminderTimeUtc" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }
    }
}
