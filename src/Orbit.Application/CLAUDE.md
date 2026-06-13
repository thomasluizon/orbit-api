# Orbit.Application — CQRS + business orchestration

MediatR commands/queries, validators, application services. The business glue between controllers and the domain.

## Feature folder layout

One folder per resource (`Habits/`, `Notifications/`, `Auth/`, `Profile/`, `Tags/`, `Chat/`, `UserFacts/`, `Goals/`, `Gamification/`, `Calendar/`, `Subscriptions/`, `Support/`, `ApiKeys/`, `ChecklistTemplates/`, `Referrals/`):

```
Habits/
  Commands/           - one file per write op (CreateHabitCommand.cs, LogHabitCommand.cs, ...); the MediatR handler lives in the same file
  Queries/            - one file per read op
  Validators/         - FluentValidation rules per command/query
  Services/           - feature-internal services (e.g., HabitScheduleService)
```

## Result<T> pattern

Every handler returns `Result<T>` from `Orbit.Domain/Common/`. Never throw for expected failures.

- `Result<T>.Success(value)` for OK
- `Result<T>.Failure(errorCode, message)` for known failures (404, 400, 403)
- `result.ToPayGateAwareResult(v => Ok(v))` (in controller) maps PAY_GATE errors to 403
- `result.PropagateError<T>()` (from `Common/ResultExtensions.cs`) re-wraps a failed Result into a new T — use this instead of manual `ErrorCode == "PAY_GATE"` ternaries

## Validation

- FluentValidation validators in `Validators/` per feature.
- Validators run automatically via the MediatR validation pipeline.
- Common rules live in shared rule classes: `Habits/Validators/SharedHabitRules.cs` has `AddTitleRules()` and `AddDaysRules()` shared between Create and Update validators. Mirror that pattern for any feature with overlapping create/update rules.
- Validate ALL invalid/edge cases — date ranges (end after start), time ranges, numeric bounds, mutually-exclusive fields, required fields. Frontend validation does NOT count.

## DRY building blocks (use these — don't reinvent)

| Need | Use |
|---|---|
| "Not found" error | `ErrorMessages.UserNotFound`, `ErrorMessages.HabitNotFound`, etc. from `Common/ErrorMessages.cs` |
| Numeric limit | `AppConstants.MaxSubHabits`, `AppConstants.MaxUserFacts`, etc. from `Common/AppConstants.cs` |
| Cache invalidation after habit mutation | `CacheInvalidationHelper.InvalidateSummaryCache(cache, userId)` |
| PayGate failure propagation | `result.PropagateError<T>()` |
| Shared habit Create/Update rules | `SharedHabitRules.AddTitleRules()` / `AddDaysRules()` |
| Paginated response | `PaginatedResponse<T>` from `Common/` |

## Schedule calculations

ALL frequency/days/interval logic lives in `Habits/Services/HabitScheduleService`. NEVER duplicate this on the frontend. The service determines if a habit is due on a given date based on:
- One-time tasks (`FrequencyUnit = null`): due only on `DueDate`
- Daily (qty=1): every day, filtered by active `Days`
- Every N days (qty>1): modular arithmetic from anchor date
- Weekly/Monthly/Yearly: weekday/day-of-month/date with interval check
- Overdue (`includeOverdue=true`): `DueDate < dateFrom && !IsCompleted`

## PayGate

`PayGateService` in `Common/` checks subscription limits (max habits, max sub-habits, max user facts, etc.). Call it in handlers BEFORE the mutation. If it fails, the handler returns a `Result<T>.Failure(...)` with error code `"PAY_GATE"`.

## Caching

`IMemoryCache` for AI-generated content (daily summaries, retrospectives). Use `CacheInvalidationHelper` for invalidation — never the 6-line manual cache removal loop.

## Patterns to mirror

| Want to add… | Look at… |
|---|---|
| New write command + validator + handler | `Habits/Commands/CreateHabitCommand.cs` + `Habits/Validators/CreateHabitCommandValidator.cs` |
| New read query | `Habits/Queries/GetHabitsQuery.cs` |
| Shared validator rules | `Habits/Validators/SharedHabitRules.cs` |
| Feature service (non-handler logic) | `Habits/Services/HabitScheduleService.cs` |
| MediatR pipeline behavior | `Behaviors/ValidationBehavior.cs` |
