using Orbit.Application.Common;

namespace Orbit.Application.Chat;

internal static class ToolResultFailureMessages
{
    private static readonly (string Suffix, string Key)[] FieldErrors =
    [
        (" is required.", "argumentRequired"),
        (" is required and must be a valid GUID.", "guidRequired"),
        (" must be a valid GUID.", "invalidGuid"),
        (" is required and must be an array.", "arrayRequired"),
        (" must be an array.", "invalidArray"),
        (" must be a non-empty array.", "nonEmptyArrayRequired"),
        (" is required and must be an object.", "objectRequired"),
        (" must be an object.", "invalidObject"),
        (" is required and must be a number.", "numberRequired"),
        (" must be a boolean.", "invalidBoolean"),
        (" must be a string.", "invalidString"),
        (" must be a string or null.", "invalidNullableString"),
        (" must be an integer or null.", "invalidNullableInteger"),
        (" must be a date in YYYY-MM-DD format.", "invalidDate"),
        (" must be a date in YYYY-MM-DD format or null.", "invalidNullableDate"),
        (" must use YYYY-MM-DD format.", "invalidDateValue"),
        (" must use HH:mm format or null.", "invalidNullableTime"),
    ];

    internal static (string, ToolResultMessageParams) Describe(string? error)
    {
        if (error is null)
            return ("failed", new());
        if (error.StartsWith("Policy denied: ", StringComparison.Ordinal))
            return ("policyDenied", new(PolicyReason: error["Policy denied: ".Length..]));
        if (error.StartsWith("Unsupported action '", StringComparison.Ordinal) && error.EndsWith("'.", StringComparison.Ordinal))
            return ("unsupportedAction", new(ActionName: error["Unsupported action '".Length..^2]));
        if (DescribeEntityFailure(error) is { } entityFailure)
            return entityFailure;
        if (error == $"A goal can have at most {AppConstants.MaxHabitsPerGoal} linked habits."
            || error == ErrorMessages.MaxHabitsPerGoal.Format(AppConstants.MaxHabitsPerGoal).Message)
            return ("maxHabitsPerGoal", new(Count: AppConstants.MaxHabitsPerGoal));
        if (error == ErrorMessages.HabitNotOwned.Message)
            return ("habitNotOwned", new());
        if (error == ErrorMessages.CannotLogFutureDate.Message)
            return ("cannotLogFutureDate", new());
        if (error == ErrorMessages.HabitNotFound.Message)
            return ("habitNotFound", new());
        if (error == ErrorMessages.TagNotFound.Message)
            return ("tagNotFound", new());
        if (error == ErrorMessages.InvalidGoalStatus.Message)
            return ("invalidGoalStatus", new());
        if (error == ErrorMessages.GoalNotFound.Message)
            return ("goalNotFound", new());

        foreach (var (suffix, key) in FieldErrors)
        {
            if (!error.EndsWith(suffix, StringComparison.Ordinal))
                continue;
            var field = error[..^suffix.Length];
            if (field.Length > 0 && field.All(character => char.IsAsciiLetterLower(character)
                || char.IsAsciiDigit(character) || character is '_' or '.'))
                return (key, new(FieldName: field));
        }

        return error switch
        {
            "Operation is unsupported by policy." => ("unsupportedByPolicy", new()),
            "An unexpected error occurred." => ("unexpectedError", new()),
            "Tool arguments exceeded the clarification stash limit." => ("clarificationLimitExceeded", new()),
            "No matching habits found to delete." => ("noHabitsToDelete", new()),
            "No matching habits found to update." => ("noHabitsToUpdate", new()),
            "No matching habits found to log." => ("noHabitsToLog", new()),
            "No matching habits found to skip." => ("noHabitsToSkip", new()),
            "No matching habits found." => ("noMatchingHabits", new()),
            "notification_ids contains an invalid ID." => ("invalidNotificationIds", new()),
            "endpoint, p256dh, and auth are required." => ("pushCredentialsRequired", new()),
            "name and color are required." => ("nameAndColorRequired", new()),
            "name and at least one item are required." => ("checklistTemplateItemsRequired", new()),
            "status is required and must be Active, Completed, or Abandoned." => ("goalStatusRequired", new()),
            "habit_ids is unavailable for this tool instance." => ("habitIdsUnavailable", new()),
            "habit_ids must be omitted or provided as a non-empty array of valid habit IDs." => ("habitIdsSelectorRequired", new()),
            "filter cannot combine with habit_ids." => ("filterConflictsWithHabitIds", new()),
            "Provide filter or habit_ids." => ("habitSelectorRequired", new()),
            "updates contains an unsupported field." => ("unsupportedUpdateField", new()),
            "filter contains an unsupported field." => ("unsupportedFilterField", new()),
            "filter.habit_ids must be an array of valid habit IDs." => ("filterHabitIdsRequired", new()),
            "filter.habit_ids must contain only valid habit IDs." => ("invalidFilterHabitIds", new()),
            "filter.frequency must be Day, Week, Month, Year, or OneTime." => ("filterFrequencyInvalid", new()),
            "filter cannot combine all=true with habit_ids." => ("filterAllConflictsWithHabitIds", new()),
            "filter must explicitly select all habits or provide at least one predicate." => ("filterSelectorRequired", new()),
            "frequency_unit must be Day, Week, Month, Year, or null." => ("frequencyUnitInvalid", new()),
            "days must contain valid weekday names." => ("invalidDays", new()),
            "reminder_times must contain only integers." => ("invalidReminderTimes", new()),
            "checklist_items contains an invalid item." => ("invalidChecklistItem", new()),
            "scheduled_reminders contains an invalid item." => ("invalidScheduledReminder", new()),
            "revised_items contains an invalid habit or date." => ("invalidRevisedHabitDate", new()),
            "revised_items contains an invalid habit ID." => ("invalidRevisedHabitId", new()),
            "revised_items contains an invalid emoji." => ("invalidRevisedEmoji", new()),
            "revised_items requires changes." => ("revisedChangesRequired", new()),
            "Invalid date format. Use YYYY-MM-DD." => ("invalidDateFormat", new()),
            "expires_at_utc must be a valid ISO-8601 UTC timestamp." => ("invalidApiKeyExpiration", new()),
            "parent_id must be a valid GUID when provided." => ("invalidParentId", new()),
            "habit_ids must contain only valid GUID strings." => ("invalidHabitIds", new()),
            "habit_ids must contain only valid habit IDs." => ("invalidHabitIdList", new()),
            "Provide either tag_ids or tag_names." => ("tagSelectorRequired", new()),
            "At least one tag name is required." => ("tagNameRequired", new()),
            "Provide either goal_id or goal_name." => ("goalSelectorRequired", new()),
            "Invalid skip_id." => ("invalidSkipId", new()),
            "Provide emoji or set infer_from_title to true." => ("emojiRequired", new()),
            "Each habit requires a non-empty title." => ("habitTitleRequired", new()),
            "No habits provided." => ("noHabitsProvided", new()),
            "Each position requires a valid habit_id GUID and an integer position." => ("invalidHabitPosition", new()),
            "Each position requires a valid goal_id GUID and an integer position." => ("invalidGoalPosition", new()),
            "Bulk habit operation failed before any changes were applied." => ("bulkOperationFailed", new()),
            "name, email, subject, and message are required." => ("supportFieldsRequired", new()),
            _ => ("failed", new())
        };
    }

    private static (string, ToolResultMessageParams)? DescribeEntityFailure(string error)
    {
        if (error == "Habit not found.")
            return ("habitNotFound", new());
        if (error == "Goal not found.")
            return ("goalNotFound", new());
        if (error.Length >= "Habit ".Length + " not found.".Length
            && error.StartsWith("Habit ", StringComparison.Ordinal) && error.EndsWith(" not found.", StringComparison.Ordinal))
            return ("habitNotFound", new(EntityId: error["Habit ".Length..^" not found.".Length]));
        if (error.Length >= "Goal ".Length + " not found.".Length
            && error.StartsWith("Goal ", StringComparison.Ordinal) && error.EndsWith(" not found.", StringComparison.Ordinal))
            return ("goalNotFound", new(EntityId: error["Goal ".Length..^" not found.".Length]));
        if (error.StartsWith("No active goal found matching '", StringComparison.Ordinal) && error.EndsWith("'.", StringComparison.Ordinal))
            return ("activeGoalNotFound", new(EntityName: error["No active goal found matching '".Length..^2]));
        return null;
    }

}
