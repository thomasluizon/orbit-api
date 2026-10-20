using System.Text.Json;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

internal static class ToolResultMessages
{
    private const string Prefix = "chat.toolResult.";

    internal static ActionResult WithToolResultMessage(this ActionResult action, AiToolCall call,
        object? payload = null, string? key = null, ToolResultMessageParams? parameters = null)
    {
        var message = key is null ? Describe(action, call, payload) : (key, parameters ?? new());
        return action with { MessageKey = Prefix + message.Item1, Params = message.Item2 };
    }

    private static (string, ToolResultMessageParams) Describe(ActionResult action, AiToolCall call, object? payload)
    {
        if (action.Status == ActionStatus.Failed)
            return ToolResultFailureMessages.Describe(action.Error);
        if (action.Status == ActionStatus.NeedsClarification)
            return ("needsClarification", new());
        if (action.Status == ActionStatus.Suggestion)
            return ("suggestedBreakdown", new(EntityName: action.EntityName));

        var args = call.Args;
        var operation = String(args, "action");
        var enabled = Boolean(args, "enabled") == true ? "enabled" : "disabled";
        return call.Name switch
        {
            "bulk_update_habit_emojis" => Bulk("updatedEmojis", payload),
            "bulk_update_habits" => Bulk("updatedHabits", payload),
            "bulk_reschedule_habits" => Bulk("rescheduledHabits", payload),
            "bulk_log_habits" => Bulk("loggedHabits", payload),
            "bulk_skip_habits" => Bulk("skippedHabits", payload),
            "bulk_delete_habits" => Bulk("deletedHabits", payload),
            "bulk_create_habits" when payload is BulkCreateResult created => ("createdHabits", new(
                Count: created.Results.Count(item => item.Status == BulkItemStatus.Success),
                TotalCount: ArrayLength(args, "habits"))),
            "reorder_habits" => ("reorderedHabits", new(Count: ArrayLength(args, "positions"))),
            "reorder_goals" => ("reorderedGoals", new(Count: ArrayLength(args, "positions"))),
            "update_profile_preferences" => Profile(operation, args),
            "set_ai_summary" => ($"aiSummary.{enabled}", new()),
            "set_proactive_astra" => ($"proactiveAstra.{enabled}", new()),
            "manage_account" => (operation switch
            {
                "reset_account" => "accountReset",
                "request_deletion" => "deletionCodeRequested",
                "confirm_deletion" => "accountDeletionConfirmed",
                _ => "completed"
            }, new()),
            "manage_subscription" => (operation == "create_checkout" ? "checkoutCreated" : "billingPortalCreated", new()),
            "manage_calendar_sync" => (operation switch
            {
                "set_selected_calendars" => "calendarSelectionUpdated",
                "set_auto_sync" => $"calendarAutoSync.{enabled}",
                "dismiss_import" => "calendarImportDismissed",
                "dismiss_suggestion" => "calendarSuggestionDismissed",
                "run_sync" => "calendarSyncRequested",
                _ => "completed"
            }, new()),
            "update_notifications" => (operation switch
            {
                "mark_read" => "notificationRead",
                "mark_all_read" => "allNotificationsRead",
                "subscribe_push" => "pushSubscribed",
                "unsubscribe_push" => "pushUnsubscribed",
                "test_push" => "testPushRequested",
                _ => "completed"
            }, new()),
            "delete_notifications" => (operation switch
            {
                "delete_one" => "notificationDeleted",
                "delete_all" => "allNotificationsDeleted",
                "delete_selected" => "notificationsDeleted",
                _ => "completed"
            }, operation == "delete_selected" ? new(Count: ArrayLength(args, "notification_ids")) : new()),
            "manage_api_keys" when operation == "revoke" => ("apiKeyRevoked", new()),
            "manage_api_keys" => ("apiKeyCreated", new(EntityName: action.EntityName)),
            "delete_tag" => ("tagDeleted", new()),
            "delete_checklist_template" => ("checklistTemplateDeleted", new()),
            "send_support_request" => ("supportRequestSent", new()),
            "get_referral_code" => ("referralCode", new(ReferralCode: action.EntityName)),
            _ => (EntityKey(call.Name), new(EntityName: action.EntityName))
        };
    }

    private static (string, ToolResultMessageParams) Bulk(string key, object? payload)
    {
        var data = payload is null ? default : JsonSerializer.SerializeToElement(payload);
        var partial = Boolean(data, "partial");
        return (partial == true ? key + "Partial" : key, new(
            Count: Integer(data, "applied_count"),
            TotalCount: Integer(data, "total_matched"),
            SkippedCount: Integer(data, "skipped_count"),
            Partial: partial));
    }

    private static (string, ToolResultMessageParams) Profile(string? operation, JsonElement args) => operation switch
    {
        "set_name" => ("nameSet", new(EntityName: String(args, "name"))),
        "set_timezone" => ("timezoneSet", new(TimeZone: String(args, "timezone"))),
        "set_language" => ("languageSet", new(Language: String(args, "language"))),
        "set_week_start_day" => ("weekStartDaySet", new(WeekStartDay: Integer(args, "week_start_day"))),
        "set_clock_format" => ("clockFormatSet", new(Hours: Boolean(args, "uses_24_hour_clock") == true ? 24 : 12)),
        "set_theme_preference" => ("themePreferenceUpdated", new()),
        "complete_onboarding" => ("onboardingCompleted", new()),
        "complete_tour" => ("tourCompleted", new()),
        "reset_tour" => ("tourReset", new()),
        _ => ("completed", new())
    };

    private static string EntityKey(string toolName) => toolName switch
    {
        "create_habit" => "createdHabit",
        "update_habit" => "updatedHabit",
        "delete_habit" => "deletedHabit",
        "duplicate_habit" => "duplicatedHabit",
        "log_habit" => "loggedHabit",
        "skip_habit" => "skippedHabit",
        "undo_skip_habit" => "undidSkipHabit",
        "move_habit" => "movedHabit",
        "move_habit_parent" => "movedHabitParent",
        "create_sub_habit" => "createdSubHabit",
        "update_checklist" => "updatedChecklist",
        "create_goal" => "createdGoal",
        "update_goal" => "updatedGoal",
        "delete_goal" => "deletedGoal",
        "update_goal_progress" => "updatedGoalProgress",
        "update_goal_status" => "updatedGoalStatus",
        "link_habits_to_goal" => "linkedHabitsToGoal",
        "link_goals_to_habit" => "linkedGoalsToHabit",
        "assign_tags" => "assignedTags",
        "create_tag" => "createdTag",
        "update_tag" => "updatedTag",
        "create_checklist_template" => "createdChecklistTemplate",
        "update_checklist_template" => "updatedChecklistTemplate",
        _ => "completed"
    };

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value);
    }

    private static string? String(JsonElement element, string name) =>
        TryProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Integer(JsonElement element, string name) =>
        TryProperty(element, name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static bool? Boolean(JsonElement element, string name) =>
        TryProperty(element, name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static int? ArrayLength(JsonElement element, string name) =>
        TryProperty(element, name, out var value) && value.ValueKind == JsonValueKind.Array ? value.GetArrayLength() : null;
}
