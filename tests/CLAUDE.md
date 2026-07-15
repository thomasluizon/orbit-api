# Tests

Three unit test projects (no integration suite — it was removed as outdated; don't add one back):

```
Orbit.Application.Tests/    - command/query handlers, validators
Orbit.Domain.Tests/         - entities, value objects, factory methods
Orbit.Infrastructure.Tests/ - services, prompt sections, controllers, MCP tools
```

## Conventions

- **xUnit + FluentAssertions** everywhere. `result.IsSuccess.Should().BeTrue()`, `value.Should().Be(...)`.
- **Unit tests mock at the ports.** Handlers use the real Application + Domain, mocking only Infrastructure interfaces (NSubstitute). Never spin up a real database.
- **Every new feature needs:**
  - Unit tests for commands, queries, validators (in `Orbit.Application.Tests`)
  - Unit tests for domain entity factory + mutator methods (in `Orbit.Domain.Tests`)
  - Unit tests for new services or prompt sections (in `Orbit.Infrastructure.Tests`)

## Test accounts (bypass email verification)

The auth flow normally requires a code emailed via Resend. Env-controlled test accounts bypass this:

- `TEST_ACCOUNTS` — comma-separated `email:code` pairs (reviewer flows, e.g. Play review, and local runs).

When `SendCodeCommand` matches one of these emails, the verification code is NOT randomized; it's the static code from the pair. Inert in production — the handler skips the bypass when `ASPNETCORE_ENVIRONMENT` is `Production`.

## Running

```bash
# all tests
dotnet test

# one project
dotnet test tests/Orbit.Application.Tests

# one test
dotnet test --filter "FullyQualifiedName~CreateHabit_Should_Succeed"
```

## Patterns to mirror

| Want to add… | Look at… |
|---|---|
| Handler unit test | `Orbit.Application.Tests/Commands/Habits/CreateHabitCommandHandlerTests.cs` |
| Domain entity test | `Orbit.Domain.Tests/Entities/HabitTests.cs` |
| Service test | `Orbit.Infrastructure.Tests/Services/UserDateServiceTests.cs` |
| Validator test | `Orbit.Application.Tests/Validators/*Tests.cs` |
| Query-builder shape test | `Orbit.Infrastructure.Tests/Persistence/HabitLogReaderTests.cs` (EF InMemory / `.AsQueryable()`) |
| N+1 / SQL round-trip-count test | `Orbit.Infrastructure.Tests/Persistence/QueryRoundTripCountTests.cs` |

## Query-shape / round-trip-count pattern

`QueryRoundTripCountTests` and the SQLite case in `SocialGraphReaderTests` catch N+1 regressions without a real Postgres: build the context via the shared `SqliteOrbitDbContextFactory` (SQLite in-memory — the only provider that emits real SQL — with the `::`-cast/filtered-index compat shim), pass a `CountingDbCommandInterceptor`, seed a small and a large row set, run the query, and assert the command count is **invariant to volume** (a count that grows with the seed is the N+1 signature). Use `SqliteOrbitDbContextFactory` for any new relational query-shape test; the ~6 inline copies of the compat shim are a known dedup follow-up.

## Benchmarks (not part of `dotnet test`)

`bench/Orbit.Benchmarks` (BenchmarkDotNet, `net10.0`) measures the pure hot paths and runs nightly via `benchmark.yml` against `bench/baseline.json` (report-only). It is intentionally excluded from the test/coverage/Stryker surface — never add it to a `dotnet test` step. Enumerate with `dotnet run -c Release --project bench/Orbit.Benchmarks -- --list flat`.
