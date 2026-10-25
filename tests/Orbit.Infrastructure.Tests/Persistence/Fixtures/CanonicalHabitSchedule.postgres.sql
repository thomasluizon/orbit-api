\set ON_ERROR_STOP on
BEGIN;
CREATE TEMP TABLE "Habits" (
    "Id" integer PRIMARY KEY,
    "Title" text NOT NULL DEFAULT 'Habit',
    "IsGeneral" boolean NOT NULL DEFAULT false,
    "IsFlexible" boolean NOT NULL DEFAULT false,
    "IsBadHabit" boolean NOT NULL DEFAULT false,
    "FrequencyUnit" integer,
    "FrequencyQuantity" integer,
    "Days" text[] NOT NULL DEFAULT '{}',
    "DueDate" date NOT NULL,
    "NextDueDate" date NOT NULL DEFAULT '0001-01-01',
    "StartDate" date NOT NULL DEFAULT '0001-01-01',
    "Schedule" integer NOT NULL DEFAULT 0
) ON COMMIT DROP;
CREATE TEMP TABLE "HabitLogs" ("HabitId" integer, "Date" date, "Value" integer) ON COMMIT DROP;
CREATE TRIGGER sync_habit_next_due_date_expand_on_write
BEFORE INSERT OR UPDATE ON "Habits"
FOR EACH ROW EXECUTE FUNCTION public.sync_habit_next_due_date_expand();
CREATE TRIGGER sync_habit_schedule_expand_on_write
BEFORE INSERT OR UPDATE ON "Habits"
FOR EACH ROW EXECUTE FUNCTION public.sync_habit_schedule_expand();

INSERT INTO "Habits" ("Id", "Schedule", "IsGeneral", "IsFlexible", "FrequencyUnit", "FrequencyQuantity", "NextDueDate", "StartDate")
VALUES (1, 0, false, false, NULL, NULL, '2026-10-28', '2026-10-26'),
       (2, 1, false, false, 0, 1, '2026-10-28', '2026-10-26'),
       (3, 2, false, true, 1, 3, '2026-10-28', '2026-10-26'),
       (4, 3, true, false, NULL, NULL, '2026-10-28', '2026-10-26');
UPDATE "Habits" SET "IsFlexible" = false WHERE "Id" = 3;
DO $$ BEGIN
    IF (SELECT "Schedule" FROM "Habits" WHERE "Id" = 3) <> 2
       OR NOT (SELECT "IsFlexible" FROM "Habits" WHERE "Id" = 3) THEN
        RAISE EXCEPTION 'Legacy flag write overwrote canonical Schedule';
    END IF;
END $$;
UPDATE "Habits" SET "IsGeneral" = true, "IsFlexible" = true;

INSERT INTO "Habits" ("Id", "Schedule", "FrequencyUnit", "FrequencyQuantity", "NextDueDate", "StartDate")
VALUES (5, 0, NULL, NULL, '2026-10-28', '2026-10-26'),
       (6, 1, 0, 1, '2026-10-28', '2026-10-26'),
       (7, 2, 1, 3, '2026-10-28', '2026-10-26'),
       (8, 3, NULL, NULL, '2026-10-28', '2026-10-26');
INSERT INTO "HabitLogs" VALUES (2, '2026-10-26', 1), (2, '2026-10-27', 1);
DO $$
DECLARE target integer;
BEGIN
    IF (SELECT array_agg("Schedule" ORDER BY "Id") FROM "Habits") <> ARRAY[0,1,2,3,0,1,2,3] THEN
        RAISE EXCEPTION 'Insert changed the schedule selected by an overlapping build';
    END IF;
    IF EXISTS (SELECT FROM "Habits" WHERE "IsGeneral" <> ("Schedule" = 3)
               OR "IsFlexible" <> ("Schedule" = 2) OR "DueDate" <> "NextDueDate") THEN
        RAISE EXCEPTION 'Insert or flag-only update left incompatible legacy columns';
    END IF;
    FOR target IN 0..3 LOOP
        UPDATE "Habits" SET "Schedule" = target,
            "FrequencyUnit" = CASE WHEN target IN (1,2) THEN 0 ELSE NULL END,
            "FrequencyQuantity" = CASE WHEN target IN (1,2) THEN 1 ELSE NULL END,
            "NextDueDate" = DATE '2026-11-04' + target;
        IF EXISTS (SELECT FROM "Habits" WHERE "Schedule" <> target
                   OR "IsGeneral" <> (target = 3) OR "IsFlexible" <> (target = 2)
                   OR "DueDate" <> DATE '2026-11-04' + target
                   OR "NextDueDate" <> DATE '2026-11-04' + target
                   OR "StartDate" <> DATE '2026-10-26') THEN
            RAISE EXCEPTION 'Canonical transition failed for schedule %', target;
        END IF;
        UPDATE "Habits" SET "Schedule" = target,
            "IsGeneral" = (target = 3), "IsFlexible" = (target = 2),
            "FrequencyUnit" = CASE WHEN target IN (1,2) THEN 0 ELSE NULL END,
            "FrequencyQuantity" = CASE WHEN target IN (1,2) THEN 1 ELSE NULL END,
            "NextDueDate" = DATE '2026-11-11' + target;
        IF EXISTS (SELECT FROM "Habits" WHERE "Schedule" <> target
                   OR "IsGeneral" <> (target = 3) OR "IsFlexible" <> (target = 2)
                   OR "DueDate" <> DATE '2026-11-11' + target
                   OR "NextDueDate" <> DATE '2026-11-11' + target
                   OR "StartDate" <> DATE '2026-10-26') THEN
            RAISE EXCEPTION 'Previous-build transition failed for schedule %', target;
        END IF;
    END LOOP;
END $$;

UPDATE "Habits" SET "StartDate" = '2026-10-23' WHERE "Id" = 2;
UPDATE "Habits" SET "StartDate" = '0001-01-01', "Title" = 'Renamed' WHERE "Id" = 2;
INSERT INTO "Habits" ("Id", "Schedule", "FrequencyUnit", "FrequencyQuantity", "NextDueDate")
VALUES (9, 1, 0, 1, '2026-10-28');
DO $$
DECLARE rejected boolean := false;
BEGIN
    IF (SELECT "StartDate" FROM "Habits" WHERE "Id" = 2) <> DATE '2026-10-23'
       OR (SELECT "StartDate" FROM "Habits" WHERE "Id" = 9) <> DATE '2026-10-28'
       OR (SELECT count(*) FROM "HabitLogs") <> 2
       OR (SELECT sum("Value") FROM "HabitLogs") <> 2 THEN
        RAISE EXCEPTION 'Start-date editing, fallback or retained history changed';
    END IF;
    BEGIN
        UPDATE "Habits" SET "Schedule" = 3 WHERE "Id" = 9;
    EXCEPTION WHEN raise_exception THEN
        rejected := true;
    END;
    IF NOT rejected THEN RAISE EXCEPTION 'Invalid canonical schedule was accepted'; END IF;
END $$;
ROLLBACK;
