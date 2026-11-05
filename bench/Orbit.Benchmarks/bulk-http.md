# Bulk HTTP request evidence

`BulkHabitsPerformanceTests` invokes the MVC request delegate for `POST /api/habits/bulk/log` and `POST /api/habits/bulk/skip` without a listening app server. It uses real MediatR validators and behaviors, the HTTP idempotency context/store, PostgreSQL repositories, user locks, date and streak services, gamification, goal completion, reminder inbox and versioned cache invalidation. Authentication is supplied by the host; distributed preference caching is in memory, and background delivery is substituted.

Each sample creates a fresh user with a bad-habit parent and four children. The ten-item case adds five ordinary recurring habits. Each habit has 400 persisted historical logs, plus current-day and older reminder notifications. Selection orders children before their parent, exercising derived parent logging and the parent's idempotent result. Both implementations use the same factories, data and final request host. Every request carries an idempotency key and is replayed to assert an identical response with one read and zero writes. Reminder and persisted habit/log outcomes are asserted.

PostgreSQL 15.19 runs in a disposable Docker container limited to 512 MiB. The .NET 10.0.401 Debug test process is configured with `DOTNET_GCHeapHardLimit=0x20000000`. The host is reused for ten requests per cohort; the first warms it and is discarded, leaving nine timed samples. Database creation, migration, seeding, host construction and replay assertions are outside the timer. Compilation runs without the heap cap. These are local measurements with loopback database traffic, not a Render CPU or full process-memory reproduction. The orchestrator owns staging HTTP round trips after release because the production interval has no endpoint timings, per the owning ticket comment.

## Results

| Operation | Items | Before p50 ms | After p50 ms | SQL commands before / after | Habit rows before / after |
| --- | ---: | ---: | ---: | ---: | ---: |
| log | 5 | 206.94 | 102.64 | 56 / 40 | 30 / 5 |
| log | 10 | 299.92 | 156.82 | 59 / 43 | 40 / 10 |
| skip | 5 | 96.04 | 33.31 | 17 / 15 | 10 / 5 |
| skip | 10 | 159.74 | 77.02 | 18 / 16 | 20 / 10 |

The phase figures below are exclusive times in milliseconds for the request at each cohort's total-latency median, not independent phase medians. Loads include query execution and materialization. Save includes every save in the idempotent transaction. Processing in transaction excludes measured child calls and includes transaction completion; HTTP and setup is the remainder. Lock wait includes both advisory and user-row acquisition.

| Operation | State | Validation | Idempotency read | Lock wait | Loads | Processing in transaction | Save | Cache invalidation | HTTP and setup |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| log 10 | before | 0.15 | 13.33 | 6.78 | 118.87 | 27.04 | 131.37 | 1.57 | 0.80 |
| log 10 | after | 0.04 | 11.08 | 3.15 | 58.87 | 22.18 | 45.39 | 0.90 | 15.22 |
| skip 10 | before | 0.06 | 10.97 | 2.55 | 66.94 | 14.05 | 63.06 | 0.80 | 1.31 |
| skip 10 | after | 0.05 | 10.91 | 2.34 | 15.98 | 16.27 | 28.96 | 1.20 | 1.32 |

The [raw evidence](bulk-http-evidence.json) retains all nine total-latency samples and the median request's complete counters and phases. SQL counts include writes. Habit row counts refer to entity-shaped habit reads, excluding scalar schedule projections and log/goal joins. The log after-case has a second, empty related-family read; selected habit rows are returned once.

## Regression proof

Before implementation, the unchanged application bulk log/skip handler suite passed all 46 tests, and the existing query-count suites passed all 11 tests. The added `HttpBulk_HistoryAndChildren_ReadEachHabitSetOnce` test fails against the baseline handler sources: log returns 40 habit rows for ten selected items, skip returns 20. With the implementation it returns ten for each endpoint. The keyed PostgreSQL timing cases pass on both implementations, including persistence, reminders and replay assertions.

For the baseline comparison, the five application files changed by this optimization were restored from `0edcb188a3d8522bb964d07427fb657ec6f522eb`: `BulkHabitLoader`, both bulk handlers, `BadHabitParentSlipService` and `GamificationService`. The final test host, seed and counters were retained. The added repository tracking lookup is unused by the baseline handlers. Restoring the saved implementation then produces the after measurement.

Set `ORBIT_POSTGRES_TEST_CONNECTION` to a disposable PostgreSQL server with database-creation permission, then run:

```sh
dotnet build tests/Orbit.Infrastructure.Tests
DOTNET_GCHeapHardLimit=0x20000000 dotnet test tests/Orbit.Infrastructure.Tests --no-build --filter 'FullyQualifiedName~BulkHabitsPerformanceTests' --logger 'console;verbosity=detailed' --nologo
```

Without the connection variable, the SQLite regression cases still run and the PostgreSQL cases skip. `BulkHabitMutationReadTests` additionally covers stale pretracked state, flexible completions outside the overdue window, unselected siblings and ancestors. The unchanged `BadHabitParentSlipTests.BulkLog_ParentRepositoryFailure_DoesNotFailOtherItems` caught a repository-wrapper compatibility issue in the broad run; repositories without the tracked-state capability retain the original per-item parent-read path. The production repository exposes the capability and keeps the measured batch path.

## Client wait audit

Consumer source: `thomasluizon/orbit-ui-mobile` at `394f547cd7141d1a98bf12125eb9e58163d6b127`, read without edits or running clients.

Web `apps/web/hooks/use-bulk-actions.ts:executeCompletion` clears selection through `finish` only after `mutateAsync` resolves. `use-bulk-log-habits.ts:onSuccess` returns `profileRefresh.then(...)`, so an active profile refetch and celebration checks extend the selection wait after the API response. `use-bulk-skip-habits.ts` settles cached rows synchronously and starts invalidations without returning their promises. Both chunk sequentially at 100 items. `apps/web/app/actions/habits.ts` calls `serverAuthMutate`; `apps/web/lib/server-fetch.ts` awaits session/language resolution, possible authentication recovery, the upstream body and schema parsing. The bulk Server Actions do not request page revalidation or supply an idempotency key.

Android `apps/mobile/hooks/use-bulk-actions.ts:executeCompletion` also awaits `mutateAsync` before settling rows and clearing selection. `use-bulk-log-habits.ts` returns an awaited profile refetch from its success callback. `use-bulk-skip-habits.ts` settles synchronously; `finalizeHabitMutation` starts invalidations and widget synchronization without awaiting them. Before delivery, `queued-api-mutation.ts` and `offline-mutations.ts:queueOrExecute` await reference resolution and connectivity; an offline operation awaits local persistence. Online mutations set an idempotency key. Both bulk hooks process chunks sequentially.

The installed `@tanstack/query-core/src/mutation.ts` was inspected directly: it awaits `options.onSuccess` and `options.onSettled` before dispatching success and returning. Both selection-tray components have no loading/pending prop or fixed delay. Successful logging can therefore keep the tray visible beyond the bulk request while profile data is fetched. This is evidence for the paired UI ticket. Live web/Android observation is reserved for the orchestrator by the work order.

## Lock scope

Validation, initial date preferences and idempotency lookup precede user-lock acquisition. Reads needed to decide eligibility and derive parent/streak writes remain inside the atomic mutation. Fresh HTTP skip loads avoid a second refresh; previously tracked instances retain refresh/conflict protection. Cache invalidation remains after the handler's mutation callback; a keyed request's ambient transaction retains its database locks through cache-version and idempotency-response writes until commit. Releasing these locks before the decision reads or the transaction commit would change concurrent behavior.
