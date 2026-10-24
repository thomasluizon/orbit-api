using Orbit.Application.Chat.Tools.Implementations;

namespace Orbit.Application.Tests.Chat.Tools;

public static partial class OptionalParameterCases
{
    private static readonly IReadOnlyDictionary<(Type ToolType, string Path), string[]> OptionalFields =
        new Dictionary<(Type, string), string[]>
        {
            [(typeof(AssignTagsTool), "")] = ["habit_id", "habit_ids", "tag_names", "tag_ids"],
            [(typeof(BulkCreateHabitsTool), "habits.items")] =
            ["description", "emoji", "frequency_unit", "frequency_quantity", "interval_weeks", "days", "due_date", "end_date", "due_time", "due_end_time", "is_bad_habit", "is_general", "is_flexible", "checklist_items", "start_date", "reminder_enabled", "reminder_times", "scheduled_reminders", "relative_reminders", "slip_alert_enabled", "tag_names", "habit_id", "tag_ids", "goal_ids", "parent_habit_id", "sub_habits"],
            [(typeof(BulkLogHabitsTool), "")] = ["filter", "habit_ids", "date", "dates"],
            [(typeof(BulkUpdateHabitEmojisTool), "")] =
            [
                "emoji_mode", "filter", "habit_ids", "emoji", "infer_from_title", "include_completed", "replace_existing"
            ],
            [(typeof(BulkUpdateHabitsTool), "filter")] =
            [
                "all", "habit_ids", "tag", "search", "is_completed", "has_emoji", "is_general", "is_bad_habit", "frequency"
            ],
            [(typeof(BulkUpdateHabitsTool), "updates")] =
            [
                "clear_fields", "title", "description", "emoji", "frequency_unit", "frequency_quantity",
                "interval_weeks", "days", "due_date", "end_date", "due_time", "due_end_time", "is_general",
                "slip_alert_enabled", "relative_reminders", "is_bad_habit", "is_flexible", "reminder_enabled",
                "reminder_times", "checklist_items", "scheduled_reminders"
            ],
            [(typeof(CreateChecklistTemplateTool), "")] = [],
            [(typeof(CreateGoalTool), "")] =
            [
                "description", "target_value", "unit", "deadline", "goal_type", "habit_ids"
            ],
            [(typeof(CreateHabitTool), "")] =
            ["description", "emoji", "frequency_unit", "frequency_quantity", "interval_weeks", "days", "due_date", "end_date", "due_time", "due_end_time", "is_bad_habit", "is_general", "is_flexible", "checklist_items", "start_date", "reminder_enabled", "reminder_times", "scheduled_reminders", "relative_reminders", "slip_alert_enabled", "tag_names", "habit_id", "tag_ids", "goal_ids", "parent_habit_id", "sub_habits"],
            [(typeof(CreateHabitTool), "sub_habits.items")] =
            ["description", "emoji", "frequency_unit", "frequency_quantity", "interval_weeks", "days", "due_date", "end_date", "due_time", "due_end_time", "is_bad_habit", "is_general", "is_flexible", "checklist_items", "start_date", "reminder_enabled", "reminder_times", "scheduled_reminders", "relative_reminders", "slip_alert_enabled", "tag_names", "habit_id", "tag_ids", "goal_ids", "parent_habit_id", "sub_habits"],
            [(typeof(CreateSubHabitTool), "")] =
            ["description", "emoji", "frequency_unit", "frequency_quantity", "interval_weeks", "days", "due_date", "end_date", "due_time", "due_end_time", "is_bad_habit", "is_general", "is_flexible", "checklist_items", "start_date", "reminder_enabled", "reminder_times", "scheduled_reminders", "relative_reminders", "slip_alert_enabled", "tag_names", "habit_id", "tag_ids", "goal_ids", "sub_habits"],
            [(typeof(DeleteChecklistTemplateTool), "")] = [],
            [(typeof(DeleteNotificationsTool), "")] = ["notification_id", "notification_ids"],
            [(typeof(GetApiKeysTool), "")] = [],
            [(typeof(GetCalendarOverviewTool), "")] =
            [
                "as_of", "calendar_ids", "date_from", "date_to", "window_from_utc", "window_to_utc", "include_calendars",
                "include_events", "include_auto_sync_state", "include_suggestions"
            ],
            [(typeof(GetChecklistTemplatesTool), "")] = [],
            [(typeof(GetGamificationOverviewTool), "")] =
            [
                "as_of", "include_profile", "include_achievements", "include_streak"
            ],
            [(typeof(GetReferralCodeTool), "")] = [],
            [(typeof(GetReferralOverviewTool), "")] = [],
            [(typeof(GetSubscriptionOverviewTool), "")] = ["include_status", "include_billing", "include_plans"],
            [(typeof(ManageAccountTool), "")] = ["code"],
            [(typeof(ManageApiKeysTool), "")] = ["key_id", "name", "scopes", "is_read_only", "expires_at_utc"],
            [(typeof(ManageCalendarSyncTool), "")] = ["calendar_ids", "enabled", "suggestion_id"],
            [(typeof(ManageFeatureFlagsTool), "")] = ["emails", "description"],
            [(typeof(ManageSubscriptionTool), "")] = ["interval"],
            [(typeof(QueryHabitsTool), "")] =
            [
                "search", "date", "include_overdue", "is_general", "is_completed", "is_bad_habit", "frequency", "tag",
                "include_sub_habits", "include_metrics", "offset", "limit"
            ],
            [(typeof(SendSupportRequestTool), "")] = [],
            [(typeof(SetProactiveAstraTool), "")] = [],
            [(typeof(UpdateChecklistTemplateTool), "")] = ["name", "items"],
            [(typeof(UpdateHabitTool), "")] =
            [
                "clear_fields", "start_date", "parent_habit_id", "clear_parent", "tag_ids", "goal_ids", "title", "description", "emoji", "frequency_unit", "frequency_quantity",
                "interval_weeks", "days", "due_date", "end_date", "due_time", "due_end_time", "is_general",
                "slip_alert_enabled", "relative_reminders", "is_bad_habit", "is_flexible", "reminder_enabled",
                "reminder_times", "checklist_items", "scheduled_reminders"
            ],
            [(typeof(UpdateNotificationsTool), "")] =
            [
                "notification_id", "endpoint", "p256dh", "auth", "release_other_account"
            ],
            [(typeof(UpdateProfilePreferencesTool), "")] =
            [
                "name", "timezone", "language", "week_start_day", "uses_24_hour_clock", "theme_preference"
            ],
        };
}
