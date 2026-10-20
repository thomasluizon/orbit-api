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
    "Days" integer[] NOT NULL DEFAULT '{}',
    "DueDate" date NOT NULL,
    "StartDate" date NOT NULL DEFAULT '0001-01-01',
    "Schedule" integer NOT NULL DEFAULT 0
) ON COMMIT DROP;
CREATE TEMP TABLE "HabitLogs" ("HabitId" integer, "Date" date, "Value" integer) ON COMMIT DROP;
CREATE TRIGGER fixture_sync_habit_schedule
BEFORE INSERT OR UPDATE ON "Habits"
FOR EACH ROW EXECUTE FUNCTION public.sync_habit_schedule_expand();
INSERT INTO "Habits" ("Id", "FrequencyUnit", "FrequencyQuantity", "DueDate", "StartDate")
VALUES (1, 0, 1, '2026-10-28', '2026-10-26');
INSERT INTO "Habits" ("Id", "FrequencyUnit", "FrequencyQuantity", "DueDate")
VALUES (2, 0, 1, '2026-10-20');
INSERT INTO "Habits" ("Id", "FrequencyUnit", "FrequencyQuantity", "IsFlexible", "DueDate")
VALUES (3, 1, 3, true, '2026-10-20');
INSERT INTO "Habits" ("Id", "DueDate") VALUES (4, '2026-10-20');
INSERT INTO "Habits" ("Id", "IsGeneral", "DueDate") VALUES (5, true, '2026-10-20');
INSERT INTO "HabitLogs" VALUES (1, '2026-10-20', 1), (1, '2026-10-19', 1);
UPDATE "Habits" SET "StartDate" = '2026-10-23', "DueDate" = '2026-10-28' WHERE "Id" = 1;
DO $$ BEGIN
    IF (SELECT "StartDate" FROM "Habits" WHERE "Id" = 1) <> DATE '2026-10-23' THEN
        RAISE EXCEPTION 'Explicit start-date edit was overwritten';
    END IF;
END $$;
UPDATE "Habits" SET "Title" = 'Renamed', "DueDate" = '2026-11-04' WHERE "Id" = 1;
UPDATE "Habits" SET "StartDate" = '2026-10-23' WHERE "Id" = 1;
UPDATE "Habits" SET "StartDate" = '0001-01-01' WHERE "Id" = 1;
UPDATE "Habits" SET "DueDate" = '2026-11-01' WHERE "Id" = 4;
DO $$
DECLARE rejected boolean := false;
BEGIN
    IF (SELECT "StartDate" FROM "Habits" WHERE "Id" = 1) <> DATE '2026-10-23'
       OR (SELECT "DueDate" FROM "Habits" WHERE "Id" = 1) <> DATE '2026-11-04'
       OR (SELECT "StartDate" FROM "Habits" WHERE "Id" = 2) <> DATE '2026-10-20' THEN
        RAISE EXCEPTION 'Legacy or unchanged writer changed the anchor or cursor';
    END IF;
    IF (SELECT array_agg("Schedule" ORDER BY "Id") FROM "Habits") <> ARRAY[1,1,2,0,3] THEN
        RAISE EXCEPTION 'Schedule synchronization failed';
    END IF;
    IF (SELECT "DueDate" FROM "Habits" WHERE "Id" = 4) <> DATE '2026-11-01'
       OR (SELECT count(*) FROM "HabitLogs") <> 2
       OR (SELECT sum("Value") FROM "HabitLogs") <> 2 THEN
        RAISE EXCEPTION 'One-time scheduling or history changed';
    END IF;
    BEGIN
        UPDATE "Habits" SET "IsGeneral" = true WHERE "Id" = 1;
    EXCEPTION WHEN OTHERS THEN
        rejected := true;
    END;
    IF NOT rejected THEN RAISE EXCEPTION 'Invalid legacy schedule was accepted'; END IF;
END $$;
ROLLBACK;
