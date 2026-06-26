using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.AI;

namespace Orbit.Infrastructure.Services;

public sealed partial class AiHabitSuggestionService(
    AiCompletionClient aiClient,
    ILogger<AiHabitSuggestionService> logger) : IHabitSuggestionService
{
    private const int MaxSuggestedSubHabits = 6;

    public async Task<Result<HabitSetupSuggestion>> SuggestSetupAsync(
        string title, string language, CancellationToken ct = default)
    {
        var prompt = BuildPrompt(title, language);

        if (logger.IsEnabled(LogLevel.Information))
            LogGeneratingSuggestion(logger, language);

        try
        {
            var dto = await aiClient.CompleteJsonAsync<HabitSuggestionDto>(
                "You help set up a habit and reply with a single JSON object, nothing else.",
                prompt,
                cancellationToken: ct,
                purpose: "habit_suggest",
                tier: AiModelTier.SubTask);

            return MapSuggestion(dto);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSuggestionFailed(logger, ex);
            return Result.Failure<HabitSetupSuggestion>(ErrorMessages.AiUnavailable);
        }
    }

    internal static string BuildPrompt(string title, string language)
    {
        var languageName = LocaleHelper.GetAiLanguageName(language);

        return $"""
            A user is creating a habit titled "{title}".
            Reply with a JSON object suggesting a sensible setup, using exactly these fields:
            - "emoji": a single emoji that best represents the habit, or null.
            - "frequencyUnit": one of "Day", "Week", "Month", "Year" for a recurring habit, or null for a one-time task.
            - "frequencyQuantity": a positive integer meaning "once every N units" (unit "Day" quantity 1 means daily; unit "Week" quantity 2 means every two weeks). Use null when "frequencyUnit" is null.
            - "days": an array of English weekday names ("Monday" through "Sunday") ONLY when the habit should run on specific weekdays with "frequencyUnit" "Day" and "frequencyQuantity" 1; otherwise an empty array.
            - "subHabits": an array of up to {MaxSuggestedSubHabits} short, concrete sub-task titles that break the habit into actionable steps, ONLY when the habit is broad enough to benefit; otherwise an empty array.
            Write any sub-habit titles in {languageName}. Respond with JSON only, no prose.
            """;
    }

    internal static Result<HabitSetupSuggestion> MapSuggestion(HabitSuggestionDto? dto)
    {
        if (dto is null)
            return Result.Failure<HabitSetupSuggestion>(ErrorMessages.AiEmptyResponse);

        var frequencyUnit = ParseFrequencyUnit(dto.FrequencyUnit);
        var frequencyQuantity = SanitizeQuantity(dto.FrequencyQuantity, frequencyUnit);
        var days = SanitizeDays(dto.Days, frequencyUnit, frequencyQuantity);
        var subHabits = SanitizeSubHabits(dto.SubHabits);

        return Result.Success(new HabitSetupSuggestion(
            SanitizeEmoji(dto.Emoji),
            frequencyUnit,
            frequencyQuantity,
            days,
            subHabits));
    }

    private static string? SanitizeEmoji(string? emoji)
    {
        var trimmed = emoji?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > AppConstants.MaxHabitEmojiLength)
            return null;
        return trimmed;
    }

    private static FrequencyUnit? ParseFrequencyUnit(string? value) =>
        Enum.TryParse<FrequencyUnit>(value, ignoreCase: true, out var unit) ? unit : null;

    private static int? SanitizeQuantity(int? quantity, FrequencyUnit? frequencyUnit)
    {
        if (frequencyUnit is null)
            return null;
        return quantity is { } value && value >= 1 ? value : 1;
    }

    private static IReadOnlyList<DayOfWeek> SanitizeDays(
        IReadOnlyList<string>? days, FrequencyUnit? frequencyUnit, int? frequencyQuantity)
    {
        if (days is null || frequencyUnit != FrequencyUnit.Day || frequencyQuantity != 1)
            return [];

        return days
            .Select(day => Enum.TryParse<DayOfWeek>(day, ignoreCase: true, out var parsed) ? parsed : (DayOfWeek?)null)
            .Where(day => day is not null)
            .Select(day => day!.Value)
            .Distinct()
            .ToList();
    }

    private static IReadOnlyList<string> SanitizeSubHabits(IReadOnlyList<string>? subHabits)
    {
        if (subHabits is null)
            return [];

        return subHabits
            .Select(title => title?.Trim() ?? string.Empty)
            .Where(title => title.Length > 0 && title.Length <= AppConstants.MaxHabitTitleLength)
            .Take(MaxSuggestedSubHabits)
            .ToList();
    }

    internal sealed record HabitSuggestionDto(
        string? Emoji,
        string? FrequencyUnit,
        int? FrequencyQuantity,
        IReadOnlyList<string>? Days,
        IReadOnlyList<string>? SubHabits);

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Generating habit setup suggestion (language: {Language})...")]
    private static partial void LogGeneratingSuggestion(ILogger logger, string language);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "AI API call failed for habit setup suggestion")]
    private static partial void LogSuggestionFailed(ILogger logger, Exception ex);
}
