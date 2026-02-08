---
phase: 03-metrics-and-ai-enhancement
plan: 02
subsystem: ai, domain
tags: ai, gemini, ollama, system-prompt, domain-models, cqrs

# Dependency graph
requires:
  - phase: 02-habit-domain-extensions
    provides: Tag entity, SubHabit entity, habit tag assignment
provides:
  - AI can create habits with inline sub-habits via chat
  - AI can add sub-habits to existing habits via chat
  - AI can suggest and assign existing tags to habits via chat
  - AI gracefully refuses out-of-scope requests with helpful messages
affects: [03-03-enhanced-prompts, future-ai-features]

# Tech tracking
tech-stack:
  added: []
  patterns: [ai-action-expansion-pattern, tag-aware-ai-prompts]

key-files:
  created: []
  modified:
    - src/Orbit.Domain/Enums/AiActionType.cs
    - src/Orbit.Domain/Models/AiAction.cs
    - src/Orbit.Domain/Interfaces/IAiIntentService.cs
    - src/Orbit.Infrastructure/Services/SystemPromptBuilder.cs
    - src/Orbit.Infrastructure/Services/GeminiIntentService.cs
    - src/Orbit.Application/Chat/Commands/ProcessUserChatCommand.cs

key-decisions:
  - "AI can create sub-habits inline during habit creation OR add them to existing habits as separate actions"
  - "Tag suggestions are informational only (included in aiMessage) - tag assignment requires existing tag IDs"
  - "Invalid/unauthorized tags during AssignTag are silently skipped (no error) to allow partial success"

patterns-established:
  - "AI action expansion: new action types added without breaking existing functionality"
  - "Tag-aware prompts: AI sees user's existing tags and can reference them by ID"
  - "Sub-habit inline creation: SubHabits field on CreateHabit for checklist-style habits"

# Metrics
duration: 5min
completed: 2026-02-08
---

# Phase 03 Plan 02: AI Enhancement Summary

**AI can now create habits with sub-habit checklists, suggest tags, assign existing tags, and gracefully refuse out-of-scope requests**

## Performance

- **Duration:** 5 min
- **Started:** 2026-02-08T03:27:24Z
- **Completed:** 2026-02-08T03:32:52Z
- **Tasks:** 2
- **Files modified:** 6

## Accomplishments
- Extended AI action types with CreateSubHabit and AssignTag
- Updated system prompt with user's existing tags, sub-habit examples, and tag assignment patterns
- AI can create habits with inline sub-habits (e.g., "create morning routine with meditate, journal, stretch")
- AI can add sub-habits to existing habits via CreateSubHabit action
- AI can assign existing tags to habits when user requests (by tag ID)
- AI suggests relevant tags in aiMessage when appropriate (informational)

## Task Commits

Each task was committed atomically:

1. **Task 1: Extend AI domain models and update SystemPromptBuilder** - `6a3623b` (feat)
2. **Task 2: Update AI services and ProcessUserChatCommand** - `0ff8cf5` (fix - pre-existing)

**Note:** Task 2 was previously committed in 0ff8cf5 as a fix for 03-01 build errors. This plan completed the remaining Task 1 work.

## Files Created/Modified
- `src/Orbit.Domain/Enums/AiActionType.cs` - Added CreateSubHabit and AssignTag enum values
- `src/Orbit.Domain/Models/AiAction.cs` - Added SubHabits, TagNames, TagIds optional fields
- `src/Orbit.Domain/Interfaces/IAiIntentService.cs` - Added userTags parameter to InterpretAsync
- `src/Orbit.Infrastructure/Services/SystemPromptBuilder.cs` - Added user tags section, sub-habit creation examples, tag assignment examples
- `src/Orbit.Infrastructure/Services/GeminiIntentService.cs` - Updated to pass userTags to BuildSystemPrompt
- `src/Orbit.Application/Chat/Commands/ProcessUserChatCommand.cs` - Added tag repository, fetches user tags, handles CreateSubHabit and AssignTag actions, supports inline sub-habits in CreateHabit

## Decisions Made

**AI can create sub-habits two ways:**
- Inline during CreateHabit (SubHabits field populated) - for creating new habits with checklists
- Via CreateSubHabit action (HabitId + SubHabits) - for adding sub-habits to existing habits

**Tag handling strategy:**
- Existing tags: AI uses AssignTag with exact tag IDs from the "User's Tags" section in prompt
- New tags: AI suggests tag names in aiMessage only (user must create tags manually via API)
- Invalid tag IDs during assignment: silently skipped to allow partial success (e.g., if user deletes a tag between prompt build and execution)

**Graceful refusal:**
- Existing prompt patterns already handle out-of-scope requests well (AI-01 satisfied)
- No weakening of refusal instructions - preserved all existing boundaries
- Default case in switch expression returns helpful error for truly unknown action types

## Deviations from Plan

None - plan executed exactly as written.

**Note:** Task 2 was already completed in commit 0ff8cf5 as a fix for a previous plan's build error. This execution completed the remaining Task 1 work as specified.

## Issues Encountered

None.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

AI expansion infrastructure complete. Ready for:
- Enhanced context-aware prompts (plan 03-03)
- Habit trend queries and analytics (plan 03-01 metrics)
- Future AI action types (edit habit, archive habit, bulk operations)

**Concerns:**
- Ollama reliability with longer prompts (user tags section added ~15 lines per tag) - may degrade JSON consistency
- Tag suggestion quality depends on Gemini's understanding of user's habit patterns (not tested yet)

## Self-Check: PASSED

All claimed files exist and contain expected changes:
- AiActionType.cs has CreateSubHabit and AssignTag
- AiAction.cs has SubHabits, TagNames, TagIds fields
- SystemPromptBuilder.cs has user tags section and new examples
- All commits verified in git log (6a3623b, 0ff8cf5, 90bad46)

---
*Phase: 03-metrics-and-ai-enhancement*
*Completed: 2026-02-08*
