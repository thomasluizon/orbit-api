using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ExpandHabitNextDueDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "NextDueDate",
                table: "Habits",
                type: "date",
                nullable: false,
                defaultValueSql: "'0001-01-01'");

            migrationBuilder.Sql("""
                UPDATE "Habits" SET "NextDueDate" = "DueDate";

                CREATE FUNCTION sync_habit_next_due_date_expand()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        IF NEW."NextDueDate" IS NULL OR NEW."NextDueDate" = DATE '0001-01-01' THEN
                            NEW."NextDueDate" := NEW."DueDate";
                        END IF;
                        NEW."DueDate" := NEW."NextDueDate";
                    ELSIF NEW."NextDueDate" IS DISTINCT FROM OLD."NextDueDate" THEN
                        NEW."DueDate" := NEW."NextDueDate";
                    ELSIF NEW."DueDate" IS DISTINCT FROM OLD."DueDate" THEN
                        NEW."NextDueDate" := NEW."DueDate";
                    END IF;
                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER sync_habit_next_due_date_expand_on_write
                BEFORE INSERT OR UPDATE ON "Habits"
                FOR EACH ROW EXECUTE FUNCTION sync_habit_next_due_date_expand();

                DO $$
                DECLARE affected_count bigint;
                BEGIN
                    SELECT COUNT(*) INTO affected_count FROM "Habits";
                    RAISE NOTICE 'Habit next-due expansion total rows: %', affected_count;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "Habits" SET "DueDate" = "NextDueDate";
                DROP TRIGGER IF EXISTS sync_habit_next_due_date_expand_on_write ON "Habits";
                DROP FUNCTION IF EXISTS sync_habit_next_due_date_expand();
                """);

            migrationBuilder.DropColumn(
                name: "NextDueDate",
                table: "Habits");
        }
    }
}
