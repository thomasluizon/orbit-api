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

        var scheduledHabits = habitList
            .Where(h => HabitScheduleService.GetScheduledDates(h, dateFrom, dateTo).Count > 0)
            .ToList();

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

        var scheduledNames = scheduledHabits.Count > 0
            ? string.Join(", ", scheduledHabits.Select(h => h.Title))
            : "(none)";

        var overdueNames = overdueHabits.Count > 0
            ? string.Join(", ", overdueHabits.Select(h => $"{h.Title} (overdue)"))
            : "(none)";

        return $"""
            Generate a daily habit summary for {date:MMMM d, yyyy}.

            Scheduled habits for today: {scheduledNames}
            Overdue habits: {overdueNames}

            Rules:
            - Write 2-4 sentences
            - Use a warm, encouraging tone
            - Mention habit names naturally in the text
            - Do NOT use markdown formatting, bullet points, or JSON
            - Write ONLY in {languageName}
            - No preamble, no sign-off -- just the summary paragraph
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
