# Distributed limiter PostgreSQL proof

Tracks thomasluizon/orbit-tickets#1597.

This opt-in console runner uses the actual service, model, production provider retries,
independent contexts and synchronized starts. It does not start a server, create a database,
apply migrations or run as part of `dotnet test`. Root owns execution against an existing,
owned, disposable PostgreSQL 17 database with current migrations already applied.

Load these keys through a secret-safe environment loader, without printing their values:

- `ORBIT_RATE_LIMIT_PG17_CONNECTION_STRING`: connection to the owned disposable database.
- `ORBIT_RATE_LIMIT_PG17_EXPECTED_DATABASE`: exact database name checked before any mutation.
- `ORBIT_RATE_LIMIT_PG17_DISPOSABLE=yes`: explicit disposable acknowledgement.

Never use production or shared data: admission retains policy-wide expiry cleanup. The
runner deletes its uniquely prefixed synthetic rows in a `finally` block, but cleanup in
an acquisition can remove expired rows outside that prefix. Error output includes exception
types and SQLSTATE chains, without messages, credentials or partition values.

```sh
dotnet run --project bench/Orbit.RateLimitProof
```

The unchanged scratch `RateLimitPostgresProof.csproj` remains compatible with this service.
This durable runner adds timing, bounded queue controls and mixed policies. To compare the
same inputs against another source tree without copying or changing its service:

```sh
dotnet run --project bench/Orbit.RateLimitProof -p:OrbitApiRoot="$RATE_LIMIT_BASELINE_REPO"
dotnet run --project bench/Orbit.RateLimitProof -p:OrbitApiRoot="$RATE_LIMIT_API_REPO"
```

Rebuild between source roots. Record each exact git SHA beside its complete output. Run both
against the same disposable instance and connection settings. The controls cover 32 cold
fixed admissions, 16 warm fixed admissions with 8 seeded, 32 cold segmented admissions,
16 warm segmented admissions with 18 seeded, 16 admissions with 19 in the previous segment,
segment rollover, exact expiry, 8 independent partitions, 8 simultaneous policies on one
partition, 8 simultaneous leases, expiry, release and stale release.

Each burst reports total milliseconds, acquisition p50/p95/max milliseconds, requests per
second, maximum transactional attempts, allowed/denied/error totals and persisted counts.
Percentiles use nearest rank, include errors and include connection/transaction/queue time
inside acquisition. Context construction happens before the start barrier. Bursts have a
30-second cancellation backstop. Three attempts remains the acceptance ceiling even when
comparing against an older baseline that violates it.

The queue controls hold a real table lock in a separate transaction, then confirm the caller
is blocked by that backend using `pg_blocking_pids` before signaling cancellation or releasing
the holder. They record time from signal to completion, assert cancellation saves nothing and
disposes its transaction, and prove subsequent admission succeeds. A failed-save control
also proves another connection can acquire while the failed context remains alive. Queue
controls have a 15-second cancellation backstop. These are hung-run guards, not production
latency targets or a wall-clock cancellation assertion.

Acceptance requires exit 0 and `PG17_ASSERT failures=0; expected=0`, no terminal errors,
matching allowed/stored totals, three or fewer attempts, no duplicate segments, passing queue
controls, and repeated baseline/candidate timing review. A passing correctness run alone does
not approve latency. The candidate serializes all short bucket admissions across policies and
partitions, so tail latency and throughput can regress under load. Root must judge the measured
queue and tail costs before release. No protected action, tool, AI operation or lease lifetime
runs while admission holds its transaction lock. The existing provider command deadline is
unchanged; caller cancellation reaches the new lock statement.

## External interface evidence

The lock statement and pre-query ordering follow PostgreSQL 17's
[LOCK source documentation](https://www.postgresql.org/docs/17/sql-lock.html).
Npgsql's installed 10.0.3 provider is exercised by the command-interception unit guard;
connection opening and command execution are explicitly suppressed there. SQLite tests
observe the ordering hook and real transaction disposal and skip PostgreSQL SQL entirely.
Neither unit path claims to execute a PostgreSQL lock.

The queue observer reads `NpgsqlConnection.ProcessID`, verified against the matching
[Npgsql 10.0.3 source](https://github.com/npgsql/npgsql/blob/v10.0.3/src/Npgsql/NpgsqlConnection.cs),
where its getter returns `Connector.BackendProcessId` after checking readiness.
The SQL identity query defines its two scalar columns explicitly. The queue query is
`SELECT @holder = ANY(pg_blocking_pids(@waiter))` and accepts only an actual boolean `true`.
The [PostgreSQL system information functions](https://www.postgresql.org/docs/17/functions-info.html)
describe the blocking backend function. Root execution must retain `PG17_IDENTITY` and
`PG17_QUEUE` receipts from the real server. Database outcomes are intentionally not replaced
with unit fixtures or claimed from documentation.
