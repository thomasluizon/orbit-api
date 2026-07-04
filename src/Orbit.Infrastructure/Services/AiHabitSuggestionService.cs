using System.Globalization;
using System.Text.Json.Serialization;
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
    private const int MaxSuggestedChecklistItems = 6;

    public async Task<Result<HabitSetupSuggestion>> SuggestSetupAsync(
        string title, string language, CancellationToken ct = default)
    {
        var prompt = BuildPrompt(title, language);

        if (logger.IsEnabled(LogLevel.Debug))
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
            Infer the most sensible setup by reasoning about what the title implies, then reply with a single JSON object using EXACTLY these fields:
            - "emoji": a single emoji that best represents the habit, or null.
            - "frequencyUnit": "Day", "Week", "Month", or "Year" for a habit that repeats, or null for a ONE-TIME task that is finished after a single completion.
            - "frequencyQuantity": a positive integer meaning "once every N units" (unit "Day" quantity 1 = daily; unit "Week" quantity 2 = every two weeks). Use null only when "frequencyUnit" is null.
            - "days": an array of English weekday names ("Monday" through "Sunday"), set ONLY when "frequencyUnit" is "Day", "frequencyQuantity" is 1, and the habit has a natural fixed-day rhythm; otherwise an empty array.
            - "isFlexible": true ONLY for a "do it N times per period" goal with no fixed weekdays (for example "a few times a week"); otherwise false.
            - "flexibleTarget": when "isFlexible" is true, the positive integer count per period (for example 4 with "frequencyUnit" "Week" means "4x per week"); otherwise null.
            - "dueTime": a 24-hour "HH:mm" time when the title implies a time of day; otherwise null.
            - "subHabits": an array of up to {MaxSuggestedSubHabits} plain-string titles of separately-trackable child habits, ONLY when the habit is a routine of distinct activities; otherwise an empty array. Each element MUST be a string, never an object.
            - "checklistItems": an array of up to {MaxSuggestedChecklistItems} plain-string tick-off steps or items for a single activity, ONLY when the habit benefits from a checklist; otherwise an empty array. Each element MUST be a string, never an object.

            Reason about intent:
            - ONE-TIME vs RECURRING: a project, errand, or deadline done once is one-time, so "frequencyUnit" is null -- for example "change the gta 6 voice", "buy a birthday gift", "file taxes". A repeated behavior recurs -- for example "meditate" (daily), "go to the gym" (a weekly rhythm), "call mom" (weekly).
            - CADENCE: prefer fixed weekdays when the activity has a natural rhythm -- "go to the gym" becomes "frequencyUnit" "Day", "frequencyQuantity" 1, "days" ["Monday","Wednesday","Friday"]. Prefer a flexible target when it is "a few times a week" with no fixed days -- "go to the gym" becomes "isFlexible" true, "frequencyUnit" "Week", "flexibleTarget" 4. Use a larger interval for occasional habits -- "deep clean the house" becomes "frequencyUnit" "Week", "frequencyQuantity" 2.
            - TIME OF DAY: set "dueTime" when the title implies one -- "morning run" becomes "07:00", "evening stretch" becomes "20:00"; otherwise null.
            - SUB-HABITS vs CHECKLIST: choose at most ONE and leave the other empty, and only when it genuinely helps. Use "subHabits" when the habit is a routine of activities each tracked on its own -- "morning routine" becomes "brush teeth", "shower", "make bed". Use "checklistItems" when the habit is ONE activity with steps or items to tick off -- "go to the supermarket" becomes "cheese", "bread", "eggs"; "workout" becomes "warm up", "cardio", "cool down".

            Write any "subHabits" and "checklistItems" titles in {languageName}. Respond with JSON only, no prose.
            """;
    }

    internal static Result<HabitSetupSuggestion> MapSuggestion(HabitSuggestionDto? dto)
    {
        if (dto is null)
            return Result.Failure<HabitSetupSuggestion>(ErrorMessages.AiEmptyResponse);

        var frequencyUnit = ParseFrequencyUnit(dto.FrequencyUnit);
        var flexibleTarget = SanitizeFlexibleTarget(dto.FlexibleTarget, dto.IsFlexible, frequencyUnit);
        var isFlexible = flexibleTarget is not null;
        var frequencyQuantity = SanitizeQuantity(dto.FrequencyQuantity, frequencyUnit, isFlexible);
        var days = SanitizeDays(dto.Days, frequencyUnit, frequencyQuantity, isFlexible);
        var subHabits = SanitizeTitles(dto.SubHabits, MaxSuggestedSubHabits, AppConstants.MaxHabitTitleLength);
        IReadOnlyList<string> checklistItems = subHabits.Count > 0
            ? []
            : SanitizeTitles(dto.ChecklistItems, MaxSuggestedChecklistItems, AppConstants.MaxChecklistItemTextLength);

        return Result.Success(new HabitSetupSuggestion(
            SanitizeEmoji(dto.Emoji),
            frequencyUnit,
            frequencyQuantity,
            days,
            isFlexible,
            flexibleTarget,
            SanitizeDueTime(dto.DueTime),
            subHabits,
            checklistItems));
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

    private static int? SanitizeFlexibleTarget(int? flexibleTarget, bool isFlexible, FrequencyUnit? frequencyUnit)
    {
        if (!isFlexible || frequencyUnit is null)
            return null;
        return flexibleTarget is { } value && value >= 1 ? value : null;
    }

    private static int? SanitizeQuantity(int? quantity, FrequencyUnit? frequencyUnit, bool isFlexible)
    {
        if (frequencyUnit is null)
            return null;
        if (isFlexible)
            return 1;
        return quantity is { } value && value >= 1 ? value : 1;
    }

    private static IReadOnlyList<DayOfWeek> SanitizeDays(
        IReadOnlyList<string>? days, FrequencyUnit? frequencyUnit, int? frequencyQuantity, bool isFlexible)
    {
        if (isFlexible || days is null || frequencyUnit != FrequencyUnit.Day || frequencyQuantity != 1)
            return [];

        return days
            .Select(day => Enum.TryParse<DayOfWeek>(day, ignoreCase: true, out var parsed) ? parsed : (DayOfWeek?)null)
            .Where(day => day is not null)
            .Select(day => day!.Value)
            .Distinct()
            .ToList();
    }

    private static string? SanitizeDueTime(string? dueTime)
    {
        if (string.IsNullOrWhiteSpace(dueTime)
            || !TimeOnly.TryParse(dueTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            return null;
        return time.ToString("HH\\:mm", CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<string> SanitizeTitles(IReadOnlyList<string>? values, int cap, int maxLength)
    {
        if (values is null)
            return [];

        return values
            .Select(title => title?.Trim() ?? string.Empty)
            .Where(title => title.Length > 0 && title.Length <= maxLength)
            .Take(cap)
            .ToList();
    }

    internal sealed record HabitSuggestionDto(
        string? Emoji,
        string? FrequencyUnit,
        int? FrequencyQuantity,
        [property: JsonConverter(typeof(TolerantStringListConverter))] IReadOnlyList<string>? Days,
        [property: JsonConverter(typeof(TolerantStringListConverter))] IReadOnlyList<string>? SubHabits,
        bool IsFlexible = false,
        int? FlexibleTarget = null,
        string? DueTime = null,
        [property: JsonConverter(typeof(TolerantStringListConverter))] IReadOnlyList<string>? ChecklistItems = null);

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Generating habit setup suggestion (language: {Language})...")]
    private static partial void LogGeneratingSuggestion(ILogger logger, string language);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "AI API call failed for habit setup suggestion")]
    private static partial void LogSuggestionFailed(ILogger logger, Exception ex);
}
