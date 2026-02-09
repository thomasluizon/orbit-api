---
phase: 07-routine-intelligence
plan: 01
subsystem: routine-analysis-infrastructure
tags: [domain-models, gemini-integration, ai-prompting, pattern-detection]
dependency_graph:
  requires: [user-facts-infrastructure, gemini-ai-provider, timezone-support]
  provides: [routine-pattern-detection, conflict-detection, time-slot-suggestions]
  affects: [ai-system-prompt, habit-creation-workflow]
tech_stack:
  added: [GeminiRoutineAnalysisService]
  patterns: [LLM-based pattern analysis, UTC-to-local timezone conversion, retry logic with exponential backoff]
key_files:
  created:
    - src/Orbit.Domain/Models/RoutineAnalysis.cs
    - src/Orbit.Domain/Interfaces/IRoutineAnalysisService.cs
    - src/Orbit.Infrastructure/Services/GeminiRoutineAnalysisService.cs
  modified:
    - src/Orbit.Infrastructure/Services/SystemPromptBuilder.cs
    - src/Orbit.Api/Program.cs
decisions:
  - title: LLM-first pattern analysis over statistical methods
    rationale: Gemini handles temporal reasoning natively, produces human-readable patterns without complex statistical algorithms
    alternatives: [ML.NET TimeSeries SSA, custom autocorrelation/FFT]
  - title: Routine patterns fed to AI via SystemPromptBuilder
    rationale: Optional parameter maintains backward compatibility, enables gradual rollout in Plan 02
    impact: No breaking changes to existing AI prompt generation
  - title: Always use Gemini for routine analysis
    rationale: Structured JSON output reliability, follows same pattern as fact extraction
    impact: Consistent behavior regardless of user's AiProvider setting
  - title: Fallback generic time slot suggestions
    rationale: UX consistency - always return 3 suggestions even with insufficient data
    impact: Users get actionable recommendations from day one
metrics:
  duration: 5min
  commits: 2
  files_created: 3
  files_modified: 2
  completed_at: 2026-02-09T18:33:16Z
---

# Phase 7 Plan 1: Routine Intelligence Infrastructure Summary

**One-liner:** LLM-powered routine pattern detection from HabitLog timestamps with conflict warnings and time slot suggestions via Gemini.

## What Was Built

Created the foundational infrastructure for routine intelligence by implementing domain models, service interface, Gemini-powered analysis service, and AI system prompt integration for time-of-day pattern detection.

### Task 1: Domain Models and Service Interface
- **RoutineAnalysis.cs** - 6 record types in single file (follows AiActionPlan.cs pattern)
  - `RoutineAnalysis` - Container for detected patterns
  - `RoutinePattern` - Pattern with habit metadata, description, consistency score (0.0-1.0), confidence (HIGH/MEDIUM/LOW), time blocks
  - `TimeBlock` - Day of week + start/end hour in user's local timezone (0-23)
  - `ConflictWarning` - Schedule conflict detection with severity levels
  - `ConflictingHabit` - Individual conflicting habit details
  - `TimeSlotSuggestion` - Triple-choice format with description, time blocks, rationale, score
- **IRoutineAnalysisService.cs** - 3 async methods
  - `AnalyzeRoutinesAsync(userId)` - Detect recurring time-of-day patterns
  - `DetectConflictsAsync(userId, frequency, days)` - Warn about scheduling conflicts
  - `SuggestTimeSlotsAsync(userId, habitTitle, frequency)` - Suggest 3 optimal time slots

**Commit:** `8d75ab0`

### Task 2: Gemini Implementation and Integration
- **GeminiRoutineAnalysisService.cs** - Full implementation following GeminiFactExtractionService pattern
  - **AnalyzeRoutinesAsync:**
    - Loads user timezone, filters user's habits, queries last 60 days of logs
    - Enforces minimum data thresholds: 7 days total, 5 logs per habit
    - Converts UTC timestamps to user's local time before Gemini analysis
    - Returns empty patterns if insufficient data (no false patterns)
    - Gemini prompt requests JSON with patterns array (consistency scores, confidence levels, time blocks)
  - **DetectConflictsAsync:**
    - Calls AnalyzeRoutinesAsync first to get current patterns
    - Returns null if no patterns detected (no conflict warning)
    - Gemini prompt evaluates new habit frequency/days against existing patterns
    - Severity levels: HIGH (same days + overlapping times), MEDIUM (same days different times), LOW (different days similar times)
  - **SuggestTimeSlotsAsync:**
    - Returns generic fallback suggestions if no patterns (morning 7-8 AM, afternoon 12-1 PM, evening 6-7 PM)
    - Gemini prompt requests EXACTLY 3 diverse suggestions with rationales
    - Ensures suggestions differ meaningfully (diversity constraint)
    - Sorted by score descending
  - **Shared infrastructure:**
    - `CallGeminiAsync<T>` - Shared method with retry logic (max 3 retries, exponential backoff 2s/4s/8s for 429 rate limits)
    - Private Gemini API DTOs (GeminiRequest, GeminiContent, GeminiPart, GeminiGenerationConfig, GeminiResponse, GeminiCandidate)
    - `RoutineJsonOptions` - JsonSerializerOptions with PropertyNameCaseInsensitive + JsonStringEnumConverter
- **SystemPromptBuilder.cs** - Updated with routine patterns support
  - Added optional parameter: `IReadOnlyList<RoutinePattern>? routinePatterns = null`
  - New section: "Your Understanding of This User's Routine" (inserted before "Today's Date")
  - Lists each pattern: `"{HabitTitle}": {Description} (confidence: {Confidence}, consistency: {ConsistencyScore:P0})`
  - Instructions for AI: warn about conflicts, suggest optimal slots, personalize scheduling advice
  - Backward compatible - default null parameter means existing callers unaffected
- **Program.cs** - DI registration
  - `builder.Services.AddHttpClient<IRoutineAnalysisService, GeminiRoutineAnalysisService>()`
  - Follows fact extraction pattern - always uses Gemini regardless of AiProvider setting

**Commit:** `23ee5d5`

## Key Technical Details

### UTC to Local Time Conversion
```csharp
var timezone = TimeZoneInfo.FindSystemTimeZoneById(user.TimeZone);
var logsWithLocalTime = filteredLogs.Select(l => new {
    CreatedAtLocal = TimeZoneInfo.ConvertTimeFromUtc(l.CreatedAtUtc, timezone)
}).ToList();
```
Critical for accurate pattern detection - patterns must reflect user's subjective time, not server UTC time.

### Minimum Data Thresholds
- **7 days total:** `(DateTime.UtcNow - logs.Min(l => l.CreatedAtUtc)).Days >= 7`
- **5 logs per habit:** `habitLogs.GroupBy(l => l.HabitId).Where(g => g.Count() >= 5)`
- **60-day window:** `l.CreatedAtUtc >= DateTime.UtcNow.AddDays(-60)`

Prevents nonsensical patterns from insufficient data, aligns with research showing meaningful patterns require consistent behavior over time.

### Gemini Prompt Structure (Pattern Detection)
- **Input:** JSON array of logs with HabitId, HabitTitle, Date, CreatedAtLocal, Frequency, FrequencyQuantity
- **Output:** JSON with patterns array (habitId, habitTitle, description, consistencyScore, confidence, timeBlocks)
- **Rules:** Require 5+ logs per habit, consistency = actual/expected logs, confidence based on cluster tightness, 1-hour time block windows

### Fallback Time Slot Suggestions
When no patterns detected, returns 3 generic suggestions with low scores (0.50, 0.40, 0.35) and "No routine data yet" rationale. Ensures UX consistency - users always get actionable recommendations.

## Deviations from Plan

None - plan executed exactly as written.

## Testing

Manual verification via `dotnet build Orbit.slnx` - full solution builds cleanly with no errors (pre-existing MSB3277 warning in IntegrationTests is cosmetic).

Integration tests deferred to Plan 02 when routine analysis is integrated into habit creation workflow and can be tested end-to-end.

## Next Phase Readiness

**Ready for Plan 02:** Integrate routine analysis into CreateHabitCommand
- IRoutineAnalysisService fully implemented and registered in DI
- Domain models ready for use in command handlers
- SystemPromptBuilder prepared for routine context (optional parameter already in place)
- No blockers identified

**Blockers:** None

**Open questions for Plan 02:**
- Should CreateHabitCommand return conflict warnings in response DTO? (Plan says yes - CreateHabitResponse with ConflictWarning property)
- Should conflict warnings block habit creation or just warn? (Plan says warn only - allow creation, inform user)
- Should time slot suggestions be included in chat responses? (Plan 02 will clarify)

## Self-Check

Verification of claims:

**Created files exist:**
```
FOUND: src/Orbit.Domain/Models/RoutineAnalysis.cs
FOUND: src/Orbit.Domain/Interfaces/IRoutineAnalysisService.cs
FOUND: src/Orbit.Infrastructure/Services/GeminiRoutineAnalysisService.cs
```

**Modified files exist:**
```
FOUND: src/Orbit.Infrastructure/Services/SystemPromptBuilder.cs
FOUND: src/Orbit.Api/Program.cs
```

**Commits exist:**
```
FOUND: 8d75ab0 (feat(07-01): add routine analysis domain models and service interface)
FOUND: 23ee5d5 (feat(07-01): implement GeminiRoutineAnalysisService and integrate with SystemPromptBuilder)
```

**Implementation verification:**
- IRoutineAnalysisService has 3 methods: ✓ (AnalyzeRoutinesAsync, DetectConflictsAsync, SuggestTimeSlotsAsync)
- GeminiRoutineAnalysisService implements all 3: ✓ (grep confirmed line 35, 171, 248)
- SystemPromptBuilder has routinePatterns parameter: ✓ (grep confirmed line 14, 154, 160)
- Program.cs registers IRoutineAnalysisService: ✓ (AddHttpClient<IRoutineAnalysisService, GeminiRoutineAnalysisService>)
- Solution builds cleanly: ✓ (dotnet build succeeded)

## Self-Check: PASSED

All files created, commits exist, implementation verified, solution builds successfully.
