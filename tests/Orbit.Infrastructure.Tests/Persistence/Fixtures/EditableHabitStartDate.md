# Editable start-date persistence verification

The run owner executes this proof on an existing, disposable PostgreSQL 17 database. The worker does not start a database or server. Use a direct or session connection through `PGSERVICE` or standard PostgreSQL environment variables; do not put credentials in command text or evidence.

First apply the repository migrations through `SeparateBonusProEntitlement`. Run:

```sh
psql --no-psqlrc --file=tests/Orbit.Infrastructure.Tests/Persistence/Fixtures/EditableHabitStartDate.postgres.sql
```

Expect a nonzero result with `Explicit start-date edit was overwritten`. The fixture invokes the installed production trigger function on temporary tables. Its transaction rolls back on disconnect after that error. Do not describe a SQLite or SQL-string assertion as this PostgreSQL proof.

Generate the corrective migration script from the design-time factory, without starting the API:

```sh
cd src/Orbit.Api
dotnet ef migrations script SeparateBonusProEntitlement AllowExplicitHabitStartDate --project ../Orbit.Infrastructure --startup-project . --idempotent --output /tmp/orbit-start-date.sql
cd ../..
psql --no-psqlrc --set=ON_ERROR_STOP=1 --file=/tmp/orbit-start-date.sql
psql --no-psqlrc --file=tests/Orbit.Infrastructure.Tests/Persistence/Fixtures/EditableHabitStartDate.postgres.sql
```

Expect both commands to return zero. Repeat both to prove migration-script and write compatibility. The fixture checks explicit create/edit readback, old inserts omitting the new column, old updates omitting it, unchanged saves, sentinel fallback, all four schedule values, one-time due-date movement, retained history and legacy validation. All fixture rows live in temporary tables and the script ends with rollback. Domain/application tests separately exercise production log, undo, catch-up, postpone, recurrence phase, General constraints and explicit-date retries.

After carrying the reviewed API to `redesign/main`, inspect the staging Terraform plan described in `infra/README.md`. This change adds no infrastructure setting or resource. Apply only a justified, reviewed target if the carried staging state requires it. Release the API through its existing release workflow, then use an authenticated staging account to create a recurring habit with `startDate` on a day outside its fixed weekdays. Edit the anchor and read detail, full detail, schedule and sync; repeat the same save and a legacy save without `startDate`. Compare existing log IDs, dates and values before and after. Check OneTime `dueDate` movement and undated General parent/child locks.

Consult `POST /api/habits/interpret-date` deliberately with `{ "phrase": "after the next holiday", "language": "en" }` and a Portuguese phrase with `pt-BR`. Inspect the returned `date` and server `referenceDate`, the account message allowance after terminal success or failure, and existing request/AI spend logs without recording phrases or account data. Confirm ambiguity and impossible explicit dates return null without reserving a message; quota, spend and provider failures keep their error codes. Verify account midnight and DST with different account timezones. Do not queue or retry this POST automatically.

The run owner reconciles `orbit-tickets#227` step 2 as a narrow extraction: reader migration, NextDueDate separation, enum documentation and flag contraction remain there. Closed `orbit-tickets#1558` remains the response-consumer work. `orbit-tickets#1586` waits for deployed API verification. After staging proof and normal merge/review gates, the run owner performs the authorized production API release before UI dispatch. No fleet-floor raise is needed. Rolling back the API or restoring the former trigger function must retain selected anchors and logs.
