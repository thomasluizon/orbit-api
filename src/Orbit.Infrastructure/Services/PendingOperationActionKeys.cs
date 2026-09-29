using System.Text.Json;

namespace Orbit.Infrastructure.Services;

public static class PendingOperationActionKeys
{
    public static string GetRequired(string operationId, string argumentsJson)
    {
        var action = GetAction(argumentsJson);
        return operationId switch
        {
            "delete_habit" => "deleteHabit",
            "bulk_update_habits" => "updateHabits",
            "bulk_reschedule_habits" => "rescheduleHabits",
            "bulk_log_habits" => "logHabits",
            "bulk_skip_habits" => "skipHabits",
            "bulk_create_habits" => "createHabits",
            "bulk_delete_habits" => "deleteHabits",
            "delete_goal" => "deleteGoal",
            "delete_tag" => "deleteTag",
            "delete_notifications" => action switch
            {
                "delete_one" => "deleteNotification",
                "delete_all" => "deleteAllNotifications",
                _ => "deleteNotifications"
            },
            "manage_calendar_sync" => action switch
            {
                "set_auto_sync" => "setCalendarSync",
                "dismiss_import" => "dismissCalendarImport",
                "dismiss_suggestion" => "dismissCalendarSuggestion",
                "run_sync" => "syncCalendar",
                _ => "manageCalendarSync"
            },
            "delete_user_facts" => "deleteUserFacts",
            "bulk_update_habit_emojis" => "updateHabitEmojis",
            "create_habit" => "createHabit",
            "create_sub_habit" => "createSubHabit",
            "update_habit" => "updateHabit",
            "duplicate_habit" => "duplicateHabit",
            "move_habit" => "moveHabit",
            "move_habit_parent" => "moveHabitParent",
            "reorder_habits" => "reorderHabits",
            "log_habit" => "logHabit",
            "skip_habit" => "skipHabit",
            "update_checklist" => "updateChecklist",
            "create_goal" => "createGoal",
            "update_goal" => "updateGoal",
            "update_goal_progress" => "updateGoalProgress",
            "update_goal_status" => "updateGoalStatus",
            "reorder_goals" => "reorderGoals",
            "link_goals_to_habit" => "linkGoalsToHabit",
            "link_habits_to_goal" => "linkHabitsToGoal",
            "create_tag" => "createTag",
            "update_tag" => "updateTag",
            "assign_tags" => "assignTags",
            "create_checklist_template" => "createChecklistTemplate",
            "delete_checklist_template" => "deleteChecklistTemplate",
            "update_profile_preferences" => "updateProfilePreferences",
            "set_ai_memory" => "setAiMemory",
            "set_ai_summary" => "setAiSummary",
            "update_notifications" => action switch
            {
                "mark_read" => "markNotificationRead",
                "mark_all_read" => "markAllNotificationsRead",
                "subscribe_push" => "subscribePush",
                "unsubscribe_push" => "unsubscribePush",
                "test_push" => "sendTestPush",
                _ => "updateNotifications"
            },
            "get_referral_code" => "viewReferralCode",
            "send_support_request" => "sendSupportRequest",
            "manage_subscription" => action switch
            {
                "create_checkout" => "createCheckout",
                "create_portal" => "openBillingPortal",
                _ => "manageSubscription"
            },
            "get_api_keys" => "viewApiKeys",
            "manage_api_keys" => action switch
            {
                "create" => "createApiKey",
                "revoke" => "revokeApiKey",
                _ => "manageApiKeys"
            },
            "manage_account" => action switch
            {
                "reset_account" => "resetAccount",
                "request_deletion" => "requestAccountDeletion",
                "confirm_deletion" => "confirmAccountDeletion",
                _ => "manageAccount"
            },
            _ => throw new InvalidOperationException($"Pending operation '{operationId}' has no action key.")
        };
    }

    private static string? GetAction(string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("action", out var action) &&
                action.ValueKind == JsonValueKind.String
                    ? action.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
