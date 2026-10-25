using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DropLegacyHabitScheduleColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS sync_habit_schedule_expand_on_write ON "Habits";
                DROP FUNCTION IF EXISTS sync_habit_schedule_expand();
                DROP TRIGGER IF EXISTS sync_habit_next_due_date_expand_on_write ON "Habits";
                DROP FUNCTION IF EXISTS sync_habit_next_due_date_expand();
                DROP INDEX IF EXISTS "IX_Habits_Reminder";
                DROP INDEX IF EXISTS "IX_Habits_DueDateAdvancement";
                """);

            migrationBuilder.DropColumn(
                name: "IsGeneral",
                table: "Habits");

            migrationBuilder.DropColumn(
                name: "IsFlexible",
                table: "Habits");

            migrationBuilder.DropColumn(
                name: "DueDate",
                table: "Habits");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsGeneral",
                table: "Habits",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsFlexible",
                table: "Habits",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateOnly>(
                name: "DueDate",
                table: "Habits",
                type: "date",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "Habits"
                SET "IsGeneral" = "Schedule" = 3,
                    "IsFlexible" = "Schedule" = 2,
                    "DueDate" = "NextDueDate";
                """);

            migrationBuilder.AlterColumn<DateOnly>(
                name: "DueDate",
                table: "Habits",
                type: "date",
                nullable: false,
                oldClrType: typeof(DateOnly),
                oldType: "date",
                oldNullable: true);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION sync_habit_schedule_expand()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    NEW."IsGeneral" := NEW."Schedule" = 3;
                    NEW."IsFlexible" := NEW."Schedule" = 2;
                    NEW."DueDate" := NEW."NextDueDate";
                    IF (NEW."IsGeneral" AND (NEW."FrequencyUnit" IS NOT NULL OR NEW."FrequencyQuantity" IS NOT NULL))
                       OR (NEW."IsGeneral" AND NEW."IsBadHabit")
                       OR (NEW."IsFlexible" AND NEW."FrequencyUnit" IS NULL)
                       OR (NEW."IsFlexible" AND cardinality(NEW."Days") > 0) THEN
                        RAISE EXCEPTION 'Invalid legacy habit schedule';
                    END IF;
                    IF TG_OP = 'INSERT' THEN
                        IF NEW."StartDate" IS NULL OR NEW."StartDate" = DATE '0001-01-01' THEN
                            NEW."StartDate" := NEW."DueDate";
                        END IF;
                    ELSIF NEW."StartDate" IS NULL OR NEW."StartDate" = DATE '0001-01-01' THEN
                        NEW."StartDate" := OLD."StartDate";
                    END IF;
                    RETURN NEW;
                END;
                $$;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER sync_habit_schedule_expand_on_write
                BEFORE INSERT OR UPDATE ON "Habits"
                FOR EACH ROW EXECUTE FUNCTION sync_habit_schedule_expand();
                """);

            migrationBuilder.Sql("""
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
                """);

            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_Habits_Reminder"
                ON "Habits" ("ReminderEnabled", "IsCompleted", "IsGeneral")
                WHERE "ReminderEnabled" = true AND "IsCompleted" = false;
                """);

            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_Habits_DueDateAdvancement"
                ON "Habits" ("DueDate", "IsCompleted", "FrequencyUnit")
                WHERE "IsCompleted" = false AND "FrequencyUnit" IS NOT NULL;
                """);
        }
    }
}
