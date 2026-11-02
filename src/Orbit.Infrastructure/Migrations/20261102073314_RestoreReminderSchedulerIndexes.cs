using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RestoreReminderSchedulerIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_SentReminders_HabitId_Date",
                table: "SentReminders",
                columns: new[] { "HabitId", "Date" })
                .Annotation("Npgsql:CreatedConcurrently", true);

            migrationBuilder.CreateIndex(
                name: "IX_Habits_Reminder",
                table: "Habits",
                columns: new[] { "ReminderEnabled", "IsCompleted", "Schedule" },
                filter: "\"ReminderEnabled\" = TRUE AND \"IsCompleted\" = FALSE")
                .Annotation("Npgsql:CreatedConcurrently", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SentReminders_HabitId_Date",
                table: "SentReminders");

            migrationBuilder.DropIndex(
                name: "IX_Habits_Reminder",
                table: "Habits");
        }
    }
}
