using Microsoft.EntityFrameworkCore.Migrations;
using Orbit.Domain.Common;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class HealHabitModelData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""
                CREATE OR REPLACE FUNCTION "NormalizeHabitModelData"()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $normalize$
                BEGIN
                    IF NEW."IsGeneral" THEN
                        NEW."EndDate" := NULL;
                    END IF;

                    IF NEW."DueTime" IS NOT NULL
                        AND jsonb_array_length(COALESCE(NEW."ScheduledReminders", '[]'::jsonb)) > 0 THEN
                        WITH offsets AS (
                            SELECT value::integer AS offset_minutes, ordinal::bigint AS source_position
                            FROM jsonb_array_elements_text(COALESCE(NEW."ReminderTimes", '[]'::jsonb))
                                WITH ORDINALITY AS existing(value, ordinal)
                            UNION ALL
                            SELECT GREATEST(
                                trunc(extract(epoch FROM (NEW."DueTime" - (scheduled.value->>'Time')::time)) / 60)::integer
                                    + CASE WHEN scheduled.value->>'When' = 'day_before' THEN 1440 ELSE 0 END,
                                0) AS offset_minutes,
                                1000 + scheduled.ordinal AS source_position
                            FROM jsonb_array_elements(NEW."ScheduledReminders")
                                WITH ORDINALITY AS scheduled(value, ordinal)
                        ), distinct_offsets AS (
                            SELECT offset_minutes, min(source_position) AS first_position
                            FROM offsets
                            GROUP BY offset_minutes
                            ORDER BY first_position
                            LIMIT {DomainConstants.MaxReminderTimes}
                        )
                        SELECT COALESCE(jsonb_agg(to_jsonb(offset_minutes) ORDER BY first_position), '[]'::jsonb)
                        INTO NEW."ReminderTimes"
                        FROM distinct_offsets;

                        NEW."ScheduledReminders" := '[]'::jsonb;
                    END IF;

                    RETURN NEW;
                END;
                $normalize$;

                DROP TRIGGER IF EXISTS "NormalizeHabitModelDataBeforeWrite" ON "Habits";
                CREATE TRIGGER "NormalizeHabitModelDataBeforeWrite"
                BEFORE INSERT OR UPDATE ON "Habits"
                FOR EACH ROW EXECUTE FUNCTION "NormalizeHabitModelData"();

                DO $audit$
                DECLARE general_count bigint;
                DECLARE mixed_count bigint;
                BEGIN
                    SELECT count(*) INTO general_count FROM "Habits"
                    WHERE "IsGeneral" AND "EndDate" IS NOT NULL;
                    SELECT count(*) INTO mixed_count FROM "Habits"
                    WHERE "DueTime" IS NOT NULL
                        AND jsonb_array_length(COALESCE("ScheduledReminders", '[]'::jsonb)) > 0;
                    RAISE NOTICE 'Habit model repair: % general dates, % mixed reminder rows', general_count, mixed_count;
                END;
                $audit$;

                UPDATE "Habits" SET "EndDate" = "EndDate"
                WHERE ("IsGeneral" AND "EndDate" IS NOT NULL)
                   OR ("DueTime" IS NOT NULL
                       AND jsonb_array_length(COALESCE("ScheduledReminders", '[]'::jsonb)) > 0);

                DO $verify$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "Habits" WHERE "IsGeneral" AND "EndDate" IS NOT NULL)
                        OR EXISTS (SELECT 1 FROM "Habits" WHERE "DueTime" IS NOT NULL
                            AND jsonb_array_length(COALESCE("ScheduledReminders", '[]'::jsonb)) > 0) THEN
                        RAISE EXCEPTION 'Habit model repair left invalid rows';
                    END IF;
                END;
                $verify$;
                """);
        }

        /// <summary>
        /// Removes the write guard. Folded reminder offsets cannot reconstruct the original scheduled representation.
        /// Cleared general end dates cannot be recovered.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS "NormalizeHabitModelDataBeforeWrite" ON "Habits";
                DROP FUNCTION IF EXISTS "NormalizeHabitModelData"();
                """);
        }
    }
}
