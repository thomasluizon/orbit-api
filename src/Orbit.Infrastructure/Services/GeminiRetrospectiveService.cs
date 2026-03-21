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

public sealed class GeminiRetrospectiveService(
    HttpClient httpClient,
    IOptions<GeminiSettings> options,
    ILogger<GeminiRetrospectiveService> logger) : IRetrospectiveService
{
    private readonly GeminiSettings _settings = options.Value;

    public async Task<Result<string>> GenerateRetrospectiveAsync(
        List<Habit> habits,
        DateOnly dateFrom,
        DateOnly dateTo,
        string period,
        string language,
        CancellationToken cancellationToken = default)
    {
        if (habits.Count == 0)
            return Result.Failure<string>("No habits found for this period.");

        var prompt = BuildRetrospectivePrompt(habits, dateFrom, dateTo, period, language);

        logger.LogInformation(
            "Calling Gemini API for retrospective (period: {Period}, from: {From}, to: {To}, language: {Language})...",
            period, dateFrom, dateTo, language);

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
                var delayMs = (int)Math.Pow(2, retryCount) * 1000;
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
            logger.LogWarning("Gemini returned empty response for retrospective");
            return Result.Failure<string>("Gemini returned empty response");
        }

        var trimmed = text.Trim();
        if (trimmed.StartsWith("```"))
        {
            var lines = trimmed.Split('\n');
            if (lines.Length >= 2)
                trimmed = string.Join('\n', lines.Skip(1).Take(lines.Length - (lines[^1].TrimStart().StartsWith("```") ? 2 : 1)));
        }

        logger.LogInformation("Retrospective generated successfully ({Length} chars)", trimmed.Trim().Length);

        return Result.Success(trimmed.Trim());
    }

    private static string BuildRetrospectivePrompt(
        List<Habit> habits,
        DateOnly dateFrom,
        DateOnly dateTo,
        string period,
        string language)
    {
        var languageName = language.ToLowerInvariant() switch
        {
            "pt-br" or "pt" => "Brazilian Portuguese",
            _ => "English"
        };

        var totalDays = dateTo.DayNumber - dateFrom.DayNumber + 1;

        var habitLines = new List<string>();
        var totalCompletions = 0;
        var totalScheduled = 0;
        var badHabitSlips = 0;

        foreach (var habit in habits.Where(h => h.ParentHabitId is null))
        {
            var scheduledDates = HabitScheduleService.GetScheduledDates(habit, dateFrom, dateTo);
            var scheduledCount = scheduledDates.Count;
            var logs = habit.Logs.Where(l => l.Date >= dateFrom && l.Date <= dateTo).ToList();
            var completedCount = logs.Count;

            if (scheduledCount == 0 && completedCount == 0)
                continue;

            totalScheduled += scheduledCount;
            totalCompletions += completedCount;

            var rate = scheduledCount > 0 ? (int)Math.Round(100.0 * completedCount / scheduledCount) : 0;

            if (habit.IsBadHabit)
            {
                badHabitSlips += completedCount;
                habitLines.Add($"- {habit.Title} (bad habit): {completedCount} slips in {totalDays} days");
            }
            else
            {
                habitLines.Add($"- {habit.Title}: {completedCount}/{scheduledCount} completed ({rate}%)");
            }

            // Sub-habits
            var children = habits.Where(h => h.ParentHabitId == habit.Id).ToList();
            foreach (var child in children)
            {
                var childLogs = child.Logs.Where(l => l.Date >= dateFrom && l.Date <= dateTo).ToList();
                var childScheduled = HabitScheduleService.GetScheduledDates(child, dateFrom, dateTo).Count;
                var childRate = childScheduled > 0 ? (int)Math.Round(100.0 * childLogs.Count / childScheduled) : 0;
                habitLines.Add($"  - {child.Title}: {childLogs.Count}/{childScheduled} ({childRate}%)");
            }
        }

        var habitSection = habitLines.Count > 0
            ? string.Join("\n", habitLines)
            : "(no habit activity)";

        var overallRate = totalScheduled > 0 ? (int)Math.Round(100.0 * totalCompletions / totalScheduled) : 0;

        return $"""
            You are a thoughtful habit coach writing a retrospective review.

            Period: Last {totalDays} days ({period}) -- {dateFrom:MMMM d} to {dateTo:MMMM d, yyyy}
            Total habits tracked: {habits.Count(h => h.ParentHabitId is null)}
            Overall completion rate: {totalCompletions}/{totalScheduled} ({overallRate}%)
            {(badHabitSlips > 0 ? $"Bad habit slips: {badHabitSlips}" : "")}

            Per-habit breakdown:
            {habitSection}

            Write a retrospective with these sections (use these exact headings):
            1. **Highlights** -- What went well, milestones reached, strong completion rates
            2. **Missed Opportunities** -- What was skipped or neglected, patterns of avoidance
            3. **Trends** -- Improving or declining patterns, consistency observations
            4. **Suggestion** -- One specific, actionable tip for the next period

            Rules:
            - Be honest but encouraging -- celebrate real wins, acknowledge real gaps
            - Reference specific habit names and numbers from the data
            - Keep each section to 2-3 sentences max
            - If a habit has 100% completion, call it out as a win
            - If a habit has <50% completion, flag it kindly
            - For bad habits, fewer slips = good progress
            - Do NOT use emojis or JSON
            - Use markdown bold for section headings only
            - Write ONLY in {languageName}
            """;
    }

    // --- Gemini API DTOs (same as GeminiSummaryService) ---

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
