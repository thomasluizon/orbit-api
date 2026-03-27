---
globs: ["tests/**"]
description: Testing conventions -- xUnit, FluentAssertions, real DB, test accounts
---

# Testing Conventions

- Integration tests (xUnit + FluentAssertions) + unit tests (Domain, Application, Infrastructure test projects)
- Run: `dotnet test`
- **Every new feature must include unit tests** covering commands, queries, validators, and domain logic
- Test accounts: `REVIEWER_TEST_EMAIL`/`REVIEWER_TEST_CODE` and `QA_TEST_EMAIL`/`QA_TEST_CODE` env vars bypass email verification for testing
- Tests hit a real database -- never mock the DB layer
- Tests run sequentially (no parallel test execution)
