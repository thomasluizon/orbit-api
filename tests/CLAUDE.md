# Tests

Three unit test projects (no integration suite — it was removed as outdated; don't add one back):

```
Orbit.Application.Tests/    - command/query handlers, validators
Orbit.Domain.Tests/         - entities, value objects, factory methods
Orbit.Infrastructure.Tests/ - services, prompt sections, controllers, MCP tools
```

## Conventions

- **xUnit + FluentAssertions** everywhere. `result.Should().BeSuccess()`, `value.Should().Be(...)`.
- **Unit tests mock at the ports.** Handlers use the real Application + Domain, mocking only Infrastructure interfaces (NSubstitute). Never spin up a real database.
- **Every new feature needs:**
  - Unit tests for commands, queries, validators (in `Orbit.Application.Tests`)
  - Unit tests for domain entity factory + mutator methods (in `Orbit.Domain.Tests`)
  - Unit tests for new services or prompt sections (in `Orbit.Infrastructure.Tests`)

## Test accounts (bypass email verification)

The auth flow normally requires a code emailed via Resend. Two env-controlled accounts bypass this:

- `REVIEWER_TEST_EMAIL` + `REVIEWER_TEST_CODE` — used by reviewer flows (e.g. Play review).
- `QA_TEST_EMAIL` + `QA_TEST_CODE` — used by QA runs.

When `SendCodeCommand` matches one of these emails, the verification code is NOT randomized; it's the static value from env. Do not enable in production.

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
| Handler unit test | `Orbit.Application.Tests/Habits/CreateHabitCommandTests.cs` |
| Domain entity test | `Orbit.Domain.Tests/Entities/HabitTests.cs` |
| Service test | `Orbit.Infrastructure.Tests/Services/UserDateServiceTests.cs` |
| Validator test | `Orbit.Application.Tests/Habits/Validators/*Tests.cs` |
