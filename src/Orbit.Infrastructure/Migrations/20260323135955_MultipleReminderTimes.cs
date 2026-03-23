using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MultipleReminderTimes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SentReminders_HabitId_Date",
                table: "SentReminders");

            migrationBuilder.AddColumn<string>(
                name: "ReminderTimes",
                table: "Habits",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[15]'::jsonb");

            // Migrate existing single values to array format before dropping old column
            migrationBuilder.Sql(
                """UPDATE "Habits" SET "ReminderTimes" = jsonb_build_array("ReminderMinutesBefore") WHERE "ReminderMinutesBefore" IS NOT NULL""");

            migrationBuilder.DropColumn(
                name: "ReminderMinutesBefore",
                table: "Habits");

            migrationBuilder.AddColumn<int>(
                name: "MinutesBefore",
                table: "SentReminders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_SentReminders_HabitId_Date_MinutesBefore",
                table: "SentReminders",
                columns: new[] { "HabitId", "Date", "MinutesBefore" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SentReminders_HabitId_Date_MinutesBefore",
                table: "SentReminders");

            migrationBuilder.DropColumn(
                name: "MinutesBefore",
                table: "SentReminders");

            migrationBuilder.DropColumn(
                name: "ReminderTimes",
                table: "Habits");

            migrationBuilder.AddColumn<int>(
                name: "ReminderMinutesBefore",
                table: "Habits",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_SentReminders_HabitId_Date",
                table: "SentReminders",
                columns: new[] { "HabitId", "Date" },
                unique: true);
        }
    }
}
