using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Services;

public sealed class GeminiSlipAlertMessageService(
    HttpClient httpClient,
    IOptions<GeminiSettings> options,
    ILogger<GeminiSlipAlertMessageService> logger) : ISlipAlertMessageService
{
    private readonly GeminiSettings _settings = options.Value;

    public async Task<Result<(string Title, string Body)>> GenerateMessageAsync(
        string habitTitle,
        DayOfWeek dayOfWeek,
        int? peakHour,
        string language,
        CancellationToken cancellationToken = default)
    {
        var languageName = language.ToLowerInvariant() switch
        {
            "pt-br" or "pt" => "Brazilian Portuguese",
            _ => "English"
        };

        var timeContext = peakHour.HasValue
            ? $"They tend to slip around {peakHour.Value}:00 on {dayOfWeek}s."
            : $"They tend to slip on {dayOfWeek}s (no specific time pattern).";

        var prompt = $"""
            You are a supportive habit coach sending a push notification to help someone avoid a bad habit slip-up.

            Bad habit: "{habitTitle}"
            Pattern: {timeContext}

            Generate a short, inspiring push notification to help them stay strong today.

            Rules:
            - Return EXACTLY two lines: first line is the notification title, second line is the body
            - Title: 5-8 words max, personal and warm (e.g., "Stay strong today!" or "You've got this!")
            - Body: 1-2 sentences max, motivational and specific to their habit
            - Be creative and varied -- don't use the same structure every time
            - Tone: supportive friend, not preachy or judgmental
            - Do NOT use emojis
            - Do NOT mention the app name
            - Write ONLY in {languageName}
            - No quotes or formatting, just plain text
            """;

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
                Temperature = 0.9
            }
        };

        try
        {
            var response = await httpClient.PostAsJsonAsync(
                $"{_settings.BaseUrl}/models/{_settings.Model}:generateContent?key={_settings.ApiKey}",
                request,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Gemini API returned {Status} for slip alert message", response.StatusCode);
                return GenerateFallback(habitTitle, language);
            }

            var geminiResponse = await response.Content.ReadFromJsonAsync<GeminiResponse>(cancellationToken);
            var text = geminiResponse?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;

            if (string.IsNullOrWhiteSpace(text))
            {
                logger.LogWarning("Gemini returned empty response for slip alert message");
                return GenerateFallback(habitTitle, language);
            }

            var lines = text.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length >= 2)
                return Result.Success((lines[0], lines[1]));

            // If only one line, use it as body with a generic title
            var fallbackTitle = language.StartsWith("pt") ? $"Fique atento: {habitTitle}" : $"Heads up: {habitTitle}";
            return Result.Success((fallbackTitle, lines[0]));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to generate slip alert message via AI");
            return GenerateFallback(habitTitle, language);
        }
    }

    private static Result<(string Title, string Body)> GenerateFallback(string habitTitle, string language)
    {
        return language.StartsWith("pt")
            ? Result.Success(($"Fique atento: {habitTitle}",
                "Voce costuma deslizar por volta desse horario. Forca -- voce consegue!"))
            : Result.Success(($"Heads up: {habitTitle}",
                "You tend to slip around this time. Stay strong -- you've got this!"));
    }

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
