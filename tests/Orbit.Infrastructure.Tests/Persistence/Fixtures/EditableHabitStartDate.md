# Editable start-date persistence verification

The run owner executes this repeatable function proof on an existing, disposable PostgreSQL 17 database. The worker does not start a database or server. Use a direct or session connection through `PGSERVICE` or standard PostgreSQL environment variables; do not put credentials in command text or evidence. Recorded baseline, repaired-function and idempotence evidence does not replace deployed HTTP-to-PostgreSQL verification.

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

For the main-targeted PR #835, the run owner inspects the staging Terraform plan described in `infra/README.md` against the reviewed source. This correction adds no infrastructure setting or resource. Apply only a justified, reviewed target if the carried staging state requires it. In GitHub > orbit-api > Actions > Release API > Run workflow, select workflow branch `main` and `environment=staging`. Before merge, set `branch=fix/ticket-1585-editable-start-date` and require its published head to equal the exact reviewed commit SHA recorded in the PR. After merge, set `branch=main` and record the carrying merge SHA instead. Confirm the workflow's selected commit, Render staging API live deploy and staging health commit all equal that expected SHA before exercising the authenticated contracts.

Use an authenticated staging account to create a recurring habit with `startDate` on a day outside its fixed weekdays. Edit the anchor and read detail, full detail, schedule and sync; repeat the same save and a legacy save without `startDate`. Verify account week-start boundaries, today's persisted resolved log, terminal parent/child schedules and denied root reactivation. Compare existing log IDs, dates and values before and after. Check OneTime `dueDate` movement and undated General parent/child locks.

Consult `POST /api/habits/interpret-date` deliberately with `{ "phrase": "after the next holiday", "language": "en" }` and a Portuguese phrase with `pt-BR`. Inspect the returned `date` and server `referenceDate`, the account message allowance after terminal success or failure, and existing request/AI spend logs without recording phrases or account data. Confirm ambiguity and impossible explicit dates return null without reserving a message, including `October 26 or 27`, `26 or 27 October`, `26 ou 27 de outubro` and `26 de outubro ou 27`. Repeated references to the same date and unsupported unambiguous phrases remain eligible for one explicit consultation. Verify the provider's completed date/null response within the 128-token cap; quota, spend and provider failures must retain their typed errors, and caller cancellation must propagate without a proposed date. Verify account midnight and DST with different account timezones. Do not queue or retry this POST automatically.

The run owner reconciles `orbit-tickets#227` step 2 as a narrow extraction: reader migration, NextDueDate separation, enum documentation and flag contraction remain there. Closed `orbit-tickets#1558` remains the response-consumer work. `orbit-tickets#1586` waits for deployed API verification. After staging proof and normal merge/review gates, the run owner selects workflow branch `main`, `environment=production` and `branch=main` in the same Release API workflow, then confirms the selected commit is live before dependent UI dispatch. No new environment key, secret, manual backfill or fleet-floor raise is needed. The existing `AllowExplicitHabitStartDate` migration ships with the API; this correction adds no migration. Rolling back the API or restoring the former trigger function must retain selected anchors and logs.
