# Orbit.Application

MediatR commands and queries, validators and application services.

## Result pattern

Each handler returns `Result<T>` from `Orbit.Domain/Common/`. Do not throw for an expected failure.

- Return `Result<T>.Success(value)` for success.
- Return `Result.Failure<T>(ErrorMessages.<Name>)` from `Common/ErrorMessages.cs` for a known failure.
- Use `result.PropagateError<T>()` from `Common/ResultExtensions.cs` to pass a failed result to a new type. Do not write an `ErrorCode == "PAY_GATE"` check.

## Validation

- Put each FluentValidation validator in the `Validators/` folder of its feature. The MediatR pipeline runs it.
- Put a rule that Create and Update share in a shared rule class, for example `Habits/Validators/SharedHabitRules.cs`.
- Validate each edge case: date and time ranges, numeric bounds, required fields, and fields that exclude each other. A frontend check does not count.

## Schedule

`HabitScheduleService` owns all frequency, day and interval logic. Never duplicate that logic on the frontend.
