using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ExpandHabitScheduleAndStartDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE "Habits" IN SHARE ROW EXCLUSIVE MODE;
                DO $$
                DECLARE invalid_count bigint;
                BEGIN
                    SELECT COUNT(*) INTO invalid_count
                    FROM "Habits"
                    WHERE ("IsGeneral" AND ("FrequencyUnit" IS NOT NULL OR "FrequencyQuantity" IS NOT NULL))
                       OR ("IsGeneral" AND "IsBadHabit")
                       OR ("IsFlexible" AND "FrequencyUnit" IS NULL)
                       OR ("IsFlexible" AND cardinality("Days") > 0);
                    IF invalid_count > 0 THEN
                        RAISE EXCEPTION 'Habit schedule expansion aborted: % invalid legacy rows', invalid_count;
                    END IF;
                END $$;
                """);

            migrationBuilder.AddColumn<int>(
                name: "Schedule",
                table: "Habits",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateOnly>(
                name: "StartDate",
                table: "Habits",
                type: "date",
                nullable: false,
                defaultValueSql: "'0001-01-01'");

            // https://github.com/thomasluizon/orbit-tickets/issues/227: StartDate is the date the habit was created to start; the clamp applies to the backfill only. Approximate the unrecoverable original from the earliest active log, else UTC creation date, capped at DueDate. ScheduledStartDate remains the recurrence anchor. Bump UpdatedAtUtc for a full delta-sync resend of the added fields.
            migrationBuilder.Sql("""
                UPDATE "Habits" AS h
                SET "StartDate" = LEAST(
                        COALESCE(
                            (SELECT MIN(l."Date") FROM "HabitLogs" AS l
                             WHERE l."HabitId" = h."Id" AND NOT l."IsDeleted"),
                            DATE(h."CreatedAtUtc" AT TIME ZONE 'UTC')),
                        h."DueDate"),
                    "Schedule" = CASE
                        WHEN h."IsGeneral" THEN 3
                        WHEN h."IsFlexible" THEN 2
                        WHEN h."FrequencyUnit" IS NULL THEN 0
                        ELSE 1
                    END,
                    "UpdatedAtUtc" = now();
                """);

            // https://github.com/thomasluizon/orbit-tickets/issues/227: older API processes still write only the legacy columns during the expand rollout.
            migrationBuilder.Sql("""
                CREATE FUNCTION sync_habit_schedule_expand()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF (NEW."IsGeneral" AND (NEW."FrequencyUnit" IS NOT NULL OR NEW."FrequencyQuantity" IS NOT NULL))
                       OR (NEW."IsGeneral" AND NEW."IsBadHabit")
                       OR (NEW."IsFlexible" AND NEW."FrequencyUnit" IS NULL)
                       OR (NEW."IsFlexible" AND cardinality(NEW."Days") > 0) THEN
                        RAISE EXCEPTION 'Invalid legacy habit schedule';
                    END IF;
                    NEW."Schedule" := CASE
                        WHEN NEW."IsGeneral" THEN 3
                        WHEN NEW."IsFlexible" THEN 2
                        WHEN NEW."FrequencyUnit" IS NULL THEN 0
                        ELSE 1
                    END;
                    IF TG_OP = 'INSERT' THEN
                        IF NEW."StartDate" IS NULL OR NEW."StartDate" = DATE '0001-01-01' THEN
                            NEW."StartDate" := NEW."DueDate";
                        END IF;
                    ELSE
                        NEW."StartDate" := OLD."StartDate";
                    END IF;
                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER sync_habit_schedule_expand_on_write
                BEFORE INSERT OR UPDATE ON "Habits"
                FOR EACH ROW EXECUTE FUNCTION sync_habit_schedule_expand();

                DO $$
                DECLARE affected_count bigint;
                BEGIN
                    SELECT COUNT(*) INTO affected_count FROM "Habits";
                    RAISE NOTICE 'Habit schedule expansion total rows: %', affected_count;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS sync_habit_schedule_expand_on_write ON "Habits";
                DROP FUNCTION IF EXISTS sync_habit_schedule_expand();
                """);

            migrationBuilder.DropColumn(
                name: "Schedule",
                table: "Habits");

            migrationBuilder.DropColumn(
                name: "StartDate",
                table: "Habits");
        }
    }
}
