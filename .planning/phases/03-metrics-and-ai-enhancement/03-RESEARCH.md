# Phase 3: Metrics and AI Enhancement - Research

**Researched:** 2026-02-08
**Domain:** Habit metrics calculation, timezone-aware streak tracking, AI enhancement patterns
**Confidence:** HIGH

## Summary

Phase 3 adds metrics capabilities (streaks, completion rates, trends) and enhances AI to handle sub-habits, tags, and graceful refusal. The domain splits into two distinct areas: **metrics calculation** (complex frequency-aware algorithms with timezone handling) and **AI enhancement** (expanding action types in existing prompt system).

**Key insight:** Streak calculation for flexible frequencies (daily, every 2 weeks, monthly, specific days) requires sophisticated date logic that accounts for user timezone. Direct calculation from logs (not cached values) ensures accuracy but needs optimization via database indexes and efficient LINQ queries.

**Primary recommendation:** Implement metrics as query handlers that calculate on-demand from HabitLog entities. Extend AiActionType enum with CreateSubHabit and AssignTag actions. Update SystemPromptBuilder to teach AI about these new capabilities and include explicit refusal patterns.

## Standard Stack

### Core (Existing)
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| Entity Framework Core | 10.0.0 | Database queries for metrics | Already in use, excellent LINQ support for aggregations |
| MediatR | 14.0.0 | Query handlers for metrics endpoints | Existing CQRS pattern, new queries fit naturally |
| TimeZoneInfo | .NET BCL | Timezone conversions | Built-in .NET, IANA timezone support via FindSystemTimeZoneById |
| LINQ | .NET BCL | Date aggregations, streak logic | Native support for GroupBy, aggregations, complex predicates |

### Supporting
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| System.Text.Json | .NET BCL | Extend AiActionPlan serialization | Already used for Gemini responses, add new enum values |
| DateOnly | .NET 6+ | Date-only calculations | Used for HabitLog.Date, no timezone component |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Real-time calculation | Cached metrics in DB | Cache = faster reads but consistency issues, complex invalidation. Real-time = accurate, simpler, good enough with indexes |
| Client-side streak logic | Server-side calculation | Server-side ensures consistency, timezone handling, avoids exposing business rules to client |
| DateTime/DateTimeOffset | DateOnly for dates | DateOnly is correct type for calendar dates without time component (HabitLog.Date) |
| JSON mode schema validation | Prompt engineering only | Gemini 2.5 Flash supports JSON schema but adds complexity. Current prompt + PropertyNameCaseInsensitive works reliably |

**Installation:**
No new packages required. All functionality uses existing dependencies.

## Architecture Patterns

### Recommended Project Structure
```
src/Orbit.Application/Habits/Queries/
├── GetHabitMetricsQuery.cs       # Single habit metrics (streaks, rates)
├── GetHabitTrendQuery.cs          # Quantifiable habit trend analysis

src/Orbit.Domain/Models/
├── HabitMetrics.cs                # DTO: streaks, completion rates
├── HabitTrend.cs                  # DTO: time series data for trends

src/Orbit.Domain/Enums/
├── AiActionType.cs                # Add: CreateSubHabit, AssignTag, SuggestTags
```

### Pattern 1: Frequency-Aware Streak Calculation
**What:** Calculate current/longest streak by determining "expected dates" based on habit frequency, then checking for log entries on those dates
**When to use:** For any habit with FrequencyUnit + FrequencyQuantity + optional Days

**Algorithm:**
```csharp
// Source: Research synthesis - frequency-aware streak algorithms
// Reference: https://javascript.plainenglish.io/stop-rebuilding-habit-apps-use-this-production-ready-starter-daf2b88a3039

// 1. Convert current UTC time to user's timezone
var userTimeZone = TimeZoneInfo.FindSystemTimeZoneById(user.TimeZone ?? "UTC");
var userNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, userTimeZone);
var today = DateOnly.FromDateTime(userNow);

// 2. Load habit logs (already DateOnly, no conversion needed)
var logs = habit.Logs.OrderByDescending(l => l.Date).ToList();

// 3. Generate "expected dates" backwards from today based on frequency
var expectedDates = new List<DateOnly>();
var currentDate = today;
int periodsToCheck = 100; // Check up to 100 periods back

for (int i = 0; i < periodsToCheck; i++)
{
    // For FrequencyUnit=Day, FrequencyQuantity=1, Days=[Monday,Wednesday,Friday]
    // expectedDates = [today if Mon/Wed/Fri, yesterday if Mon/Wed/Fri, ...]

    if (habit.Days.Count > 0 && habit.FrequencyQuantity == 1)
    {
        // Specific days mode: check if currentDate matches any allowed day
        if (habit.Days.Contains(currentDate.DayOfWeek))
            expectedDates.Add(currentDate);

        currentDate = currentDate.AddDays(-1);
    }
    else
    {
        // Every N periods mode
        expectedDates.Add(currentDate);

        currentDate = habit.FrequencyUnit switch
        {
            FrequencyUnit.Day => currentDate.AddDays(-habit.FrequencyQuantity),
            FrequencyUnit.Week => currentDate.AddDays(-7 * habit.FrequencyQuantity),
            FrequencyUnit.Month => currentDate.AddMonths(-habit.FrequencyQuantity),
            FrequencyUnit.Year => currentDate.AddYears(-habit.FrequencyQuantity),
            _ => currentDate.AddDays(-1)
        };
    }
}

// 4. Calculate streak: count consecutive expected dates with logs
int currentStreak = 0;
var logDates = new HashSet<DateOnly>(logs.Select(l => l.Date));

foreach (var expectedDate in expectedDates)
{
    if (logDates.Contains(expectedDate))
        currentStreak++;
    else
        break; // Streak broken
}

// 5. Find longest streak (similar logic scanning all logs)
```

**Key considerations:**
- DateOnly (HabitLog.Date) has no timezone - it's calendar date in user's context
- User.TimeZone converts UTC "now" to user's local "today" for streak calculation
- Days feature (Mon/Wed/Fri) only valid when FrequencyQuantity=1 (existing domain rule)
- Negative habits allow multiple logs per day - use distinct dates for streak counting

### Pattern 2: Completion Rate Calculation
**What:** Calculate percentage of expected completions within a time range (weekly, monthly)
**When to use:** For dashboard metrics, progress visualization

```csharp
// Source: Research synthesis - completion rate patterns
// Reference: https://habitify.me/onboarding-instruction/progress

public decimal CalculateCompletionRate(
    Habit habit,
    DateOnly startDate,
    DateOnly endDate,
    HashSet<DateOnly> logDates)
{
    // 1. Generate all expected dates in range
    var expectedDates = GenerateExpectedDatesInRange(habit, startDate, endDate);

    // 2. Count how many expected dates have logs
    int completed = expectedDates.Count(date => logDates.Contains(date));

    // 3. Return percentage
    return expectedDates.Count > 0
        ? (decimal)completed / expectedDates.Count * 100
        : 0;
}

// For quantifiable habits: average value over time instead of completion %
public decimal CalculateAverageValue(List<HabitLog> logs)
{
    return logs.Count > 0 ? logs.Average(l => l.Value) : 0;
}
```

### Pattern 3: Trend Analysis for Quantifiable Habits
**What:** Group quantifiable habit logs by time period (week/month), calculate aggregates (avg, min, max)
**When to use:** Quantifiable habits (Type=Quantifiable) with sufficient log history

```csharp
// Source: LINQ grouping patterns
// Reference: https://atashbahar.com/post/2017-04-27-group-by-day-week-month-quarter-and-year-in-entity-framework

// Group by week - return time series data
var trendData = logs
    .GroupBy(l => new {
        Year = l.Date.Year,
        Week = CultureInfo.CurrentCulture.Calendar.GetWeekOfYear(
            l.Date.ToDateTime(TimeOnly.MinValue),
            CalendarWeekRule.FirstDay,
            DayOfWeek.Monday)
    })
    .Select(g => new TrendPoint
    {
        Period = $"{g.Key.Year}-W{g.Key.Week:00}",
        Average = g.Average(l => l.Value),
        Minimum = g.Min(l => l.Value),
        Maximum = g.Max(l => l.Value),
        Count = g.Count()
    })
    .OrderBy(t => t.Period)
    .ToList();

// Group by month - simpler, more stable
var monthlyTrend = logs
    .GroupBy(l => new { l.Date.Year, l.Date.Month })
    .Select(g => new TrendPoint
    {
        Period = $"{g.Key.Year}-{g.Key.Month:00}",
        Average = g.Average(l => l.Value),
        Minimum = g.Min(l => l.Value),
        Maximum = g.Max(l => l.Value),
        Count = g.Count()
    })
    .OrderBy(t => t.Period)
    .ToList();
```

### Pattern 4: AI Action Expansion
**What:** Add new action types to AiActionType enum and handlers in ProcessUserChatCommandHandler
**When to use:** Adding new AI capabilities (sub-habits, tags)

**Current flow:**
```
User message → InterpretAsync (Gemini) → AiActionPlan → ProcessUserChatCommand → Execute actions
```

**To add CreateSubHabit action:**
```csharp
// 1. Update enum
public enum AiActionType
{
    LogHabit,
    CreateHabit,
    CreateSubHabit,  // NEW
    AssignTag,       // NEW
    SuggestTags      // NEW
}

// 2. Update AiAction record (already has optional fields)
public record AiAction
{
    // ... existing fields ...
    public List<string>? SubHabits { get; init; }  // NEW: ["meditate", "journal", "stretch"]
    public List<string>? TagNames { get; init; }   // NEW: ["morning", "wellness"]
    public List<Guid>? TagIds { get; init; }       // NEW: for AssignTag
}

// 3. Add handler case
var actionResult = action.Type switch
{
    AiActionType.LogHabit => await ExecuteLogHabitAsync(action, request.UserId, cancellationToken),
    AiActionType.CreateHabit => await ExecuteCreateHabitAsync(action, request.UserId, cancellationToken),
    AiActionType.CreateSubHabit => await ExecuteCreateSubHabitAsync(action, request.UserId, cancellationToken),
    AiActionType.AssignTag => await ExecuteAssignTagAsync(action, request.UserId, cancellationToken),
    AiActionType.SuggestTags => Result.Success(), // Information only, no DB action
    _ => Result.Failure($"Unknown action type: {action.Type}")
};

// 4. Implement handler
private async Task<Result> ExecuteCreateSubHabitAsync(
    AiAction action, Guid userId, CancellationToken ct)
{
    if (action.HabitId is null || action.SubHabits is null || action.SubHabits.Count == 0)
        return Result.Failure("HabitId and SubHabits array required.");

    var habit = await habitRepository.FindOneTrackedAsync(
        h => h.Id == action.HabitId && h.UserId == userId,
        cancellationToken: ct);

    if (habit is null)
        return Result.Failure("Habit not found.");

    int sortOrder = habit.SubHabits.Count;
    foreach (var subHabitTitle in action.SubHabits)
    {
        var result = habit.AddSubHabit(subHabitTitle, sortOrder++);
        if (result.IsFailure)
            return result;
    }

    habitRepository.Update(habit); // Triggers EF change tracking
    return Result.Success();
}
```

### Pattern 5: AI Graceful Refusal
**What:** Teach AI to return empty actions array with helpful message for out-of-scope requests
**When to use:** User asks non-habit-related questions, one-time tasks, etc.

**System prompt pattern:**
```csharp
// Source: LLM safety and prompt engineering patterns
// Reference: https://www.lakera.ai/blog/prompt-engineering-guide

sb.AppendLine("""
### What You CANNOT Do:
- Answer general questions (trivia, facts, explanations)
- Help with homework, work assignments, or academic problems
- Provide advice, recommendations, or opinions unrelated to habits
- Have conversations unrelated to habit management
- Manage one-time tasks or to-do items (I only handle recurring habits)

### When Users Ask Out-of-Scope Questions:
Return an empty actions array and a polite message. Examples:

User: "What's the capital of France?"
{
  "actions": [],
  "aiMessage": "I'm Orbit AI - I only help with habits. For general questions, try a general-purpose assistant!"
}

User: "I need to buy milk today"
{
  "actions": [],
  "aiMessage": "That sounds like a one-time task. I focus on recurring habits. If you'd like to create a 'Weekly grocery shopping' habit, I can help!"
}
""");
```

**Key principle:** Offer graceful exit strategy - acknowledge request, explain limitation, suggest alternative if applicable.

### Anti-Patterns to Avoid

- **Calculating metrics in Domain entities:** Metrics are queries, not entity behavior. Keep calculation logic in Application/Queries, not in Habit class
- **Caching streaks in database:** Adds complexity (invalidation, consistency), premature optimization. Calculate on-demand with proper indexes
- **Client-side timezone conversion:** Server must determine "today" using User.TimeZone to ensure consistency across devices
- **Ignoring Days feature in streak logic:** Days=[Monday, Wednesday, Friday] means only those days count - don't calculate daily streaks
- **Using DateTime for date comparisons:** HabitLog.Date is DateOnly - no time component, no timezone. Don't convert to DateTime for date math
- **Making AI action types too granular:** Don't create UpdateHabitTags, RemoveTags, etc. - use AssignTag/RemoveTag, let handler figure out if tag exists

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Timezone database | IANA timezone data files | TimeZoneInfo.FindSystemTimeZoneById | Cross-platform, maintained by .NET, handles DST automatically |
| Date arithmetic for frequencies | Custom date math functions | DateOnly.AddDays/AddMonths/AddYears | Built-in, tested, handles edge cases (leap years, month boundaries) |
| Moving average calculations | Manual window calculation | LINQ with Skip/Take + Average | Less error-prone, readable, optimized by runtime |
| ISO week number | Calendar week calculation | CultureInfo.CurrentCulture.Calendar.GetWeekOfYear | Handles different week numbering systems (ISO 8601, US, etc.) |
| JSON schema validation for AI | Custom JSON validation | Gemini's responseMimeType + prompt engineering | Gemini 2.5 Flash JSON mode is reliable, schema validation adds complexity without benefit for simple structures |
| Consecutive date grouping | Custom grouping algorithm | LINQ GroupBy with date difference logic | Community-tested patterns exist, avoid off-by-one errors |

**Key insight:** Date/time logic is deceptively complex (DST, leap years, week boundaries, month-end edge cases). Use BCL functions aggressively. Custom implementations inevitably have bugs.

## Common Pitfalls

### Pitfall 1: Ignoring User Timezone for "Today"
**What goes wrong:** Streak calculation uses UTC "today" instead of user's local "today", breaking streaks incorrectly
**Why it happens:** HabitLog.Date is DateOnly (no timezone), easy to assume UTC-only system
**How to avoid:**
- Always get User.TimeZone from database
- Convert DateTime.UtcNow to user timezone: `TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, userTimeZone)`
- Extract DateOnly from converted DateTime: `DateOnly.FromDateTime(userNow)`
- This is user's local "today" for streak calculations
**Warning signs:** User in Asia reports broken streak when logging at 11pm local time (UTC tomorrow), or user in US sees streak break when logging at 1am local time (still today for them)

**Example:**
```csharp
// WRONG - ignores user timezone
var today = DateOnly.FromDateTime(DateTime.UtcNow);

// CORRECT - uses user timezone
var userTimeZone = TimeZoneInfo.FindSystemTimeZoneById(user.TimeZone ?? "UTC");
var userNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, userTimeZone);
var today = DateOnly.FromDateTime(userNow);
```

### Pitfall 2: Not Accounting for Days Feature in Streaks
**What goes wrong:** Habit with Days=[Monday, Friday] shows broken streak on Tuesday (expected!)
**Why it happens:** Days feature restricts which days count - only FrequencyQuantity=1 allows Days
**How to avoid:**
- Check if habit.Days.Count > 0 before generating expected dates
- When Days is populated, only include dates matching those DayOfWeek values
- Don't treat as daily habit - treat as "specific days only" habit
**Warning signs:** User reports "I have a Mon/Wed/Fri habit but my streak is 0" - code is checking every day instead of M/W/F

### Pitfall 3: Multiple Logs Per Day for Boolean Habits
**What goes wrong:** Streak calculation counts duplicate logs for same date, inflating streak
**Why it happens:** Negative habits allow multiple logs per day (slip-up tracking)
**How to avoid:**
- Use `logs.Select(l => l.Date).Distinct()` or `HashSet<DateOnly>` for streak calculations
- Streak is about consecutive *dates*, not consecutive *logs*
**Warning signs:** Negative habit shows impossibly long streak (365 days from 100 logs)

### Pitfall 4: Gemini JSON Schema Complexity Limits
**What goes wrong:** Complex nested schemas cause InvalidArgument 400 errors from Gemini API
**Why it happens:** Gemini has practical limits on schema depth, array constraints, property count
**How to avoid:**
- Keep JSON schemas simple - favor flat structures over deep nesting
- Use prompt engineering for validation instead of JSON schema constraints
- Current approach (PropertyNameCaseInsensitive + JsonStringEnumConverter) works well
- If adding JSON schema, test with complex examples early
**Warning signs:** Gemini API returns 400 InvalidArgument errors with large/complex AiActionPlan schemas

### Pitfall 5: Calculating Metrics Without Database Indexes
**What goes wrong:** Metrics queries are slow, timeout on large log collections
**Why it happens:** Calculating streaks requires iterating through logs, filtering by date ranges
**How to avoid:**
- Existing index on (HabitId, Date) in HabitLogs table is critical
- EF Core translates LINQ efficiently when proper indexes exist
- For 10k+ logs, consider fetching last N logs instead of all logs: `habit.Logs.OrderByDescending(l => l.Date).Take(1000)`
**Warning signs:** GET /api/habits/{id}/metrics takes >2 seconds with moderate data

### Pitfall 6: AI Refusing Valid Habit Requests
**What goes wrong:** AI returns empty actions array for valid habit creation requests
**Why it happens:** Overly broad refusal patterns in system prompt, ambiguous wording
**How to avoid:**
- Test refusal patterns with borderline cases ("track when I call my mom" - recurring or one-time?)
- Use specific keywords for refusal ("general questions", "homework", "one-time task")
- Include positive examples in prompt showing what IS in scope
- Log AI responses during testing to catch false refusals
**Warning signs:** User reports "AI won't create my habit" for obviously habit-related requests

### Pitfall 7: Forgetting EF Change Tracking for Sub-Habits
**What goes wrong:** Sub-habits created in handler don't persist to database
**Why it happens:** `habit.AddSubHabit()` modifies tracked entity, but forgot to call `habitRepository.Update(habit)`
**How to avoid:**
- After modifying tracked entity (habit loaded via FindOneTrackedAsync), call `Update()` to ensure EF detects changes
- Alternatively, rely on EF auto-detection if entity already tracked
- Existing pattern: FindOneTrackedAsync + Update after domain method
**Warning signs:** AI says "Created sub-habits!" but they don't appear in database

## Code Examples

### Timezone-Aware "Today" Calculation
```csharp
// Source: .NET TimeZoneInfo documentation
// URL: https://learn.microsoft.com/en-us/dotnet/api/system.timezoneinfo.converttimefromutc

public DateOnly GetUserToday(User user)
{
    var timeZoneId = user.TimeZone ?? "UTC";
    var timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
    var userNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZone);
    return DateOnly.FromDateTime(userNow);
}
```

### Query Handler for Metrics
```csharp
// Source: Existing GetHabitsQuery pattern + research synthesis
public record GetHabitMetricsQuery(Guid UserId, Guid HabitId) : IRequest<Result<HabitMetrics>>;

public class GetHabitMetricsQueryHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<User> userRepository)
    : IRequestHandler<GetHabitMetricsQuery, Result<HabitMetrics>>
{
    public async Task<Result<HabitMetrics>> Handle(
        GetHabitMetricsQuery request,
        CancellationToken cancellationToken)
    {
        // 1. Load habit with logs
        var habit = await habitRepository.FindAsync(
            h => h.Id == request.HabitId && h.UserId == request.UserId && h.IsActive,
            q => q.Include(h => h.Logs),
            cancellationToken);

        if (habit.Count == 0)
            return Result.Failure<HabitMetrics>("Habit not found.");

        var habitEntity = habit[0];

        // 2. Load user for timezone
        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return Result.Failure<HabitMetrics>("User not found.");

        // 3. Calculate today in user timezone
        var today = GetUserToday(user);

        // 4. Calculate streaks
        var (currentStreak, longestStreak) = CalculateStreaks(habitEntity, today);

        // 5. Calculate completion rates
        var weekStart = today.AddDays(-7);
        var monthStart = today.AddMonths(-1);

        var weeklyRate = CalculateCompletionRate(habitEntity, weekStart, today);
        var monthlyRate = CalculateCompletionRate(habitEntity, monthStart, today);

        return Result.Success(new HabitMetrics
        {
            CurrentStreak = currentStreak,
            LongestStreak = longestStreak,
            WeeklyCompletionRate = weeklyRate,
            MonthlyCompletionRate = monthlyRate
        });
    }
}
```

### Updating SystemPromptBuilder for Sub-Habits
```csharp
// Source: Existing SystemPromptBuilder + AI context management research
// Reference: https://bytebridge.medium.com/ai-agents-context-management-breakthroughs-and-long-running-task-execution-d5cee32aeaa4

// Add to system prompt (around line 27):
sb.AppendLine("""
- Create habits with sub-habits/checklists (e.g., "morning routine: meditate, journal, stretch")
- Suggest relevant tags when creating habits
- Assign existing tags to habits
""");

// Add to examples section:
sb.AppendLine("""
User: "Create morning routine with meditate, journal, and stretch"
{
  "actions": [
    {
      "type": "CreateHabit",
      "title": "Morning Routine",
      "habitType": "Boolean",
      "frequencyUnit": "Day",
      "frequencyQuantity": 1,
      "subHabits": ["Meditate", "Journal", "Stretch"]
    },
    {
      "type": "SuggestTags",
      "tagNames": ["morning", "wellness"]
    }
  ],
  "aiMessage": "Created your morning routine with 3 sub-habits! I suggest tags: morning, wellness"
}

User: "Add wellness tag to my meditation habit" (Meditation habit ID: "abc...", wellness tag ID: "def...")
{
  "actions": [
    {
      "type": "AssignTag",
      "habitId": "abc-123-def-456",
      "tagIds": ["def-789-ghi-012"]
    }
  ],
  "aiMessage": "Added 'wellness' tag to your meditation habit!"
}
""");
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Global streak (ignores frequency) | Frequency-aware streaks | 2024-2026 | Habit apps now correctly handle "every 2 weeks" and "Mon/Wed/Fri" patterns instead of treating everything as daily |
| UTC-only date tracking | User timezone awareness | 2024-2025 | Prevents incorrect streak breaks due to timezone mismatches |
| Simple chatbots (scripted flows) | Agentic AI with multi-step planning | 2025-2026 | AI agents can break down complex requests ("create morning routine with X, Y, Z") into multiple coordinated actions |
| DateTime for dates | DateOnly for calendar dates | .NET 6 (2021) | Type system enforces date-only semantics, prevents time-of-day bugs |
| JSON mode without schema | JSON mode with schema validation | Gemini 1.5 (2024) | Structured output support, but practical complexity limits exist |
| Cached metrics | Real-time calculation with indexes | Ongoing | Modern databases + indexes make real-time calculation viable, reduces cache invalidation complexity |

**Deprecated/outdated:**
- **DateTime for HabitLog dates**: Use DateOnly - date without time component
- **Client-side timezone handling**: Server must control "today" calculation for consistency
- **Global daily streak logic**: Must account for FrequencyUnit, FrequencyQuantity, Days
- **Single-action AI responses**: 2026 pattern supports multi-action plans (create habit + add sub-habits + suggest tags in one response)

## Open Questions

1. **Trend analysis time range limits**
   - What we know: LINQ grouping by week/month works well
   - What's unclear: Should we limit trend queries to last 12 months? 24 months? All time?
   - Recommendation: Start with "last 12 months" default, add query parameter for custom range. Most users won't have >12 months of data in early adoption

2. **Moving average calculations**
   - What we know: Can calculate 7-day, 30-day moving averages with LINQ windowing
   - What's unclear: Does user need moving averages or simple weekly/monthly aggregates?
   - Recommendation: Start with simple aggregates (weekly avg, monthly avg). Add moving averages if users request "smoothed trend lines"

3. **AI tag suggestion confidence threshold**
   - What we know: AI can suggest tags based on habit title/description
   - What's unclear: Should AI auto-assign suggested tags or only suggest? What's the UX?
   - Recommendation: Use SuggestTags action that returns tag names in aiMessage. User decides whether to accept. Don't auto-assign without user confirmation

4. **Sub-habit creation limits**
   - What we know: AI can parse "morning routine with X, Y, Z" into sub-habits array
   - What's unclear: Should we limit sub-habits per habit? 10? 20?
   - Recommendation: Set reasonable limit (e.g., 20 sub-habits) in domain validation. Prevents AI from creating absurdly long checklists

5. **Negative habit metrics interpretation**
   - What we know: Negative habits allow multiple logs per day (slip-ups)
   - What's unclear: For negative habits, is "streak" helpful? Or should we show "days without slip-up"?
   - Recommendation: Invert streak logic for negative habits - current streak = consecutive days WITHOUT logs. Longest streak = longest period without slip-ups. This makes sense to users ("7-day smoke-free streak")

## Sources

### Primary (HIGH confidence)
- Microsoft Learn - .NET DateTime/TimeZoneInfo: https://learn.microsoft.com/en-us/dotnet/standard/datetime/
- Microsoft Learn - TimeZoneInfo.ConvertTimeFromUtc: https://learn.microsoft.com/en-us/dotnet/api/system.timezoneinfo.converttimefromutc
- Microsoft Learn - DateOnly: https://learn.microsoft.com/en-us/dotnet/standard/datetime/how-to-use-dateonly-timeonly
- Microsoft Learn - EF Core Performance: https://learn.microsoft.com/en-us/ef/core/performance/
- Google Cloud - Gemini Structured Output: https://docs.cloud.google.com/vertex-ai/generative-ai/docs/multimodal/control-generated-output
- Google AI - Gemini Structured Output Docs: https://ai.google.dev/gemini-api/docs/structured-output

### Secondary (MEDIUM confidence)
- Lakera - Prompt Engineering Guide 2026: https://www.lakera.ai/blog/prompt-engineering-guide
- ByteBridge - AI Agents Context Management: https://bytebridge.medium.com/ai-agents-context-management-breakthroughs-and-long-running-task-execution-d5cee32aeaa4
- DEV Community - LLM Prompt Challenges: https://latitude-blog.ghost.io/blog/common-llm-prompt-engineering-challenges-and-solutions/
- Medium - LLM Safety Refusals: https://medium.com/@ahmadalismail/llm-safety-essentials-refusals-and-prompt-injections-3ebaaa05c244
- Habitify - Progress Tracking Docs: https://habitify.me/onboarding-instruction/progress
- Code Maze - ASP.NET Core Best Practices: https://code-maze.com/aspnetcore-webapi-best-practices/
- DevIQ - REPR Pattern: https://deviq.com/design-patterns/repr-design-pattern/
- Khaled Atashbahar - EF Group By Date: https://atashbahar.com/post/2017-04-27-group-by-day-week-month-quarter-and-year-in-entity-framework

### Tertiary (LOW confidence - market context only)
- Plain English - Production Habit App Starter: https://javascript.plainenglish.io/stop-rebuilding-habit-apps-use-this-production-ready-starter-daf2b88a3039
- Pattrn - Habit Tracker with AI/Analytics: https://pattrn.io/blog/the-best-habit-tracker-for-2026-with-ai-analytics-and-charts
- Zapier - Best Habit Tracker Apps: https://zapier.com/blog/best-habit-tracker-app/

## Metadata

**Confidence breakdown:**
- Timezone handling: HIGH - Official .NET docs, existing User.TimeZone field validates approach
- Streak calculation: MEDIUM - Algorithm pattern clear, but frequency-aware logic needs careful testing (edge cases: month boundaries, leap years, DST transitions)
- Completion rate calculation: HIGH - Simple expected-vs-actual counting, well-understood pattern
- Trend analysis: MEDIUM - LINQ grouping patterns established, but UI/UX needs refinement (time ranges, aggregation levels)
- AI enhancement (sub-habits/tags): HIGH - Direct extension of existing AiAction pattern, JSON mode supports nested arrays
- AI graceful refusal: HIGH - Prompt engineering pattern well-documented, existing system prompt structure supports expansion
- Performance (indexes): HIGH - Existing indexes on (HabitId, Date) support metrics queries efficiently

**Research date:** 2026-02-08
**Valid until:** ~30 days (2026-03-08) - stable domain, but AI capabilities evolving rapidly

**Critical success factors:**
1. ✅ Timezone-aware "today" calculation using User.TimeZone
2. ✅ Frequency-aware streak logic (Days feature, FrequencyUnit/Quantity)
3. ✅ Distinct date counting for negative habits (multiple logs per day)
4. ✅ Database indexes for efficient metrics queries
5. ✅ AI prompt patterns for graceful refusal
6. ✅ Multi-action support in AI responses (create habit + sub-habits + tags)

**Next steps for planner:**
- Create PLAN.md files for metrics endpoints (METR-01, METR-02, METR-03)
- Create PLAN.md files for AI enhancements (AI-01, AI-02, AI-03)
- Define DTOs (HabitMetrics, HabitTrend) in Domain/Models
- Define query handlers in Application/Habits/Queries
- Update AiActionType enum and ProcessUserChatCommand handler
- Expand SystemPromptBuilder with sub-habit and tag examples
