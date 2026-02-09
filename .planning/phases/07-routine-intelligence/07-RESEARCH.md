# Phase 7: Routine Intelligence - Research

**Researched:** 2026-02-09
**Domain:** Time-series pattern detection, LLM-based routine analysis, schedule optimization, conflict detection
**Confidence:** MEDIUM-HIGH

## Summary

Phase 7 implements routine intelligence by analyzing habit log timestamps to detect recurring time-of-day patterns, warn users about scheduling conflicts, and suggest optimal time slots for new habits. Research reveals this is an emerging domain combining traditional time-series analysis with modern LLM capabilities.

The core approach is **LLM-native pattern analysis**: rather than implementing complex statistical algorithms (autocorrelation, FFT, STL decomposition), we leverage Gemini's natural ability to analyze temporal data and extract routine insights. The existing `HabitLog.CreatedAtUtc` timestamp provides sufficient data for pattern detection without additional schema changes. Patterns are expressed as natural language insights ("you typically log Exercise Mon/Wed/Fri around 7am - 70% consistency") and stored as UserFacts for persistence.

Current best practices in 2026 emphasize **LLM-powered time series analysis** over traditional statistical methods for user-facing applications. LLMs excel at: (1) extracting human-interpretable patterns from timestamps, (2) explaining confidence levels in natural language, (3) reasoning about schedule conflicts, and (4) generating contextual time slot recommendations. The triple-choice suggestion format follows modern AI assistant UX patterns.

**Primary recommendation:** Implement an LLM-first routine analysis system that queries HabitLog timestamps, prompts Gemini to detect patterns and conflicts, and returns structured recommendations. No ML.NET or statistical libraries needed—Gemini handles pattern recognition natively. Focus on prompt engineering for accurate pattern extraction and conflict detection logic.

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| Gemini 2.5 Flash | Current | Pattern analysis + conflict detection + time slot suggestions | Already integrated, 1M token context handles all habit logs, structured output for recommendations |
| Entity Framework Core | 10.0.0 | Query HabitLog.CreatedAtUtc timestamps | Already in stack, proven for temporal queries |
| MediatR | 14.0.0 | CQRS for routine analysis commands/queries | Already in stack, established pattern |
| System.Text.Json | Built-in | Deserialize routine analysis responses | Already used for AiActionPlan deserialization |

### Supporting
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| NodaTime | 3.x | Timezone-aware timestamp analysis | Optional - if User.TimeZone conversions become complex. DateTime.UtcNow + TimeZoneInfo may suffice for MVP |
| User.TimeZone | Existing | Convert UTC timestamps to user's local time | Already implemented in Phase 1, reuse for pattern detection |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| LLM pattern analysis | ML.NET TimeSeries (SSA forecasting) | ML.NET requires training data, handles numeric forecasting well but lacks natural language pattern explanation. LLM approach needs no training, produces human-readable insights. |
| LLM pattern analysis | Custom autocorrelation/FFT algorithms | Statistical methods are accurate but complex, require domain expertise, produce numeric outputs that need interpretation. LLM is simpler, explains patterns naturally. |
| Store patterns as UserFacts | New RoutinePattern entity | UserFact reuse is simpler, leverages existing infrastructure. Dedicated entity only if pattern-specific queries become performance bottleneck. |
| Gemini for all analysis | Hybrid: ML.NET for detection + Gemini for explanation | Hybrid adds complexity. Gemini alone handles both detection and explanation in single call. Defer ML.NET unless Gemini accuracy proves insufficient. |

**Installation:**
```bash
# No new packages needed for MVP
# All required packages already in stack

# Optional: if timezone handling becomes complex
dotnet add package NodaTime --version 3.2.0
```

## Architecture Patterns

### Recommended Project Structure
```
src/
├── Orbit.Domain/
│   ├── Interfaces/
│   │   └── IRoutineAnalysisService.cs      # New interface
│   └── Models/
│       ├── RoutinePattern.cs                # LLM response model
│       └── TimeSlotSuggestion.cs            # Triple-choice format
├── Orbit.Application/
│   ├── Habits/Commands/
│   │   └── CreateHabitCommand.cs            # MODIFY: Add conflict detection
│   └── Routines/
│       ├── Commands/
│       │   └── AnalyzeRoutinesCommand.cs    # New (internal/admin)
│       └── Queries/
│           └── GetRoutinePatternsQuery.cs   # New (optional - for UI display)
├── Orbit.Infrastructure/
│   └── Services/
│       ├── GeminiRoutineAnalysisService.cs  # New: pattern detection + conflict detection + time slot suggestions
│       └── SystemPromptBuilder.cs           # MODIFY: Add routine analysis instructions
└── Orbit.Api/
    └── Controllers/
        └── HabitsController.cs              # MODIFY: CreateHabit returns conflict warnings in response
```

### Pattern 1: LLM-Based Pattern Detection

**What:** Query HabitLog timestamps, prompt Gemini to extract recurring time-of-day patterns with confidence scores

**When to use:** User creates new habit, or on-demand routine analysis

**Example:**
```csharp
// IRoutineAnalysisService interface
public interface IRoutineAnalysisService
{
    Task<Result<RoutineAnalysis>> AnalyzeRoutinesAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<Result<ConflictWarning?>> DetectConflictsAsync(
        Guid userId,
        FrequencyUnit? frequencyUnit,
        int? frequencyQuantity,
        IReadOnlyList<DayOfWeek>? days,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<TimeSlotSuggestion>>> SuggestTimeSlotsAsync(
        Guid userId,
        FrequencyUnit? frequencyUnit,
        int? frequencyQuantity,
        CancellationToken cancellationToken = default);
}

// Gemini prompt for pattern detection
var prompt = $$"""
Analyze these habit log timestamps and detect recurring time-of-day patterns.

User timezone: {{user.TimeZone}}
Current date: {{DateOnly.FromDateTime(DateTime.UtcNow)}}

Habit logs (UTC timestamps):
{{JsonSerializer.Serialize(habitLogs.Select(l => new {
    l.HabitId,
    HabitTitle = habits.First(h => h.Id == l.HabitId).Title,
    l.CreatedAtUtc,
    l.Date
}))}}

For each habit, identify:
1. Recurring time-of-day patterns (e.g., "logged Mon/Wed/Fri around 7am")
2. Consistency score (% of expected logs that occurred, based on frequency)
3. Confidence level (HIGH: 80%+, MEDIUM: 60-79%, LOW: <60%)

Return JSON:
{
  "patterns": [
    {
      "habitId": "guid",
      "habitTitle": "string",
      "description": "user typically logs this Mon/Wed/Fri around 7:00 AM",
      "consistencyScore": 0.70,
      "confidence": "MEDIUM",
      "timeBlocks": [
        { "dayOfWeek": "Monday", "startHour": 7, "endHour": 8 },
        { "dayOfWeek": "Wednesday", "startHour": 7, "endHour": 8 }
      ]
    }
  ]
}

Rules:
- Convert UTC timestamps to user's timezone before analysis
- Require at least 7 days of data for pattern detection
- If <7 days of data, return empty patterns array
- Consistency score = (actual logs / expected logs based on habit frequency)
- Confidence = how reliably logs occur at detected time (cluster tightness)
""";
```

**Source:** Based on [LLM-Powered Time-Series Analysis (Towards Data Science)](https://towardsdatascience.com/llm-powered-time-series-analysis/)

### Pattern 2: Schedule Conflict Detection

**What:** When user creates new habit, check if scheduling conflicts with detected routine blocks

**When to use:** CreateHabitCommand handler, before persisting habit

**Example:**
```csharp
// In CreateHabitCommandHandler
public async Task<Result<Guid>> Handle(CreateHabitCommand request, CancellationToken cancellationToken)
{
    // ... existing validation ...

    // Check for schedule conflicts (if habit has frequency/days defined)
    ConflictWarning? conflictWarning = null;
    if (request.FrequencyUnit.HasValue && request.FrequencyQuantity.HasValue)
    {
        var conflictResult = await _routineAnalysisService.DetectConflictsAsync(
            userId,
            request.FrequencyUnit,
            request.FrequencyQuantity,
            request.Days,
            cancellationToken);

        if (conflictResult.IsSuccess)
            conflictWarning = conflictResult.Value;
    }

    // Create habit (allow creation even with conflicts - warning only)
    var habitResult = Habit.Create(...);
    await _habitRepository.AddAsync(habitResult.Value);
    await _unitOfWork.SaveChangesAsync(cancellationToken);

    // Return habit ID with optional conflict warning
    return Result.Success(new CreateHabitResponse(
        HabitId: habitResult.Value.Id,
        ConflictWarning: conflictWarning));
}

// Gemini prompt for conflict detection
var prompt = $$"""
Detect schedule conflicts between a new habit and existing routine patterns.

New habit:
- Frequency: {{frequencyUnit}} / {{frequencyQuantity}}
- Days: {{string.Join(", ", days ?? [])}}

Existing routine patterns:
{{JsonSerializer.Serialize(existingPatterns)}}

Return JSON:
{
  "hasConflict": true/false,
  "conflictingHabits": [
    {
      "habitId": "guid",
      "habitTitle": "string",
      "conflictDescription": "both scheduled Mon/Wed/Fri mornings"
    }
  ],
  "severity": "HIGH" | "MEDIUM" | "LOW",
  "recommendation": "Consider scheduling this on Tuesdays/Thursdays instead"
}

Rules:
- HIGH severity: same days + overlapping time blocks (within 1 hour)
- MEDIUM severity: same days, different times
- LOW severity: different days but similar time-of-day
- If no patterns detected yet, return hasConflict: false
""";
```

**Source:** Based on [Advanced Conflict Detection Algorithms (myshyft.com)](https://www.myshyft.com/blog/conflict-detection-algorithms/)

### Pattern 3: Triple-Choice Time Slot Suggestions

**What:** Suggest 3 optimal time slots for new habit based on routine gaps

**When to use:** User creates habit without specifying days, or requests schedule optimization

**Example:**
```csharp
// Response model
public record TimeSlotSuggestion
{
    public required string Description { get; init; }  // "Tuesday/Thursday mornings (8-9 AM)"
    public required IReadOnlyList<TimeBlock> TimeBlocks { get; init; }
    public required string Rationale { get; init; }    // "No conflicts, follows your morning routine pattern"
    public required decimal Score { get; init; }       // 0.85 (0-1 scale)
}

public record TimeBlock
{
    public required DayOfWeek DayOfWeek { get; init; }
    public required int StartHour { get; init; }       // 0-23 (user's local timezone)
    public required int EndHour { get; init; }
}

// Gemini prompt for time slot suggestions
var prompt = $$"""
Suggest 3 optimal time slots for a new habit based on routine gaps.

New habit:
- Title: {{habitTitle}}
- Frequency: {{frequencyUnit}} / {{frequencyQuantity}}

Existing routine patterns:
{{JsonSerializer.Serialize(existingPatterns)}}

User timezone: {{user.TimeZone}}

Return JSON with EXACTLY 3 suggestions:
{
  "suggestions": [
    {
      "description": "Tuesday/Thursday mornings (8-9 AM)",
      "timeBlocks": [
        { "dayOfWeek": "Tuesday", "startHour": 8, "endHour": 9 },
        { "dayOfWeek": "Thursday", "startHour": 8, "endHour": 9 }
      ],
      "rationale": "No conflicts, follows your established morning routine pattern",
      "score": 0.85
    },
    {
      "description": "Monday/Wednesday/Friday evenings (7-8 PM)",
      "timeBlocks": [...],
      "rationale": "Available time, but conflicts with existing evening habits 1 day/week",
      "score": 0.70
    },
    {
      "description": "Weekend mornings (9-10 AM)",
      "timeBlocks": [...],
      "rationale": "Good for weekly habits, but you have limited weekend activity history",
      "score": 0.60
    }
  ]
}

Rules:
- Return EXACTLY 3 suggestions, ordered by score (highest first)
- Score based on: (1) no conflicts, (2) alignment with existing patterns, (3) time availability
- Ensure suggestions match habit's frequency (e.g., Daily/1 = 7 days, Weekly/2 = 2 days/week)
- Include specific time blocks in user's local timezone
- Rationale explains scoring: why this slot is optimal (or suboptimal)
""";
```

**Source:** Based on [AI Scheduling Assistant Best Practices 2026](https://thedigitalprojectmanager.com/tools/ai-scheduling-assistant/)

### Pattern 4: Routine Pattern Persistence via UserFacts

**What:** Store detected routine patterns as UserFacts for AI context and user visibility

**When to use:** After successful routine analysis, persist patterns for future reference

**Example:**
```csharp
// After analyzing routines via Gemini
var routineAnalysis = await _routineAnalysisService.AnalyzeRoutinesAsync(userId);

if (routineAnalysis.IsSuccess && routineAnalysis.Value.Patterns.Count > 0)
{
    foreach (var pattern in routineAnalysis.Value.Patterns)
    {
        var factText = $"Routine: {pattern.Description} (consistency: {pattern.ConsistencyScore:P0}, confidence: {pattern.Confidence})";

        var factResult = UserFact.Create(
            userId,
            factText,
            category: "routine");  // Category for filtering routine-specific facts

        if (factResult.IsSuccess)
        {
            await _userFactRepository.AddAsync(factResult.Value);
        }
    }

    await _unitOfWork.SaveChangesAsync(cancellationToken);
}

// Later: Load routine facts into system prompt for conflict detection context
var routineFacts = await _userFactRepository.FindAsync(
    f => f.UserId == userId && f.Category == "routine");

var systemPrompt = SystemPromptBuilder.BuildSystemPrompt(
    habits,
    tags,
    userFacts: routineFacts  // Include routine patterns in AI context
);
```

**Source:** Phase 5 UserFact infrastructure, extended for routine patterns

### Anti-Patterns to Avoid

- **Premature ML.NET optimization:** Don't implement complex SSA/SARIMA algorithms before validating LLM approach. Gemini handles pattern detection well for user-scale data (<1000 logs). Reserve ML.NET for future scale if needed.
- **Ignoring timezone conversions:** Habit logs store `CreatedAtUtc`, but patterns must be detected in user's local time. Always convert before prompting Gemini, or pattern detection will be nonsensical.
- **Blocking habit creation on conflicts:** Conflicts are warnings, not errors. Users should be able to override. Return conflict warnings in response, don't fail the command.
- **Storing raw timestamps in prompts:** Don't send 1000 individual timestamps to Gemini. Aggregate by habit, include only relevant metadata (HabitId, Title, CreatedAtUtc converted to local time). Exceed context limits quickly otherwise.
- **Confidence scores without explanation:** Don't return "70% confidence" without explaining what that means ("logged 7 of last 10 expected times"). LLMs can generate human-readable explanations naturally.
- **Triple-choice with varying quality:** Always return EXACTLY 3 suggestions, even if quality degrades. Third suggestion might have low score (0.40), but format consistency matters for UX. Explain limitations in rationale.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Time-series pattern detection | Custom autocorrelation/FFT/STL decomposition | Gemini with structured prompt | Time-series algorithms are complex, assume numeric patterns. Gemini handles temporal reasoning natively, produces human-readable patterns. |
| Confidence score calculation | Manual statistics (z-scores, std dev) | LLM-generated confidence levels | LLM can assess pattern consistency from raw data, explain confidence naturally. Manual stats require interpretation layer. |
| Conflict detection algorithm | Custom overlap/intersection logic | Gemini reasoning over patterns | Edge cases are numerous (partial overlaps, time buffer zones, multi-day patterns). LLM handles nuanced conflicts better than rule-based logic. |
| Time slot recommendation | Graph-based optimization algorithms | Gemini contextual suggestions | Optimization algorithms maximize utilization, but ignore user context (morning person vs night owl). LLM incorporates behavioral patterns naturally. |

**Key insight:** Routine intelligence is a natural language reasoning task disguised as a time-series problem. LLMs excel at: "given these timestamps and habit frequencies, when does the user typically do X?" Leverage Gemini's reasoning capabilities rather than building statistical models.

## Common Pitfalls

### Pitfall 1: Insufficient Data for Pattern Detection

**What goes wrong:** User has 2-3 habit logs, system tries to detect patterns, returns nonsensical or low-confidence patterns

**Why it happens:** Statistical and LLM methods both require minimum sample size for meaningful pattern detection

**How to avoid:**
- Require at least 7 days of habit logs OR minimum 5 logs per habit for pattern detection
- Return empty patterns array if insufficient data, with clear message: "Not enough data yet - check back after logging for a week"
- Don't show conflict warnings if no patterns detected yet
- Store threshold as configuration (e.g., `MinLogsForPatternDetection = 7`)

**Warning signs:** User sees "you typically log Exercise on Mondays" after logging once on Monday. Patterns change drastically day-to-day in first week.

**Source:** [What can machine learning teach us about habit formation? (PNAS)](https://www.pnas.org/doi/10.1073/pnas.2216115120) - research shows 76% of patterns emerge after consistent behavior over time

### Pitfall 2: UTC vs Local Time Confusion in Pattern Detection

**What goes wrong:** User logs habit at 7am local time (EST), but `CreatedAtUtc` is 12:00 PM UTC. Gemini detects "noon pattern" instead of "7am morning pattern"

**Why it happens:** HabitLog.CreatedAtUtc stores server time (UTC), but patterns must be detected in user's subjective time (local timezone)

**How to avoid:**
- Always convert `CreatedAtUtc` to user's local time using `User.TimeZone` before sending to Gemini
- Use `TimeZoneInfo.ConvertTimeFromUtc(habitLog.CreatedAtUtc, TimeZoneInfo.FindSystemTimeZoneById(user.TimeZone))`
- Include user's timezone in prompt explicitly: "User timezone: America/New_York"
- Validate timezone conversion in integration tests with users in different timezones

**Warning signs:** User complains "I always log in the morning, why does it say I log at noon?" Different timezone users see different incorrect patterns.

**Source:** Existing codebase pattern in `ProcessUserChatCommandHandler.GetUserToday()` - reuse timezone logic

### Pitfall 3: Stale Routine Patterns in UserFacts

**What goes wrong:** User's routine changes (new job, new schedule), but old routine patterns persist in UserFacts, causing incorrect conflict warnings

**Why it happens:** UserFacts have no expiration policy. Phase 5 deferred auto-expiration decision to future phases.

**How to avoid:**
- Implement routine fact refresh: when analyzing routines, soft-delete existing routine facts (Category == "routine") and replace with fresh analysis
- Alternative: Add UpdatedAtUtc check - ignore routine facts older than 30 days
- Give users control: GET /api/user-facts can filter by category, users can delete outdated routine facts
- Periodic cleanup job (optional): soft-delete routine facts older than 60 days

**Warning signs:** User says "I don't do that anymore" when shown conflict warnings. Conflict warnings reference habits that were deleted weeks ago.

**Source:** Phase 5 Open Questions - fact expiration policy deferred. Routine patterns have natural shelf life (unlike preferences).

### Pitfall 4: Token Limit Exceeded with Large Log History

**What goes wrong:** User has 500+ habit logs, all logs sent to Gemini for pattern analysis, request exceeds context limit or costs excessive tokens

**Why it happens:** Sending raw log arrays to LLM doesn't scale. Each log is ~50 tokens (habitId, title, CreatedAtUtc, Date).

**How to avoid:**
- Limit analysis window: only analyze last 30-60 days of logs (most relevant for current routine)
- Query: `habitLogs.Where(l => l.CreatedAtUtc >= DateTime.UtcNow.AddDays(-60))`
- Aggregate logs per habit: instead of individual timestamps, send summary statistics
- Example: "Exercise habit: 18 logs in last 30 days, timestamps: [...]" (only include actual timestamps, not full objects)
- Monitor prompt token count in development logs

**Warning signs:** Gemini API returns 400 Bad Request (context length exceeded). Response time >10 seconds for routine analysis. Token costs spike with active users.

**Source:** [Gemini API context limits](https://ai.google.dev/gemini-api/docs/prompting-strategies) - 1M token context, but cost scales with input size

### Pitfall 5: Triple-Choice Suggestions with Duplicate Time Blocks

**What goes wrong:** Gemini returns 3 suggestions that are nearly identical (e.g., all "weekday mornings" with slight variations)

**Why it happens:** LLM optimization bias toward highest-score solution. Without explicit diversity constraint, suggestions cluster around global optimum.

**How to avoid:**
- Add diversity constraint to prompt: "Ensure suggestions differ meaningfully (different days OR different times). Avoid returning 3 variations of the same time block."
- Validate response: if suggestions have >70% overlapping time blocks, regenerate with stricter diversity prompt
- Example diversity check: `suggestions.SelectMany(s => s.TimeBlocks).Distinct().Count() >= 6` (3 suggestions × 2+ unique blocks each)
- Provide fallback suggestions if diversity fails (e.g., morning/afternoon/evening defaults)

**Warning signs:** Users complain "all 3 suggestions are basically the same." Frontend shows identical-looking time blocks.

**Source:** [AI Recommendation Optimization Guide 2026](https://www.trysight.ai/blog/ai-recommendation-optimization-guide) - diversity in recommendations

### Pitfall 6: Conflict Detection Ignoring Habit Frequency

**What goes wrong:** User creates "Weekly/1 on Mondays" habit, system warns of conflict with "Daily/1" habit that runs all week including Mondays

**Why it happens:** Conflict detection compares days-of-week only, ignores that Daily habit already accounts for Monday

**How to avoid:**
- Prompt must include frequency context: "Daily habits occupy all days. Weekly/2 on Mon/Wed conflicts with Daily, but Weekly/1 on Monday is expected overlap."
- Conflict severity scoring: Daily + Weekly = LOW (expected overlap), Weekly + Weekly on same day = MEDIUM, same frequency + same time = HIGH
- Clarify in conflict warning: "Note: 'Exercise' is daily and will naturally overlap with any weekly habit"

**Warning signs:** Every new habit shows conflicts with daily habits. Users ignore warnings because they're always present.

**Source:** Common scheduling conflict classification - [Scheduling Conflicts Types & Solutions](https://truein.com/blogs/scheduling-conflicts)

## Code Examples

Verified patterns from official sources and existing codebase:

### Routine Analysis Service Interface

```csharp
// src/Orbit.Domain/Interfaces/IRoutineAnalysisService.cs
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Domain.Interfaces;

public interface IRoutineAnalysisService
{
    /// <summary>
    /// Analyzes user's habit log timestamps to detect recurring time-of-day patterns
    /// </summary>
    Task<Result<RoutineAnalysis>> AnalyzeRoutinesAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Detects if a new habit's scheduling conflicts with detected routine patterns
    /// </summary>
    Task<Result<ConflictWarning?>> DetectConflictsAsync(
        Guid userId,
        FrequencyUnit? frequencyUnit,
        int? frequencyQuantity,
        IReadOnlyList<DayOfWeek>? days,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Suggests 3 optimal time slots for a new habit based on routine gaps
    /// </summary>
    Task<Result<IReadOnlyList<TimeSlotSuggestion>>> SuggestTimeSlotsAsync(
        Guid userId,
        FrequencyUnit? frequencyUnit,
        int? frequencyQuantity,
        CancellationToken cancellationToken = default);
}
```

### Routine Analysis Response Models

```csharp
// src/Orbit.Domain/Models/RoutineAnalysis.cs
namespace Orbit.Domain.Models;

public record RoutineAnalysis
{
    public required IReadOnlyList<RoutinePattern> Patterns { get; init; }
}

public record RoutinePattern
{
    public required Guid HabitId { get; init; }
    public required string HabitTitle { get; init; }
    public required string Description { get; init; }  // "user typically logs this Mon/Wed/Fri around 7:00 AM"
    public required decimal ConsistencyScore { get; init; }  // 0.0 - 1.0
    public required string Confidence { get; init; }  // "HIGH" | "MEDIUM" | "LOW"
    public required IReadOnlyList<TimeBlock> TimeBlocks { get; init; }
}

public record TimeBlock
{
    public required DayOfWeek DayOfWeek { get; init; }
    public required int StartHour { get; init; }  // 0-23 (user's local timezone)
    public required int EndHour { get; init; }    // 0-23
}

public record ConflictWarning
{
    public required bool HasConflict { get; init; }
    public required IReadOnlyList<ConflictingHabit> ConflictingHabits { get; init; }
    public required string Severity { get; init; }  // "HIGH" | "MEDIUM" | "LOW"
    public required string? Recommendation { get; init; }
}

public record ConflictingHabit
{
    public required Guid HabitId { get; init; }
    public required string HabitTitle { get; init; }
    public required string ConflictDescription { get; init; }  // "both scheduled Mon/Wed/Fri mornings"
}

public record TimeSlotSuggestion
{
    public required string Description { get; init; }  // "Tuesday/Thursday mornings (8-9 AM)"
    public required IReadOnlyList<TimeBlock> TimeBlocks { get; init; }
    public required string Rationale { get; init; }  // "No conflicts, follows your morning routine pattern"
    public required decimal Score { get; init; }  // 0.0 - 1.0
}
```

### GeminiRoutineAnalysisService Implementation

```csharp
// src/Orbit.Infrastructure/Services/GeminiRoutineAnalysisService.cs
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using System.Text.Json;

namespace Orbit.Infrastructure.Services;

public class GeminiRoutineAnalysisService : IRoutineAnalysisService
{
    private readonly HttpClient _httpClient;
    private readonly IRepository<User> _userRepository;
    private readonly IRepository<Habit> _habitRepository;
    private readonly IRepository<HabitLog> _habitLogRepository;
    private readonly string _apiKey;

    private const int MinDaysForPatternDetection = 7;
    private const int AnalysisWindowDays = 60;

    public GeminiRoutineAnalysisService(
        HttpClient httpClient,
        IRepository<User> userRepository,
        IRepository<Habit> habitRepository,
        IRepository<HabitLog> habitLogRepository,
        IConfiguration configuration)
    {
        _httpClient = httpClient;
        _userRepository = userRepository;
        _habitRepository = habitRepository;
        _habitLogRepository = habitLogRepository;
        _apiKey = configuration["Gemini:ApiKey"]
            ?? throw new InvalidOperationException("Gemini API key not configured");
    }

    public async Task<Result<RoutineAnalysis>> AnalyzeRoutinesAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        // 1. Load user for timezone
        var user = await _userRepository.FindAsync(u => u.Id == userId);
        if (user is null)
            return Result.Failure<RoutineAnalysis>("User not found");

        var timezone = TimeZoneInfo.FindSystemTimeZoneById(user.TimeZone);

        // 2. Load recent habit logs (last 60 days)
        var cutoffDate = DateTime.UtcNow.AddDays(-AnalysisWindowDays);
        var habitLogs = await _habitLogRepository.FindAsync(
            l => l.HabitId != Guid.Empty && l.CreatedAtUtc >= cutoffDate);

        if (!habitLogs.Any())
            return Result.Success(new RoutineAnalysis { Patterns = [] });

        // 3. Check minimum data requirement
        var daysSinceFirstLog = (DateTime.UtcNow - habitLogs.Min(l => l.CreatedAtUtc)).Days;
        if (daysSinceFirstLog < MinDaysForPatternDetection)
            return Result.Success(new RoutineAnalysis { Patterns = [] });

        // 4. Load habits for titles
        var habitIds = habitLogs.Select(l => l.HabitId).Distinct().ToList();
        var habits = await _habitRepository.FindAsync(h => habitIds.Contains(h.Id));

        // 5. Convert logs to local time and prepare for prompt
        var logsWithLocalTime = habitLogs.Select(l => new
        {
            l.HabitId,
            HabitTitle = habits.FirstOrDefault(h => h.Id == l.HabitId)?.Title ?? "Unknown",
            l.Date,
            CreatedAtLocal = TimeZoneInfo.ConvertTimeFromUtc(l.CreatedAtUtc, timezone),
            Frequency = habits.FirstOrDefault(h => h.Id == l.HabitId)?.FrequencyUnit?.ToString(),
            FrequencyQuantity = habits.FirstOrDefault(h => h.Id == l.HabitId)?.FrequencyQuantity
        }).ToList();

        // 6. Build Gemini prompt
        var prompt = $$"""
        Analyze these habit log timestamps and detect recurring time-of-day patterns.

        User timezone: {{user.TimeZone}}
        Current date: {{DateOnly.FromDateTime(DateTime.UtcNow)}}
        Analysis window: Last {{AnalysisWindowDays}} days

        Habit logs (local time):
        {{JsonSerializer.Serialize(logsWithLocalTime, new JsonSerializerOptions { WriteIndented = true })}}

        For each habit, identify:
        1. Recurring time-of-day patterns (e.g., "logged Mon/Wed/Fri around 7am")
        2. Consistency score (% of expected logs that occurred, based on habit frequency)
        3. Confidence level (HIGH: 80%+, MEDIUM: 60-79%, LOW: <60%)

        Return JSON:
        {
          "patterns": [
            {
              "habitId": "guid",
              "habitTitle": "string",
              "description": "user typically logs this Mon/Wed/Fri around 7:00 AM",
              "consistencyScore": 0.70,
              "confidence": "MEDIUM",
              "timeBlocks": [
                { "dayOfWeek": "Monday", "startHour": 7, "endHour": 8 },
                { "dayOfWeek": "Wednesday", "startHour": 7, "endHour": 8 }
              ]
            }
          ]
        }

        Rules:
        - Timestamps are already converted to user's local timezone
        - Require at least 5 logs per habit for pattern detection
        - Consistency score = (actual logs / expected logs based on habit frequency)
        - Confidence = how reliably logs occur at detected time (cluster tightness)
        - Time blocks: round to nearest hour, use 1-hour windows
        - If habit has <5 logs, exclude from patterns
        """;

        // 7. Call Gemini API
        var geminiResult = await CallGeminiAsync<RoutineAnalysis>(prompt, cancellationToken);
        return geminiResult;
    }

    // Similar implementations for DetectConflictsAsync and SuggestTimeSlotsAsync...

    private async Task<Result<T>> CallGeminiAsync<T>(string prompt, CancellationToken cancellationToken)
    {
        // Reuse existing GeminiIntentService pattern with retry logic
        // ...implementation details...
    }
}
```

**Source:** Based on existing `GeminiIntentService` pattern + [Gemini Prompt Design Strategies](https://ai.google.dev/gemini-api/docs/prompting-strategies)

### Extended CreateHabitCommand Response

```csharp
// src/Orbit.Application/Habits/Commands/CreateHabitCommand.cs
// Response DTO
public record CreateHabitResponse(
    Guid HabitId,
    ConflictWarning? ConflictWarning  // NEW: optional conflict warning
);

// Handler modification
public class CreateHabitCommandHandler : IRequestHandler<CreateHabitCommand, Result<CreateHabitResponse>>
{
    private readonly IRepository<Habit> _habitRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IRoutineAnalysisService _routineAnalysisService;  // NEW

    public async Task<Result<CreateHabitResponse>> Handle(
        CreateHabitCommand request,
        CancellationToken cancellationToken)
    {
        // ... existing habit creation logic ...

        // NEW: Detect schedule conflicts (warning only, don't block)
        ConflictWarning? conflictWarning = null;
        if (request.FrequencyUnit.HasValue && request.FrequencyQuantity.HasValue)
        {
            var conflictResult = await _routineAnalysisService.DetectConflictsAsync(
                request.UserId,
                request.FrequencyUnit,
                request.FrequencyQuantity,
                request.Days,
                cancellationToken);

            if (conflictResult.IsSuccess)
                conflictWarning = conflictResult.Value;
        }

        var habitResult = Habit.Create(...);
        await _habitRepository.AddAsync(habitResult.Value);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(new CreateHabitResponse(
            HabitId: habitResult.Value.Id,
            ConflictWarning: conflictWarning));
    }
}
```

### Integration Test Example

```csharp
// tests/Orbit.IntegrationTests/RoutineAnalysisIntegrationTests.cs
[Fact]
public async Task AnalyzeRoutines_WithSufficientData_ShouldDetectPatterns()
{
    // Arrange: Create user with timezone
    var user = await CreateUserAsync("America/New_York");

    // Create habit: Daily exercise
    var habit = await CreateHabitAsync(user.Id, "Exercise", FrequencyUnit.Day, 1);

    // Log habit at consistent times (7-8 AM EST) for 14 days on Mon/Wed/Fri
    var baseDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-14));
    var timezone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    for (int i = 0; i < 14; i++)
    {
        var date = baseDate.AddDays(i);
        if (date.DayOfWeek is DayOfWeek.Monday or DayOfWeek.Wednesday or DayOfWeek.Friday)
        {
            // Create log at 7:30 AM EST (convert to UTC for CreatedAtUtc)
            var localTime = new DateTime(date.Year, date.Month, date.Day, 7, 30, 0);
            var utcTime = TimeZoneInfo.ConvertTimeToUtc(localTime, timezone);

            await LogHabitAsync(habit.Id, date, createdAtUtc: utcTime);
        }
    }

    // Act
    var response = await _client.GetAsync($"/api/routines/analyze");

    // Assert
    response.StatusCode.Should().Be(HttpStatusCode.OK);
    var result = await response.Content.ReadFromJsonAsync<RoutineAnalysis>();

    result!.Patterns.Should().ContainSingle();
    var pattern = result.Patterns.First();

    pattern.HabitId.Should().Be(habit.Id);
    pattern.HabitTitle.Should().Be("Exercise");
    pattern.Description.Should().Contain("Mon").And.Contain("Wed").And.Contain("Fri");
    pattern.Description.Should().Contain("7").Or.Contain("morning");
    pattern.ConsistencyScore.Should().BeGreaterThan(0.8m);  // High consistency
    pattern.Confidence.Should().Be("HIGH");

    pattern.TimeBlocks.Should().HaveCount(3);  // Mon, Wed, Fri
    pattern.TimeBlocks.Should().AllSatisfy(tb =>
    {
        tb.StartHour.Should().BeInRange(6, 8);  // Around 7 AM
        tb.EndHour.Should().Be(tb.StartHour + 1);  // 1-hour window
    });
}

[Fact]
public async Task CreateHabit_WithScheduleConflict_ShouldWarnButAllowCreation()
{
    // Arrange: Create existing pattern (Mon/Wed/Fri mornings)
    var user = await CreateUserAsync("America/New_York");
    var existingHabit = await CreateHabitAsync(user.Id, "Exercise", FrequencyUnit.Weekly, 3,
        days: [DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday]);

    await CreateRoutinePatternAsync(user.Id, existingHabit.Id,
        days: [DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday],
        timeBlock: (7, 8));

    // Act: Create conflicting habit (Mon/Wed mornings)
    var response = await _client.PostAsJsonAsync("/api/habits", new
    {
        title = "Meditation",
        frequencyUnit = "Weekly",
        frequencyQuantity = 2,
        days = new[] { "Monday", "Wednesday" }
    });

    // Assert
    response.StatusCode.Should().Be(HttpStatusCode.OK);  // Creation allowed
    var result = await response.Content.ReadFromJsonAsync<CreateHabitResponse>();

    result!.HabitId.Should().NotBeEmpty();
    result.ConflictWarning.Should().NotBeNull();
    result.ConflictWarning!.HasConflict.Should().BeTrue();
    result.ConflictWarning.ConflictingHabits.Should().ContainSingle(h => h.HabitId == existingHabit.Id);
    result.ConflictWarning.Severity.Should().BeOneOf("HIGH", "MEDIUM");
    result.ConflictWarning.Recommendation.Should().NotBeNullOrWhiteSpace();
}
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Statistical time-series analysis (ARIMA, SSA) | LLM-based pattern extraction | 2025-2026 | Simpler implementation, natural language output, no training data required. Trades statistical rigor for interpretability. |
| Rule-based conflict detection | LLM reasoning over schedules | 2025-2026 | Handles edge cases better (partial overlaps, context-aware severity). Requires careful prompt engineering. |
| Graph/optimization algorithms for scheduling | LLM contextual suggestions | 2025-2026 | Incorporates user behavior patterns, explains rationale. Less mathematically optimal, more human-aligned. |
| Store patterns in dedicated tables | Store as UserFacts (existing infrastructure) | Phase 7 decision | Reuses fact extraction pipeline, unified memory system. Trades query performance for architectural simplicity. |
| Binary conflict detection (yes/no) | Severity levels + recommendations | 2026 UX best practices | Users can make informed decisions. Reduces false alarm fatigue. |

**Deprecated/outdated:**
- **ML.NET TimeSeries without context:** ML.NET SSA is powerful for numeric forecasting but doesn't explain patterns naturally. LLM approach dominates for user-facing habit apps in 2026.
- **Extension-based pattern libraries:** Deedle, Accord.NET time-series extensions assume statistical expertise. LLM-first approach lowers barrier to entry.
- **Hard-blocking on conflicts:** Modern UX treats conflicts as warnings, not errors. User autonomy > system enforcement.

## Open Questions

1. **Should routine patterns auto-refresh, or user-triggered?**
   - What we know: Patterns can become stale (Pitfall 3), users change routines
   - What's unclear: Frequency of refresh (daily analysis expensive, weekly may lag), trigger mechanism
   - Recommendation: Refresh on-demand when creating new habit (conflict detection requires fresh patterns). Background job refreshes routine facts weekly for all users. User can manually trigger refresh via API.

2. **How to handle users with irregular schedules?**
   - What we know: Some users (shift workers, freelancers) have no consistent routine
   - What's unclear: Should system skip pattern detection, or detect multi-modal patterns (e.g., "50% morning person, 50% evening person")
   - Recommendation: Detect patterns if consistency score >40%. Below that, return "no consistent pattern detected - your schedule varies significantly." Avoid forcing patterns where none exist.

3. **Should time slot suggestions be interactive (chat-based)?**
   - What we know: Requirements specify triple-choice format (static suggestions), but chat interface exists
   - What's unclear: Whether user should ask "when should I schedule meditation?" and get conversational suggestions
   - Recommendation: Phase 7 implements static triple-choice in CreateHabit response. Phase 8+ can add chat-based scheduling optimization ("Ask AI when to schedule this habit").

4. **Confidence score granularity: percentage or categorical?**
   - What we know: Requirements specify confidence scores, existing research uses both approaches
   - What's unclear: Whether "70% confidence" is more useful than "MEDIUM confidence" for users
   - Recommendation: Return both. Categorical (HIGH/MEDIUM/LOW) for quick scanning, numeric (0.0-1.0) for power users who want details. Display categorical by default, numeric in tooltip/details view.

5. **How many routine patterns to store as UserFacts?**
   - What we know: UserFact has no pagination, too many facts bloat system prompt
   - What's unclear: Store all detected patterns (could be 20+ habits), or only high-confidence ones?
   - Recommendation: Store only HIGH and MEDIUM confidence patterns (consistency >60%). Filter LOW confidence patterns to avoid noise. Cap at 50 most recent routine facts per user.

## Sources

### Primary (HIGH confidence)
- [Gemini API Prompt Design Strategies](https://ai.google.dev/gemini-api/docs/prompting-strategies) - Structured output, reasoning tasks
- [LLM-Powered Time-Series Analysis (Towards Data Science)](https://towardsdatascience.com/llm-powered-time-series-analysis/) - LLM pattern extraction techniques
- [Prompt Engineering for Time-Series Analysis (Towards Data Science)](https://towardsdatascience.com/prompt-engineering-for-time-series-analysis-with-large-language-models/) - Temporal prompting patterns
- [ML.NET TimeSeries Package](https://www.nuget.org/packages/Microsoft.ML.TimeSeries/) - Alternative statistical approach
- [Microsoft Learn: Time Series Forecasting with ML.NET](https://learn.microsoft.com/en-us/dotnet/machine-learning/tutorials/time-series-demand-forecasting) - SSA algorithm details

### Secondary (MEDIUM confidence)
- [AI Scheduling Assistant Best Practices 2026](https://thedigitalprojectmanager.com/tools/ai-scheduling-assistant/) - Conflict detection, time slot recommendations
- [Advanced Conflict Detection Algorithms (myshyft.com)](https://www.myshyft.com/blog/conflict-detection-algorithms/) - Rule-based vs ML approaches
- [Scheduling Conflicts Types & Solutions](https://truein.com/blogs/scheduling-conflicts) - Conflict severity classification
- [AI Recommendation Optimization Guide 2026](https://www.trysight.ai/blog/ai-recommendation-optimization-guide) - Triple-choice format, diversity in suggestions
- [What can machine learning teach us about habit formation? (PNAS)](https://www.pnas.org/doi/10.1073/pnas.2216115120) - Habit pattern research, time lag importance
- [Pattrn: Best Habit Tracker with AI Analytics 2026](https://pattrn.io/blog/the-best-habit-tracker-for-2026-with-ai-analytics-and-charts) - Modern habit tracking features
- [Pattern Recognition in Time Series (Baeldung CS)](https://www.baeldung.com/cs/pattern-recognition-time-series) - Traditional algorithms overview
- [Time Series Clustering Overview (Towards Data Science)](https://towardsdatascience.com/time-series-clustering-deriving-trends-and-archetypes-from-sequential-data-bb87783312b4/) - Clustering approaches

### Tertiary (LOW confidence)
- [WebSearch: Recurrent patterns in time series detection](https://milvus.io/ai-quick-reference/what-are-recurrent-patterns-in-time-series-and-how-are-they-detected) - General concepts, needs domain validation
- [WebSearch: CCE confidence metrics](https://arxiv.org/html/2509.01098v1) - Academic metric, may not apply to habit tracking
- [Pattern Detection for Incident Management (January 2026)](https://oneuptime.com/blog/post/2026-01-30-pattern-detection/view) - Log/metric patterns, different domain but relevant concepts

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH - Gemini 2.5 Flash already integrated, proven for structured output, no new dependencies
- LLM-first approach: MEDIUM-HIGH - Emerging pattern (2025-2026), multiple sources confirm trend, but less battle-tested than statistical methods
- Architecture patterns: MEDIUM - Reuses Phase 5 UserFact infrastructure (HIGH), but routine-specific queries untested (MEDIUM)
- Conflict detection logic: MEDIUM - UX patterns well-established, but LLM-based reasoning less predictable than rule-based systems
- Pitfalls: HIGH - Timezone issues, stale data, token limits are known problems from existing phases

**Research date:** 2026-02-09
**Valid until:** ~45 days (rapidly evolving - LLM time-series techniques advancing quickly)
**Re-verification recommended before:** 2026-03-25

**Notes:**
- No CONTEXT.md exists for this phase - all architectural decisions are recommendations, open for discussion
- LLM-first approach is opinionated but aligns with existing Gemini integration and Phase 5 fact extraction patterns
- Alternative statistical approach (ML.NET) documented but not recommended for MVP - can pivot if LLM accuracy insufficient
- Triple-choice format follows modern AI assistant UX, but exact implementation (static vs interactive) flexible
- Conflict detection as warning (not blocker) is critical for user autonomy - don't enforce, inform
