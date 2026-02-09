---
phase: 05-user-learning-system
plan: 01
subsystem: ai
tags: [gemini, fact-extraction, ai-personalization, entity-framework, soft-delete]

# Dependency graph
requires:
  - phase: 04-multi-action-foundation
    provides: Multi-action chat pipeline with per-action error handling
provides:
  - UserFact entity with soft delete and prompt injection detection
  - Fact extraction service with structured Gemini API calls
  - Dual-pass chat pipeline (action execution + fact extraction)
  - Personalized AI responses using extracted user facts
affects: [06-multimodal-actions, 07-routine-intelligence]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - Dual-pass AI pipeline (primary action + fact extraction)
    - Non-critical AI calls with graceful degradation
    - Global EF Core query filters for soft delete

key-files:
  created:
    - src/Orbit.Domain/Entities/UserFact.cs
    - src/Orbit.Domain/Models/ExtractedFacts.cs
    - src/Orbit.Domain/Interfaces/IFactExtractionService.cs
    - src/Orbit.Infrastructure/Services/GeminiFactExtractionService.cs
    - src/Orbit.Infrastructure/Migrations/20260209202351_AddUserFacts.cs
  modified:
    - src/Orbit.Infrastructure/Persistence/OrbitDbContext.cs
    - src/Orbit.Infrastructure/Services/SystemPromptBuilder.cs
    - src/Orbit.Application/Chat/Commands/ProcessUserChatCommand.cs
    - src/Orbit.Api/Program.cs

key-decisions:
  - "Fact extraction always uses Gemini (even with Ollama provider) for structured output reliability"
  - "Fact extraction failure is non-critical - logged as warning, doesn't affect chat response"
  - "Soft delete for UserFacts with global query filter - allows fact cleanup without losing history"
  - "Basic prompt injection detection in UserFact.Create validates extracted facts"

patterns-established:
  - "Dual-pass AI pipeline: primary action execution + secondary fact extraction"
  - "Non-critical AI enhancement pattern: try-catch wrapper, warning logs, graceful degradation"

# Metrics
duration: 6min
completed: 2026-02-09
---

# Phase 5 Plan 1: Fact Extraction Foundation Summary

**UserFact entity with soft delete, Gemini-powered fact extraction service, and dual-pass chat pipeline for personalized AI responses**

## Performance

- **Duration:** 6 min 17 sec
- **Started:** 2026-02-09T20:21:05Z
- **Completed:** 2026-02-09T20:27:22Z
- **Tasks:** 2
- **Files modified:** 14

## Accomplishments
- UserFact entity with soft delete, factory validation, and prompt injection detection
- GeminiFactExtractionService extracts structured facts from conversations using Gemini API
- SystemPromptBuilder includes "What You Know About This User" section with extracted facts
- Chat pipeline loads user facts and passes them to AI for personalized responses
- Dual-pass fact extraction: facts extracted after action execution and persisted asynchronously
- Fact extraction failures don't affect chat responses (non-critical, graceful degradation)

## Task Commits

Each task was committed atomically:

1. **Task 1: Create UserFact entity, extraction interface, and Gemini extraction service** - `a594b23` (feat)
2. **Task 2: Integrate fact extraction into chat pipeline and system prompt** - `17572ea` (feat)

## Files Created/Modified
- `src/Orbit.Domain/Entities/UserFact.cs` - UserFact entity with soft delete, factory validation, prompt injection detection
- `src/Orbit.Domain/Models/ExtractedFacts.cs` - Fact extraction response model with FactCandidate records
- `src/Orbit.Domain/Interfaces/IFactExtractionService.cs` - Fact extraction service interface
- `src/Orbit.Infrastructure/Services/GeminiFactExtractionService.cs` - Gemini-based fact extraction with structured prompt and retry logic
- `src/Orbit.Infrastructure/Persistence/OrbitDbContext.cs` - Added UserFacts DbSet with global query filter for soft delete
- `src/Orbit.Infrastructure/Migrations/20260209202351_AddUserFacts.cs` - EF Core migration for UserFacts table
- `src/Orbit.Infrastructure/Services/SystemPromptBuilder.cs` - Added "What You Know About This User" section with facts
- `src/Orbit.Domain/Interfaces/IAiIntentService.cs` - Updated interface to accept userFacts parameter
- `src/Orbit.Infrastructure/Services/GeminiIntentService.cs` - Passes userFacts to SystemPromptBuilder
- `src/Orbit.Infrastructure/Services/AiIntentService.cs` - Ollama service passes userFacts to SystemPromptBuilder
- `src/Orbit.Application/Chat/Commands/ProcessUserChatCommand.cs` - Loads user facts, passes to AI, extracts facts after execution
- `src/Orbit.Api/Program.cs` - Registers IFactExtractionService, configures Gemini settings globally

## Decisions Made

**Fact extraction provider strategy:**
- Always use Gemini for fact extraction (even when Ollama is primary provider)
- Rationale: Gemini's structured JSON output is highly reliable (1.6s response, consistent format)
- Ollama's JSON is inconsistent (30s response, frequent parsing failures)
- GeminiSettings configured globally to support both providers

**Non-critical fact extraction:**
- Fact extraction failures don't affect chat response
- Try-catch wrapper logs warning and continues
- Rationale: User experience shouldn't degrade if fact extraction fails
- Extracted facts enhance future conversations but aren't required for current response

**Soft delete for UserFacts:**
- UserFact has IsDeleted/DeletedAtUtc fields with global query filter
- Rationale: Allows cleanup of incorrect/stale facts without losing history
- Future admin feature can restore or permanently delete

**Prompt injection protection:**
- UserFact.Create validates extracted facts for suspicious patterns
- Rejects facts containing "ignore", "system:", "you must", "instruction:"
- Rationale: Prevents AI-extracted facts from injecting malicious prompts into future system prompts

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

**Raw string literal syntax error:**
- C# 13 raw string literals with JSON braces required `$$"""` (double dollar) syntax
- Single dollar (`$"""`) doesn't allow JSON braces `{}` in content
- Fixed by changing to `$$"""` and using `{{variable}}` for interpolation
- All JSON examples in prompt use single braces (not doubled)

## User Setup Required

None - no external service configuration required. Gemini API key already configured from previous phases.

## Next Phase Readiness

**Ready for Phase 5 Plan 2 (Fact Management Endpoints):**
- UserFact entity and repository available for CRUD operations
- Soft delete pattern established for fact cleanup
- Global query filter automatically excludes deleted facts

**Ready for Phase 6 (Multimodal Actions):**
- Fact extraction pattern can extend to image-based facts ("User prefers visual charts")
- SystemPromptBuilder can include image-derived facts

**Ready for Phase 7 (Routine Intelligence):**
- User facts can inform routine suggestions ("User is a morning person" → suggest AM habits)
- Facts provide context for pattern detection

**Concerns:**
- No integration tests yet for fact extraction
- Fact deduplication not implemented (same fact can be extracted multiple times)
- Category validation not enforced (accepts any string, not just "preference", "routine", "context")

## Self-Check: PASSED

**Created files verified:**
- FOUND: src/Orbit.Domain/Entities/UserFact.cs
- FOUND: src/Orbit.Domain/Models/ExtractedFacts.cs
- FOUND: src/Orbit.Domain/Interfaces/IFactExtractionService.cs
- FOUND: src/Orbit.Infrastructure/Services/GeminiFactExtractionService.cs
- FOUND: src/Orbit.Infrastructure/Migrations/20260209202351_AddUserFacts.cs

**Commits verified:**
- FOUND: a594b23 (Task 1)
- FOUND: 17572ea (Task 2)

---
*Phase: 05-user-learning-system*
*Completed: 2026-02-09*
