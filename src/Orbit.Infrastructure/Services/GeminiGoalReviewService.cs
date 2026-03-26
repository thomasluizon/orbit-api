using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Services;

public sealed class GeminiGoalReviewService(
    HttpClient httpClient,
    IOptions<GeminiSettings> options,
    ILogger<GeminiGoalReviewService> logger) : IGoalReviewService
{
    private readonly GeminiSettings _settings = options.Value;

    public async Task<Result<string>> GenerateReviewAsync(
        string goalsContext,
        string language,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(goalsContext))
            return Result.Failure<string>("No goals data provided.");

        var languageName = language.ToLowerInvariant() switch
        {
            "pt-br" or "pt" => "Brazilian Portuguese",
            _ => "English"
        };

        var prompt = $"""
            You are a goal progress coach. Review the user's active goals and provide a concise summary.

            GOALS DATA:
            {goalsContext}

            RULES:
            - Write a natural-language review in {languageName}
            - 4-6 sentences maximum
            - Mention what is on track, what is at risk, and actionable suggestions
            - Be encouraging but honest
            - No markdown formatting, no emojis, no JSON
            - Plain text only
            """;

        logger.LogInformation("Calling Gemini API for goal review (language: {Language})...", language);

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
            logger.LogWarning("Gemini returned empty response for goal review");
            return Result.Failure<string>("Gemini returned empty response");
        }

        var trimmed = text.Trim();
        if (trimmed.StartsWith("```"))
        {
            var lines = trimmed.Split('\n');
            if (lines.Length >= 2)
                trimmed = string.Join('\n', lines.Skip(1).Take(lines.Length - (lines[^1].TrimStart().StartsWith("```") ? 2 : 1)));
        }

        logger.LogInformation("Goal review generated successfully ({Length} chars)", trimmed.Trim().Length);

        return Result.Success(trimmed.Trim());
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
