using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Validators;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Tests.Chat;

public sealed class PendingOperationChangePreviewerTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private readonly IGenericRepository<Habit> _habits = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<Goal> _goals = Substitute.For<IGenericRepository<Goal>>();
    private readonly IGenericRepository<Tag> _tags = Substitute.For<IGenericRepository<Tag>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();

    public PendingOperationChangePreviewerTests() =>
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new DateOnly(2026, 9, 25));

    [Theory]
    [InlineData(HabitSchedule.OneTime, false)]
    [InlineData(HabitSchedule.Recurring, false)]
    [InlineData(HabitSchedule.Flexible, false)]
    [InlineData(HabitSchedule.General, false)]
    public async Task Preview_KeepsScheduleFieldsAndFingerprint(HabitSchedule schedule, bool divergentFlags)
    {
        var habit = Habit.Create(new HabitCreateParams(UserId, "Habit",
            schedule is HabitSchedule.Recurring or HabitSchedule.Flexible ? FrequencyUnit.Day : null,
            schedule == HabitSchedule.Flexible ? 2 : schedule == HabitSchedule.Recurring ? 1 : null,
            new DateOnly(2026, 9, 25), IsGeneral: schedule == HabitSchedule.General, IsFlexible: schedule == HabitSchedule.Flexible)).Value;
        Setup([habit]);
        var args = JsonSerializer.Serialize(new
        {
            habit_id = habit.Id,
            title = "Renamed",
            is_general = schedule == HabitSchedule.General,
            is_flexible = schedule == HabitSchedule.Flexible
        });
        var bulkArgs = JsonSerializer.Serialize(new
        {
            filter = new { all = true },
            updates = new
            {
                title = "Renamed",
                is_general = schedule == HabitSchedule.General,
                is_flexible = schedule == HabitSchedule.Flexible
            }
        });
        var before = await Preview("update_habit", args);
        var bulkBefore = await Preview("bulk_update_habits", bulkArgs);
        if (divergentFlags)
        {
            typeof(Habit).GetProperty(nameof(Habit.IsGeneral))!.SetValue(habit, !habit.IsGeneral);
            typeof(Habit).GetProperty(nameof(Habit.IsFlexible))!.SetValue(habit, !habit.IsFlexible);
        }

        var preview = await Preview("update_habit", args);

        preview.Should().NotBeNull();
        preview!.Changes.Should().Contain(field => field.Field == "title" && field.NewValue == "Renamed");
        var bulkPreview = await Preview("bulk_update_habits", bulkArgs);
        bulkPreview.Should().NotBeNull();
        bulkPreview!.Changes.Should().ContainSingle().Which.Field.Should().Be("title");
        bulkPreview.Should().BeEquivalentTo(bulkBefore);
        preview.Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task ChildCreation_PreviewsEverySuppliedFieldAndResolvesOwnedDependencies()
    {
        var parent = CreateHabit("Parent", null);
        var tag = Tag.Create(UserId, "Health", "#123456").Value;
        _habits.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>()).Returns([parent]);
        _tags.FindAsync(Arg.Any<Expression<Func<Tag, bool>>>(), Arg.Any<CancellationToken>()).Returns([tag]);
        var args = JsonSerializer.SerializeToElement(new
        {
            parent_habit_id = parent.Id,
            title = "Child",
            description = "Outside",
            emoji = "🏃",
            frequency_unit = "Day",
            frequency_quantity = 1,
            interval_weeks = 2,
            days = new[] { "Monday" },
            due_date = "2026-10-19",
            end_date = "2026-12-31",
            due_time = "09:00",
            due_end_time = "10:00",
            is_bad_habit = false,
            is_flexible = false,
            reminder_enabled = true,
            reminder_times = new[] { 15 },
            slip_alert_enabled = true,
            relative_reminders = new[] { new { minutes_before = 30 } },
            scheduled_reminders = Array.Empty<object>(),
            checklist_items = new[] { new { text = "Shoes", is_checked = true } },
            tag_ids = new[] { tag.Id }
        });
        var previewer = new PendingOperationChangePreviewer(_habits, _goals, _tags, _userDateService,
            new AiToolRegistry([new CreateSubHabitTool(Substitute.For<MediatR.IMediator>())]));
        var preview = (await previewer.PreviewAsync(UserId, "create_sub_habit", args)).Value;
        preview.Should().NotBeNull();
        var fields = preview!.Items!.Single().Fields;
        fields.Select(f => f.Field).Should().BeEquivalentTo(args.EnumerateObject().Select(p => p.Name));
        foreach (var property in args.EnumerateObject())
            fields.Single(f => f.Field == property.Name).ProposedValue!.Value.GetRawText().Should().Be(property.Value.GetRawText());
        fields.Single(f => f.Field == "parent_habit_id").NewValue.Should().Be("Parent");
        fields.Single(f => f.Field == "tag_ids").NewValue.Should().Be("Health");
        _tags.FindAsync(Arg.Any<Expression<Func<Tag, bool>>>(), Arg.Any<CancellationToken>()).Returns([]);
        ((await previewer.PreviewAsync(UserId, "create_sub_habit", args)).Value).Should().BeNull();
    }

    [Fact]
    public async Task InlineChildren_PreviewRetainsEverySuppliedChildValue()
    {
        var args = JsonDocument.Parse("""{"title":"Parent","frequency_unit":"Day","sub_habits":[{"title":"Child","due_time":"09:00","due_end_time":"10:00","relative_reminders":[{"minutes_before":30}],"checklist_items":[{"text":"Shoes","is_checked":true}],"tag_names":["Health"]}]}""").RootElement;
        var preview = await Preview("create_habit", args.GetRawText());
        preview.Should().NotBeNull();
        var field = preview!.Items!.Single().Fields.Single(f => f.Field == "sub_habits");
        field.ProposedValue!.Value.GetRawText().Should().Be(args.GetProperty("sub_habits").GetRawText());
        field.NewValue.Should().Contain("09:00").And.Contain("10:00").And.Contain("minutes_before").And.Contain("Shoes").And.Contain("Health");
    }

    [Theory]
    [InlineData("due_end_time", "\"15:00\"", "15:00")]
    [InlineData("is_general", "true", "true")]
    [InlineData("slip_alert_enabled", "true", "true")]
    [InlineData("relative_reminders", "[{\"minutes_before\":30}]", "30 minutes before")]
    public async Task NewFields_PreviewEffectiveValueAndAllowRevision(string field, string value, string expected)
    {
        var habit = Habit.Create(new HabitCreateParams(UserId, "Read", null, null, new DateOnly(2026, 9, 25),
            DueTime: new TimeOnly(9, 0))).Value;
        Setup([habit]);
        var preview = await Preview("bulk_update_habits", $$$"""{"filter":{"all":true},"updates":{"{{{field}}}":{{{value}}}}}""");
        preview.Should().NotBeNull();
        var change = preview!.Items!.Single().Fields.Single(c => c.Field == field);
        change.NewValue.Should().Be(expected);
        change.IsEditable.Should().BeTrue();
        change.ProposedValue.Should().NotBeNull();
        habit.IsGeneral.Should().BeFalse();
        habit.DueEndTime.Should().BeNull();
        habit.RelativeReminders.Should().BeEmpty();
    }

    [Fact]
    public async Task Unlog_DefaultDate_PreviewShowsUserToday()
    {
        var habit = CreateHabit("Beber água", null);
        Setup([habit]);
        var preview = await Preview("unlog_habit", JsonSerializer.Serialize(new { habit_id = habit.Id }));
        preview.Should().NotBeNull();
        preview!.Items.Should().ContainSingle().Which.EntityName.Should().Be(habit.Title);
        preview.Changes.Should().Contain(change => change.Field == "date" && change.NewValue == "2026-09-25");
    }

    [Fact]
    public async Task RoundThree_BulkDelete_UsesNormalizedTitleWordPrefixes()
    {
        var water = CreateHabit("Beber água", null);
        var reading = CreateHabit("Ler 10 minutos", null);
        var allergy = CreateHabit("Alergia", null);
        allergy.Update(new HabitUpdateParams(allergy.Title, "Ler sobre água e meditação",
            allergy.FrequencyUnit, allergy.FrequencyQuantity, allergy.Days.ToArray(), allergy.IsBadHabit, allergy.DueDate))
            .IsSuccess.Should().BeTrue();
        Setup([water, reading, allergy]);
        foreach (var search in new[] { "agua", "ÁGUA", "agu beb" })
        {
            var preview = await Preview("bulk_delete_habits", JsonSerializer.Serialize(new { filter = new { search } }));
            preview!.Items.Should().ContainSingle().Which.EntityId.Should().Be(water.Id);
        }
        var read = await Preview("bulk_delete_habits", """{"filter":{"search":"ler"}}""");
        read!.Items.Should().ContainSingle().Which.EntityId.Should().Be(reading.Id);
        (await Preview("bulk_delete_habits", """{"filter":{"search":"medit"}}""")).Should().BeNull();
    }

    [Fact]
    public async Task RoundThree_UnchangedUpdateFields_JsonOrderDoesNotInvalidateApproval()
    {
        Setup([CreateHabit("Read", null)]);
        var first = await Preview("bulk_update_habits", """{"filter":{"all":true},"updates":{"title":"Read","description":null}}""");
        var reordered = await Preview("bulk_update_habits", """{"updates":{"description":null,"title":"Read"},"filter":{"all":true}}""");
        reordered!.PreviewFingerprint.Should().Be(first!.PreviewFingerprint);
    }

    [Fact]
    public async Task RoundThree_CreateChildren_ShowsParentInEveryItemAndOnceInChanges()
    {
        var parent = CreateHabit("Academia", null);
        _habits.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>()).Returns(new[] { parent });
        var preview = await Preview("bulk_create_habits", JsonSerializer.Serialize(new
        {
            parent_habit_id = parent.Id,
            habits = new[] { new { title = "Aquecimento" }, new { title = "Treino" }, new { title = "Alongamento" } }
        }));
        preview!.Items.Should().HaveCount(3).And.OnlyContain(item => item.Fields.Any(field => field.Field == "parent_habit_id"
            && field.NewValue == "Academia" && !field.IsEditable));
        preview.Changes.Should().ContainSingle().Which.Should().Match<Orbit.Domain.Models.PendingOperationChange>(
            field => field.Field == "parent_habit_id" && field.NewValue == "Academia");
    }

    [Theory]
    [InlineData("Alergia", "ler", false)]
    [InlineData("Ler 10 minutos", "leitura", false)]
    [InlineData("Beber água", "AGUA", true)]
    [InlineData("Meditação", "medit", true)]
    [InlineData("Meditação diária", "DIÁ MED", true)]
    [InlineData("Meditação diária", "med falt", false)]
    [InlineData("Meditação", "!!!", false)]
    public void RoundThree_NameMatching_UsesNormalizedWordPrefixes(string title, string search, bool matches) =>
        HabitNameMatcher.Matches(title, search).Should().Be(matches);

    [Fact]
    public async Task PreviewAsync_Create_JsonPropertyOrderDoesNotChangeApproval()
    {
        var first = await Preview("bulk_create_habits", """{"habits":[{"title":"Academia","frequency_unit":"Day","sub_habits":[{"title":"Treino"}]}]}""");
        var stored = await Preview("bulk_create_habits", """{"habits":[{"sub_habits":[{"title":"Treino"}],"frequency_unit":"Day","title":"Academia"}]}""");

        stored!.PreviewFingerprint.Should().Be(first!.PreviewFingerprint);
    }

    [Fact]
    public async Task PreviewAsync_NoMatches_DoesNotOfferEmptyPreview()
    {
        Setup([]);

        var preview = await Preview("bulk_log_habits", """{"filter":{"search":"meditação"}}""");

        preview.Should().BeNull();
    }

    [Theory]
    [InlineData("bulk_log_habits")]
    [InlineData("bulk_skip_habits")]
    public async Task PreviewAsync_RevisedDates_IgnoreOriginalRangeAndKeepOfferedIdentity(string operationId)
    {
        var habit = CreateHabit("Read", null);
        Setup([habit]);
        var itemId = $"{habit.Id}:2026-10-19";
        var arguments = JsonSerializer.Serialize(new
        {
            habit_ids = new[] { habit.Id },
            dates = new[] { "2026-10-18", "2026-10-19" },
            revised_items = new[] { new { habit_id = habit.Id, date = "2026-10-17", preview_item_id = itemId } }
        });

        var preview = await Preview(operationId, arguments);

        var retained = preview!.Items.Should().ContainSingle().Subject;
        retained.ItemId.Should().Be(itemId);
        retained.EntityId.Should().Be(habit.Id);
        retained.Fields.Should().ContainSingle().Which.NewValue.Should().Be("2026-10-17");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    public async Task PreviewAsync_InvalidRevisedDates_DoNotFallBackToOriginalRange(string revisedItems)
    {
        var habit = CreateHabit("Read", null);
        Setup([habit]);

        var preview = await Preview("bulk_log_habits", $$"""
            {"habit_ids":["{{habit.Id}}"],"dates":["2026-10-18","2026-10-19"],"revised_items":{{revisedItems}}}
            """);

        preview.Should().BeNull();
    }

    [Fact]
    public async Task PreviewAsync_GoalLink_OnlyLinkedRecordChangesInvalidateApproval()
    {
        var linked = CreateHabit("Ler 10 minutos", null);
        var unrelated = CreateHabit("Beber água", null);
        var habits = new[] { linked, unrelated };
        _habits.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => habits.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToList());
        var arguments = JsonSerializer.Serialize(new { title = "Ler livros", habit_ids = new[] { linked.Id } });
        var initial = await Preview("create_goal", arguments);
        unrelated.PostponeTo(new DateOnly(2026, 9, 26));
        var afterUnrelated = await Preview("create_goal", arguments);
        linked.PostponeTo(new DateOnly(2026, 9, 27));
        var afterLinked = await Preview("create_goal", arguments);

        afterUnrelated!.PreviewFingerprint.Should().Be(initial!.PreviewFingerprint);
        afterLinked!.PreviewFingerprint.Should().NotBe(initial.PreviewFingerprint);
    }

    [Fact]
    public async Task PreviewAsync_ThreeMatches_UsesEachRealOldEmoji()
    {
        var habits = new[] { CreateHabit("One", "🔴"), CreateHabit("Two", "🟡"), CreateHabit("Three", "🔵") };
        Setup(habits);

        var preview = await Preview("bulk_update_habits", """{"filter":{"all":true},"updates":{"emoji":"✅"}}""");

        preview!.ChangeTargetCount.Should().Be(3);
        preview.Changes.Select(row => row.Field).Should().OnlyContain(field => field == "emoji");
        preview.Changes.Select(row => row.OldValue).Should().BeEquivalentTo(["🔴", "🟡", "🔵"]);
        preview.Changes.Select(row => row.NewValue).Should().OnlyContain(value => value == "✅");
    }

    [Fact]
    public async Task PreviewAsync_BulkDelete_ReturnsTargetPreview()
    {
        Setup([CreateHabit("One", null), CreateHabit("Two", null)]);

        var preview = await Preview("bulk_delete_habits", """{"filter":{"all":true}}""");

        preview.Should().NotBeNull();
        preview!.ChangeTargetCount.Should().Be(2);
        AssertRemovesData(preview, true);
    }

    [Fact]
    public async Task PreviewAsync_FortyMatches_ListsTenEntitiesAndTotal()
    {
        Setup(Enumerable.Range(1, 40).Select(index => CreateHabit($"Habit {index}", null)).ToArray());

        var preview = await Preview("bulk_update_habits", """{"filter":{"all":true},"updates":{"emoji":"✅"}}""");

        preview!.ChangeTargetCount.Should().Be(40);
        preview.Changes.Select(row => row.EntityId).Distinct().Should().HaveCount(10);
        preview.Items.Should().HaveCount(40);
        preview.Items!.Select(item => item.ItemId).Should().OnlyHaveUniqueItems();
        preview.PreviewFingerprint.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("bulk_delete_habits", "delete")]
    [InlineData("bulk_log_habits", "date")]
    [InlineData("bulk_skip_habits", "date")]
    public async Task PreviewAsync_BulkActions_ListEachTarget(string operationId, string field)
    {
        Setup([CreateHabit("One", null), CreateHabit("Two", null)]);

        var preview = await Preview(operationId, """{"filter":{"all":true}}""");

        preview!.Items.Should().HaveCount(2);
        preview.Items!.SelectMany(item => item.Fields).Select(change => change.Field)
            .Should().OnlyContain(value => value == field);
        preview.Items.SelectMany(item => item.Fields).Select(change => change.IsEditable)
            .Should().OnlyContain(value => value == (field == "date"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"infer_from_title\":true}")]
    [InlineData("{\"emoji\":\"✅\",\"infer_from_title\":true}")]
    public async Task PreviewAsync_InferredEmojis_OffersExplicitEditableValues(string arguments)
    {
        var habits = new[] { CreateHabit("One", null), CreateHabit("Two", null) };
        Setup(habits);
        var inference = Substitute.For<IHabitEmojiInferenceService>();
        inference.InferAsync(UserId, Arg.Any<IReadOnlyList<HabitEmojiInferenceInput>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyDictionary<Guid, string>>(
                habits.ToDictionary(habit => habit.Id, _ => "📚")));
        var tool = new BulkUpdateHabitEmojisTool(_habits, inference, Substitute.For<IUnitOfWork>(),
            NullLogger<BulkUpdateHabitEmojisTool>.Instance);

        var preview = (await new PendingOperationChangePreviewer(_habits, _goals, _tags, _userDateService,
            new AiToolRegistry([tool])).PreviewAsync(UserId, tool.Name,
            JsonSerializer.Deserialize<JsonElement>(arguments))).Value;

        preview.Should().NotBeNull();
        preview!.ChangeTargetCount.Should().Be(habits.Length);
        preview.Items!.SelectMany(item => item.Fields).Should().OnlyContain(field =>
            field.Field == "emoji" && field.NewValue == "📚" && field.IsEditable);
        preview.ExecutionArguments!.Value.GetProperty("infer_from_title").GetBoolean().Should().BeFalse();
        preview.ExecutionArguments.Value.GetProperty("revised_items").GetArrayLength().Should().Be(habits.Length);
        JsonSerializer.SerializeToElement(preview, JsonSerializerOptions.Web)
            .TryGetProperty("executionArguments", out _).Should().BeFalse();
        habits.Should().OnlyContain(habit => habit.Emoji == null);
    }

    [Fact]
    public async Task PreviewAsync_EmojiInferenceStopsMidway_OffersNoUninferredChangesAndWritesNothing()
    {
        var habits = Enumerable.Range(1, 55).Select(index => CreateHabit($"Habit {index}", null)).ToArray();
        Setup(habits);
        var inference = Substitute.For<IHabitEmojiInferenceService>();
        var work = Substitute.For<IUnitOfWork>();
        var count = 0;
        inference.InferAsync(UserId, Arg.Any<IReadOnlyList<HabitEmojiInferenceInput>>(), Arg.Any<CancellationToken>())
            .Returns(call => ++count == 2
                ? Result.Failure<IReadOnlyDictionary<Guid, string>>("budget unavailable")
                : Result.Success<IReadOnlyDictionary<Guid, string>>(call.ArgAt<IReadOnlyList<HabitEmojiInferenceInput>>(1)
                    .ToDictionary(input => input.HabitId, _ => "📚")));
        var tool = new BulkUpdateHabitEmojisTool(_habits, inference, work,
            NullLogger<BulkUpdateHabitEmojisTool>.Instance);

        var result = await new PendingOperationChangePreviewer(_habits, _goals, _tags, _userDateService,
            new AiToolRegistry([tool])).PreviewAsync(UserId, tool.Name,
            JsonSerializer.Deserialize<JsonElement>("""{"infer_from_title":true}"""));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("budget unavailable");
        count.Should().Be(2);
        habits.Should().OnlyContain(habit => habit.Emoji == null);
        await work.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        await _habits.DidNotReceiveWithAnyArgs().FindTrackedAsync(default!, default!, default);
    }

    [Fact]
    public async Task PreviewAsync_ExplicitEmoji_ListsEachApprovedValue()
    {
        Setup([CreateHabit("One", "🔴"), CreateHabit("Two", "🔵")]);

        var preview = await Preview("bulk_update_habit_emojis", """{"emoji":"✅"}""");

        preview!.Items.Should().HaveCount(2);
        preview.Items!.SelectMany(item => item.Fields).Should()
            .OnlyContain(field => field.Field == "emoji" && field.NewValue == "✅");
    }

    [Fact]
    public async Task PreviewAsync_Create_UsesStableInputIndexes()
    {
        var preview = await Preview("bulk_create_habits", """{"habits":[{"title":"One"},{"title":"Two"}]}""");

        preview!.Items.Should().HaveCount(2);
        preview.Items!.Select(item => item.ItemId).Should().Equal("0", "1");
        preview.Items.SelectMany(item => item.Fields).Select(field => field.Field)
            .Should().OnlyContain(field => field == "title");
        preview.Items[0].Fields[0].IsEditable.Should().BeTrue();
        preview.Items[0].Fields[0].ProposedValue!.Value.GetString().Should().Be("One");
        AssertRemovesData(preview, false);
    }

    [Fact]
    public async Task PreviewAsync_Create_InvalidItemCarriesValidatorFieldError()
    {
        using var arguments = JsonDocument.Parse("""{"habits":[{"title":"Valid","frequency_unit":"Day","frequency_quantity":1},{"title":"Invalid","frequency_unit":"Day","frequency_quantity":0,"preview_item_id":"retained"}]}""");
        var command = new BulkCreateHabitsCommand(UserId, arguments.RootElement.GetProperty("habits")
            .EnumerateArray().Select(item => BulkCreateHabitsTool.ParseBulkHabitItem(item)!).ToList());
        var validation = await new BulkCreateHabitsCommandValidator().ValidateAsync(command);
        var failure = validation.Errors.Should().ContainSingle().Subject;
        failure.PropertyName.Should().Be("Habits[1].FrequencyQuantity");
        failure.ErrorCode.Should().Be("GreaterThanValidator");

        var preview = await Preview("bulk_create_habits", arguments.RootElement.GetRawText());

        preview!.Items!.Select(item => item.ItemId).Should().Equal("0", "retained");
        var items = JsonSerializer.SerializeToElement(preview, JsonSerializerOptions.Web).GetProperty("items");
        items[0].TryGetProperty("validationErrors", out _).Should().BeFalse();
        items[1].TryGetProperty("validationErrors", out var errors).Should().BeTrue();
        errors.GetArrayLength().Should().Be(1);
        errors[0].GetProperty("field").GetString().Should().Be("frequency_quantity");
        errors[0].GetProperty("errorCode").GetString().Should().Be(failure.ErrorCode);
        errors[0].GetProperty("error").GetString().Should().Be(failure.ErrorMessage);
    }

    [Theory]
    [InlineData("""{"title":""}""", "title")]
    [InlineData("""{}""", "title")]
    [InlineData("""{"title":"Invalid","frequency_unit":"Day","frequency_quantity":-1}""", "frequency_quantity")]
    public async Task PreviewAsync_Create_InvalidFieldsRemainVisible(string itemJson, string field)
    {
        var preview = await Preview("bulk_create_habits", $$"""{"habits":[{{itemJson}}]}""");

        preview!.Items.Should().ContainSingle().Which.ValidationErrors.Should()
            .Contain(error => error.Field == field);
        preview.Items![0].Fields.Should().Contain(change => change.Field == field && change.IsEditable);
    }

    [Fact]
    public async Task PreviewAsync_Create_ValidBatchPreservesPreviewAndFingerprint()
    {
        const string json = """{"habits":[{"title":"Valid","frequency_unit":"Day","frequency_quantity":1}]}""";
        var preview = await Preview("bulk_create_habits", json);
        var item = preview!.Items.Should().ContainSingle().Subject;

        item.ValidationErrors.Should().BeNull();
        preview.Changes.Should().BeEmpty();
        preview.ChangeTargetCount.Should().Be(1);
        item.Fields.Select(field => field.Field).Should().Equal("title", "frequency_quantity", "frequency_unit");
        var legacyItemJson = JsonSerializer.SerializeToElement(item);
        legacyItemJson.EnumerateObject().Select(property => property.Name).Should().Equal(
            "ItemId", "EntityId", "EntityName", "Fields", "StateFingerprint", "RemovesData");
        preview.PreviewFingerprint.Should().Be(AgentOperationFingerprint.Compute(
            "bulk_create_habits", JsonSerializer.Serialize(preview.Items)));
    }

    [Theory]
    [InlineData("bulk_log_habits", """{"filter":{"all":true}}""")]
    [InlineData("bulk_skip_habits", """{"filter":{"all":true}}""")]
    [InlineData("bulk_update_habits", """{"filter":{"all":true},"updates":{"emoji":"✅"}}""")]
    [InlineData("bulk_reschedule_habits", """{"filter":{"all":true},"due_date":"2026-10-01"}""")]
    public async Task PreviewAsync_ReversibleBulkWrite_ReportsNoDataRemoval(string operation, string arguments)
    {
        Setup([CreateHabit("One", null), CreateHabit("Two", null)]);

        var preview = await Preview(operation, arguments);

        preview!.Items.Should().HaveCount(2);
        AssertRemovesData(preview, false);
    }

    [Fact]
    public async Task PreviewAsync_MixedBatch_MarksExactlyTheDeleteItems()
    {
        var habit = CreateHabit("Existing", null);
        Setup([habit]);
        var create = await Preview("bulk_create_habits", """{"habits":[{"title":"New"}]}""");
        var log = await Preview("bulk_log_habits", """{"filter":{"all":true}}""");
        var skip = await Preview("bulk_skip_habits", """{"filter":{"all":true}}""");
        var delete = await Preview("delete_habit", $$"""{"habit_id":"{{habit.Id}}"}""");
        var bulkDelete = await Preview("bulk_delete_habits", """{"filter":{"all":true}}""");

        var items = new[] { create, log, skip, delete, bulkDelete }.SelectMany(preview => preview!.Items!);
        var json = JsonSerializer.SerializeToElement(items, JsonSerializerOptions.Web);

        json.EnumerateArray().Select(item => item.GetProperty("removesData").GetBoolean())
            .Should().Equal(false, false, false, true, true);
    }

    private static void AssertRemovesData(Orbit.Domain.Models.PendingOperationChangePreview preview, bool expected)
    {
        var json = JsonSerializer.SerializeToElement(preview, JsonSerializerOptions.Web);
        var items = json.GetProperty("items").EnumerateArray().ToList();
        items.Should().NotBeEmpty();
        foreach (var item in items)
        {
            item.TryGetProperty("removesData", out var removesData).Should().BeTrue();
            removesData.GetBoolean().Should().Be(expected);
        }
    }

    [Fact]
    public async Task PreviewAsync_Create_PreservesNestedProposedValues()
    {
        var preview = await Preview("bulk_create_habits", """{"habits":[{"title":"One","checklist_items":[{"text":"First","is_checked":true}],"sub_habits":[{"title":"Child"}]}]}""");

        var fields = preview!.Items![0].Fields;
        fields.Single(field => field.Field == "checklist_items").ProposedValue!.Value[0]
            .GetProperty("is_checked").GetBoolean().Should().BeTrue();
        fields.Single(field => field.Field == "sub_habits").ProposedValue!.Value[0]
            .GetProperty("title").GetString().Should().Be("Child");
        fields.Should().OnlyContain(field => field.IsEditable);
    }

    [Fact]
    public async Task PreviewAsync_Reschedule_ShowsOldAndNewDate()
    {
        Setup([CreateHabit("One", null)]);

        var preview = await Preview("bulk_reschedule_habits", """{"filter":{"all":true},"due_date":"2026-10-01"}""");

        preview!.Changes.Should().ContainSingle().Which.Should().Match<Orbit.Domain.Models.PendingOperationChange>(
            row => row.Field == "due_date" && row.OldValue == "2026-09-25" && row.NewValue == "2026-10-01");
    }

    [Fact]
    public async Task PreviewAsync_ZeroMatches_OffersNoPreview()
    {
        Setup([]);

        var preview = await Preview("bulk_update_habits", """{"filter":{"all":true},"updates":{"emoji":"✅"}}""");

        preview.Should().BeNull();
    }

    [Fact]
    public async Task PreviewAsync_FlexibleOnly_ShowsDaysClearedByUpdate()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "One", FrequencyUnit.Day, 1, new DateOnly(2026, 9, 25),
            Days: [DayOfWeek.Monday, DayOfWeek.Wednesday])).Value;
        Setup([habit]);

        var preview = await Preview("bulk_update_habits", """{"filter":{"all":true},"updates":{"is_flexible":true}}""");

        preview!.Changes.Should().Contain(row => row.Field == "days"
            && row.OldValue == "Monday, Wednesday" && row.NewValue == "");
        preview.Changes.Should().Contain(row => row.Field == "is_flexible"
            && row.OldValue == "false" && row.NewValue == "true");
        habit.Days.Should().Equal(DayOfWeek.Monday, DayOfWeek.Wednesday);
    }

    [Fact]
    public async Task PreviewAsync_ScheduledReminderForTimedHabit_ShowsNormalizedStore()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "One", FrequencyUnit.Day, 1, new DateOnly(2026, 9, 25),
            DueTime: new TimeOnly(9, 0), ReminderTimes: [])).Value;
        Setup([habit]);

        var preview = await Preview("bulk_update_habits", """{"filter":{"all":true},"updates":{"scheduled_reminders":[{"when":"same_day","time":"08:30"}]}}""");

        preview!.Changes.Should().Contain(
            row => row.Field == "scheduled_reminders" && row.OldValue == "(none)" && row.NewValue == "same_day 08:30");
        preview.Items![0].Fields.Single(field => field.Field == "scheduled_reminders")
            .ProposedValue!.Value[0].GetProperty("time").GetString().Should().Be("08:30");
        habit.ReminderTimes.Should().BeEmpty();
    }

    [Fact]
    public async Task PreviewAsync_EqualSizeReminderAndChecklistReplacement_ShowsActualValues()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "One", FrequencyUnit.Day, 1, new DateOnly(2026, 9, 25),
            DueTime: new TimeOnly(9, 0), ReminderTimes: [15],
            ChecklistItems: [new ChecklistItem("Warm up", false)])).Value;
        Setup([habit]);

        var preview = await Preview("bulk_update_habits", """{"filter":{"all":true},"updates":{"reminder_times":[30],"checklist_items":[{"text":"Stretch"}]}}""");

        preview!.Changes.Should().Contain(row => row.Field == "reminder_times"
            && row.OldValue == "15 min before due" && row.NewValue == "30 min before due");
        preview.Changes.Should().Contain(row => row.Field == "checklist_items"
            && row.OldValue == "Warm up" && row.NewValue == "Stretch");
    }

    [Fact]
    public async Task PreviewAsync_LongChecklist_ShowsThreeValuesAndRemainder()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "One", FrequencyUnit.Day, 1, new DateOnly(2026, 9, 25),
            ChecklistItems: [new("One", false), new("Two", false), new("Three", false), new("Four", false), new("Five", false)])).Value;
        Setup([habit]);

        var preview = await Preview("bulk_update_habits", """{"filter":{"all":true},"updates":{"checklist_items":[{"text":"Alpha"},{"text":"Beta"},{"text":"Gamma"},{"text":"Delta"},{"text":"Epsilon"}]}}""");

        preview!.Changes.Should().ContainSingle().Which.Should().Match<Orbit.Domain.Models.PendingOperationChange>(
            row => row.Field == "checklist_items"
                && row.OldValue == "One, Two, Three, +2 more"
                && row.NewValue == "Alpha, Beta, Gamma, +2 more");
        var serialized = JsonSerializer.SerializeToElement(preview.Items![0].Fields[0],
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        serialized.GetProperty("isEditable").GetBoolean().Should().BeTrue();
        serialized.GetProperty("proposedValue").EnumerateArray()
            .Select(item => item.GetProperty("text").GetString())
            .Should().Equal("Alpha", "Beta", "Gamma", "Delta", "Epsilon");
    }

    [Fact]
    public async Task PreviewAsync_ChecklistFourthItemReplacement_ShowsChangedWindow()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "One", FrequencyUnit.Day, 1, new DateOnly(2026, 9, 25),
            ChecklistItems: [new("One", false), new("Two", false), new("Three", false), new("Four", false), new("Five", false)])).Value;
        Setup([habit]);

        var preview = await Preview("bulk_update_habits", """{"filter":{"all":true},"updates":{"checklist_items":[{"text":"One"},{"text":"Two"},{"text":"Three"},{"text":"Changed"},{"text":"Five"}]}}""");

        preview!.Changes.Should().ContainSingle().Which.Should().Match<Orbit.Domain.Models.PendingOperationChange>(
            row => row.Field == "checklist_items"
                && row.OldValue == "+3 earlier, Four, Five"
                && row.NewValue == "+3 earlier, Changed, Five");
    }

    [Fact]
    public async Task PreviewAsync_FifthScheduledReminderReplacement_ShowsChangedWindow()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "One", FrequencyUnit.Day, 1, new DateOnly(2026, 9, 25),
            ScheduledReminders: [
                new(ScheduledReminderWhen.SameDay, new TimeOnly(8, 0)),
                new(ScheduledReminderWhen.SameDay, new TimeOnly(9, 0)),
                new(ScheduledReminderWhen.SameDay, new TimeOnly(10, 0)),
                new(ScheduledReminderWhen.SameDay, new TimeOnly(11, 0)),
                new(ScheduledReminderWhen.SameDay, new TimeOnly(12, 0))])).Value;
        Setup([habit]);

        var preview = await Preview("bulk_update_habits", """{"filter":{"all":true},"updates":{"scheduled_reminders":[{"when":"same_day","time":"08:00"},{"when":"same_day","time":"09:00"},{"when":"same_day","time":"10:00"},{"when":"same_day","time":"11:00"},{"when":"same_day","time":"12:30"}]}}""");

        preview!.Changes.Should().ContainSingle().Which.Should().Match<Orbit.Domain.Models.PendingOperationChange>(
            row => row.Field == "scheduled_reminders"
                && row.OldValue == "+4 earlier, same_day 12:00"
                && row.NewValue == "+4 earlier, same_day 12:30");
        var proposed = preview.Items![0].Fields.Single(field => field.Field == "scheduled_reminders")
            .ProposedValue!.Value;
        proposed.GetArrayLength().Should().Be(5);
        proposed[4].GetProperty("when").GetString().Should().Be("same_day");
        proposed[4].GetProperty("time").GetString().Should().Be("12:30");
    }

    [Fact]
    public async Task PreviewAsync_CrossMidnightRelativeReminder_ShowsOffsetWithoutClock()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "One", FrequencyUnit.Day, 1, new DateOnly(2026, 9, 25),
            DueTime: new TimeOnly(0, 30), ReminderTimes: [15])).Value;
        Setup([habit]);

        var preview = await Preview("bulk_update_habits", """{"filter":{"all":true},"updates":{"reminder_times":[60]}}""");

        preview!.Changes.Should().ContainSingle().Which.Should().Match<Orbit.Domain.Models.PendingOperationChange>(
            row => row.Field == "reminder_times"
                && row.OldValue == "15 min before due"
                && row.NewValue == "60 min before due");
    }

    [Fact]
    public async Task LiveIconFailureReason_BudgetFailureRemainsTypedInsteadOfBecomingAnAbsentPreview()
    {
        var habit = CreateHabit("Read", null);
        Setup([habit]);
        var inference = Substitute.For<IHabitEmojiInferenceService>();
        inference.InferAsync(UserId, Arg.Any<IReadOnlyList<HabitEmojiInferenceInput>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<IReadOnlyDictionary<Guid, string>>(new AppError(
                "AI_DAILY_BUDGET_EXCEEDED", "Your daily AI safety budget has been reached. Please try again tomorrow.")));
        var work = Substitute.For<IUnitOfWork>();
        var tool = new BulkUpdateHabitEmojisTool(_habits, inference, work,
            NullLogger<BulkUpdateHabitEmojisTool>.Instance);
        var previewer = new PendingOperationChangePreviewer(_habits, _goals, _tags, _userDateService,
            new AiToolRegistry([tool]));

        var result = await previewer.TryPreviewAsync(UserId, tool.Name,
            JsonSerializer.Deserialize<JsonElement>("""{"emoji_mode":"infer","infer_from_title":true,"filter":{"all":true}}"""),
            NullLogger<PendingOperationChangePreviewer>.Instance, default);

        result.IsFailure.Should().BeTrue("an inference refusal is not a successful missing or stale preview");
        result.ErrorCode.Should().Be("AI_DAILY_BUDGET_EXCEEDED");
        habit.Emoji.Should().BeNull();
        await work.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LiveIconFailureReason_IncompleteMappingRemainsAPreparationFailure()
    {
        var habit = CreateHabit("Read", null);
        Setup([habit]);
        var inference = Substitute.For<IHabitEmojiInferenceService>();
        inference.InferAsync(UserId, Arg.Any<IReadOnlyList<HabitEmojiInferenceInput>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>()));
        var work = Substitute.For<IUnitOfWork>();
        var tool = new BulkUpdateHabitEmojisTool(_habits, inference, work,
            NullLogger<BulkUpdateHabitEmojisTool>.Instance);
        var previewer = new PendingOperationChangePreviewer(_habits, _goals, _tags, _userDateService,
            new AiToolRegistry([tool]));

        var result = await previewer.TryPreviewAsync(UserId, tool.Name,
            JsonSerializer.Deserialize<JsonElement>("""{"emoji_mode":"infer","infer_from_title":true,"filter":{"all":true}}"""),
            NullLogger<PendingOperationChangePreviewer>.Instance, default);

        result.IsFailure.Should().BeTrue("a missing inferred icon cannot be a successful absent or stale preview");
        result.Error.Should().Be("preview_unavailable");
        habit.Emoji.Should().BeNull();
        await work.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LiveIconFailureReason_CompleteMappingStillOffersAnExplicitUnwrittenPreview()
    {
        var habit = CreateHabit("Read", null);
        Setup([habit]);
        var inference = Substitute.For<IHabitEmojiInferenceService>();
        inference.InferAsync(UserId, Arg.Any<IReadOnlyList<HabitEmojiInferenceInput>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string> { [habit.Id] = "📚" }));
        var work = Substitute.For<IUnitOfWork>();
        var tool = new BulkUpdateHabitEmojisTool(_habits, inference, work,
            NullLogger<BulkUpdateHabitEmojisTool>.Instance);
        var previewer = new PendingOperationChangePreviewer(_habits, _goals, _tags, _userDateService,
            new AiToolRegistry([tool]));

        var result = await previewer.TryPreviewAsync(UserId, tool.Name,
            JsonSerializer.Deserialize<JsonElement>("""{"emoji_mode":"infer","infer_from_title":true,"filter":{"all":true}}"""),
            NullLogger<PendingOperationChangePreviewer>.Instance, default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Items.Should().ContainSingle();
        result.Value.Items!.Single().Fields.Should().Contain(field => field.Field == "emoji" && field.NewValue == "📚");
        habit.Emoji.Should().BeNull();
        await work.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("missing", "missing_mapping")]
    [InlineData("unknown", "unexpected_mapping")]
    [InlineData("invalid", "invalid_emoji")]
    [InlineData("refusal", "unknown")]
    [InlineData("throw", "unknown")]
    public async Task InferredIcons_InvalidChunk_EmitsOnlySafePreparationReceipt(string fault, string reason)
    {
        var habit = CreateHabit("private title", null);
        Setup([habit]);
        var inference = Substitute.For<IHabitEmojiInferenceService>();
        var mapping = new Dictionary<Guid, string> { [habit.Id] = "📚" };
        if (fault == "missing") mapping.Clear();
        if (fault == "unknown") mapping[Guid.NewGuid()] = "📚";
        if (fault == "invalid") mapping[habit.Id] = "private provider text";
        inference.InferAsync(UserId, Arg.Any<IReadOnlyList<HabitEmojiInferenceInput>>(), Arg.Any<CancellationToken>())
            .Returns(_ => fault == "throw" ? throw new InvalidOperationException("private exception")
                : fault == "refusal" ? Result.Failure<IReadOnlyDictionary<Guid, string>>("preview_unavailable")
                : Result.Success<IReadOnlyDictionary<Guid, string>>(mapping));
        var logger = new ReceiptLogger();
        var work = Substitute.For<IUnitOfWork>();
        var tool = new BulkUpdateHabitEmojisTool(_habits, inference, work, logger);
        var result = await new PendingOperationChangePreviewer(_habits, _goals, _tags, _userDateService,
            new AiToolRegistry([tool])).TryPreviewAsync(UserId, tool.Name,
                JsonSerializer.Deserialize<JsonElement>("{}"), NullLogger.Instance, default);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("preview_unavailable");
        logger.Exception.Should().BeNull();
        logger.Receipt.Where(pair => pair.Key != "{OriginalFormat}").Should().BeEquivalentTo(new Dictionary<string, object?>
        {
            ["Stage"] = fault is "throw" or "refusal" ? "inference" : "mapping",
            ["Reason"] = reason,
            ["ChunkIndex"] = 1,
            ["ChunkCount"] = 1,
            ["ChunkTargetCount"] = 1,
            ["TargetCount"] = 1
        });
        habit.Emoji.Should().BeNull();
        await work.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InferredIcons_CallerCancellation_PropagatesWithoutAnApproval()
    {
        Setup([CreateHabit("Read", null)]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var inference = Substitute.For<IHabitEmojiInferenceService>();
        inference.InferAsync(UserId, Arg.Any<IReadOnlyList<HabitEmojiInferenceInput>>(), cancellation.Token)
            .Returns(Task.FromCanceled<Result<IReadOnlyDictionary<Guid, string>>>(cancellation.Token));
        var tool = new BulkUpdateHabitEmojisTool(_habits, inference, Substitute.For<IUnitOfWork>(),
            NullLogger<BulkUpdateHabitEmojisTool>.Instance);
        var previewer = new PendingOperationChangePreviewer(_habits, _goals, _tags, _userDateService, new AiToolRegistry([tool]));

        var act = () => previewer.TryPreviewAsync(UserId, tool.Name, JsonSerializer.Deserialize<JsonElement>("{}"),
            NullLogger.Instance, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class ReceiptLogger : Microsoft.Extensions.Logging.ILogger<BulkUpdateHabitEmojisTool>
    {
        public IReadOnlyList<KeyValuePair<string, object?>> Receipt { get; private set; } = [];
        public Exception? Exception { get; private set; }
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Receipt = ((IEnumerable<KeyValuePair<string, object?>>)state!).ToArray();
            Exception = exception;
        }
    }

    private async Task<Orbit.Domain.Models.PendingOperationChangePreview?> Preview(string operation, string json) =>
        (await new PendingOperationChangePreviewer(_habits, _goals, _tags, _userDateService,
            new AiToolRegistry([])).PreviewAsync(
            UserId, operation, JsonDocument.Parse(json).RootElement)).Value;

    private void Setup(IReadOnlyList<Habit> habits) =>
        _habits.FindAsync(
                Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(),
                Arg.Any<CancellationToken>())
            .Returns(habits);

    private static Habit CreateHabit(string title, string? emoji) =>
        Habit.Create(new HabitCreateParams(
            UserId, title, FrequencyUnit.Day, 1, new DateOnly(2026, 9, 25), Emoji: emoji)).Value;
}
