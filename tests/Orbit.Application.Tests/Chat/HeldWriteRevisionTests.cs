using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Auth.Services;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Tests.Chat;

public sealed class HeldWriteRevisionTests
{
    public static TheoryData<string, string, string, string, string> EditableTools => new()
    {
        { "create_habit", """{"title":"Read"}""", "title", "\"Write\"", "Title" },
        { "update_habit", """{"habit_id":"$habit","title":"Read"}""", "title", "\"Write\"", "Title" },
        { "assign_tags", """{"habit_id":"$habit","tag_names":["Study"]}""", "tag_names", """["Work"]""", "Tags.0.Name" },
        { "bulk_create_habits", """{"habits":[{"title":"Read"}]}""", "title", "\"Write\"", "Habits.0.Title" },
        { "bulk_update_habits", """{"filter":{"all":true},"updates":{"title":"Read"}}""", "title", "\"Write\"", "Changes.Title" },
        { "bulk_reschedule_habits", """{"filter":{"all":true},"due_date":"2026-10-01"}""", "due_date", "\"2026-10-02\"", "Changes.DueDate" },
        { "bulk_log_habits", """{"filter":{"all":true},"date":"2026-09-29"}""", "date", "\"2026-09-30\"", "Items.0.Date" },
        { "bulk_skip_habits", """{"filter":{"all":true},"date":"2026-09-29"}""", "date", "\"2026-09-30\"", "Items.0.Date" },
        { "bulk_update_habit_emojis", """{"filter":{"all":true},"emoji":"📚"}""", "emoji", "\"✍️\"", "Emoji" },
        { "create_goal", """{"title":"Read","goal_type":"Standard"}""", "title", "\"Write\"", "Title" },
        { "create_sub_habit", """{"parent_habit_id":"$habit","title":"Read"}""", "title", "\"Write\"", "Title" },
        { "log_habit", """{"habit_id":"$habit","date":"2026-09-29"}""", "date", "\"2026-09-30\"", "Date" },
        { "skip_habit", """{"habit_id":"$habit","date":"2026-09-29"}""", "date", "\"2026-09-30\"", "Date" },
        { "update_profile_preferences", """{"action":"set_timezone","timezone":"UTC"}""", "timezone", "\"America/Sao_Paulo\"", "TimeZone" },
        { "update_profile_preferences", """{"action":"set_language","language":"en"}""", "language", "\"pt-BR\"", "Language" },
        { "update_profile_preferences", """{"action":"set_week_start_day","week_start_day":0}""", "week_start_day", "1", "WeekStartDay" },
        { "update_profile_preferences", """{"action":"set_clock_format","uses_24_hour_clock":false}""", "uses_24_hour_clock", "true", "Uses24HourClock" },
        { "update_profile_preferences", """{"action":"set_theme_preference","theme_preference":"light"}""", "theme_preference", "\"dark\"", "Preference" },
        { "set_ai_memory", """{"enabled":true}""", "enabled", "false", "Enabled" },
        { "set_ai_summary", """{"enabled":true}""", "enabled", "false", "Enabled" },
        { "update_notifications", """{"action":"subscribe_push","endpoint":"https://example.com/a","p256dh":"key","auth":"secret"}""", "endpoint", "\"https://example.com/b\"", "Endpoint" },
        { "update_notifications", """{"action":"unsubscribe_push","endpoint":"https://example.com/a"}""", "endpoint", "\"https://example.com/b\"", "Endpoint" },
        { "manage_calendar_sync", """{"action":"set_auto_sync","enabled":true}""", "enabled", "false", "Enabled" },
        { "create_checklist_template", """{"name":"Prepare","items":["Read"]}""", "items", """["Write"]""", "Items.0" },
        { "manage_subscription", """{"action":"create_checkout","interval":"monthly"}""", "interval", "\"yearly\"", "Interval" },
        { "manage_api_keys", """{"action":"create","name":"Read","scopes":["read_habits"]}""", "name", "\"Write\"", "Name" },
        { "manage_account", """{"action":"confirm_deletion","code":"000000"}""", "code", "\"$code\"", "Code" },
        { "send_support_request", """{"name":"Reader","email":"reader@example.com","subject":"Help","message":"Read"}""", "message", "\"Write\"", "Message" },
        { "update_checklist", """{"habit_id":"$habit","checklist_items":[{"text":"Read"}]}""", "checklist_items", """[{"text":"Write"}]""", "ChecklistItems.0.Text" },
        { "reorder_habits", """{"positions":[{"habit_id":"$habit","position":0}]}""", "positions", """[{"habit_id":"$habit","position":2}]""", "Positions.0.Position" },
        { "reorder_goals", """{"positions":[{"goal_id":"$goal","position":0}]}""", "positions", """[{"goal_id":"$goal","position":2}]""", "Positions.0.Position" },
        { "create_tag", """{"name":"Read","color":"#7c3aed"}""", "name", "\"Write\"", "Name" },
        { "update_tag", """{"tag_id":"$tag","name":"Read","color":"#7c3aed"}""", "name", "\"Write\"", "Name" },
        { "update_goal", """{"goal_id":"$goal","title":"Read"}""", "title", "\"Write\"", "Title" },
        { "update_goal_progress", """{"goal_id":"$goal","current_value":1}""", "current_value", "2", "CurrentValue" },
        { "update_goal_status", """{"goal_id":"$goal","status":"Completed"}""", "status", "\"Abandoned\"", "Status" },
        { "suggest_breakdown", """{"title":"Read","suggested_sub_habits":[{"title":"Read"}]}""", "title", "\"Write\"", "EntityName" }
    };

    [Theory]
    [MemberData(nameof(EditableTools))]
    public async Task ValidEdit_ExecutesStoredRevisedValue(string name, string arguments, string field,
        string value, string executionPath)
    {
        using var context = new HeldWriteTestContext();
        var tool = context.Tool(name);
        var (revision, revised) = await context.ReviseAsync(tool, arguments, $"{{\"{field}\":{value}}}");
        revision.IsSuccess.Should().BeTrue(revision.Error);
        context.Commands.Should().BeEmpty("checking a revision cannot dispatch a write command");
        context.Added.Should().BeEmpty();
        context.Habit.Title.Should().Be("Read");
        context.Goal.CurrentValue.Should().Be(0);
        context.Goal.Status.Should().Be(GoalStatus.Active);
        await context.UnitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());

        var execution = await tool.ExecuteAsync(revised, context.UserId, CancellationToken.None);

        execution.Success.Should().BeTrue(execution.Error);
        object written = name switch
        {
            "assign_tags" or "bulk_update_habit_emojis" or "update_habit" => context.Habit,
            "create_habit" => context.Added.OfType<Habit>().Single(),
            "create_goal" => context.Added.OfType<Goal>().Single(),
            "update_goal" or "update_goal_progress" or "update_goal_status" => context.Goal,
            "suggest_breakdown" => execution,
            _ => context.Commands.Single()
        };
        var actual = AtPath(JsonSerializer.SerializeToElement(written), executionPath);
        var expected = name switch
        {
            "create_checklist_template" or "update_checklist" => JsonSerializer.SerializeToElement("Write"),
            "assign_tags" => JsonSerializer.SerializeToElement("Work"),
            "reorder_habits" or "reorder_goals" => JsonSerializer.SerializeToElement(2),
            "update_goal_status" => JsonSerializer.SerializeToElement((int)GoalStatus.Abandoned),
            _ => JsonSerializer.Deserialize<JsonElement>(context.Expand(value))
        };
        JsonElement.DeepEquals(actual, expected).Should().BeTrue($"{name} must execute the approved edited value at {executionPath}, got {actual}");
    }

    public static IEnumerable<object[]> EditableFields => EditableTools.Select(row => row.Take(3).ToArray());

    [Theory]
    [MemberData(nameof(EditableFields))]
    public async Task WrongType_ReturnsInvalidRevisionAndPreservesHold(string name, string arguments,
        string field)
    {
        using var context = new HeldWriteTestContext();
        var (revision, revised) = await context.ReviseAsync(context.Tool(name), arguments, $"{{\"{field}\":{{}}}}");
        revision.IsSuccess.Should().BeFalse();
        revision.Error.Should().Be("invalid_revision");
        JsonElement.DeepEquals(revised, JsonSerializer.Deserialize<JsonElement>(context.Expand(arguments)))
            .Should().BeTrue();
        context.Commands.Should().BeEmpty();
        context.Added.Should().BeEmpty();
    }

    public static TheoryData<string, string, string> InvalidValues => new()
    {
        { "assign_tags", """{"habit_id":"$habit","tag_names":["Study"]}""", """{"tag_names":[""]}""" },
        { "bulk_create_habits", """{"habits":[{"title":"Read","sub_habits":[{"title":"Step"}]}]}""", """{"sub_habits":[{"title":""}]}""" },
        { "bulk_create_habits", """{"habits":[{"title":"Read"}]}""", """{"title":""}""" },
        { "bulk_update_habits", """{"filter":{"all":true},"updates":{"frequency_unit":"Day"}}""", """{"frequency_unit":"Fortnight"}""" },
        { "bulk_update_habits", """{"filter":{"all":true},"updates":{"title":"Read"}}""", """{"title":""}""" },
        { "bulk_reschedule_habits", """{"filter":{"all":true},"due_date":"2026-10-01"}""", """{"due_date":"10/02/2026"}""" },
        { "bulk_log_habits", """{"filter":{"all":true},"date":"2026-09-29"}""", """{"date":"2026-10-01"}""" },
        { "bulk_skip_habits", """{"filter":{"all":true},"date":"2026-09-29"}""", """{"date":"2026-10-01"}""" },
        { "bulk_update_habit_emojis", """{"filter":{"all":true},"emoji":"📚"}""", """{"emoji":"words"}""" },
        { "create_goal", """{"title":"Read","goal_type":"Standard"}""", """{"goal_type":"Unknown"}""" },
        { "create_goal", """{"title":"Read","target_value":1}""", """{"target_value":0}""" },
        { "create_goal", """{"title":"Read","unit":"books"}""", """{"unit":""}""" },
        { "create_sub_habit", """{"parent_habit_id":"$habit","title":"Read"}""", """{"title":""}""" },
        { "log_habit", """{"habit_id":"$habit","date":"2026-09-29"}""", """{"date":"2026-10-01"}""" },
        { "skip_habit", """{"habit_id":"$habit","date":"2026-09-29"}""", """{"date":"2026-10-01"}""" },
        { "update_profile_preferences", """{"action":"set_timezone","timezone":"UTC"}""", """{"timezone":"Invalid/Zone"}""" },
        { "update_profile_preferences", """{"action":"set_language","language":"en"}""", """{"language":"invalid"}""" },
        { "update_profile_preferences", """{"action":"set_week_start_day","week_start_day":0}""", """{"week_start_day":2}""" },
        { "update_profile_preferences", """{"action":"set_theme_preference","theme_preference":"light"}""", """{"theme_preference":"invalid"}""" },
        { "update_notifications", """{"action":"subscribe_push","endpoint":"https://example.com/a","p256dh":"key","auth":"secret"}""", """{"endpoint":"http://example.com/b"}""" },
        { "manage_subscription", """{"action":"create_checkout","interval":"monthly"}""", """{"interval":"weekly"}""" },
        { "manage_api_keys", """{"action":"create","name":"Key","scopes":["read_habits"]}""", """{"scopes":["invented:write"]}""" },
        { "manage_api_keys", """{"action":"create","name":"Key","is_read_only":false}""", """{"is_read_only":null}""" },
        { "manage_account", """{"action":"confirm_deletion","code":"$code"}""", """{"code":"000000"}""" },
        { "send_support_request", """{"name":"Reader","email":"reader@example.com","subject":"Help","message":"Read"}""", """{"email":"invalid"}""" },
        { "update_checklist", """{"habit_id":"$habit","checklist_items":[{"text":"Read"}]}""", """{"checklist_items":[{"text":""}]}""" },
        { "reorder_habits", """{"positions":[{"habit_id":"$habit","position":0}]}""", """{"positions":[{"habit_id":"$foreign","position":2}]}""" },
        { "reorder_habits", """{"positions":[{"habit_id":"$habit","position":0}]}""", """{"positions":[{"habit_id":"$habit","position":-1}]}""" },
        { "reorder_goals", """{"positions":[{"goal_id":"$goal","position":0}]}""", """{"positions":[{"goal_id":"$foreign","position":2}]}""" },
        { "create_tag", """{"name":"Read","color":"#7c3aed"}""", """{"color":"invalid"}""" },
        { "update_tag", """{"tag_id":"$tag","name":"Read","color":"#7c3aed"}""", """{"name":""}""" },
        { "update_goal", """{"goal_id":"$goal","target_value":1}""", """{"target_value":0}""" },
        { "update_goal", """{"goal_id":"$goal","title":"Read"}""", """{"title":null}""" },
        { "update_goal_progress", """{"goal_id":"$goal","current_value":1}""", """{"current_value":-1}""" },
        { "update_goal_status", """{"goal_id":"$goal","status":"Completed"}""", """{"status":"Active"}""" }
    };

    [Theory]
    [MemberData(nameof(InvalidValues))]
    public async Task InvalidValue_ReturnsInvalidRevisionAndPreservesHold(string name, string arguments, string edits)
    {
        using var context = new HeldWriteTestContext();
        var (revision, revised) = await context.ReviseAsync(context.Tool(name), arguments, edits);
        revision.Error.Should().Be("invalid_revision");
        JsonElement.DeepEquals(revised, JsonSerializer.Deserialize<JsonElement>(context.Expand(arguments)))
            .Should().BeTrue();
        context.Commands.Should().BeEmpty();
        context.Added.Should().BeEmpty();
    }

    [Theory]
    [InlineData("assign_tags", "tag_names")]
    [InlineData("create_checklist_template", "items")]
    [InlineData("update_checklist", "checklist_items")]
    public async Task OverCapList_ReturnsInvalidRevisionAndPreservesHold(string name, string field)
    {
        using var context = new HeldWriteTestContext();
        var arguments = name switch
        {
            "assign_tags" => """{"habit_id":"$habit","tag_names":["Study"]}""",
            "create_checklist_template" => """{"name":"Study","items":["Read"]}""",
            _ => """{"habit_id":"$habit","checklist_items":[{"text":"Read"}]}"""
        };
        var cap = name == "assign_tags" ? AppConstants.MaxTagsPerHabit : AppConstants.MaxChecklistItems;
        var entries = Enumerable.Repeat(name == "update_checklist" ? (object)new { text = "Read" } : "Read", cap + 1);
        var edits = JsonSerializer.Serialize(new Dictionary<string, object> { [field] = entries });
        var (revision, revised) = await context.ReviseAsync(context.Tool(name), arguments, edits);
        revision.Error.Should().Be("invalid_revision");
        JsonElement.DeepEquals(revised, JsonSerializer.Deserialize<JsonElement>(context.Expand(arguments)))
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("update_profile_preferences", """{"action":"set_language","language":"en","timezone":"UTC"}""", "timezone")]
    [InlineData("manage_account", """{"action":"request_deletion","code":"000000"}""", "code")]
    [InlineData("manage_subscription", """{"action":"create_portal","interval":"monthly"}""", "interval")]
    [InlineData("manage_calendar_sync", """{"action":"run_sync","enabled":true}""", "enabled")]
    [InlineData("manage_api_keys", """{"action":"revoke","key_id":"$habit","name":"Key"}""", "name")]
    [InlineData("update_notifications", """{"action":"mark_all_read","endpoint":"https://example.com/a"}""", "endpoint")]
    [InlineData("suggest_breakdown", """{"title":"Read","suggested_sub_habits":[{"title":"Read"}]}""", "suggested_sub_habits")]
    public async Task IgnoredArgument_RemainsFixed(string name, string arguments, string field)
    {
        using var context = new HeldWriteTestContext();
        var tool = context.Tool(name);
        var preview = AgentArgumentPreview.Build(name, JsonSerializer.Deserialize<JsonElement>(context.Expand(arguments)),
            null, JsonSerializer.SerializeToElement(tool.GetParameterSchema()));
        preview!.Items![0].Fields.Single(change => change.Field == field).IsEditable.Should().BeFalse();
        var (revision, _) = await context.ReviseAsync(tool, arguments, $"{{\"{field}\":null}}");
        revision.Error.Should().Be("field_not_offered");
    }

    [Theory]
    [InlineData("create_tag", """{"name":"Other","color":"#7c3aed"}""")]
    [InlineData("update_tag", """{"tag_id":"$tag","name":"Study","color":"#7c3aed"}""")]
    public async Task DuplicateTagName_ReturnsInvalidRevision(string name, string arguments)
    {
        using var context = new HeldWriteTestContext();
        context.Records.Add(Tag.Create(context.UserId, "Taken", "#ffffff").Value);
        var (revision, revised) = await context.ReviseAsync(context.Tool(name), arguments, """{"name":"Taken"}""");
        revision.Error.Should().Be("invalid_revision");
        JsonElement.DeepEquals(revised, JsonSerializer.Deserialize<JsonElement>(context.Expand(arguments))).Should().BeTrue();
    }

    [Fact]
    public async Task DeleteSelectedNotifications_ChecksEveryOwnerWithoutDeleting()
    {
        using var context = new HeldWriteTestContext();
        var own = Notification.Create(context.UserId, "Read", "Read");
        var foreign = Notification.Create(Guid.NewGuid(), "Foreign", "Foreign");
        context.Records.AddRange([own, foreign]);
        var args = JsonSerializer.SerializeToElement(new { action = "delete_selected", notification_ids = new[] { own.Id, foreign.Id } });

        var result = await ((IArgumentCheckTool)context.Tool("delete_notifications"))
            .CheckArgumentsAsync(args, context.UserId, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        own.IsDeleted.Should().BeFalse();
        foreign.IsDeleted.Should().BeFalse();
        context.Commands.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task AccountDeletionRevision_SharesLockoutWithOrdinaryConfirmation(int ordinaryAttempts)
    {
        using var context = new HeldWriteTestContext();
        var email = context.Records.OfType<User>().Single().Email;
        var tool = context.Tool("manage_account");
        const string arguments = """{"action":"confirm_deletion","code":"$code"}""";
        var original = JsonSerializer.Deserialize<JsonElement>(context.Expand(arguments));

        for (var attempt = 0; attempt < ordinaryAttempts; attempt++)
            context.Challenges.Confirm(EmailChallengeOperation.AccountDeletion, email, "000000")
                .Error.Should().Be(ErrorMessages.InvalidDeletionCode.Format(AppConstants.MaxVerificationAttempts - attempt - 1).Message);
        for (var attempt = ordinaryAttempts; attempt < AppConstants.MaxVerificationAttempts; attempt++)
        {
            var (invalid, unchanged) = await context.ReviseAsync(tool, arguments, """{"code":"000000"}""");
            invalid.Error.Should().Be("invalid_revision");
            JsonElement.DeepEquals(unchanged, original).Should().BeTrue();
        }

        var (locked, stored) = await context.ReviseAsync(tool, arguments, """{"code":"$code"}""");
        locked.Error.Should().Be("invalid_revision");
        JsonElement.DeepEquals(stored, original).Should().BeTrue();
        var check = await ((IArgumentCheckTool)tool).CheckArgumentsAsync(original, context.UserId, CancellationToken.None);
        check.Error.Should().Be(ErrorMessages.TooManyCodeAttempts.Message);
        var confirmation = context.Challenges.Confirm(EmailChallengeOperation.AccountDeletion, email, context.DeletionCode);
        confirmation.Error.Should().Be(ErrorMessages.TooManyCodeAttempts.Message);
        context.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task AccountDeletionRevision_LeavesCorrectCodeForOrdinaryConfirmation()
    {
        using var context = new HeldWriteTestContext();
        var tool = context.Tool("manage_account");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var (revision, _) = await context.ReviseAsync(tool,
                """{"action":"confirm_deletion","code":"000000"}""", """{"code":"$code"}""");
            revision.IsSuccess.Should().BeTrue(revision.Error);
        }
        var email = context.Records.OfType<User>().Single().Email;
        context.Challenges.Confirm(EmailChallengeOperation.AccountDeletion, email, context.DeletionCode)
            .IsSuccess.Should().BeTrue();
        context.Commands.Should().BeEmpty();
    }

    [Theory]
    [InlineData(EmailChallengeOperation.AccountDeletion)]
    [InlineData(EmailChallengeOperation.ApiKeyManagement)]
    public void ChallengeCheck_WrongCodesExhaustConfirmationBudget(EmailChallengeOperation operation)
    {
        using var context = new HeldWriteTestContext();
        const string email = "challenge@example.com";
        var code = context.Challenges.Issue(operation, email).Value;
        for (var attempt = 0; attempt < AppConstants.MaxVerificationAttempts; attempt++)
        {
            var invalid = context.Challenges.CheckConfirmation(operation, email, "000000");
            var expected = operation == EmailChallengeOperation.AccountDeletion
                ? ErrorMessages.InvalidDeletionCode : ErrorMessages.InvalidApiKeyCreationCode;
            invalid.Error.Should().Be(expected.Format(AppConstants.MaxVerificationAttempts - attempt - 1).Message);
        }

        context.Challenges.CheckConfirmation(operation, email, code)
            .Error.Should().Be(ErrorMessages.TooManyCodeAttempts.Message);
        context.Challenges.Confirm(operation, email, code)
            .Error.Should().Be(ErrorMessages.TooManyCodeAttempts.Message);
    }

    [Theory]
    [InlineData("checklist_items", """[{"text":"Before"}]""", """[{"text":"After","is_checked":true}]""")]
    [InlineData("checklist_items", """[{"text":"Before"}]""", """[{"text":"After","is_checked":false}]""")]
    [InlineData("checklist_items", """[{"text":"Before"}]""", """[{"text":"After"}]""")]
    [InlineData("scheduled_reminders", """[{"when":"same_day","time":"08:00"}]""", """[{"when":"same_day","time":"09:00"}]""")]
    [InlineData("scheduled_reminders", """[{"when":"same_day","time":"08:00"}]""", """[{"when":"day_before","time":"09:00"}]""")]
    public async Task BulkObjectListEdit_ExecutesStoredRevisedValue(string field, string before, string after)
    {
        using var context = new HeldWriteTestContext();
        var tool = context.Tool("bulk_update_habits");
        var arguments = $$$"""{"filter":{"all":true},"updates":{"{{{field}}}":{{{before}}}}}""";
        var (revision, revised) = await context.ReviseAsync(tool, arguments, $$"""{"{{field}}":{{after}}}""");

        revision.IsSuccess.Should().BeTrue(revision.Error);
        context.Commands.Should().BeEmpty();
        await context.UnitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        var execution = await tool.ExecuteAsync(revised, context.UserId, CancellationToken.None);

        execution.Success.Should().BeTrue(execution.Error);
        var command = context.Commands.Should().ContainSingle().Subject.Should().BeOfType<BulkUpdateHabitsCommand>().Subject;
        if (field == "checklist_items")
        {
            command.Changes.HasChecklistItems.Should().BeTrue();
            command.Changes.ChecklistItems.Should().Equal(new ChecklistItem("After", after.Contains("true", StringComparison.Ordinal)));
        }
        else
        {
            command.Changes.HasScheduledReminders.Should().BeTrue();
            command.Changes.ScheduledReminders.Should().Equal(new ScheduledReminderTime(
                after.Contains("day_before", StringComparison.Ordinal) ? ScheduledReminderWhen.DayBefore : ScheduledReminderWhen.SameDay,
                new TimeOnly(9, 0)));
        }
    }

    [Theory]
    [InlineData("checklist_items", """[{"is_checked":false}]""")]
    [InlineData("checklist_items", """[{"text":null}]""")]
    [InlineData("checklist_items", """[{"text":"After","is_checked":"false"}]""")]
    [InlineData("checklist_items", """[{"text":"After","unexpected":true}]""")]
    [InlineData("scheduled_reminders", """[{"when":"same_day"}]""")]
    [InlineData("scheduled_reminders", """[{"time":"09:00"}]""")]
    [InlineData("scheduled_reminders", """[{"when":"next_day","time":"09:00"}]""")]
    [InlineData("scheduled_reminders", """[{"when":"same_day","time":"9:00"}]""")]
    [InlineData("scheduled_reminders", """[{"when":"same_day","time":900}]""")]
    [InlineData("scheduled_reminders", """[{"when":"same_day","time":"09:00","unexpected":true}]""")]
    public async Task BulkObjectListMalformedEntry_ReturnsInvalidRevisionAndPreservesHold(string field, string entries)
    {
        using var context = new HeldWriteTestContext();
        var before = field == "checklist_items" ? """[{"text":"Before"}]""" : """[{"when":"same_day","time":"08:00"}]""";
        var arguments = $$$"""{"filter":{"all":true},"updates":{"{{{field}}}":{{{before}}}}}""";
        var (revision, stored) = await context.ReviseAsync(context.Tool("bulk_update_habits"), arguments,
            $$"""{"{{field}}":{{entries}}}""");

        revision.Error.Should().Be("invalid_revision");
        JsonElement.DeepEquals(stored, JsonSerializer.Deserialize<JsonElement>(arguments)).Should().BeTrue();
        context.Commands.Should().BeEmpty();
        await context.UnitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    private static JsonElement AtPath(JsonElement value, string path)
    {
        foreach (var part in path.Split('.'))
            value = int.TryParse(part, out var index) ? value[index] : value.GetProperty(part);
        return value;
    }
}
