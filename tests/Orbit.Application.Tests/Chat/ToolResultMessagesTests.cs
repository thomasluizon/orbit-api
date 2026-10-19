using System.Text.Json;
using FluentAssertions;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Chat;

public class ToolResultMessagesTests
{
    [Theory]
    [InlineData("create_habit", "createdHabit")]
    [InlineData("update_habit", "updatedHabit")]
    [InlineData("delete_habit", "deletedHabit")]
    [InlineData("duplicate_habit", "duplicatedHabit")]
    [InlineData("log_habit", "loggedHabit")]
    [InlineData("skip_habit", "skippedHabit")]
    [InlineData("undo_skip_habit", "undidSkipHabit")]
    [InlineData("move_habit", "movedHabit")]
    [InlineData("move_habit_parent", "movedHabitParent")]
    [InlineData("create_sub_habit", "createdSubHabit")]
    [InlineData("update_checklist", "updatedChecklist")]
    [InlineData("create_goal", "createdGoal")]
    [InlineData("update_goal", "updatedGoal")]
    [InlineData("delete_goal", "deletedGoal")]
    [InlineData("update_goal_progress", "updatedGoalProgress")]
    [InlineData("update_goal_status", "updatedGoalStatus")]
    [InlineData("link_habits_to_goal", "linkedHabitsToGoal")]
    [InlineData("link_goals_to_habit", "linkedGoalsToHabit")]
    [InlineData("assign_tags", "assignedTags")]
    [InlineData("create_tag", "createdTag")]
    [InlineData("update_tag", "updatedTag")]
    [InlineData("create_checklist_template", "createdChecklistTemplate")]
    public void EntityCard_PreservesUserContent(string tool, string key)
    {
        var legacy = new ActionResult("LegacyType", ActionStatus.Success, EntityName: "Ler um livro");

        var card = legacy.WithToolResultMessage(Call(tool));

        card.MessageKey.Should().Be("chat.toolResult." + key);
        card.Params.Should().Be(new ToolResultMessageParams(EntityName: "Ler um livro"));
        WithoutMetadata(card).Should().Be(WithoutMetadata(legacy));
    }

    [Theory]
    [InlineData("bulk_update_habit_emojis", "Updated emojis for", "updatedEmojis")]
    [InlineData("bulk_update_habits", "Updated", "updatedHabits")]
    [InlineData("bulk_reschedule_habits", "Rescheduled", "rescheduledHabits")]
    [InlineData("bulk_log_habits", "Logged", "loggedHabits")]
    [InlineData("bulk_skip_habits", "Skipped", "skippedHabits")]
    [InlineData("bulk_delete_habits", "Deleted", "deletedHabits")]
    public void BulkCard_UsesActualFormatterPayloadAndPreservesLegacyText(string tool, string verb, string key)
    {
        foreach (var applied in new[] { 0, 1, 185 })
        {
            var partial = applied != 185;
            var result = BulkUpdateHabitsTool.BuildResult(new BulkHabitMutationResult(applied, 185, 185 - applied, partial),
                verb, includeUpdatedCount: tool == "bulk_update_habit_emojis");
            var legacy = new ActionResult("LegacyType", ActionStatus.Success, EntityName: result.EntityName);

            var card = legacy.WithToolResultMessage(Call(tool), result.Payload);

            card.MessageKey.Should().Be("chat.toolResult." + key + (partial ? "Partial" : ""));
            card.Params.Should().Be(new ToolResultMessageParams(Count: applied, TotalCount: 185, SkippedCount: 185 - applied, Partial: partial));
            card.EntityName.Should().Be($"{verb} {applied} of 185 matching habit(s). Skipped {185 - applied}. {(partial ? "Partial" : "Complete")} result.");
            WithoutMetadata(card).Should().Be(WithoutMetadata(legacy));
        }
    }

    [Theory]
    [InlineData("manage_account", "reset_account", "accountReset")]
    [InlineData("manage_account", "request_deletion", "deletionCodeRequested")]
    [InlineData("manage_account", "confirm_deletion", "accountDeletionConfirmed")]
    [InlineData("manage_subscription", "create_checkout", "checkoutCreated")]
    [InlineData("manage_subscription", "create_portal", "billingPortalCreated")]
    [InlineData("manage_calendar_sync", "dismiss_import", "calendarImportDismissed")]
    [InlineData("manage_calendar_sync", "dismiss_suggestion", "calendarSuggestionDismissed")]
    [InlineData("manage_calendar_sync", "run_sync", "calendarSyncRequested")]
    [InlineData("update_notifications", "mark_read", "notificationRead")]
    [InlineData("update_notifications", "mark_all_read", "allNotificationsRead")]
    [InlineData("update_notifications", "subscribe_push", "pushSubscribed")]
    [InlineData("update_notifications", "unsubscribe_push", "pushUnsubscribed")]
    [InlineData("update_notifications", "test_push", "testPushRequested")]
    [InlineData("delete_notifications", "delete_one", "notificationDeleted")]
    [InlineData("delete_notifications", "delete_all", "allNotificationsDeleted")]
    [InlineData("manage_api_keys", "revoke", "apiKeyRevoked")]
    [InlineData("delete_tag", "", "tagDeleted")]
    [InlineData("delete_checklist_template", "", "checklistTemplateDeleted")]
    [InlineData("send_support_request", "", "supportRequestSent")]
    [InlineData("manage_feature_flags", "", "featureFlagsUpdated")]
    [InlineData("update_profile_preferences", "set_theme_preference", "themePreferenceUpdated")]
    [InlineData("update_profile_preferences", "complete_onboarding", "onboardingCompleted")]
    [InlineData("update_profile_preferences", "complete_tour", "tourCompleted")]
    [InlineData("update_profile_preferences", "reset_tour", "tourReset")]
    public void SummaryCard_DoesNotPassEnglishSummaryAsEntityName(string tool, string operation, string key)
    {
        var card = new ActionResult("LegacyType", ActionStatus.Success, EntityName: "Legacy English summary")
            .WithToolResultMessage(Call(tool, JsonSerializer.Serialize(new { action = operation })));

        card.MessageKey.Should().Be("chat.toolResult." + key);
        card.Params.Should().Be(new ToolResultMessageParams());
        card.EntityName.Should().Be("Legacy English summary");
    }

    [Theory]
    [InlineData("set_ai_summary", "aiSummary")]
    [InlineData("set_proactive_astra", "proactiveAstra")]
    [InlineData("manage_calendar_sync", "calendarAutoSync")]
    public void ToggleCard_HasKeysForBothStates(string tool, string key)
    {
        foreach (var enabled in new[] { false, true })
        {
            var args = JsonSerializer.Serialize(new { action = "set_auto_sync", enabled });
            var card = new ActionResult("LegacyType", ActionStatus.Success).WithToolResultMessage(Call(tool, args));

            card.MessageKey.Should().Be($"chat.toolResult.{key}.{(enabled ? "enabled" : "disabled")}");
            card.Params.Should().Be(new ToolResultMessageParams());
        }
    }

    [Theory]
    [InlineData("set_timezone", "timezone", "America/Sao_Paulo", "timezoneSet")]
    [InlineData("set_language", "language", "pt-BR", "languageSet")]
    public void ProfileCard_UsesPreferenceValue(string operation, string parameter, string value, string key)
    {
        var args = JsonSerializer.Serialize(new Dictionary<string, string> { ["action"] = operation, [parameter] = value });
        var card = new ActionResult("LegacyType", ActionStatus.Success).WithToolResultMessage(Call("update_profile_preferences", args));

        card.MessageKey.Should().Be("chat.toolResult." + key);
        card.Params.Should().Be(new ToolResultMessageParams(Value: value));
    }

    [Theory]
    [InlineData(0, false, 12)]
    [InlineData(1, true, 24)]
    public void ProfileCard_UsesNumbersForDayAndClock(int day, bool uses24HourClock, int hours)
    {
        var dayArgs = JsonSerializer.Serialize(new { action = "set_week_start_day", week_start_day = day });
        var clockArgs = JsonSerializer.Serialize(new { action = "set_clock_format", uses_24_hour_clock = uses24HourClock });
        var legacy = new ActionResult("LegacyType", ActionStatus.Success);

        var dayCard = legacy.WithToolResultMessage(Call("update_profile_preferences", dayArgs));
        var clockCard = legacy.WithToolResultMessage(Call("update_profile_preferences", clockArgs));

        dayCard.MessageKey.Should().Be("chat.toolResult.weekStartDaySet");
        dayCard.Params.Should().Be(new ToolResultMessageParams(WeekStartDay: day));
        clockCard.MessageKey.Should().Be("chat.toolResult.clockFormatSet");
        clockCard.Params.Should().Be(new ToolResultMessageParams(Hours: hours));
    }

    [Fact]
    public void CreateHabitsCard_CountsOnlySuccessfulResults()
    {
        var payload = new BulkCreateResult([new(0, BulkItemStatus.Success), new(1, BulkItemStatus.Failed)]);
        var card = new ActionResult("BulkCreateHabits", ActionStatus.Success, EntityName: "1/2 habits created")
            .WithToolResultMessage(Call("bulk_create_habits", """{"habits":[{},{}]}"""), payload);

        card.MessageKey.Should().Be("chat.toolResult.createdHabits");
        card.Params.Should().Be(new ToolResultMessageParams(Count: 1, TotalCount: 2));
    }

    [Theory]
    [InlineData("reorder_habits", "reorderedHabits", """{"positions":[{},{}]}""")]
    [InlineData("reorder_goals", "reorderedGoals", """{"positions":[{},{}]}""")]
    [InlineData("delete_notifications", "notificationsDeleted", """{"action":"delete_selected","notification_ids":["first","second"]}""")]
    public void CountCard_UsesRawArrayCount(string tool, string key, string args)
    {
        var card = new ActionResult("LegacyType", ActionStatus.Success).WithToolResultMessage(Call(tool, args));

        card.MessageKey.Should().Be("chat.toolResult." + key);
        card.Params.Should().Be(new ToolResultMessageParams(Count: 2));
    }

    [Theory]
    [InlineData("manage_api_keys", """{"action":"create"}""", "apiKeyCreated")]
    [InlineData("get_referral_code", "{}", "referralCode")]
    public void ValueCard_PreservesNameOrCode(string tool, string args, string key)
    {
        var card = new ActionResult("LegacyType", ActionStatus.Success, EntityName: "My value")
            .WithToolResultMessage(Call(tool, args));

        card.MessageKey.Should().Be("chat.toolResult." + key);
        card.Params.Should().Be(tool == "get_referral_code"
            ? new ToolResultMessageParams(Value: "My value") : new ToolResultMessageParams(EntityName: "My value"));
    }

    [Theory]
    [InlineData("Operation is unsupported by policy.", "unsupportedByPolicy")]
    [InlineData("Confirmation required before this action can run.", "confirmationRequired")]
    [InlineData("An unexpected error occurred.", "unexpectedError")]
    [InlineData("Tool arguments exceeded the clarification stash limit.", "clarificationLimitExceeded")]
    [InlineData("Policy denied: missing_scope", "policyDenied")]
    [InlineData("A command validation error", "failed")]
    public void FailureCard_HasKeyWithoutEnglishErrorInParams(string error, string key)
    {
        var card = new ActionResult("LegacyType", ActionStatus.Failed, Error: error).WithToolResultMessage(Call("create_habit"));

        card.MessageKey.Should().Be("chat.toolResult." + key);
        card.Params.Should().Be(new ToolResultMessageParams());
        card.Error.Should().Be(error);
    }

    [Theory]
    [InlineData(ActionStatus.Suggestion, "suggestedBreakdown")]
    [InlineData(ActionStatus.NeedsClarification, "needsClarification")]
    public void InteractiveCard_HasKey(ActionStatus status, string key)
    {
        var card = new ActionResult("LegacyType", status, EntityName: "Morning routine")
            .WithToolResultMessage(Call("suggest_breakdown"));

        card.MessageKey.Should().Be("chat.toolResult." + key);
        card.Params.Should().Be(status == ActionStatus.Suggestion
            ? new ToolResultMessageParams(EntityName: "Morning routine") : new ToolResultMessageParams());
    }

    [Fact]
    public void UnknownFutureSuccess_StillHasMetadata()
    {
        var card = new ActionResult("FutureTool", ActionStatus.Success).WithToolResultMessage(Call("future_tool"));

        card.MessageKey.Should().Be("chat.toolResult.completed");
        card.Params.Should().Be(new ToolResultMessageParams());
    }

    [Fact]
    public void ActionContractSnapshot_OnlyAppendsOptionalFields()
    {
        var legacy = new ActionResult("CreateHabit", ActionStatus.Success,
            Guid.Parse("11111111-1111-1111-1111-111111111111"), "Read");
        var expected = """{"type":"CreateHabit","status":"Success","entityId":"11111111-1111-1111-1111-111111111111","entityName":"Read","error":null,"field":null,"suggestedSubHabits":null,"clarificationRequest":null}""";

        JsonSerializer.Serialize(legacy, ChatStreamEvent.SerializerOptions).Should().Be(expected);
        var enriched = legacy.WithToolResultMessage(Call("create_habit"));
        WithoutMetadata(enriched).Should().Be(expected);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(enriched, ChatStreamEvent.SerializerOptions));
        json.RootElement.EnumerateObject().Select(property => property.Name).Should().HaveCount(10);
        json.RootElement.GetProperty("messageKey").GetString().Should().Be("chat.toolResult.createdHabit");
        json.RootElement.GetProperty("params").GetRawText().Should().Be("""{"entityName":"Read"}""");
        JsonSerializer.Deserialize<ActionResult>(expected, ChatStreamEvent.SerializerOptions).Should().Be(legacy);
        JsonSerializer.Deserialize<ActionResult>(JsonSerializer.Serialize(enriched, ChatStreamEvent.SerializerOptions),
            ChatStreamEvent.SerializerOptions).Should().Be(enriched);
    }

    private static AiToolCall Call(string name, string args = "{}") => new(name, "call", JsonDocument.Parse(args).RootElement);

    private static string WithoutMetadata(ActionResult card) =>
        JsonSerializer.Serialize(card with { MessageKey = null, Params = null }, ChatStreamEvent.SerializerOptions);
}
