---
phase: 02-habit-domain-extensions
plan: 02
subsystem: api
tags: [cqrs, mediatr, fluent-validation, ef-core-includes, sub-habits, negative-habits, clean-architecture]

# Dependency graph
requires:
  - phase: 02-habit-domain-extensions plan 01
    provides: SubHabit/SubHabitLog/Tag entities, Habit.AddSubHabit/RemoveSubHabit methods, Habit.IsNegative flag, HabitLog.Note, FindAsync with includes
provides:
  - AddSubHabitCommand to add sub-habit to existing habit
  - RemoveSubHabitCommand to deactivate sub-habit
  - LogSubHabitCommand to log sub-habit completions for a date
  - CreateHabitCommand accepts IsNegative flag and initial SubHabits list
  - LogHabitCommand accepts optional Note parameter
  - GetHabitsQuery includes SubHabits (active, sorted) and Tags
  - HabitsController with 3 new sub-habit endpoints
  - AiAction.IsNegative and AiAction.Note properties
  - SystemPromptBuilder with negative habit and note awareness
  - ProcessUserChatCommand passes IsNegative and Note through to domain
  - FindOneTrackedAsync repository method for tracked entity queries with includes
  - Habit.LogSubHabitCompletions domain method
affects: [02-habit-domain-extensions plan 03, 03-metrics-ai-enhancement]

# Tech tracking
tech-stack:
  added:
    - "Microsoft.EntityFrameworkCore 10.0.2 in Application project (for Include extension methods)"
  patterns:
    - "FindOneTrackedAsync for tracked entity queries needing navigation property includes"
    - "Domain-level LogSubHabitCompletions to encapsulate SubHabitLog creation (internal factory)"
    - "SubHabitCompletion record as command parameter for structured sub-habit log input"

key-files:
  created:
    - src/Orbit.Application/Habits/Commands/AddSubHabitCommand.cs
    - src/Orbit.Application/Habits/Commands/RemoveSubHabitCommand.cs
    - src/Orbit.Application/Habits/Commands/LogSubHabitCommand.cs
    - src/Orbit.Application/Habits/Validators/AddSubHabitCommandValidator.cs
    - src/Orbit.Application/Habits/Validators/LogSubHabitCommandValidator.cs
  modified:
    - src/Orbit.Application/Habits/Commands/CreateHabitCommand.cs
    - src/Orbit.Application/Habits/Commands/LogHabitCommand.cs
    - src/Orbit.Application/Habits/Queries/GetHabitsQuery.cs
    - src/Orbit.Application/Habits/Validators/CreateHabitCommandValidator.cs
    - src/Orbit.Application/Habits/Validators/LogHabitCommandValidator.cs
    - src/Orbit.Api/Controllers/HabitsController.cs
    - src/Orbit.Infrastructure/Services/SystemPromptBuilder.cs
    - src/Orbit.Application/Chat/Commands/ProcessUserChatCommand.cs
    - src/Orbit.Domain/Models/AiAction.cs
    - src/Orbit.Domain/Entities/Habit.cs
    - src/Orbit.Domain/Interfaces/IGenericRepository.cs
    - src/Orbit.Infrastructure/Persistence/GenericRepository.cs
    - src/Orbit.Application/Orbit.Application.csproj

key-decisions:
  - "Added Microsoft.EntityFrameworkCore to Application project for Include support in queries -- pragmatic clean architecture tradeoff"
  - "Used FindOneTrackedAsync instead of OrbitDbContext injection to keep Application layer independent of Infrastructure"
  - "Added Habit.LogSubHabitCompletions domain method because SubHabitLog.Create is internal to Domain"

patterns-established:
  - "FindOneTrackedAsync: repository method for tracked entity queries needing Include"
  - "Domain encapsulation of internal factories: aggregate root methods create child entities"
  - "SubHabitCompletion record: structured input for batch sub-habit operations"

# Metrics
duration: 7min
completed: 2026-02-08
---

# Phase 2 Plan 2: Application Logic for Sub-Habits, Negative Habits, and Log Notes Summary

**Sub-habit CRUD commands, negative habit support, optional log notes, GetHabits with includes, and AI prompt updates for IsNegative + Note fields**

## Performance

- **Duration:** 7 min
- **Started:** 2026-02-08T02:35:38Z
- **Completed:** 2026-02-08T02:43:02Z
- **Tasks:** 2
- **Files modified:** 18

## Accomplishments
- Created AddSubHabitCommand, RemoveSubHabitCommand, LogSubHabitCommand with full handlers and validators for sub-habit lifecycle management
- Extended CreateHabitCommand with IsNegative flag and initial SubHabits list; extended LogHabitCommand with optional Note
- Updated GetHabitsQuery to include SubHabits (active, sorted by SortOrder) and Tags via repository FindAsync with includes
- Added 3 new HabitsController endpoints: POST sub-habits, DELETE sub-habits/{subHabitId}, POST sub-habits/log
- Updated AiAction model with IsNegative and Note properties; updated SystemPromptBuilder with negative habit rules and note examples
- Updated ProcessUserChatCommand to pass IsNegative to Habit.Create and Note to habit.Log

## Task Commits

Each task was committed atomically:

1. **Task 1: Sub-habit commands, negative habit + notes updates, and validators** - `3c83f1f` (feat)
2. **Task 2: HabitsController endpoints, AI prompt update, and chat handler** - `c71087a` (feat)

## Files Created/Modified
- `src/Orbit.Application/Habits/Commands/AddSubHabitCommand.cs` - Command + handler to add sub-habit to existing habit via tracked query
- `src/Orbit.Application/Habits/Commands/RemoveSubHabitCommand.cs` - Command + handler to deactivate a sub-habit
- `src/Orbit.Application/Habits/Commands/LogSubHabitCommand.cs` - Command + handler to log sub-habit completions with SubHabitCompletion record
- `src/Orbit.Application/Habits/Validators/AddSubHabitCommandValidator.cs` - Validates UserId, HabitId, Title (max 200), SortOrder >= 0
- `src/Orbit.Application/Habits/Validators/LogSubHabitCommandValidator.cs` - Validates UserId, HabitId, Completions non-empty, each SubHabitId non-empty
- `src/Orbit.Application/Habits/Commands/CreateHabitCommand.cs` - Added IsNegative and SubHabits parameters, handler iterates to add sub-habits
- `src/Orbit.Application/Habits/Commands/LogHabitCommand.cs` - Added Note parameter, passed to habit.Log()
- `src/Orbit.Application/Habits/Queries/GetHabitsQuery.cs` - Uses FindAsync with Include(SubHabits.Where(active).OrderBy(sort)).Include(Tags)
- `src/Orbit.Application/Habits/Validators/CreateHabitCommandValidator.cs` - Added SubHabits validation (max 20, each non-empty, max 200 chars)
- `src/Orbit.Application/Habits/Validators/LogHabitCommandValidator.cs` - Added Note max 500 chars validation
- `src/Orbit.Api/Controllers/HabitsController.cs` - Updated CreateHabitRequest/LogHabitRequest, added 3 sub-habit endpoints
- `src/Orbit.Infrastructure/Services/SystemPromptBuilder.cs` - Negative habit rules, examples, note field in action type descriptions
- `src/Orbit.Application/Chat/Commands/ProcessUserChatCommand.cs` - Passes IsNegative to Habit.Create, Note to habit.Log
- `src/Orbit.Domain/Models/AiAction.cs` - Added IsNegative and Note nullable properties
- `src/Orbit.Domain/Entities/Habit.cs` - Added LogSubHabitCompletions domain method
- `src/Orbit.Domain/Interfaces/IGenericRepository.cs` - Added FindOneTrackedAsync method
- `src/Orbit.Infrastructure/Persistence/GenericRepository.cs` - Implemented FindOneTrackedAsync (tracked, with includes)
- `src/Orbit.Application/Orbit.Application.csproj` - Added Microsoft.EntityFrameworkCore 10.0.2 package reference

## Decisions Made
- Added Microsoft.EntityFrameworkCore to Application project csproj for Include extension methods -- necessary for GetHabitsQuery and sub-habit commands to use eager loading. This is a pragmatic clean architecture tradeoff; the Application layer only uses EF Core query abstractions (IQueryable), not the full DbContext.
- Used FindOneTrackedAsync on the repository instead of injecting OrbitDbContext directly into Application-layer handlers. This preserves the dependency inversion principle where Application does not reference Infrastructure.
- Added Habit.LogSubHabitCompletions domain method because SubHabitLog.Create is `internal` to the Domain assembly and cannot be called from Application. The aggregate root properly encapsulates child entity creation.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Application project cannot reference OrbitDbContext from Infrastructure**
- **Found during:** Task 1 (AddSubHabitCommand, RemoveSubHabitCommand, LogSubHabitCommand)
- **Issue:** Plan specified injecting `OrbitDbContext` into Application-layer handlers. Application project only references Domain, not Infrastructure (clean architecture boundary).
- **Fix:** Added `FindOneTrackedAsync` method to `IGenericRepository<T>` interface and `GenericRepository<T>` implementation. This provides tracked entity queries with includes while keeping Application independent of Infrastructure.
- **Files modified:** `src/Orbit.Domain/Interfaces/IGenericRepository.cs`, `src/Orbit.Infrastructure/Persistence/GenericRepository.cs`
- **Verification:** Solution builds cleanly; handlers use repository abstraction instead of DbContext.
- **Committed in:** 3c83f1f (Task 1 commit)

**2. [Rule 3 - Blocking] SubHabitLog.Create is internal to Domain assembly**
- **Found during:** Task 1 (LogSubHabitCommand)
- **Issue:** Plan specified creating SubHabitLog directly in the handler. SubHabitLog.Create has `internal` access modifier, inaccessible from Application.
- **Fix:** Added `Habit.LogSubHabitCompletions(DateOnly date, IReadOnlyList<(Guid, bool)> completions)` domain method on the aggregate root. This encapsulates SubHabitLog creation within the Domain boundary, validates sub-habit ownership, and returns the created logs.
- **Files modified:** `src/Orbit.Domain/Entities/Habit.cs`
- **Verification:** Solution builds cleanly; LogSubHabitCommand uses domain method correctly.
- **Committed in:** 3c83f1f (Task 1 commit)

**3. [Rule 3 - Blocking] Microsoft.EntityFrameworkCore not referenced in Application project**
- **Found during:** Task 1 (GetHabitsQuery, AddSubHabitCommand)
- **Issue:** Using `Include()` extension method requires Microsoft.EntityFrameworkCore namespace, but Application project only had FluentValidation and MediatR.
- **Fix:** Added `<PackageReference Include="Microsoft.EntityFrameworkCore" Version="10.0.2" />` to Orbit.Application.csproj.
- **Files modified:** `src/Orbit.Application/Orbit.Application.csproj`
- **Verification:** Solution builds cleanly with zero errors.
- **Committed in:** 3c83f1f (Task 1 commit)

---

**Total deviations:** 3 auto-fixed (3 blocking issues)
**Impact on plan:** All fixes necessary to maintain clean architecture boundaries while delivering planned functionality. No scope creep. The FindOneTrackedAsync and LogSubHabitCompletions patterns improve the codebase design over the plan's DbContext-injection approach.

## Issues Encountered

None beyond the blocking issues handled as deviations above.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness
- All HABIT-01 through HABIT-05 requirements have working API endpoints
- Sub-habit CRUD and logging fully operational via HabitsController
- GetHabits returns habits with sub-habits and tags included
- AI prompt updated for negative habits and notes -- ready for Plan 03 (tag management + migration)
- Pre-existing MSB3277 warning in IntegrationTests (EF Core Relational version conflict) remains cosmetic

## Self-Check: PASSED

All 18 files verified present. Both task commits (3c83f1f, c71087a) verified in git log.

---
*Phase: 02-habit-domain-extensions*
*Completed: 2026-02-08*
