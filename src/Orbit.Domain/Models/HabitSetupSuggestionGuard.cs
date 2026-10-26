using System.Globalization;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Models;

public static class HabitSetupSuggestionGuard
{
    public static bool IsValid(HabitSetupSuggestion suggestion)
    {
        if (!ValidEmojiAndDays(suggestion)
            || !ValidTitles(suggestion.SubHabits) || !ValidTitles(suggestion.ChecklistItems)
            || suggestion.SubHabits.Count > 0 && suggestion.ChecklistItems.Count > 0
            || suggestion.DueTime is not null && !TimeOnly.TryParseExact(suggestion.DueTime, "HH:mm",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return false;
        if (suggestion.FrequencyUnit is null)
            return suggestion.FrequencyQuantity is null && !suggestion.IsFlexible
                && suggestion.FlexibleTarget is null && suggestion.Days.Count == 0;
        if (suggestion.FrequencyQuantity is not (>= 1 and <= 365))
            return false;
        if (suggestion.Days.Count > 0 && (suggestion.FrequencyUnit != FrequencyUnit.Day
                || suggestion.FrequencyQuantity != 1 || suggestion.IsFlexible))
            return false;
        return suggestion.IsFlexible
            ? suggestion.FrequencyQuantity == 1 && suggestion.FlexibleTarget is >= 1 and <= 366
            : suggestion.FlexibleTarget is null;
    }

    private static bool ValidEmojiAndDays(HabitSetupSuggestion suggestion)
        => suggestion.Emoji?.Length is not > 16 && suggestion.Emoji?.Any(char.IsControl) != true
           && (suggestion.Emoji is null || !OnboardingInput.ContainsUrl(suggestion.Emoji))
           && (suggestion.FrequencyUnit is null || Enum.IsDefined(suggestion.FrequencyUnit.Value))
           && suggestion.Days.Count <= 7 && suggestion.Days.Distinct().Count() == suggestion.Days.Count
           && suggestion.Days.All(day => Enum.IsDefined(day));

    private static bool ValidTitles(IReadOnlyList<string> titles)
        => titles.Count <= 6 && titles.All(OnboardingInput.IsTitleAllowed);
}
