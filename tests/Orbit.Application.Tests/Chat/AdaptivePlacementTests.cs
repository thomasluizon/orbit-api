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
    public static IEnumerable<object[]> SwitchCases =>
        from pattern in new[] { "checklists", "children", "flat", "empty" }
        from placement in new[] { HabitPlacement.SubHabit, HabitPlacement.ChecklistItem, HabitPlacement.NewParent }
        from operation in new[] { "create_habit", "create_sub_habit", "bulk_create_habits" }
        select new object[] { pattern, placement, operation };

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
        var preview = (await previewer.PreviewAsync(context.UserId, operation, current)).Value!;
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
            new(preview.PreviewFingerprint!, [new(preview.Items!.Single().ItemId, Placement: placement)]), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Preview!.Items!.Single().Placement!.Selected.Should().Be(placement);
        result.Preview.Items!.Single().Placement!.Alternatives.Should().BeEquivalentTo(
            new[] { HabitPlacement.SubHabit, HabitPlacement.ChecklistItem, HabitPlacement.NewParent }.Where(value => value != placement));
        var changed = operation == "bulk_create_habits" ? current.GetProperty("habits")[0] : current;
        foreach (var property in source.EnumerateObject())
            JsonElement.DeepEquals(changed.GetProperty(property.Name), property.Value).Should().BeTrue(property.Name);
        context.Commands.Should().BeEmpty();
        var repeat = await previewer.PreviewAsync(context.UserId, operation, current);
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
