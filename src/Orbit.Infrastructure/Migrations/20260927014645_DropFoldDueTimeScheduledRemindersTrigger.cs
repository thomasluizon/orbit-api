using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DropFoldDueTimeScheduledRemindersTrigger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS fold_due_time_scheduled_reminders_on_write ON "Habits";
                DROP FUNCTION IF EXISTS fold_due_time_scheduled_reminders();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Restoring the obsolete reminder trigger would reintroduce incorrect updates.");
        }
    }
}
