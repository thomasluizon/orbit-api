---
phase: 04-multi-action-foundation
plan: 01
subsystem: AI Chat Pipeline
tags: [ai, chat, multi-action, error-handling, suggestions]
dependency_graph:
  requires: [phase-03 AI integration, ProcessUserChatCommand handler]
  provides: [multi-action chat responses, per-action error handling, SuggestBreakdown action type]
  affects: [chat endpoint response shape, AI prompt templates, integration tests]
tech_stack:
  added: [ActionResult response model, ActionStatus enum]
  patterns: [per-action try-catch, partial failure handling, suggestion-only actions]
key_files:
  created: []
  modified:
    - src/Orbit.Domain/Enums/AiActionType.cs
    - src/Orbit.Domain/Models/AiAction.cs
    - src/Orbit.Application/Chat/Commands/ProcessUserChatCommand.cs
    - src/Orbit.Infrastructure/Services/SystemPromptBuilder.cs
    - tests/Orbit.IntegrationTests/AiChatIntegrationTests.cs
key_decisions:
  - decision: "Use ActionResult with ActionStatus enum for per-action responses"
    rationale: "Enables frontend to show detailed success/failure status for each action in batch"
    alternatives: "Single error message for entire batch (rejected - poor UX for partial failures)"
  - decision: "SuggestBreakdown returns Suggestion status without creating entities"
    rationale: "User must confirm before creation, enabling AI to propose structured breakdowns"
    alternatives: "Auto-create with confirmation flag (rejected - less explicit)"
  - decision: "Execute methods return (Guid? Id, string? Name) tuple"
    rationale: "Provides both entity ID and display name for ActionResult population"
    alternatives: "Return only ID (rejected - frontend would need additional queries)"
metrics:
  duration: 8min
  completed: 2026-02-09T16:43:53Z
---

# Phase 4 Plan 01: Multi-Action Chat Pipeline Summary

**One-liner:** AI chat now processes multiple actions per prompt with per-action success/failure tracking and SuggestBreakdown for habit decomposition proposals.

## Performance

- **Duration:** 8 minutes
- **Started:** 2026-02-09T16:35:47Z
- **Completed:** 2026-02-09T16:43:53Z
- **Tasks completed:** 3 of 3
- **Files modified:** 5
- **Commits:** 3

## Accomplishments

Refactored the AI chat pipeline to support multi-action requests with structured per-action results:

1. **Domain Models Extended**
   - Added `SuggestBreakdown` to `AiActionType` enum
   - Added `SuggestedSubHabits` field to `AiAction` for breakdown proposals
   - New `ActionResult` record with type, status, entityId, entityName, error fields
   - New `ActionStatus` enum (Success, Failed, Suggestion)

2. **ChatResponse Restructured**
   - Changed from `(ExecutedActions: string[], AiMessage)` to `(AiMessage, Actions: ActionResult[])`
   - Breaking change to response shape (intentional per requirements)
   - Enables frontend to show detailed per-action status

3. **Handler Refactored**
   - Wrapped each action execution in try-catch for partial failure handling
   - Updated Execute methods to return `Result<(Guid? Id, string? Name)>` for entity tracking
   - Added `ExecuteSuggestBreakdown` method for suggestion-only actions
   - Successful actions still commit even if later actions fail

4. **AI Prompt Enhanced**
   - Strengthened multi-action guidance (rules 7-9)
   - Added 4 multi-action examples: multiple creates, multiple logs, mixed actions, SuggestBreakdown
   - Documented when to use SuggestBreakdown vs CreateHabit with subHabits
   - Added SuggestBreakdown to action types documentation

5. **Integration Tests Updated**
   - Updated all 12 existing tests for new ChatResponse shape
   - Added `Chat_MultipleCreates_ShouldSucceedForAll` test
   - Added `Chat_PartialFailure_ShouldReturnMixedStatuses` test
   - All test assertions now check `Actions` array with `Type`/`Status` fields

## Task Commits

| Task | Commit  | Description                                              |
| ---- | ------- | -------------------------------------------------------- |
| 1    | 53c5cdb | Add multi-action support and SuggestBreakdown to chat    |
| 2    | a74eeb3 | Update AI prompt for multi-action and SuggestBreakdown   |
| 3    | d4ec462 | Update AI chat tests for multi-action ChatResponse       |

## Files Created

None (refactored existing files only).

## Files Modified

1. **src/Orbit.Domain/Enums/AiActionType.cs**
   - Added `SuggestBreakdown` enum value

2. **src/Orbit.Domain/Models/AiAction.cs**
   - Added `List<AiAction>? SuggestedSubHabits { get; init; }` field

3. **src/Orbit.Application/Chat/Commands/ProcessUserChatCommand.cs**
   - Replaced `ChatResponse(ExecutedActions, AiMessage)` with `ChatResponse(AiMessage, Actions)`
   - Added `ActionResult` record with type, status, entityId, entityName, error, field, suggestedSubHabits
   - Added `ActionStatus` enum
   - Refactored handler with per-action try-catch and ActionResult aggregation
   - Updated Execute methods to return `Result<(Guid? Id, string? Name)>`
   - Added `ExecuteSuggestBreakdown` method

4. **src/Orbit.Infrastructure/Services/SystemPromptBuilder.cs**
   - Strengthened multi-action guidance in Core Rules (rules 7-9)
   - Added SuggestBreakdown to "What You CAN Do" section
   - Added SuggestBreakdown documentation to Action Types section
   - Added 4 multi-action examples (116 lines added)

5. **tests/Orbit.IntegrationTests/AiChatIntegrationTests.cs**
   - Updated `ChatResponse` DTO to `(AiMessage, Actions[])`
   - Added `ActionResultDto` record
   - Updated all 12 existing test assertions for new response shape
   - Added 2 new multi-action tests

## Decisions Made

1. **Per-action error handling with structured responses** — Each action gets its own ActionResult with success/failure status, enabling frontend to show detailed feedback for batch operations. Alternative of single error message rejected as poor UX for partial failures.

2. **SuggestBreakdown as suggestion-only action** — Returns ActionStatus.Suggestion without creating entities, requiring explicit user confirmation. Alternative of auto-create with confirmation flag rejected as less explicit.

3. **Execute methods return (Id, Name) tuple** — Provides both entity ID and display name for populating ActionResult, avoiding additional frontend queries. Alternative of ID-only rejected as incomplete.

## Deviations from Plan

None — plan executed exactly as written.

## Issues Encountered

None. Build warnings from locked API process (running app) are cosmetic and do not affect functionality.

## Next Phase Readiness

**Ready for Plan 02 (Bulk Habit Operations)** — Multi-action foundation complete. ChatResponse now supports structured action results. Integration tests verify multi-action behavior.

**Blockers:** None.

**Recommendations:** Plan 02 can proceed with bulk create/log endpoints that return the same ActionResult structure.

## Self-Check

Verifying key file modifications:

```bash
# Check AiActionType has SuggestBreakdown
grep -q "SuggestBreakdown" src/Orbit.Domain/Enums/AiActionType.cs && echo "✓ AiActionType.SuggestBreakdown exists"

# Check AiAction has SuggestedSubHabits
grep -q "SuggestedSubHabits" src/Orbit.Domain/Models/AiAction.cs && echo "✓ AiAction.SuggestedSubHabits exists"

# Check ChatResponse has ActionResult
grep -q "ActionResult" src/Orbit.Application/Chat/Commands/ProcessUserChatCommand.cs && echo "✓ ChatResponse.ActionResult exists"

# Check SystemPromptBuilder has SuggestBreakdown documentation
grep -q "SuggestBreakdown" src/Orbit.Infrastructure/Services/SystemPromptBuilder.cs && echo "✓ SystemPromptBuilder.SuggestBreakdown documented"

# Check integration tests updated
grep -q "Actions.Should" tests/Orbit.IntegrationTests/AiChatIntegrationTests.cs && echo "✓ Integration tests updated for Actions array"
```

**Self-Check Result:** PASSED — All 5 key file modifications verified.

Commits verified in git log:
```
d4ec462 test(04-01): update AI chat tests for multi-action ChatResponse
a74eeb3 feat(04-01): update AI prompt for multi-action and SuggestBreakdown
53c5cdb feat(04-01): add multi-action support and SuggestBreakdown to chat pipeline
```
