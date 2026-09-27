using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FoldDueTimeScheduledReminders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RelativeReminders",
                table: "Habits",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.Sql("""
                UPDATE "Habits"
                SET "RelativeReminders" = "ScheduledReminders",
                    "ScheduledReminders" = '[]'::jsonb
                WHERE "DueTime" IS NOT NULL
                  AND jsonb_array_length("ScheduledReminders") > 0;

                CREATE OR REPLACE FUNCTION fold_due_time_scheduled_reminders()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW."DueTime" IS NOT NULL
                       AND jsonb_array_length(NEW."ScheduledReminders") > 0 THEN
                        SELECT COALESCE(jsonb_agg(value), '[]'::jsonb)
                        INTO NEW."RelativeReminders"
                        FROM (
                            SELECT DISTINCT value
                            FROM jsonb_array_elements(
                                NEW."RelativeReminders" || NEW."ScheduledReminders") AS entries(value)
                        ) AS distinct_reminders;
                        NEW."ScheduledReminders" := '[]'::jsonb;
                    END IF;
                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER fold_due_time_scheduled_reminders_on_write
                BEFORE INSERT OR UPDATE ON "Habits"
                FOR EACH ROW EXECUTE FUNCTION fold_due_time_scheduled_reminders();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Reverting relative reminders would discard user reminders.");
        }
    }
}
