# Tests

## Rules

- A relational query-shape test builds its context with `SqliteOrbitDbContextFactory`.
- A test that needs Postgres behavior uses `Orbit.Infrastructure.Tests/Persistence/PostgresTestDatabase.cs`.
- `Orbit.Infrastructure.Tests/Persistence/QueryRoundTripCountTests.cs` guards against N+1 queries. It builds the context with `SqliteOrbitDbContextFactory` and a `CountingDbCommandInterceptor`. It seeds a small and a large row set. It asserts that the command count does not change with the row count. Copy this pattern for a new query-shape test.

## Test accounts

These accounts skip the email code of Amazon SES.

- `TEST_ACCOUNTS` holds comma-separated `email:code` pairs for staging and local runs. It does nothing in production. Production startup fails when it is set.
- `REVIEW_ACCOUNT_EMAIL` and `REVIEW_ACCOUNT_CODE` name the one store-review account of production. Production seeds its six-digit code and sends no email. Other environments ignore the pair.

When `SendCodeCommand` matches one of these emails, it uses the static code of the pair.

## Run

```bash
dotnet test
dotnet test tests/Orbit.Application.Tests
dotnet test --filter "FullyQualifiedName~CreateHabit_Should_Succeed"
```
