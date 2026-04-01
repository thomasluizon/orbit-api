using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.AI;

namespace Orbit.Infrastructure.Services;

public sealed class AiRoutineAnalysisService(
    AiCompletionClient aiClient,
    IGenericRepository<User> userRepository,
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<HabitLog> habitLogRepository,
    IMemoryCache cache,
    ILogger<AiRoutineAnalysisService> logger) : IRoutineAnalysisService
{
    private const int MinDaysForPatternDetection = 7;
    private const int AnalysisWindowDays = 60;
    private const int MinLogsPerHabit = 5;

    private static readonly TimeSpan RoutineCacheDuration = TimeSpan.FromHours(1);

    public async Task<Result<RoutineAnalysis>> AnalyzeRoutinesAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = $"routine_analysis_{userId}";
        if (cache.TryGetValue(cacheKey, out RoutineAnalysis? cached) && cached is not null)
        {
            logger.LogInformation("Routine analysis cache hit for user {UserId} ({PatternCount} patterns)",
                userId, cached.Patterns.Count);
            return Result.Success(cached);
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var users = await userRepository.FindAsync(u => u.Id == userId, cancellationToken);
        var user = users.FirstOrDefault();
        if (user is null)
            return Result.Failure<RoutineAnalysis>(Orbit.Application.Common.ErrorMessages.UserNotFound);

        if (string.IsNullOrEmpty(user.TimeZone))
        {
            logger.LogInformation("User {UserId} has no timezone set - returning empty patterns", userId);
            return Result.Success(new RoutineAnalysis { Patterns = [] });
        }

        var timezone = TimeZoneInfo.FindSystemTimeZoneById(user.TimeZone);

        var userHabits = await habitRepository.FindAsync(h => h.UserId == userId, cancellationToken);
        if (!userHabits.Any())
        {
            logger.LogInformation("User {UserId} has no habits - returning empty patterns", userId);
            return Result.Success(new RoutineAnalysis { Patterns = [] });
        }

        var habitIds = userHabits.Select(h => h.Id).ToList();

        var cutoffDate = DateTime.UtcNow.AddDays(-AnalysisWindowDays);
        var allLogs = await habitLogRepository.FindAsync(
            l => l.CreatedAtUtc >= cutoffDate,
            cancellationToken);

        var habitLogs = allLogs.Where(l => habitIds.Contains(l.HabitId)).ToList();

        if (!habitLogs.Any())
        {
            logger.LogInformation("User {UserId} has no habit logs - returning empty patterns", userId);
            return Result.Success(new RoutineAnalysis { Patterns = [] });
        }

        var daysSinceFirstLog = (DateTime.UtcNow - habitLogs.Min(l => l.CreatedAtUtc)).Days;
        if (daysSinceFirstLog < MinDaysForPatternDetection)
        {
            logger.LogInformation("User {UserId} has only {Days} days of data (minimum {MinDays}) - returning empty patterns",
                userId, daysSinceFirstLog, MinDaysForPatternDetection);
            return Result.Success(new RoutineAnalysis { Patterns = [] });
        }

        var habitLogCounts = habitLogs.GroupBy(l => l.HabitId)
            .Where(g => g.Count() >= MinLogsPerHabit)
            .Select(g => g.Key)
            .ToList();

        if (!habitLogCounts.Any())
        {
            logger.LogInformation("User {UserId} has no habits with {MinLogs}+ logs - returning empty patterns",
                userId, MinLogsPerHabit);
            return Result.Success(new RoutineAnalysis { Patterns = [] });
        }

        var filteredLogs = habitLogs.Where(l => habitLogCounts.Contains(l.HabitId)).ToList();

        var logsWithLocalTime = filteredLogs.Select(l => new
        {
            l.HabitId,
            HabitTitle = userHabits.FirstOrDefault(h => h.Id == l.HabitId)?.Title ?? "Unknown",
            l.Date,
            CreatedAtLocal = TimeZoneInfo.ConvertTimeFromUtc(l.CreatedAtUtc, timezone),
            Frequency = userHabits.FirstOrDefault(h => h.Id == l.HabitId)?.FrequencyUnit?.ToString(),
            FrequencyQuantity = userHabits.FirstOrDefault(h => h.Id == l.HabitId)?.FrequencyQuantity
        }).ToList();

        var prompt = $$"""
        Analyze these habit log timestamps and detect recurring time-of-day patterns.

        User timezone: {{user.TimeZone}}
        Current date: {{DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timezone))}}
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
        - Require at least {{MinLogsPerHabit}} logs per habit for pattern detection
        - Consistency score = (actual logs / expected logs based on habit frequency)
        - Confidence = how reliably logs occur at detected time (cluster tightness)
        - Time blocks: round to nearest hour, use 1-hour windows
        - If habit has <{{MinLogsPerHabit}} logs, exclude from patterns
        """;

        logger.LogInformation("Calling AI API for routine analysis (user {UserId}, {LogCount} logs)...", userId, filteredLogs.Count);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

        var result = await CallAiAsync<RoutineAnalysis>(prompt, timeoutCts.Token);

        stopwatch.Stop();
        if (result.IsSuccess)
        {
            cache.Set(cacheKey, result.Value, RoutineCacheDuration);
            logger.LogInformation("Routine analysis completed in {ElapsedMs}ms - detected {PatternCount} patterns (cached for {CacheMinutes}min)",
                stopwatch.ElapsedMilliseconds, result.Value.Patterns.Count, RoutineCacheDuration.TotalMinutes);
        }

        return result;
    }

    public async Task<Result<ConflictWarning?>> DetectConflictsAsync(
        Guid userId,
        FrequencyUnit? frequencyUnit,
        int? frequencyQuantity,
        IReadOnlyList<DayOfWeek>? days,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var patternsResult = await AnalyzeRoutinesAsync(userId, cancellationToken);
        if (patternsResult.IsFailure)
            return Result.Failure<ConflictWarning?>(patternsResult.Error);

        if (patternsResult.Value.Patterns.Count == 0)
        {
            logger.LogInformation("No patterns detected for user {UserId} - no conflict warning", userId);
            return Result.Success<ConflictWarning?>(null);
        }

        var daysStr = days is not null && days.Count > 0
            ? string.Join(", ", days)
            : "not specified";

        var prompt = $$"""
        Detect schedule conflicts between a new habit and existing routine patterns.

        New habit:
        - Frequency: {{frequencyUnit?.ToString() ?? "one-time"}} / {{frequencyQuantity?.ToString() ?? "N/A"}}
        - Days: {{daysStr}}

        Existing routine patterns:
        {{JsonSerializer.Serialize(patternsResult.Value.Patterns, new JsonSerializerOptions { WriteIndented = true })}}

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
        - If no meaningful conflict, return hasConflict: false with empty conflictingHabits array
        - Daily habits naturally overlap with weekly/monthly habits - only flag if time conflict exists
        """;

        logger.LogInformation("Calling AI API for conflict detection (user {UserId})...", userId);

        var result = await CallAiAsync<ConflictWarning>(prompt, cancellationToken);

        stopwatch.Stop();
        if (result.IsSuccess)
        {
            logger.LogInformation("Conflict detection completed in {ElapsedMs}ms - hasConflict: {HasConflict}",
                stopwatch.ElapsedMilliseconds, result.Value.HasConflict);

            if (!result.Value.HasConflict)
                return Result.Success<ConflictWarning?>(null);

            return Result.Success<ConflictWarning?>(result.Value);
        }

        return Result.Failure<ConflictWarning?>(result.Error);
    }

    public async Task<Result<IReadOnlyList<TimeSlotSuggestion>>> SuggestTimeSlotsAsync(
        Guid userId,
        string habitTitle,
        FrequencyUnit? frequencyUnit,
        int? frequencyQuantity,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var patternsResult = await AnalyzeRoutinesAsync(userId, cancellationToken);
        if (patternsResult.IsFailure)
            return Result.Failure<IReadOnlyList<TimeSlotSuggestion>>(patternsResult.Error);

        if (patternsResult.Value.Patterns.Count == 0)
        {
            logger.LogInformation("No patterns detected for user {UserId} - returning generic suggestions", userId);

            var fallbackSuggestions = new List<TimeSlotSuggestion>
            {
                new()
                {
                    Description = "Morning (7-8 AM)",
                    TimeBlocks = new[]
                    {
                        new TimeBlock { DayOfWeek = DayOfWeek.Monday, StartHour = 7, EndHour = 8 },
                        new TimeBlock { DayOfWeek = DayOfWeek.Tuesday, StartHour = 7, EndHour = 8 },
                        new TimeBlock { DayOfWeek = DayOfWeek.Wednesday, StartHour = 7, EndHour = 8 },
                        new TimeBlock { DayOfWeek = DayOfWeek.Thursday, StartHour = 7, EndHour = 8 },
                        new TimeBlock { DayOfWeek = DayOfWeek.Friday, StartHour = 7, EndHour = 8 }
                    },
                    Rationale = "No routine data yet - morning is a common time for building new habits",
                    Score = 0.50m
                },
                new()
                {
                    Description = "Afternoon (12-1 PM)",
                    TimeBlocks = new[]
                    {
                        new TimeBlock { DayOfWeek = DayOfWeek.Monday, StartHour = 12, EndHour = 13 },
                        new TimeBlock { DayOfWeek = DayOfWeek.Tuesday, StartHour = 12, EndHour = 13 },
                        new TimeBlock { DayOfWeek = DayOfWeek.Wednesday, StartHour = 12, EndHour = 13 },
                        new TimeBlock { DayOfWeek = DayOfWeek.Thursday, StartHour = 12, EndHour = 13 },
                        new TimeBlock { DayOfWeek = DayOfWeek.Friday, StartHour = 12, EndHour = 13 }
                    },
                    Rationale = "No routine data yet - midday provides a consistent schedule anchor",
                    Score = 0.40m
                },
                new()
                {
                    Description = "Evening (6-7 PM)",
                    TimeBlocks = new[]
                    {
                        new TimeBlock { DayOfWeek = DayOfWeek.Monday, StartHour = 18, EndHour = 19 },
                        new TimeBlock { DayOfWeek = DayOfWeek.Tuesday, StartHour = 18, EndHour = 19 },
                        new TimeBlock { DayOfWeek = DayOfWeek.Wednesday, StartHour = 18, EndHour = 19 },
                        new TimeBlock { DayOfWeek = DayOfWeek.Thursday, StartHour = 18, EndHour = 19 },
                        new TimeBlock { DayOfWeek = DayOfWeek.Friday, StartHour = 18, EndHour = 19 }
                    },
                    Rationale = "No routine data yet - evening allows for post-work activities",
                    Score = 0.35m
                }
            };

            return Result.Success<IReadOnlyList<TimeSlotSuggestion>>(fallbackSuggestions);
        }

        var prompt = $$"""
        Suggest 3 optimal time slots for a new habit based on routine gaps.

        New habit:
        - Title: {{habitTitle}}
        - Frequency: {{frequencyUnit?.ToString() ?? "one-time"}} / {{frequencyQuantity?.ToString() ?? "N/A"}}

        Existing routine patterns:
        {{JsonSerializer.Serialize(patternsResult.Value.Patterns, new JsonSerializerOptions { WriteIndented = true })}}

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
        - Ensure suggestions differ meaningfully (different days OR different times) - avoid 3 variations of same slot
        """;

        logger.LogInformation("Calling AI API for time slot suggestions (user {UserId}, habit: {HabitTitle})...",
            userId, habitTitle);

        var result = await CallAiAsync<TimeSlotSuggestionsWrapper>(prompt, cancellationToken);

        stopwatch.Stop();
        if (result.IsSuccess)
        {
            var suggestions = result.Value.Suggestions;

            if (suggestions.Count != 3)
                logger.LogWarning("AI returned {Count} suggestions instead of 3 - using as-is", suggestions.Count);

            var sortedSuggestions = suggestions.OrderByDescending(s => s.Score).ToList();

            logger.LogInformation("Time slot suggestions completed in {ElapsedMs}ms - {Count} suggestions",
                stopwatch.ElapsedMilliseconds, sortedSuggestions.Count);

            return Result.Success<IReadOnlyList<TimeSlotSuggestion>>(sortedSuggestions);
        }

        return Result.Failure<IReadOnlyList<TimeSlotSuggestion>>(result.Error);
    }

    private async Task<Result<T>> CallAiAsync<T>(string prompt, CancellationToken cancellationToken)
    {
        try
        {
            var result = await aiClient.CompleteJsonAsync<T>(prompt, temperature: 0.1, cancellationToken);

            if (result is null)
                return Result.Failure<T>("AI returned empty response");

            return Result.Success(result);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Failed to deserialize AI response");
            return Result.Failure<T>($"JSON deserialization error: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Routine analysis failed");
            return Result.Failure<T>($"Routine analysis error: {ex.Message}");
        }
    }

    private sealed record TimeSlotSuggestionsWrapper
    {
        [JsonPropertyName("suggestions")]
        public IReadOnlyList<TimeSlotSuggestion> Suggestions { get; init; } = [];
    }
}
