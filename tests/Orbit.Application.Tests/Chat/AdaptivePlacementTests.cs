using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Validators;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Validators;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Tests.Chat;

public sealed class AdaptivePlacementTests
{
    public static IEnumerable<object?[]> NegotiationCases =>
        from pattern in new[] { "checklists", "children", "flat", "empty" }
        from operation in new[] { "create_habit", "create_sub_habit", "bulk_create_habits" }
        from supported in new bool?[] { null, false, true }
        select new object?[] { pattern, operation, supported };

    [Theory]
    [MemberData(nameof(NegotiationCases))]
    public async Task Preview_NegotiatesPlacementBeforeBindingExecution(string pattern, string operation, bool? supported)
    {
        using var context = new HeldWriteTestContext();
        AddPattern(context, pattern);
        var tool = context.Tool(operation);
        var previewer = new PendingOperationChangePreviewer(context.Repository<Habit>(), context.Repository<Goal>(),
            context.Repository<Tag>(), context.Dates, new AiToolRegistry([tool]));
        var source = JsonSerializer.SerializeToElement(new
        {
            parent_habit_id = context.Habit.Id,
            title = "Stretch",
            frequency_unit = "Day",
            due_time = "09:00",
            reminder_enabled = true,
            reminder_times = new[] { 15 }
        });
        var arguments = operation == "bulk_create_habits" ? JsonSerializer.SerializeToElement(new { habits = new[] { source } }) : source;
        var client = supported is null ? null : new AgentClientContext(SupportsParts: true,
            SupportsPendingOperationChanges: true, SupportsPendingOperationPlacement: supported);

        var result = await previewer.PreviewAsync(context.UserId, operation, arguments, clientContext: client);

        result.IsSuccess.Should().BeTrue();
        var preview = result.Value!;
        var item = preview.Items.Should().ContainSingle().Which;
        item.Fields.Should().Contain(field => field.Field == "due_time" && field.NewValue == "09:00");
        item.Fields.Should().Contain(field => field.Field == "reminder_enabled" && field.NewValue == bool.TrueString);
        var bound = preview.ExecutionArguments ?? arguments;
        var boundItem = operation == "bulk_create_habits" ? bound.GetProperty("habits")[0] : bound;
        if (supported == true)
        {
            var expected = pattern switch
            {
                "checklists" => HabitPlacement.ChecklistItem,
                "flat" => HabitPlacement.NewParent,
                _ => HabitPlacement.SubHabit
            };
            item.Placement!.Selected.Should().Be(expected);
            item.Placement.Recommended.Should().Be(expected);
            item.Placement.Alternatives.Should().BeEquivalentTo(
                new[] { HabitPlacement.SubHabit, HabitPlacement.ChecklistItem, HabitPlacement.NewParent }.Where(value => value != expected));
            boundItem.GetProperty("placement").GetString().Should().Be(expected);
        }
        else
        {
            item.Placement.Should().BeNull();
            boundItem.TryGetProperty("placement", out _).Should().BeFalse();
            boundItem.GetProperty("parent_habit_id").GetGuid().Should().Be(context.Habit.Id);
        }
    }

    public static IEnumerable<object[]> SwitchCases =>
        from pattern in new[] { "checklists", "children", "flat", "empty" }
        from placement in new[] { HabitPlacement.SubHabit, HabitPlacement.ChecklistItem, HabitPlacement.NewParent }
        from operation in new[] { "create_habit", "create_sub_habit", "bulk_create_habits" }
        select new object[] { pattern, placement, operation };

    [Theory]
    [InlineData(HabitPlacement.ChecklistItem)]
    [InlineData(HabitPlacement.NewParent)]
    public async Task LegacyPreview_RemovesModelPlacementAndPreservesDisplayedChildren(string placement)
    {
        using var context = new HeldWriteTestContext();
        context.Habit.UpdateChecklist([new("Coffee", true)]);
        var tool = context.Tool("create_habit");
        var previewer = new PendingOperationChangePreviewer(context.Repository<Habit>(), context.Repository<Goal>(),
            context.Repository<Tag>(), context.Dates, new AiToolRegistry([tool]));
        var arguments = JsonSerializer.SerializeToElement(new
        {
            parent_habit_id = context.Habit.Id,
            title = "Workout",
            frequency_unit = "Day",
            due_time = "09:00",
            reminder_enabled = true,
            reminder_times = new[] { 15 },
            placement,
            sub_habits = new[] { new { title = "Warm-up", frequency_unit = "Day", placement } }
        });

        var preview = (await previewer.PreviewAsync(context.UserId, tool.Name, arguments)).Value!;
        var bound = preview.ExecutionArguments!.Value;
        bound.TryGetProperty("placement", out _).Should().BeFalse();
        bound.GetProperty("sub_habits")[0].TryGetProperty("placement", out _).Should().BeFalse();
        preview.Items!.Single().Placement.Should().BeNull();
        (await tool.ExecuteAsync(bound, context.UserId, default)).Success.Should().BeTrue();
        var workout = context.Added.OfType<Habit>().Should().ContainSingle(habit => habit.Title == "Workout").Which;
        workout.ParentHabitId.Should().Be(context.Habit.Id);
        workout.DueTime.Should().Be(new TimeOnly(9, 0));
        workout.ReminderEnabled.Should().BeTrue();
        workout.ReminderTimes.Should().Equal(15);
        context.Added.OfType<Habit>().Should().ContainSingle(habit => habit.Title == "Warm-up" && habit.ParentHabitId == workout.Id);
        context.Habit.ChecklistItems.Should().Equal(new ChecklistItem("Coffee", true));
    }

    [Theory]
    [MemberData(nameof(SwitchCases))]
    public async Task Switch_PreservesAllOtherFieldsAndExecutesSelectedPlacement(string pattern, string placement, string operation)
    {
        using var context = new HeldWriteTestContext();
        AddPattern(context, pattern);
        var tool = context.Tool(operation);
        var registry = new AiToolRegistry([tool]);
        var previewer = new PendingOperationChangePreviewer(context.Repository<Habit>(), context.Repository<Goal>(),
            context.Repository<Tag>(), context.Dates, registry);
        var source = JsonSerializer.SerializeToElement(new
        {
            parent_habit_id = context.Habit.Id,
            title = "Stretch",
            description = "Gently",
            emoji = "🌱",
            frequency_unit = "Day",
            frequency_quantity = 1,
            days = new[] { "Monday" },
            due_date = "2026-10-05",
            start_date = "2026-10-01",
            end_date = "2026-12-31",
            due_time = "09:00",
            due_end_time = "09:15",
            reminder_enabled = true,
            reminder_times = new[] { 15 },
            relative_reminders = new[] { new { minutes_before = 30 } },
            tag_ids = new[] { context.Tag.Id },
            goal_ids = new[] { context.Goal.Id },
            checklist_items = new[] { new { text = "Shoes", is_checked = false } }
        });
        var current = operation == "bulk_create_habits" ? JsonSerializer.SerializeToElement(new { habits = new[] { source } }) : source;
        var preview = (await previewer.PreviewAsync(context.UserId, operation, current, clientContext: new(SupportsPendingOperationPlacement: true))).Value!;
        preview.Should().NotBeNull();
        var originalPlacement = preview.Items!.Single().Placement!;
        originalPlacement.Recommended.Should().Be(pattern switch
        {
            "checklists" => HabitPlacement.ChecklistItem,
            "flat" => HabitPlacement.NewParent,
            _ => HabitPlacement.SubHabit
        });
        originalPlacement.Reason.Keys.Should().BeEquivalentTo("en", "pt-BR");
        current = preview.ExecutionArguments ?? current;
        var pendingId = Guid.NewGuid();
        var store = Substitute.For<IPendingAgentOperationStore>();
        store.GetExecution(context.UserId, pendingId).Returns(_ => new PendingAgentOperationExecution(pendingId,
            "habit", operation, current, AgentExecutionSurface.Chat, AgentConfirmationRequirement.FreshConfirmation));
        store.Revise(context.UserId, pendingId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(call => { current = JsonSerializer.Deserialize<JsonElement>(call.ArgAt<string>(3)); return true; });
        var service = new PendingOperationRevisionService(store, previewer, new RevisePendingOperationRequestValidator(), registry);

        var result = await service.ReviseAsync(context.UserId, pendingId,
            new(preview.PreviewFingerprint!, [new(preview.Items!.Single().ItemId, Placement: placement)], new(SupportsPendingOperationPlacement: true)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Preview!.Items!.Single().Placement!.Selected.Should().Be(placement);
        result.Preview.Items!.Single().Placement!.Alternatives.Should().BeEquivalentTo(
            new[] { HabitPlacement.SubHabit, HabitPlacement.ChecklistItem, HabitPlacement.NewParent }.Where(value => value != placement));
        var changed = operation == "bulk_create_habits" ? current.GetProperty("habits")[0] : current;
        foreach (var property in source.EnumerateObject())
            JsonElement.DeepEquals(changed.GetProperty(property.Name), property.Value).Should().BeTrue(property.Name);
        context.Commands.Should().BeEmpty();
        var repeat = await previewer.PreviewAsync(context.UserId, operation, current, clientContext: new(SupportsPendingOperationPlacement: true));
        repeat.Value!.PreviewFingerprint.Should().Be(result.Preview.PreviewFingerprint);
        context.Mediator.Send(Arg.Any<AppendChecklistItemCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(context.Habit.Id));
        context.Mediator.Send(Arg.Any<CreateHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(Guid.NewGuid()));
        context.Mediator.Send(Arg.Any<LinkGoalsToHabitCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        (await tool.ExecuteAsync(current, context.UserId, default)).Success.Should().BeTrue();
        if (operation != "create_habit")
        {
            if (placement == HabitPlacement.ChecklistItem)
                await context.Mediator.Received(1).Send(Arg.Is<AppendChecklistItemCommand>(command => command.HabitId == context.Habit.Id && command.Text == "Stretch"), Arg.Any<CancellationToken>());
            else if (placement == HabitPlacement.NewParent)
                await context.Mediator.Received(1).Send(Arg.Is<CreateHabitCommand>(command => command.Title == "Stretch" && command.GoalIds!.Single() == context.Goal.Id), Arg.Any<CancellationToken>());
            else
                context.Commands.Should().ContainSingle(command => command is CreateSubHabitCommand);
        }
    }

    [Theory]
    [InlineData("checklists", "sub_habit")]
    [InlineData("children", "checklist_item")]
    public void DestinationStructure_TakesPrecedenceOverAccountPattern(string pattern, string expected)
    {
        using var context = new HeldWriteTestContext();
        AddPattern(context, pattern);
        if (expected == HabitPlacement.ChecklistItem) context.Habit.UpdateChecklist([new("Coffee", false)]);
        else context.Records.Add(Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, context.UserId,
            "Coffee", FrequencyUnit.Day, 1, context.Today, ParentHabitId: context.Habit.Id)).Value);
        HabitPlacementRecommendation.For(context.Habit, context.Records.OfType<Habit>().ToArray())
            .Recommended.Should().Be(expected);
    }

    [Fact]
    public void Pattern_ReportsUnstructuredLeavesWithoutTreatingThemAsSeparateParents()
    {
        using var context = new HeldWriteTestContext();
        AddPattern(context, "children");
        var habits = context.Records.OfType<Habit>().Where(habit => habit.UserId == context.UserId).ToArray();
        var pattern = HabitStructurePattern.From(habits);
        pattern.SubHabits.Should().Be(3);
        pattern.Neither.Should().Be(4);
        pattern.SeparateParents.Should().Be(1);
        HabitPlacementRecommendation.For(context.Habit, habits).Recommended.Should().Be(HabitPlacement.SubHabit);
    }

    [Fact]
    public void Pattern_MostlySeparateParentsTakesPrecedenceOverAnIsolatedChecklist()
    {
        using var context = new HeldWriteTestContext();
        AddPattern(context, "flat");
        context.Records.OfType<Habit>().First(habit => habit.Title == "Routine 0").UpdateChecklist([new("Step", false)]);
        HabitPlacementRecommendation.For(context.Habit, context.Records.OfType<Habit>().Where(habit => habit.UserId == context.UserId).ToArray())
            .Recommended.Should().Be(HabitPlacement.NewParent);
    }

    [Fact]
    public async Task ForeignDestination_IsNeverOffered()
    {
        using var context = new HeldWriteTestContext();
        var tool = context.Tool("create_sub_habit");
        var previewer = new PendingOperationChangePreviewer(context.Repository<Habit>(), context.Repository<Goal>(),
            context.Repository<Tag>(), context.Dates, new AiToolRegistry([tool]));
        var preview = await previewer.PreviewAsync(context.UserId, tool.Name,
            JsonSerializer.SerializeToElement(new { title = "Stretch", parent_habit_id = context.ForeignHabit.Id }));
        preview.Value.Should().BeNull();
    }

    [Fact]
    public async Task Append_ChangesOnlyTheChecklistAndKeepsExistingItems()
    {
        using var context = new HeldWriteTestContext();
        context.Habit.UpdateChecklist([new("Coffee", true)]);
        var before = JsonSerializer.SerializeToElement(context.Habit);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var handler = new AppendChecklistItemCommandHandler(context.Repository<Habit>(), context.UnitOfWork, cache, context.Dates);

        var result = await handler.Handle(new(context.UserId, context.Habit.Id, "Stretch"), default);

        result.IsSuccess.Should().BeTrue();
        context.Habit.ChecklistItems.Should().Equal(new ChecklistItem("Coffee", true), new ChecklistItem("Stretch", false));
        var after = JsonSerializer.SerializeToElement(context.Habit);
        foreach (var property in before.EnumerateObject().Where(property => property.Name is not ("ChecklistItems" or "UpdatedAtUtc")))
            JsonElement.DeepEquals(after.GetProperty(property.Name), property.Value).Should().BeTrue(property.Name);
        var foreign = await handler.Handle(new(context.UserId, context.ForeignHabit.Id, "Stretch"), default);
        foreign.IsFailure.Should().BeTrue();
        context.ForeignHabit.ChecklistItems.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void AppendValidator_RejectsEmptyText(string? text)
    {
        new AppendChecklistItemCommandValidator().Validate(new AppendChecklistItemCommand(Guid.NewGuid(), Guid.NewGuid(), text!)).IsValid.Should().BeFalse();
    }

    [Fact]
    public void PromptFacts_ContainCompleteDestinationAndExcludeOtherAccounts()
    {
        using var context = new HeldWriteTestContext();
        context.Habit.UpdateChecklist([new("Coffee", true)]);
        var text = HabitPlacementContext.Build(context.Records.OfType<Habit>().Where(habit => habit.UserId == context.UserId).ToArray(), "Add Stretch to Read");
        var json = JsonDocument.Parse(text[text.IndexOf('{')..]).RootElement;
        json.GetProperty("pattern").GetProperty("Checklists").GetInt32().Should().Be(1);
        var destination = json.GetProperty("destinations").EnumerateArray().Should().ContainSingle().Which;
        destination.GetProperty("checklist_items")[0].GetProperty("text").GetString().Should().Be("Coffee");
        destination.GetProperty("sub_habits").GetArrayLength().Should().Be(0);
        text.Should().NotContain("Foreign");
    }

    private static void AddPattern(HeldWriteTestContext context, string pattern)
    {
        var roots = Enumerable.Range(0, pattern == "empty" ? 0 : 3).Select(index => Habit.Create(new HabitCreateParams(
            HabitSchedule.Recurring, context.UserId, $"Routine {index}", FrequencyUnit.Day, 1, context.Today)).Value).ToArray();
        context.Records.AddRange(roots);
        if (pattern == "checklists") foreach (var root in roots) root.UpdateChecklist([new("Step", false)]);
        if (pattern == "children") foreach (var root in roots) context.Records.Add(Habit.Create(new HabitCreateParams(
            HabitSchedule.Recurring, context.UserId, "Step", FrequencyUnit.Day, 1, context.Today, ParentHabitId: root.Id)).Value);
    }
}
