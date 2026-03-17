using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Services;

public sealed class GeminiSummaryService(
    HttpClient httpClient,
    IOptions<GeminiSettings> options,
    ILogger<GeminiSummaryService> logger) : ISummaryService
{
    private readonly GeminiSettings _settings = options.Value;

    public async Task<Result<string>> GenerateSummaryAsync(
        IEnumerable<Habit> allHabits,
        DateOnly dateFrom,
        DateOnly dateTo,
        bool includeOverdue,
        string language,
        CancellationToken cancellationToken = default)
    {
        var habitList = allHabits.ToList();

        // Find top-level habits scheduled for this date range
        var scheduledTopLevel = habitList
            .Where(h => h.ParentHabitId is null
                         && HabitScheduleService.GetScheduledDates(h, dateFrom, dateTo).Count > 0)
            .ToList();

        var scheduledTopLevelIds = scheduledTopLevel.Select(h => h.Id).ToHashSet();

        // Include ALL sub-habits of scheduled parents (regardless of their own schedule)
        var children = habitList
            .Where(h => h.ParentHabitId is not null && scheduledTopLevelIds.Contains(h.ParentHabitId.Value))
            .ToList();

        var scheduledHabits = scheduledTopLevel.Concat(children).ToList();

        var overdueHabits = includeOverdue
            ? habitList
                .Where(h => !h.IsCompleted && h.DueDate < dateFrom)
                .ToList()
            : [];

        var prompt = BuildSummaryPrompt(scheduledHabits, overdueHabits, dateFrom, language);

        logger.LogInformation("Calling Gemini API for daily summary (date: {Date}, language: {Language})...",
            dateFrom, language);

        var request = new GeminiRequest
        {
            Contents =
            [
                new GeminiContent
                {
                    Parts = [new GeminiPart { Text = prompt }]
                }
            ],
            GenerationConfig = new GeminiGenerationConfig
            {
                Temperature = 0.7
            }
        };

        HttpResponseMessage? response = null;
        int retryCount = 0;
        const int maxRetries = 3;

        while (retryCount <= maxRetries)
        {
            response = await httpClient.PostAsJsonAsync(
                $"{_settings.BaseUrl}/models/{_settings.Model}:generateContent?key={_settings.ApiKey}",
                request,
                cancellationToken);

            if (response.IsSuccessStatusCode || response.StatusCode != System.Net.HttpStatusCode.TooManyRequests)
                break;

            retryCount++;
            if (retryCount <= maxRetries)
            {
                var delayMs = (int)Math.Pow(2, retryCount) * 1000; // 2s, 4s, 8s
                logger.LogWarning("Rate limited by Gemini. Retrying in {DelayMs}ms (attempt {Retry}/{Max})...",
                    delayMs, retryCount, maxRetries);
                await Task.Delay(delayMs, cancellationToken);
            }
        }

        if (!response!.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError("Gemini API returned {Status}: {Body}", response.StatusCode, errorBody);
            return Result.Failure<string>($"Gemini API error: {response.StatusCode}");
        }

        var geminiResponse = await response.Content.ReadFromJsonAsync<GeminiResponse>(cancellationToken);
        var text = geminiResponse?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;

        if (string.IsNullOrWhiteSpace(text))
        {
            logger.LogWarning("Gemini returned empty response for daily summary");
            return Result.Failure<string>("Gemini returned empty response");
        }

        // Strip markdown backtick fences as safety net
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```"))
        {
            var lines = trimmed.Split('\n');
            if (lines.Length >= 2)
                trimmed = string.Join('\n', lines.Skip(1).Take(lines.Length - (lines[^1].TrimStart().StartsWith("```") ? 2 : 1)));
        }

        logger.LogInformation("Daily summary generated successfully ({Length} chars)", trimmed.Trim().Length);

        return Result.Success(trimmed.Trim());
    }

    private static string BuildSummaryPrompt(
        IReadOnlyList<Habit> scheduledHabits,
        IReadOnlyList<Habit> overdueHabits,
        DateOnly date,
        string language)
    {
        var languageName = language.ToLowerInvariant() switch
        {
            "pt-br" or "pt" => "Brazilian Portuguese",
            _ => "English"
        };

        var habitLines = new List<string>();
        foreach (var habit in scheduledHabits)
        {
            var status = habit.IsCompleted ? "done" : "pending";
            var children = scheduledHabits
                .Where(h => h.ParentHabitId == habit.Id)
                .ToList();

            if (habit.ParentHabitId is not null)
                continue; // will be listed under parent

            if (children.Count > 0)
            {
                var doneCount = children.Count(c => c.IsCompleted);
                habitLines.Add($"- {habit.Title} ({status}, {doneCount}/{children.Count} sub-tasks done)");
                foreach (var child in children)
                {
                    var childStatus = child.IsCompleted ? "done" : "pending";
                    habitLines.Add($"  - {child.Title} ({childStatus})");
                }
            }
            else
            {
                habitLines.Add($"- {habit.Title} ({status})");
            }
        }

        var habitSection = habitLines.Count > 0
            ? string.Join("\n", habitLines)
            : "(no habits scheduled)";

        var overdueSection = overdueHabits.Count > 0
            ? string.Join("\n", overdueHabits.Select(h => $"- {h.Title}"))
            : "(none)";

        var totalCount = scheduledHabits.Count;
        var doneTotal = scheduledHabits.Count(h => h.IsCompleted);

        return $"""
            You are a friendly habit coach. Write a short daily briefing for the user.

            Date: {date:MMMM d, yyyy}
            Progress: {doneTotal}/{totalCount} habits completed

            Today's habits:
            {habitSection}

            Overdue from previous days:
            {overdueSection}

            Rules:
            - Write 2-3 short sentences, like a supportive friend
            - Focus on what's ahead: mention specific pending habits by name
            - If some habits are done, briefly acknowledge progress
            - If there are overdue habits, gently remind without guilt
            - Keep it casual and concise, not overly enthusiastic
            - Do NOT use markdown, bullet points, emojis, or JSON
            - Write ONLY in {languageName}
            - No greeting, no sign-off -- just the briefing
            """;
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
}
