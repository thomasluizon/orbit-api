# Canonical habit schedule persistence verification

Use the existing PostgreSQL fixture path with an existing disposable database, through `PGSERVICE` or standard PostgreSQL environment variables. The worker must not start a database or API server. Never put credentials in command text or evidence. This proof executes the installed production functions on temporary tables and rolls back every fixture row.

With repository migrations applied through `TrackOAuthClientIdentity`, run:

```sh
psql --no-psqlrc --file=tests/Orbit.Infrastructure.Tests/Persistence/Fixtures/CanonicalHabitSchedule.postgres.sql
```

Expect a nonzero exit with `Legacy flag write overwrote canonical Schedule`. The previous function derives the schedule from a flag-only write. The older `EditableHabitStartDate.postgres.sql` fixture describes pre-enum writers and is not the overlap contract for this release.

Generate the idempotent migration script through the design-time factory without starting the API or connecting EF to a live database:

```sh
cd src/Orbit.Api
dotnet ef migrations script TrackOAuthClientIdentity UnmapLegacyHabitScheduleColumns --project ../Orbit.Infrastructure --startup-project . --context OrbitDbContext --idempotent --output /tmp/orbit-canonical-habit-schedule.sql
cd ../..
psql --no-psqlrc --set=ON_ERROR_STOP=1 --file=/tmp/orbit-canonical-habit-schedule.sql
psql --no-psqlrc --file=tests/Orbit.Infrastructure.Tests/Persistence/Fixtures/CanonicalHabitSchedule.postgres.sql
```

Expect both commands to return zero. Repeat the script and fixture commands to prove idempotence. The fixture invokes both cursor and schedule triggers under their production names, preserving their execution order. It covers all four schedules, all schedule transitions from both overlapping write shapes, a flag-only write, conflicting flags, explicit and sentinel start dates, cursor changes, retained logs and canonical validation. The table columns and write shapes come from the repository migrations and EF configuration, including PostgreSQL `text[]` days.

The previous build, `dec1187a`, writes `Schedule` and `NextDueDate`, synchronizes `IsGeneral` and `IsFlexible` in `SaveChanges` and reads `DueDate` as a generated shadow property. This release writes only the canonical fields. The replacement schedule function derives both flags and `DueDate` on each insert or update; it never assigns `Schedule`. A legacy flag-only write is normalized to the row's canonical schedule. Both builds therefore retain the schedule written by the current canonical writer, with matching legacy flags. The existing cursor trigger remains compatible because both overlapping builds write `NextDueDate`.

The migration keeps all physical columns and performs no row backfill. Its rollback restores the exact previous schedule function. Revert application instances to a build that still synchronizes the flags before restoring that function.

The physical drop belongs in a later release after all API instances run this release. In Render Dashboard, inspect the live deploy for `orbit-api` and `orbit-api-staging`, confirm the selected commit and instance drain, and take a fresh export of each backing Render PostgreSQL database through its Exports screen before that destructive release. Record the export identity, successful completion and carrying commits. Verify the later migration removes the three columns and compatibility functions while preserving canonical schedule, next-due date, start date and logs. This release does not change the API contract or `AppConfigs.MinSupportedVersion`.
