---
globs: ["src/**"]
description: CQRS, Result<T>, factory methods, timezone, validation, and DRY patterns
---

# Coding Conventions

## CQRS & Patterns

- Commands (write) and Queries (read) in separate folders per feature
- Result<T> pattern for error handling across all handlers
- FluentValidation validators in Validators/ folder per feature
- Domain entities use factory methods (e.g., `Habit.Create()`, `User.Create()`)
- Generic repository + Unit of Work pattern
- Soft deletes for UserFacts
- All endpoints except /health and auth require JWT Bearer token
- Schedule calculations (frequency, days, intervals) live in `HabitScheduleService` -- never on the frontend

## Timezone Rule

All user-facing dates MUST use `IUserDateService.GetUserTodayAsync(userId)` to get the user's timezone-aware "today". NEVER use `DateOnly.FromDateTime(DateTime.UtcNow)` for user-facing logic. `DateTime.UtcNow` is only acceptable for: `CreatedAtUtc` timestamps in entity factories, and cache key generation. The user sets their timezone in their profile (`User.TimeZone`). If no timezone is set, it falls back to UTC.

## Validation

- **Every new feature must include validation for all invalid/edge-case scenarios**, both in the domain entity (factory/update methods) and FluentValidation validators.
- Never rely on the frontend alone for validation -- the backend is the source of truth and safety net.
- Examples: date ranges (end must be after start), time ranges (end time must be after start time), numeric bounds, mutually exclusive options, required fields.

## DRY Patterns

- **Error messages:** Use `ErrorMessages.UserNotFound`, `ErrorMessages.HabitNotFound`, etc. from `Common/ErrorMessages.cs`. Never hardcode "not found" strings.
- **Magic numbers:** Use `AppConstants.MaxSubHabits`, `AppConstants.MaxUserFacts`, etc. from `Common/AppConstants.cs`. Never inline limits.
- **Cache invalidation:** Use `CacheInvalidationHelper.InvalidateSummaryCache(cache, userId)` after any habit mutation. Never manually write the 6-line cache removal loop.
- **PayGate propagation:** Use `result.PropagateError<T>()` from `Common/ResultExtensions.cs` to propagate PayGate failures. Never manually check `ErrorCode == "PAY_GATE"` with ternary.
- **PayGate controller responses:** Use `result.ToPayGateAwareResult(v => Ok(v))` from `Extensions/ResultActionResultExtensions.cs` in controllers. Never manually write the 403/PAY_GATE response block.
- **Shared validation:** `SharedHabitRules` in `Habits/Validators/` has `AddTitleRules()` and `AddDaysRules()` shared between Create and Update validators.
