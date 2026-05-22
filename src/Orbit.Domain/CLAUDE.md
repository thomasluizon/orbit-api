# Orbit.Domain — entities + value objects + interfaces

Pure domain model. No EF Core attributes, no `Microsoft.Extensions.*` references, no application/infrastructure dependencies.

## Layout

```
Entities/         - aggregate roots (Habit, User, Tag, Goal, ...) + child entities (HabitLog, etc.)
Enums/            - domain enums (FrequencyUnit, UserPlan, ...)
Interfaces/       - abstractions Infrastructure implements (IUserDateService, IPushNotificationService, ITokenService, ...)
Models/           - domain DTOs / value-bearing types (HabitMetrics, SlipPattern, ExtractedFacts)
ValueObjects/     - immutable value types (ChecklistItem, ...)
Common/           - cross-entity domain primitives
```

## Entity rules

- **Factory methods only for construction.** `Habit.Create(...)`, `User.Create(...)`, `Tag.Create(...)`. Constructors are private/protected. Factories enforce invariants and return `Result<T>` (or throw a domain exception for impossible states).
- **All mutations through methods.** No public setters on aggregates. Use `habit.SetTitle(...)`, `user.SetTimezone(...)`, etc. Methods enforce invariants.
- **`CreatedAtUtc` set inside the factory.** Use `DateTime.UtcNow` — this is one of the two places it's acceptable (the other is cache key generation in Application).
- **Soft deletes for entities that need history.** `UserFact` uses soft delete via `DeletedAtUtc`.

## Domain events

(Patterns vary — check what's already there before adding new event infrastructure.) When an aggregate root state change matters across feature boundaries, prefer raising a domain event from inside the entity method to coupling features through Application services.

## Value objects

Use for concepts that are immutable, comparable by value, and have invariants — `ChecklistItem`, `TimeRange`-style types. Equality compares all fields; never expose internal state via setters.

## Timezone-aware logic

The `IUserDateService` interface lives here; the implementation in Infrastructure resolves the user's timezone from `User.TimeZone`. NEVER add a static `DateTime.UtcNow` call to a domain method that decides "is this user's today" — pass the resolved date in.

## Patterns to mirror

| Want to add… | Look at… |
|---|---|
| New entity | `Entities/Habit.cs` (factory + mutators) |
| New value object | `ValueObjects/ChecklistItem.cs` |
| New domain interface | `Interfaces/IUserDateService.cs` |
| New enum | `Enums/FrequencyUnit.cs` |
