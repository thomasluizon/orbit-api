---
phase: 01-infrastructure-foundation
plan: 03
subsystem: domain, api, ai
tags: [ef-core, migrations, ai-prompt, cqrs, clean-architecture]

# Dependency graph
requires:
  - phase: 01-01
    provides: EF Core migrations infrastructure (baseline migration)
provides:
  - Habits-only domain model (no task entities)
  - Habits-only AI action types (LogHabit, CreateHabit)
  - Habits-only AI system prompt
  - RemoveTaskItems migration (drops Tasks table)
  - Updated integration tests (12 scenarios, habits-only)
affects: [02-habit-enhancements, 03-ai-insights]

# Tech tracking
tech-stack:
  added: []
  patterns: [habits-only-domain, task-rejection-in-ai-prompt]

key-files:
  created:
    - src/Orbit.Infrastructure/Migrations/20260208015116_RemoveTaskItems.cs
  modified:
    - src/Orbit.Domain/Enums/AiActionType.cs
    - src/Orbit.Domain/Models/AiAction.cs
    - src/Orbit.Domain/Interfaces/IAiIntentService.cs
    - src/Orbit.Application/Chat/Commands/ProcessUserChatCommand.cs
    - src/Orbit.Infrastructure/Persistence/OrbitDbContext.cs
    - src/Orbit.Infrastructure/Services/SystemPromptBuilder.cs
    - src/Orbit.Infrastructure/Services/GeminiIntentService.cs
    - src/Orbit.Infrastructure/Services/AiIntentService.cs
    - tests/Orbit.IntegrationTests/AiChatIntegrationTests.cs

key-decisions:
  - "Habits-only domain: removed all task management code (entity, enum, commands, queries, controller)"
  - "AI prompt explicitly rejects task-like requests with habit redirect suggestions"
  - "AiAction record no longer has DueDate, TaskId, or NewStatus properties"

patterns-established:
  - "Habits-only AI: system prompt instructs AI to return empty actions for task-like requests"
  - "Layer-by-layer deletion: Domain -> Application -> Infrastructure -> API for clean removal"

# Metrics
duration: 8min
completed: 2026-02-08
---

# Phase 1 Plan 3: Remove Task Management Summary

**Removed all task management code across 4 layers (15 files), AI prompt rewritten for habits-only, RemoveTaskItems migration drops Tasks table**

## Performance

- **Duration:** 8 min
- **Started:** 2026-02-08T01:44:27Z
- **Completed:** 2026-02-08T01:52:30Z
- **Tasks:** 2
- **Files modified:** 15 (7 deleted, 1 created, 7 modified)

## Accomplishments
- Deleted TaskItem entity, TaskItemStatus enum, TasksController, and 4 Application/Tasks files
- Stripped AiActionType to only LogHabit and CreateHabit; removed task properties from AiAction
- Rewrote AI system prompt to be habits-only with explicit task-like request rejection guidance
- Generated and applied RemoveTaskItems migration (DropTable Tasks)
- Updated integration tests from 15 to 12 scenarios, added task-redirect rejection test

## Task Commits

Each task was committed atomically:

1. **Task 1: Remove task code from Domain + Application + Infrastructure + API layers** - `1b3ae79` (feat)
2. **Task 2: Update integration tests + generate RemoveTaskItems migration** - `da153dd` (feat)

## Files Created/Modified

**Deleted:**
- `src/Orbit.Domain/Entities/TaskItem.cs` - Task entity (removed)
- `src/Orbit.Domain/Enums/TaskItemStatus.cs` - Task status enum (removed)
- `src/Orbit.Application/Tasks/Commands/CreateTaskCommand.cs` - Create task CQRS command (removed)
- `src/Orbit.Application/Tasks/Commands/UpdateTaskCommand.cs` - Update task CQRS command (removed)
- `src/Orbit.Application/Tasks/Commands/DeleteTaskCommand.cs` - Delete task CQRS command (removed)
- `src/Orbit.Application/Tasks/Queries/GetTasksQuery.cs` - Get tasks CQRS query (removed)
- `src/Orbit.Api/Controllers/TasksController.cs` - Tasks REST controller (removed)

**Created:**
- `src/Orbit.Infrastructure/Migrations/20260208015116_RemoveTaskItems.cs` - Migration to drop Tasks table

**Modified:**
- `src/Orbit.Domain/Enums/AiActionType.cs` - Only LogHabit and CreateHabit remain
- `src/Orbit.Domain/Models/AiAction.cs` - Removed DueDate, TaskId, NewStatus properties
- `src/Orbit.Domain/Interfaces/IAiIntentService.cs` - Removed pendingTasks parameter
- `src/Orbit.Application/Chat/Commands/ProcessUserChatCommand.cs` - Removed task repository, task queries, task execution methods
- `src/Orbit.Infrastructure/Persistence/OrbitDbContext.cs` - Removed Tasks DbSet and model config
- `src/Orbit.Infrastructure/Services/SystemPromptBuilder.cs` - Habits-only prompt with task rejection
- `src/Orbit.Infrastructure/Services/GeminiIntentService.cs` - Removed pendingTasks parameter
- `src/Orbit.Infrastructure/Services/AiIntentService.cs` - Removed pendingTasks parameter
- `tests/Orbit.IntegrationTests/AiChatIntegrationTests.cs` - 12 habits-only tests

## Decisions Made
- Removed DueDate from AiAction entirely (was only used for task creation and LogHabit date override, which used DateOnly.FromDateTime(DateTime.UtcNow) as fallback)
- AI prompt explicitly tells AI to reject one-time task requests with a helpful redirect to habits
- Kept "buy milk" example in AI prompt as an out-of-scope example showing empty actions response
- Updated out-of-scope test regex to remove "task" from expected patterns since the AI now talks about habits only

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered
- File deletions via `rm` command were not persisting (files reappearing on disk). Resolved by using `git rm` instead, which properly stages the deletion and removes the file from the working tree.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness
- Codebase is now habits-only with zero task references in application code
- Database has Tasks table dropped via applied migration
- AI prompt instructs habits-only behavior
- Ready for Phase 1 Plan 2 (validation) or Phase 2 (habit enhancements)

## Self-Check: PASSED

All created/modified files verified on disk. All deleted files confirmed absent. Both commit hashes (1b3ae79, da153dd) found in git log. Build succeeds with 0 errors.

---
*Phase: 01-infrastructure-foundation*
*Completed: 2026-02-08*
