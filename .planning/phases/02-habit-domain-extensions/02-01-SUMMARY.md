---
phase: 02-habit-domain-extensions
plan: 01
subsystem: database
tags: [ef-core, migrations, domain-entities, clean-architecture, postgresql]

# Dependency graph
requires:
  - phase: 01-infrastructure-foundation
    provides: EF Core migrations pipeline, baseline schema, GenericRepository
provides:
  - SubHabit entity with HabitId FK, Title, SortOrder, IsActive, factory Create method
  - SubHabitLog entity with SubHabitId FK, Date, IsCompleted
  - Tag entity with UserId, Name, Color (hex validation), factory Create method
  - HabitTag join entity with composite key (no Entity base class)
  - Habit.IsNegative flag with updated duplicate log check for negative habits
  - Habit.SubHabits and Habit.Tags navigation properties
  - Habit.AddSubHabit() and Habit.RemoveSubHabit() domain methods
  - HabitLog.Note optional property
  - User.TimeZone property with IANA validation via TimeZoneInfo
  - GenericRepository FindAsync overload with Func<IQueryable<T>, IQueryable<T>> includes
  - AddHabitDomainExtensions migration (4 tables, 3 columns, 4 indexes)
affects: [02-habit-domain-extensions plan 02, 02-habit-domain-extensions plan 03]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "HabitTag join entity without Entity base class for composite keys"
    - "FindAsync with includes parameter for eager loading navigation properties"
    - "IReadOnlyCollection backed by private List for encapsulated collections (SubHabits pattern)"

key-files:
  created:
    - src/Orbit.Domain/Entities/SubHabit.cs
    - src/Orbit.Domain/Entities/SubHabitLog.cs
    - src/Orbit.Domain/Entities/Tag.cs
    - src/Orbit.Domain/Entities/HabitTag.cs
    - src/Orbit.Infrastructure/Migrations/20260208022947_AddHabitDomainExtensions.cs
  modified:
    - src/Orbit.Domain/Entities/Habit.cs
    - src/Orbit.Domain/Entities/HabitLog.cs
    - src/Orbit.Domain/Entities/User.cs
    - src/Orbit.Domain/Interfaces/IGenericRepository.cs
    - src/Orbit.Infrastructure/Persistence/GenericRepository.cs
    - src/Orbit.Infrastructure/Persistence/OrbitDbContext.cs

key-decisions:
  - "SubHabit is a separate entity (not self-referencing Habit) for simplicity"
  - "HabitTag does not extend Entity base class -- uses composite key (HabitId, TagId)"
  - "Negative boolean habits allow multiple logs per day (slip-up tracking)"
  - "User.TimeZone validated via TimeZoneInfo.FindSystemTimeZoneById for cross-platform IANA support"

patterns-established:
  - "Join entities without Entity base: HabitTag pattern for many-to-many with composite keys"
  - "Repository includes: FindAsync overload accepting query transformation function"
  - "Domain method encapsulation: Habit.AddSubHabit/RemoveSubHabit manage child collection"

# Metrics
duration: 5min
completed: 2026-02-08
---

# Phase 2 Plan 1: Domain Model Extensions Summary

**SubHabit/SubHabitLog/Tag/HabitTag entities with IsNegative habit flag, log notes, user timezone, repository includes, and EF Core migration**

## Performance

- **Duration:** 5 min
- **Started:** 2026-02-08T02:26:12Z
- **Completed:** 2026-02-08T02:31:22Z
- **Tasks:** 2
- **Files modified:** 11

## Accomplishments
- Created 4 new domain entities (SubHabit, SubHabitLog, Tag, HabitTag) following established factory method and Result pattern conventions
- Extended Habit with IsNegative flag, SubHabits/Tags navigations, and AddSubHabit/RemoveSubHabit domain methods; updated duplicate log check to allow multiple logs per day for negative boolean habits
- Extended HabitLog with optional Note, User with IANA-validated TimeZone
- Configured all EF Core relationships with cascade deletes, indexes, and composite key on HabitTag
- Added GenericRepository FindAsync overload supporting Include-based eager loading
- Generated single migration covering all schema changes (4 new tables, 3 new columns, 4 indexes)

## Task Commits

Each task was committed atomically:

1. **Task 1: Create new domain entities and extend existing ones** - `a656a9b` (feat)
2. **Task 2: Configure DbContext, update repository, and generate migration** - `be99fc1` (feat)

## Files Created/Modified
- `src/Orbit.Domain/Entities/SubHabit.cs` - Sub-habit entity with HabitId FK, Title, SortOrder, IsActive, Deactivate()
- `src/Orbit.Domain/Entities/SubHabitLog.cs` - Sub-habit log entity with SubHabitId, Date, IsCompleted
- `src/Orbit.Domain/Entities/Tag.cs` - Tag entity with UserId, Name, Color (hex), hex validation
- `src/Orbit.Domain/Entities/HabitTag.cs` - Join entity with composite key, no Entity base class
- `src/Orbit.Domain/Entities/Habit.cs` - Added IsNegative, SubHabits, Tags, AddSubHabit(), RemoveSubHabit(), updated Log() and Create()
- `src/Orbit.Domain/Entities/HabitLog.cs` - Added Note property, updated Create() signature
- `src/Orbit.Domain/Entities/User.cs` - Added TimeZone, SetTimeZone(), ClearTimeZone()
- `src/Orbit.Domain/Interfaces/IGenericRepository.cs` - Added FindAsync overload with includes parameter
- `src/Orbit.Infrastructure/Persistence/GenericRepository.cs` - Implemented FindAsync with includes
- `src/Orbit.Infrastructure/Persistence/OrbitDbContext.cs` - Added 3 DbSets, full relationship config for SubHabit, SubHabitLog, Tag, HabitTag
- `src/Orbit.Infrastructure/Migrations/20260208022947_AddHabitDomainExtensions.cs` - Migration with 4 tables, 3 columns, 4 indexes

## Decisions Made
- SubHabit is a separate entity, not self-referencing Habit -- keeps Habit entity simple for the "checklist" requirement
- HabitTag does not extend Entity base -- composite key (HabitId, TagId) avoids unnecessary Id column
- Negative boolean habits allow multiple logs per day to support slip-up tracking
- User.TimeZone uses IANA IDs validated via TimeZoneInfo.FindSystemTimeZoneById for cross-platform compatibility

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

None.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness
- Domain model foundation complete -- Plans 02 and 03 can build commands/queries/controllers on top
- Migration is pending and will auto-apply on next app startup via MigrateAsync
- GenericRepository includes support ready for GetHabits query with SubHabits/Tags eager loading

## Self-Check: PASSED

All 12 files verified present. Both task commits (a656a9b, be99fc1) verified in git log.

---
*Phase: 02-habit-domain-extensions*
*Completed: 2026-02-08*
