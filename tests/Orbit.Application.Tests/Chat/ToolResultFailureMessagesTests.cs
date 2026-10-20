using System.Text.Json;
using System.Linq.Expressions;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Domain.Models;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;

namespace Orbit.Application.Tests.Chat;

public sealed class ToolResultFailureMessagesTests
{
    public static TheoryData<string, string, ToolResultMessageParams> SharedFailures => new()
    {
        { ErrorMessages.HabitNotFound.Message, "habitNotFound", new() },
        { ErrorMessages.GoalNotFound.Message, "goalNotFound", new() },
        { ErrorMessages.TagNotFound.Message, "tagNotFound", new() },
        { ErrorMessages.InvalidGoalStatus.Message, "invalidGoalStatus", new() },
        { ErrorMessages.HabitNotOwned.Message, "habitNotOwned", new() },
        { ErrorMessages.CannotLogFutureDate.Message, "cannotLogFutureDate", new() },
        { ErrorMessages.MaxHabitsPerGoal.Format(AppConstants.MaxHabitsPerGoal).Message,
            "maxHabitsPerGoal", new(Count: AppConstants.MaxHabitsPerGoal) },
        { $"A goal can have at most {AppConstants.MaxHabitsPerGoal} linked habits.",
            "maxHabitsPerGoal", new(Count: AppConstants.MaxHabitsPerGoal) }
    };

    [Theory]
    [MemberData(nameof(SharedFailures))]
    public void SharedFailure_UsesSpecificKeyAndTypedParams(string error, string key, ToolResultMessageParams parameters)
    {
        var message = ToolResultFailureMessages.Describe(error);

        message.Should().Be((key, parameters));
    }

    [Fact]
    public async Task BulkDeleteFailure_UsesActualSelectionResult()
    {
        var repository = Substitute.For<IGenericRepository<Habit>>();
        repository.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Habit>());
        var tool = new BulkDeleteHabitsTool(Substitute.For<IMediator>(), repository);
        var call = new AiToolCall(tool.Name, "call", JsonDocument.Parse("""{"filter":{"all":true}}""").RootElement);

        var result = await tool.ExecuteAsync(call.Args, Guid.NewGuid(), CancellationToken.None);
        var card = new ActionResult(tool.Name, ActionStatus.Failed, Error: result.Error).WithToolResultMessage(call);

        result.Success.Should().BeFalse();
        card.Error.Should().Be("No matching habits found to delete.");
        card.MessageKey.Should().Be("chat.toolResult.noHabitsToDelete");
        card.Params.Should().Be(new ToolResultMessageParams());
    }

    [Theory]
    [InlineData("Operation is unsupported by policy.", "unsupportedByPolicy")]
    [InlineData("An unexpected error occurred.", "unexpectedError")]
    [InlineData("Tool arguments exceeded the clarification stash limit.", "clarificationLimitExceeded")]
    [InlineData("No matching habits found to delete.", "noHabitsToDelete")]
    [InlineData("No matching habits found to update.", "noHabitsToUpdate")]
    [InlineData("No matching habits found to log.", "noHabitsToLog")]
    [InlineData("No matching habits found to skip.", "noHabitsToSkip")]
    [InlineData("No matching habits found.", "noMatchingHabits")]
    [InlineData("notification_ids contains an invalid ID.", "invalidNotificationIds")]
    [InlineData("endpoint, p256dh, and auth are required.", "pushCredentialsRequired")]
    [InlineData("name and color are required.", "nameAndColorRequired")]
    [InlineData("name and at least one item are required.", "checklistTemplateItemsRequired")]
    [InlineData("status is required and must be Active, Completed, or Abandoned.", "goalStatusRequired")]
    [InlineData("habit_ids is unavailable for this tool instance.", "habitIdsUnavailable")]
    [InlineData("habit_ids must be omitted or provided as a non-empty array of valid habit IDs.", "habitIdsSelectorRequired")]
    [InlineData("filter cannot combine with habit_ids.", "filterConflictsWithHabitIds")]
    [InlineData("Provide filter or habit_ids.", "habitSelectorRequired")]
    [InlineData("updates contains an unsupported field.", "unsupportedUpdateField")]
    [InlineData("filter contains an unsupported field.", "unsupportedFilterField")]
    [InlineData("filter.habit_ids must be an array of valid habit IDs.", "filterHabitIdsRequired")]
    [InlineData("filter.habit_ids must contain only valid habit IDs.", "invalidFilterHabitIds")]
    [InlineData("filter.frequency must be Day, Week, Month, Year, or OneTime.", "filterFrequencyInvalid")]
    [InlineData("filter cannot combine all=true with habit_ids.", "filterAllConflictsWithHabitIds")]
    [InlineData("filter must explicitly select all habits or provide at least one predicate.", "filterSelectorRequired")]
    [InlineData("frequency_unit must be Day, Week, Month, Year, or null.", "frequencyUnitInvalid")]
    [InlineData("days must contain valid weekday names.", "invalidDays")]
    [InlineData("reminder_times must contain only integers.", "invalidReminderTimes")]
    [InlineData("checklist_items contains an invalid item.", "invalidChecklistItem")]
    [InlineData("scheduled_reminders contains an invalid item.", "invalidScheduledReminder")]
    [InlineData("revised_items contains an invalid habit or date.", "invalidRevisedHabitDate")]
    [InlineData("revised_items contains an invalid habit ID.", "invalidRevisedHabitId")]
    [InlineData("revised_items contains an invalid emoji.", "invalidRevisedEmoji")]
    [InlineData("revised_items requires changes.", "revisedChangesRequired")]
    [InlineData("Invalid date format. Use YYYY-MM-DD.", "invalidDateFormat")]
    [InlineData("expires_at_utc must be a valid ISO-8601 UTC timestamp.", "invalidApiKeyExpiration")]
    [InlineData("parent_id must be a valid GUID when provided.", "invalidParentId")]
    [InlineData("habit_ids must contain only valid GUID strings.", "invalidHabitIds")]
    [InlineData("habit_ids must contain only valid habit IDs.", "invalidHabitIdList")]
    [InlineData("Provide either tag_ids or tag_names.", "tagSelectorRequired")]
    [InlineData("At least one tag name is required.", "tagNameRequired")]
    [InlineData("Provide either goal_id or goal_name.", "goalSelectorRequired")]
    [InlineData("Invalid skip_id.", "invalidSkipId")]
    [InlineData("Provide emoji or set infer_from_title to true.", "emojiRequired")]
    [InlineData("Each habit requires a non-empty title.", "habitTitleRequired")]
    [InlineData("No habits provided.", "noHabitsProvided")]
    [InlineData("Each position requires a valid habit_id GUID and an integer position.", "invalidHabitPosition")]
    [InlineData("Each position requires a valid goal_id GUID and an integer position.", "invalidGoalPosition")]
    [InlineData("Bulk habit operation failed before any changes were applied.", "bulkOperationFailed")]
    [InlineData("This request was started by an earlier server version. Nothing more was changed. Check the habits and repeat the request.", "bulkReplayExpired")]
    [InlineData("name, email, subject, and message are required.", "supportFieldsRequired")]
    [InlineData("Habit not found.", "habitNotFound")]
    [InlineData("Goal not found.", "goalNotFound")]
    public void LiteralFailure_PreservesTextAndUsesSpecificKey(string error, string key)
    {
        var card = new ActionResult("Tool", ActionStatus.Failed, Error: error)
            .WithToolResultMessage(new AiToolCall("tool", "call", JsonDocument.Parse("{}").RootElement));

        card.MessageKey.Should().Be("chat.toolResult." + key);
        card.Params.Should().Be(new ToolResultMessageParams());
        card.Error.Should().Be(error);
    }

    [Theory]
    [InlineData("action is required.", "argumentRequired", "action")]
    [InlineData("goal_id is required and must be a valid GUID.", "guidRequired", "goal_id")]
    [InlineData("key_id must be a valid GUID.", "invalidGuid", "key_id")]
    [InlineData("habits is required and must be an array.", "arrayRequired", "habits")]
    [InlineData("days must be an array.", "invalidArray", "days")]
    [InlineData("revised_items must be a non-empty array.", "nonEmptyArrayRequired", "revised_items")]
    [InlineData("filter is required and must be an object.", "objectRequired", "filter")]
    [InlineData("filter must be an object.", "invalidObject", "filter")]
    [InlineData("current_value is required and must be a number.", "numberRequired", "current_value")]
    [InlineData("filter.all must be a boolean.", "invalidBoolean", "filter.all")]
    [InlineData("filter.tag must be a string.", "invalidString", "filter.tag")]
    [InlineData("emoji must be a string or null.", "invalidNullableString", "emoji")]
    [InlineData("interval_weeks must be an integer or null.", "invalidNullableInteger", "interval_weeks")]
    [InlineData("due_date must be a date in YYYY-MM-DD format.", "invalidDate", "due_date")]
    [InlineData("end_date must be a date in YYYY-MM-DD format or null.", "invalidNullableDate", "end_date")]
    [InlineData("date must use YYYY-MM-DD format.", "invalidDateValue", "date")]
    [InlineData("due_time must use HH:mm format or null.", "invalidNullableTime", "due_time")]
    public void FieldFailure_ExtractsFieldName(string error, string key, string field)
    {
        var (actualKey, parameters) = ToolResultFailureMessages.Describe(error);

        actualKey.Should().Be(key);
        parameters.Should().Be(new ToolResultMessageParams(FieldName: field));
    }

    [Theory]
    [InlineData("Policy denied: missing_scope", "policyDenied", """{"policyReason":"missing_scope"}""")]
    [InlineData("Unsupported action 'unknown'.", "unsupportedAction", """{"actionName":"unknown"}""")]
    [InlineData("Habit 11111111-1111-1111-1111-111111111111 not found.", "habitNotFound", """{"entityId":"11111111-1111-1111-1111-111111111111"}""")]
    [InlineData("Goal 11111111-1111-1111-1111-111111111111 not found.", "goalNotFound", """{"entityId":"11111111-1111-1111-1111-111111111111"}""")]
    [InlineData("No active goal found matching 'Read'.", "activeGoalNotFound", """{"entityName":"Read"}""")]
    public void DynamicFailure_ExtractsTypedParams(string error, string key, string expected)
    {
        var (actualKey, parameters) = ToolResultFailureMessages.Describe(error);

        actualKey.Should().Be(key);
        JsonSerializer.Serialize(parameters, ChatStreamEvent.SerializerOptions).Should().Be(expected);
    }

    [Theory]
    [InlineData(typeof(ManageAccountTool), "{}", "argumentRequired", "action")]
    [InlineData(typeof(ManageApiKeysTool), """{"action":"create"}""", "argumentRequired", "name")]
    [InlineData(typeof(UpdateProfilePreferencesTool), """{"action":"unknown"}""", "unsupportedAction", "unknown")]
    [InlineData(typeof(DeleteNotificationsTool), """{"action":"unknown"}""", "unsupportedAction", "unknown")]
    [InlineData(typeof(DeleteNotificationsTool), """{"action":"delete_selected","notification_ids":[]}""", "nonEmptyArrayRequired", "notification_ids")]
    [InlineData(typeof(DeleteNotificationsTool), """{"action":"delete_selected","notification_ids":["invalid"]}""", "invalidNotificationIds", null)]
    public async Task ToolExecutionFailure_UsesProducerError(Type toolType, string args, string key, string? value)
    {
        var tool = (IAiTool)Activator.CreateInstance(toolType, Substitute.For<IMediator>())!;
        var call = new AiToolCall(tool.Name, "call", JsonDocument.Parse(args).RootElement);

        var result = await tool.ExecuteAsync(call.Args, Guid.NewGuid(), CancellationToken.None);
        var card = new ActionResult(tool.Name, ActionStatus.Failed, Error: result.Error).WithToolResultMessage(call);

        result.Success.Should().BeFalse();
        card.Error.Should().Be(result.Error);
        card.MessageKey.Should().Be("chat.toolResult." + key);
        card.Params.Should().Be(key == "unsupportedAction" ? new ToolResultMessageParams(ActionName: value)
            : new ToolResultMessageParams(FieldName: value));
    }
}
