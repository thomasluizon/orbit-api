using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Services;

public sealed class GeminiRoutineAnalysisService(
    HttpClient httpClient,
    IOptions<GeminiSettings> options,
    IGenericRepository<User> userRepository,
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<HabitLog> habitLogRepository,
    ILogger<GeminiRoutineAnalysisService> logger) : IRoutineAnalysisService
{
    private readonly GeminiSettings _settings = options.Value;

    private const int MinDaysForPatternDetection = 7;
    private const int AnalysisWindowDays = 60;
    private const int MinLogsPerHabit = 5;

    private static readonly JsonSerializerOptions RoutineJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<Result<RoutineAnalysis>> AnalyzeRoutinesAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // 1. Load user for timezone
        var users = await userRepository.FindAsync(u => u.Id == userId, cancellationToken);
        var user = users.FirstOrDefault();
        if (user is null)
            return Result.Failure<RoutineAnalysis>("User not found");

        if (string.IsNullOrEmpty(user.TimeZone))
        {
            logger.LogInformation("User {UserId} has no timezone set - returning empty patterns", userId);
            return Result.Success(new RoutineAnalysis { Patterns = [] });
        }

        var timezone = TimeZoneInfo.FindSystemTimeZoneById(user.TimeZone);

        // 2. Load user's habits first (HabitLog has no UserId, need to filter via habits)
        var userHabits = await habitRepository.FindAsync(h => h.UserId == userId, cancellationToken);
        if (!userHabits.Any())
        {
            logger.LogInformation("User {UserId} has no habits - returning empty patterns", userId);
            return Result.Success(new RoutineAnalysis { Patterns = [] });
        }

        var habitIds = userHabits.Select(h => h.Id).ToList();

        // 3. Load recent habit logs (last 60 days) for user's habits
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

        // 4. Check minimum data requirement
        var daysSinceFirstLog = (DateTime.UtcNow - habitLogs.Min(l => l.CreatedAtUtc)).Days;
        if (daysSinceFirstLog < MinDaysForPatternDetection)
        {
            logger.LogInformation("User {UserId} has only {Days} days of data (minimum {MinDays}) - returning empty patterns",
                userId, daysSinceFirstLog, MinDaysForPatternDetection);
            return Result.Success(new RoutineAnalysis { Patterns = [] });
        }

        // 5. Filter habits with fewer than MinLogsPerHabit logs
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

        // 6. Convert logs to local time and prepare for prompt
        var logsWithLocalTime = filteredLogs.Select(l => new
        {
            l.HabitId,
            HabitTitle = userHabits.FirstOrDefault(h => h.Id == l.HabitId)?.Title ?? "Unknown",
            l.Date,
            CreatedAtLocal = TimeZoneInfo.ConvertTimeFromUtc(l.CreatedAtUtc, timezone),
            Frequency = userHabits.FirstOrDefault(h => h.Id == l.HabitId)?.FrequencyUnit?.ToString(),
            FrequencyQuantity = userHabits.FirstOrDefault(h => h.Id == l.HabitId)?.FrequencyQuantity
        }).ToList();

        // 7. Build Gemini prompt
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
        - Require at least {{MinLogsPerHabit}} logs per habit for pattern detection
        - Consistency score = (actual logs / expected logs based on habit frequency)
        - Confidence = how reliably logs occur at detected time (cluster tightness)
        - Time blocks: round to nearest hour, use 1-hour windows
        - If habit has <{{MinLogsPerHabit}} logs, exclude from patterns
        """;

        // 8. Call Gemini API
        logger.LogInformation("🔵 Calling Gemini API for routine analysis (user {UserId}, {LogCount} logs)...", userId, filteredLogs.Count);

        var result = await CallGeminiAsync<RoutineAnalysis>(prompt, cancellationToken);

        stopwatch.Stop();
        if (result.IsSuccess)
        {
            logger.LogInformation("✅ Routine analysis completed in {ElapsedMs}ms - detected {PatternCount} patterns",
                stopwatch.ElapsedMilliseconds, result.Value.Patterns.Count);
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

        // 1. Get current patterns
        var patternsResult = await AnalyzeRoutinesAsync(userId, cancellationToken);
        if (patternsResult.IsFailure)
            return Result.Failure<ConflictWarning?>(patternsResult.Error);

        if (patternsResult.Value.Patterns.Count == 0)
        {
            logger.LogInformation("No patterns detected for user {UserId} - no conflict warning", userId);
            return Result.Success<ConflictWarning?>(null);
        }

        // 2. Build conflict detection prompt
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

        logger.LogInformation("🔵 Calling Gemini API for conflict detection (user {UserId})...", userId);

        var result = await CallGeminiAsync<ConflictWarning>(prompt, cancellationToken);

        stopwatch.Stop();
        if (result.IsSuccess)
        {
            var hasConflict = result.Value.HasConflict;
            logger.LogInformation("✅ Conflict detection completed in {ElapsedMs}ms - hasConflict: {HasConflict}",
                stopwatch.ElapsedMilliseconds, hasConflict);

            if (!hasConflict)
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

        // 1. Get current patterns
        var patternsResult = await AnalyzeRoutinesAsync(userId, cancellationToken);
        if (patternsResult.IsFailure)
            return Result.Failure<IReadOnlyList<TimeSlotSuggestion>>(patternsResult.Error);

        // 2. If no patterns, return generic fallback suggestions
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

        // 3. Build time slot suggestion prompt
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

        logger.LogInformation("🔵 Calling Gemini API for time slot suggestions (user {UserId}, habit: {HabitTitle})...",
            userId, habitTitle);

        var result = await CallGeminiAsync<TimeSlotSuggestionsWrapper>(prompt, cancellationToken);

        stopwatch.Stop();
        if (result.IsSuccess)
        {
            var suggestions = result.Value.Suggestions;

            // Ensure exactly 3 suggestions, sorted by score descending
            if (suggestions.Count != 3)
            {
                logger.LogWarning("Gemini returned {Count} suggestions instead of 3 - using as-is", suggestions.Count);
            }

            var sortedSuggestions = suggestions.OrderByDescending(s => s.Score).ToList();

            logger.LogInformation("✅ Time slot suggestions completed in {ElapsedMs}ms - {Count} suggestions",
                stopwatch.ElapsedMilliseconds, sortedSuggestions.Count);

            return Result.Success<IReadOnlyList<TimeSlotSuggestion>>(sortedSuggestions);
        }

        return Result.Failure<IReadOnlyList<TimeSlotSuggestion>>(result.Error);
    }

    private async Task<Result<T>> CallGeminiAsync<T>(string prompt, CancellationToken cancellationToken)
    {
        var request = new GeminiRequest
        {
            Contents = new[]
            {
                new GeminiContent
                {
                    Parts = new[]
                    {
                        new GeminiPart { Text = prompt }
                    }
                }
            },
            GenerationConfig = new GeminiGenerationConfig
            {
                Temperature = 0.1,
                ResponseMimeType = "application/json"
            }
        };

        try
        {
            var url = $"{_settings.BaseUrl}/models/{_settings.Model}:generateContent?key={_settings.ApiKey}";

            // Retry logic for rate limiting (same as GeminiFactExtractionService)
            HttpResponseMessage? response = null;
            int retryCount = 0;
            int maxRetries = 3;

            while (retryCount <= maxRetries)
            {
                response = await httpClient.PostAsJsonAsync(url, request, cancellationToken);

                if (response.IsSuccessStatusCode || response.StatusCode != System.Net.HttpStatusCode.TooManyRequests)
                {
                    break;
                }

                retryCount++;
                if (retryCount <= maxRetries)
                {
                    var delayMs = (int)Math.Pow(2, retryCount) * 1000; // Exponential backoff: 2s, 4s, 8s
                    logger.LogWarning("⚠️  Rate limited. Retrying in {DelayMs}ms (attempt {Retry}/{Max})...",
                        delayMs, retryCount, maxRetries);
                    await Task.Delay(delayMs, cancellationToken);
                }
            }

            if (!response!.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError("Gemini API returned {Status}: {Body}", response.StatusCode, errorBody);
                return Result.Failure<T>($"Gemini API error: {response.StatusCode}");
            }

            var geminiResponse = await response.Content.ReadFromJsonAsync<GeminiResponse>(cancellationToken);
            var text = geminiResponse?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;

            if (string.IsNullOrWhiteSpace(text))
            {
                logger.LogWarning("Gemini returned empty response");
                return Result.Failure<T>("Gemini returned empty response");
            }

            logger.LogInformation("📄 GEMINI ROUTINE ANALYSIS JSON: {Json}", text);

            var deserialized = JsonSerializer.Deserialize<T>(text, RoutineJsonOptions);

            if (deserialized is null)
            {
                logger.LogError("Failed to deserialize Gemini response as {Type}", typeof(T).Name);
                return Result.Failure<T>($"Failed to deserialize response as {typeof(T).Name}");
            }

            return Result.Success(deserialized);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Failed to deserialize Gemini response");
            return Result.Failure<T>($"JSON deserialization error: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Routine analysis failed");
            return Result.Failure<T>($"Routine analysis error: {ex.Message}");
        }
    }

    // --- Gemini API DTOs ---

    private record GeminiRequest
    {
        [JsonPropertyName("contents")]
        public GeminiContent[] Contents { get; init; } = [];

        [JsonPropertyName("generationConfig")]
        public GeminiGenerationConfig? GenerationConfig { get; init; }
    }

    private record GeminiContent
    {
        [JsonPropertyName("parts")]
        public GeminiPart[] Parts { get; init; } = [];
    }

    private record GeminiPart
    {
        [JsonPropertyName("text")]
        public string Text { get; init; } = string.Empty;
    }

    private record GeminiGenerationConfig
    {
        [JsonPropertyName("temperature")]
        public double Temperature { get; init; }

        [JsonPropertyName("responseMimeType")]
        public string ResponseMimeType { get; init; } = string.Empty;
    }

    private record GeminiResponse
    {
        [JsonPropertyName("candidates")]
        public GeminiCandidate[]? Candidates { get; init; }
    }

    private record GeminiCandidate
    {
        [JsonPropertyName("content")]
        public GeminiContent? Content { get; init; }
    }

    private record TimeSlotSuggestionsWrapper
    {
        [JsonPropertyName("suggestions")]
        public IReadOnlyList<TimeSlotSuggestion> Suggestions { get; init; } = [];
    }
}
