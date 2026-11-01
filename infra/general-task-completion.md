# General task completion release

Tracks thomasluizon/orbit-tickets#1940.

General is a one-time task with no date. Completion persists until an unlog.
The API contract retains `isGeneral` and every existing field and type.

Before the production API release, use **Render > production Postgres > Recovery >
Create export** and record the new completed export's identity in the release
evidence, following [the release procedure](README.md#production-api-image-source).

Run the following read-only preflight in the production PostgreSQL connection
listed under **Render > production Postgres > Connect**. Record every returned
count in the release evidence before deploying. These counts have to come from
production; unit fixtures cannot establish them.

```sql
BEGIN READ ONLY;

WITH eligible AS (
    SELECT h."Id", h."RepeatIntervalMinutes"
    FROM "Habits" h
    WHERE h."Schedule" = 3
      AND h."IsCompleted" = false
      AND h."IsDeleted" = false
      AND EXISTS (
          SELECT 1
          FROM "HabitLogs" l
          WHERE l."HabitId" = h."Id"
            AND l."Value" > 0
            AND l."IsDeleted" = false
            AND NOT EXISTS (
                SELECT 1
                FROM "HabitLogs" undone
                WHERE undone."HabitId" = h."Id"
                  AND undone."Value" > 0
                  AND undone."IsDeleted" = true
                  AND (undone."DeletedAtUtc" IS NULL
                       OR undone."DeletedAtUtc" >= l."CreatedAtUtc")
            )
      )
)
SELECT
    (SELECT count(*) FROM eligible) AS "RowsToComplete",
    (SELECT count(*) FROM eligible WHERE "RepeatIntervalMinutes" IS NOT NULL)
        AS "RowsToCompleteWithRepeat",
    (SELECT count(*) FROM "Habits" WHERE "Schedule" = 3 AND "IsDeleted" = false)
        AS "ActiveGeneralRows",
    (SELECT count(*) FROM "Habits" WHERE "Schedule" = 3 AND "IsDeleted" = false
         AND "RepeatIntervalMinutes" IS NOT NULL) AS "ActiveGeneralRowsWithRepeat",
    (SELECT count(*) FROM "Habits" WHERE "Schedule" = 3
         AND "RepeatIntervalMinutes" IS NOT NULL) AS "AllGeneralRowsWithRepeat";

COMMIT;
```

Migration `20261101145236_CompleteGeneralTasks` runs during the normal API startup
migration. It completes nondeleted General tasks with a surviving positive log
created after every known unlog. A deleted positive log's `DeletedAtUtc` is its
unlog instant. Missing unlog timestamps conservatively leave the task open.
Log dates do not determine completion, including when a person logged a past date.
Soft-deleted tasks, tasks with no positive logs, tasks with later unlogs, already
completed tasks and every other schedule remain unchanged.

Only `Habits.IsCompleted` changes. Existing log rows, titles, due dates, timestamps,
reminder settings and all other stored fields remain intact.
`GeneralTaskCompletionBackfill` records exactly the IDs changed from false to true;
`Down` reopens those IDs and removes that table. Rollback changes their completion
state even if a task has been used since release, so retain the export and review
subsequent writes when choosing rollback.

General rows with `RepeatIntervalMinutes` retain their interval and active window.
Their reminders run while the task is open. Completion stops reminders permanently;
unlogging reopens the task and makes its stored reminder settings eligible again.
The migration does not turn these tasks into recurring habits.
Older positive logs remain as history when an unlog reopens a task. A new
completion uses a fresh completion ordinal and does not toggle or discard that
history. The permanent completion guard still prevents a second completion.

After the production API release, confirm the migration ID in
`__EFMigrationsHistory` and compare this count with the recorded preflight:

```sql
SELECT count(*) AS "BackfilledRows" FROM "GeneralTaskCompletionBackfill";
```

Release the UI form change through thomasluizon/orbit-tickets#1922 after the API.

Generate the exact PostgreSQL scripts without connecting to a database from
`src/Orbit.Api`, with unused design-time connection strings:

```sh
ConnectionStrings__DefaultConnection='Host=unused;Database=unused' \
ConnectionStrings__SessionConnection='Host=unused;Database=unused' \
dotnet ef migrations script 20261031174509_AddEvalDailyBudget \
    20261101145236_CompleteGeneralTasks --project ../Orbit.Infrastructure \
    --startup-project . --output /tmp/general-task-completion-up.sql

ConnectionStrings__DefaultConnection='Host=unused;Database=unused' \
ConnectionStrings__SessionConnection='Host=unused;Database=unused' \
dotnet ef migrations script 20261101145236_CompleteGeneralTasks \
    20261031174509_AddEvalDailyBudget --project ../Orbit.Infrastructure \
    --startup-project . --output /tmp/general-task-completion-down.sql
```

The migration's `Up` emits `CREATE TABLE IF NOT EXISTS`, `INSERT ... SELECT ... ON
CONFLICT DO NOTHING` with the preflight predicate, and an `UPDATE` setting only
`IsCompleted = true` for recorded IDs. `Down` emits `CREATE TABLE IF NOT EXISTS`,
an `UPDATE` setting only `IsCompleted = false` for recorded IDs, and `DROP TABLE IF
EXISTS`. EF wraps each script in a transaction and updates `__EFMigrationsHistory`.
