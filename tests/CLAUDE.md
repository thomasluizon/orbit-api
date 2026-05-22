# Tests

Four test projects:

```
Orbit.IntegrationTests/   - end-to-end via real DB + WebApplicationFactory
Orbit.Application.Tests/  - command/query handlers, validators
Orbit.Domain.Tests/       - entities, value objects, factory methods
Orbit.Infrastructure.Tests/ - services, repositories
```

## Conventions

- **xUnit + FluentAssertions** everywhere. `result.Should().BeSuccess()`, `value.Should().Be(...)`.
- **Sequential execution.** Integration tests share a real database; parallel runs corrupt state. xUnit's `[Collection]` is set so tests don't fight.
- **Never mock the DB layer.** Integration tests hit a real PostgreSQL. Unit tests on handlers use the real Application + Domain, mocking only Infrastructure ports.
- **Every new feature needs:**
  - Unit tests for commands, queries, validators (in `Orbit.Application.Tests`)
  - Unit tests for domain entity factory + mutator methods (in `Orbit.Domain.Tests`)
  - Integration test exercising the endpoint end-to-end (in `Orbit.IntegrationTests`)

## DB setup

- Connection string for tests comes from `appsettings.Test.json` or env vars.
- Test DB is created/migrated per-fixture, then dropped or truncated between tests (depending on the fixture).
- The `WebApplicationFactory<Program>` pattern wires up the real DI container with the test DB substituted in.

## Test accounts (bypass email verification)

The auth flow normally requires a code emailed via Resend. For tests + QA, two env-controlled accounts bypass this:

- `REVIEWER_TEST_EMAIL` + `REVIEWER_TEST_CODE` — used by E2E + reviewer flows.
- `QA_TEST_EMAIL` + `QA_TEST_CODE` — used by QA runs.

When `SendCodeCommand` matches one of these emails, the verification code is NOT randomized; it's the static value from env. Useful in CI; do not enable in production.

## Running

```bash
# all tests
dotnet test

# one project
dotnet test tests/Orbit.IntegrationTests

# one test
dotnet test --filter "FullyQualifiedName~CreateHabit_Should_Succeed"
```

## Patterns to mirror

| Want to add… | Look at… |
|---|---|
| Handler unit test | `Orbit.Application.Tests/Habits/CreateHabitCommandTests.cs` |
| Domain entity test | `Orbit.Domain.Tests/Entities/HabitTests.cs` |
| Integration test | `Orbit.IntegrationTests/Habits/HabitsControllerTests.cs` |
| Validator test | `Orbit.Application.Tests/Habits/Validators/*Tests.cs` |
