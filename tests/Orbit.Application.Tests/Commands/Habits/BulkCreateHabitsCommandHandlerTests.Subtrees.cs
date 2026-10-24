using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Microsoft.Extensions.DependencyInjection;
using FluentValidation;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Services;
using Orbit.Application.Habits.Validators;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Tests.Commands.Habits;

public partial class BulkCreateHabitsCommandHandlerTests
{
    private static BulkHabitItem Chain(int levels, BulkHabitItem? leaf = null)
    {
        var item = leaf ?? new BulkHabitItem("Node", null, null, null);
        for (var i = 1; i < levels; i++)
            item = new("Node", null, null, null, SubHabits: [item]);
        return item;
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    [InlineData(AppConstants.MaxHabitDepth, false)]
    [InlineData(AppConstants.MaxHabitDepth, true)]
    public async Task ConfiguredDepth_PreflightAndExecutionAgreeWithoutAttachingOverLimit(int maxDepth, bool over)
    {
        _config.GetAsync(AppConfigKeys.MaxHabitDepth, AppConstants.MaxHabitDepth, Arg.Any<CancellationToken>())
            .Returns(maxDepth);
        var (_, saved) = CaptureTagAndHabitAdds();
        var command = new BulkCreateHabitsCommand(UserId, [Chain(maxDepth + (over ? 1 : 0))]);

        var check = await BulkCreateHabitsCommandHandler.CheckArgumentsAsync(command, _userDateService, _config, CancellationToken.None);
        var result = await _handler.Handle(command, CancellationToken.None);

        check.IsSuccess.Should().Be(!over);
        result.Value.Results.Single().Status.Should().Be(over ? BulkItemStatus.Failed : BulkItemStatus.Success);
        saved.Should().HaveCount(over ? 0 : maxDepth);
        if (over)
        {
            check.Error.Should().Be(ErrorMessages.MaxDepthReached.Format(maxDepth).Message);
            result.Value.Results.Single().Field.Should().Be("SubHabits");
        }
        await _config.Received(2).GetAsync(AppConfigKeys.MaxHabitDepth, AppConstants.MaxHabitDepth, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public async Task ChatPreflight_UsesTheConfiguredDepth(int levels, bool valid)
    {
        _config.GetAsync(AppConfigKeys.MaxHabitDepth, AppConstants.MaxHabitDepth, Arg.Any<CancellationToken>()).Returns(2);
        using var services = new ServiceCollection().AddSingleton(_userDateService).AddSingleton(_config)
            .AddSingleton<IValidator<BulkCreateHabitsCommand>, BulkCreateHabitsCommandValidator>().BuildServiceProvider();

        var result = await new CheckChatCommandQueryHandler(services).Handle(
            new(new BulkCreateHabitsCommand(UserId, [Chain(levels)])), CancellationToken.None);

        result.IsSuccess.Should().Be(valid);
    }

    [Fact]
    public async Task Handle_ResolvedInheritanceAndOverrides_PreservePhaseAndSiblingPositions()
    {
        var (_, saved) = CaptureTagAndHabitAdds();
        var existing = Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, UserId, "Existing", null, null, Today, Position: 7)).Value;
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>()).Returns([existing]);
        var root = new BulkHabitItem("Repeated", null, FrequencyUnit.Day, 1, Days: [DayOfWeek.Monday],
            DueDate: Today.AddDays(3), IntervalWeeks: 2, SubHabits:
            [
                new("Repeated", null, null, null, SubHabits: [new("Repeated", null, null, null), new("Repeated", null, null, null)]),
                new("Override", null, FrequencyUnit.Day, 1, Days: [DayOfWeek.Tuesday], DueDate: Today.AddDays(4), IntervalWeeks: 3,
                    SubHabits: [new("Inherited override", null, null, null),
                        new("Weekly", null, FrequencyUnit.Week, 2, DueDate: Today.AddDays(5))])
            ]);

        var result = await _handler.Handle(new(UserId, [root, new("Next root", null, null, null)]), CancellationToken.None);

        result.Value.Results.Select(r => r.Index).Should().Equal(0, 1);
        saved.Should().HaveCount(8).And.OnlyContain(h => h.UserId == UserId);
        saved.Select(h => h.Id).Should().OnlyHaveUniqueItems();
        var parent = saved.Single(h => h.ParentHabitId == null && h.Title == "Repeated");
        parent.Position.Should().Be(8);
        saved.Single(h => h.Title == "Next root").Position.Should().Be(9);
        var children = saved.Where(h => h.ParentHabitId == parent.Id).ToList();
        children.Select(h => h.Position).Should().Equal(0, 1);
        saved.Where(h => h.ParentHabitId == children[0].Id).Select(h => h.Position).Should().Equal(0, 1);
        var inherited = saved.Where(h => h.Title == "Repeated");
        foreach (var node in inherited)
        {
            node.FrequencyUnit.Should().Be(FrequencyUnit.Day);
            node.FrequencyQuantity.Should().Be(1);
            node.Days.Should().Equal(DayOfWeek.Monday);
            node.IntervalWeeks.Should().Be(2);
            node.StartDate.Should().Be(parent.StartDate);
            HabitScheduleService.GetScheduledDates(node, Today, Today.AddDays(45), 1).Should()
                .Equal(HabitScheduleService.GetScheduledDates(parent, Today, Today.AddDays(45), 1));
        }
        var overridden = children[1];
        var grandchild = saved.Single(h => h.Title == "Inherited override");
        grandchild.NextDueDate.Should().Be(overridden.NextDueDate).And.Be(Today.AddDays(4));
        grandchild.StartDate.Should().Be(overridden.StartDate);
        grandchild.IntervalWeeks.Should().Be(3);
        grandchild.Days.Should().Equal(DayOfWeek.Tuesday);
        var weekly = saved.Single(h => h.Title == "Weekly");
        weekly.ParentHabitId.Should().Be(overridden.Id);
        weekly.Position.Should().Be(1);
        weekly.NextDueDate.Should().Be(Today.AddDays(5));
        weekly.FrequencyQuantity.Should().Be(2);
        weekly.Days.Should().BeEmpty();
        weekly.IntervalWeeks.Should().BeNull();
        await _habitRepo.Received(1).FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>());
        await _payGate.Received(1).CanCreateHabits(UserId, 2, Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).AcquireUserRowLockAsync(UserId, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_DescendantFields_PreserveExplicitValuesAndReminderNormalization(bool timed)
    {
        var (_, saved) = CaptureTagAndHabitAdds();
        var checklist = new ChecklistItem("Checked", true);
        var scheduled = new ScheduledReminderTime(ScheduledReminderWhen.DayBefore, new TimeOnly(7, 0));
        var leaf = new BulkHabitItem(" Leaf ", " Description ", FrequencyUnit.Week, 2,
            IsBadHabit: true, DueDate: Today.AddDays(2), DueTime: timed ? new TimeOnly(9, 0) : null,
            DueEndTime: new TimeOnly(10, 0), ReminderEnabled: true, ReminderTimes: [0, 10080],
            ScheduledReminders: [scheduled], RelativeReminders: timed ? [new(15)] : null,
            IsFlexible: true, EndDate: Today.AddDays(10), ChecklistItems: [checklist], GoogleEventId: "descendant-event",
            Emoji: "⭐", Tags: ["Health"]);
        var command = new BulkCreateHabitsCommand(UserId, [Chain(3, leaf)]);

        new BulkCreateHabitsCommandValidator().Validate(command).IsValid.Should().BeTrue();
        (await _handler.Handle(command, CancellationToken.None)).Value.Results.Single().Status.Should().Be(BulkItemStatus.Success);

        var node = saved.Single(h => h.Title == "Leaf");
        node.Description.Should().Be("Description");
        node.Emoji.Should().Be("⭐");
        node.FrequencyUnit.Should().Be(FrequencyUnit.Week);
        node.FrequencyQuantity.Should().Be(2);
        node.IsBadHabit.Should().BeTrue();
        (node.Schedule == HabitSchedule.General).Should().BeFalse();
        (node.Schedule == HabitSchedule.Flexible).Should().BeTrue();
        node.NextDueDate.Should().Be(Today.AddDays(2));
        node.DueTime.Should().Be(leaf.DueTime);
        node.DueEndTime.Should().Be(leaf.DueEndTime);
        node.EndDate.Should().Be(leaf.EndDate);
        node.ChecklistItems.Should().Equal(checklist);
        node.GoogleEventId.Should().Be("descendant-event");
        node.Tags.Single().Name.Should().Be("Health");
        node.ReminderEnabled.Should().BeTrue();
        node.ReminderTimes.Should().Equal(0, 10080);
        node.GetScheduledRemindersForLegacyClients().Should().Equal(scheduled);
        node.ScheduledReminders.Should().Equal(timed ? [] : [scheduled]);
        node.RelativeReminders.Should().Equal(timed ? [new RelativeReminderTime(15), RelativeReminderTime.FromScheduled(scheduled)] : []);
    }

    [Fact]
    public async Task Handle_GeneralClassificationAndDefaults_ResolveThroughImmediateParent()
    {
        var (_, saved) = CaptureTagAndHabitAdds();
        var root = Chain(4) with
        {
            IsGeneral = true,
            DueDate = Today.AddDays(3),
            Tags = ["Root only"],
            ReminderEnabled = true,
            ReminderTimes = [60],
            Emoji = "⭐",
            EndDate = null
        };

        var result = await _handler.Handle(new(UserId, [root]), CancellationToken.None);

        result.Value.Results.Single().Status.Should().Be(BulkItemStatus.Success);
        saved.Should().HaveCount(4).And.OnlyContain(h => (h.Schedule == HabitSchedule.General) && h.NextDueDate == Today.AddDays(3));
        foreach (var child in saved.Skip(1))
        {
            child.Tags.Should().BeEmpty();
            child.Emoji.Should().BeNull();
            child.ReminderEnabled.Should().BeFalse();
            child.ReminderTimes.Should().Equal(15);
            child.ChecklistItems.Should().BeEmpty();
        }
    }

    public static TheoryData<BulkHabitItem> InvalidDescendants => new()
    {
        new("", null, null, null),
        new(new string('x', AppConstants.MaxHabitTitleLength + 1), null, null, null),
        new("Invalid", new string('x', AppConstants.MaxHabitDescriptionLength + 1), null, null),
        new("Invalid", null, FrequencyUnit.Day, 0),
        new("Invalid", null, FrequencyUnit.Week, 1, Days: [DayOfWeek.Monday]),
        new("Invalid", null, null, null, Days: [DayOfWeek.Monday]),
        new("Invalid", null, null, null, IsFlexible: true),
        new("Invalid", null, null, null, IsBadHabit: true),
        new("Invalid", null, FrequencyUnit.Day, 1, IsFlexible: true, Days: [DayOfWeek.Monday]),
        new("Invalid", null, null, null, DueTime: new(9, 0), DueEndTime: new(8, 0)),
        new("Invalid", null, FrequencyUnit.Day, 1, DueDate: Today, EndDate: Today.AddDays(-1)),
        new("Invalid", null, null, null, EndDate: Today.AddDays(1)),
        new("Invalid", null, null, null, Emoji: new string('x', AppConstants.MaxHabitEmojiLength + 1)),
        new("Invalid", null, null, null, Tags: [new string('x', 51)]),
        new("Invalid", null, null, null, Tags: Enumerable.Repeat("Tag", AppConstants.MaxTagsPerHabit + 1).ToList()),
        new("Invalid", null, null, null, IntervalWeeks: 0),
        new("Invalid", null, null, null, ScheduledReminders: [new((ScheduledReminderWhen)99, new(8, 0))]),
        new("Invalid", null, null, null, ReminderTimes: [-1]),
        new("Invalid", null, null, null, ReminderTimes: [10081]),
        new("Invalid", null, null, null, ReminderTimes: [15, 15]),
        new("Invalid", null, null, null, RelativeReminders: [new(15)]),
        new("Invalid", null, null, null, DueTime: new(9, 0),
            RelativeReminders: [new(MinutesBefore: 15, When: ScheduledReminderWhen.SameDay, Time: new(8, 0))]),
        new("Invalid", null, null, null, ChecklistItems: [new(new string('x', AppConstants.MaxChecklistItemTextLength + 1), false)])
    };

    [Theory]
    [MemberData(nameof(InvalidDescendants))]
    public async Task InvalidLaterGrandchild_FailsPreflightAndCreatesNoPrefixWhileIndependentRootsSucceed(BulkHabitItem invalid)
    {
        var (tags, saved) = CaptureTagAndHabitAdds();
        var affected = new BulkHabitItem("Affected", null, null, null, IsGeneral: invalid.IsBadHabit, Tags: ["Unattached"],
            SubHabits: [new("Earlier sibling", null, null, null, Tags: ["Also unattached"]),
                new("Later sibling", null, null, null, SubHabits: [new("Earlier leaf", null, null, null), invalid])]);
        var command = new BulkCreateHabitsCommand(UserId, [new("Before", null, null, null), affected, new("After", null, null, null)]);

        var check = await BulkCreateHabitsCommandHandler.CheckArgumentsAsync(command, _userDateService, _config, CancellationToken.None);
        var result = await _handler.Handle(command, CancellationToken.None);

        check.IsFailure.Should().BeTrue();
        saved.Select(h => h.Title).Should().Equal("Before", "After");
        tags.Should().BeEmpty();
        result.Value.Results.Select(r => r.Status).Should().Equal(BulkItemStatus.Success, BulkItemStatus.Failed, BulkItemStatus.Success);
        result.Value.Results.Select(r => r.Index).Should().Equal(0, 1, 2);
        result.Value.Results[1].Field.Should().Be("SubHabits");
        result.Value.AppliedCount.Should().Be(2);
        result.Value.TotalMatched.Should().Be(3);
        result.Value.SkippedCount.Should().Be(1);
        result.Value.Partial.Should().BeTrue();
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        json.RootElement.GetProperty("applied_count").GetInt32().Should().Be(2);
        json.RootElement.GetProperty("total_matched").GetInt32().Should().Be(3);
        json.RootElement.GetProperty("skipped_count").GetInt32().Should().Be(1);
        json.RootElement.GetProperty("partial").GetBoolean().Should().BeTrue();
        await _tagRepo.DidNotReceive().FindTrackedAsync(Arg.Any<Expression<Func<Tag, bool>>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_DescendantOnlyTags_LoadsAccountTagsOnceAndReusesAcrossDepthsAndRoots()
    {
        var existing = Tag.Create(UserId, "Health", "#123456").Value;
        var foreign = Tag.Create(Guid.NewGuid(), "Private", "#123456").Value;
        _tagRepo.FindTrackedAsync(Arg.Any<Expression<Func<Tag, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => new[] { existing, foreign }.Where(call.Arg<Expression<Func<Tag, bool>>>().Compile()).ToList());
        var (tags, saved) = CaptureTagAndHabitAdds();
        var leaf = new BulkHabitItem("Leaf", null, null, null, Tags: [" health ", "NEW", "new", "Private"]);
        var root = Chain(3, leaf);

        var result = await _handler.Handle(new(UserId, [root, Chain(2, leaf)]), CancellationToken.None);

        result.Value.AppliedCount.Should().Be(2);
        tags.Should().HaveCount(2).And.OnlyContain(t => t.UserId == UserId);
        var leaves = saved.Where(h => h.Title == "Leaf").ToList();
        leaves.Should().HaveCount(2);
        foreach (var node in leaves)
        {
            node.Tags.Should().HaveCount(3).And.Contain(existing).And.NotContain(foreign);
            node.Tags.Should().OnlyContain(t => t.UserId == UserId);
            node.Tags.Single(t => t.Name == "New").Should().BeSameAs(tags.Single(t => t.Name == "New"));
        }
        await _tagRepo.Received(1).FindTrackedAsync(Arg.Any<Expression<Func<Tag, bool>>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ChildGoogleEvent_MarksOnlySuccessfullyCreatedDescendantImported()
    {
        var matching = CreateCalendarSuggestion(UserId, "child-event");
        var failed = CreateCalendarSuggestion(UserId, "failed-event");
        var saved = StubCalendarImportRepositories(matching, failed);
        var valid = Chain(3, new("Imported", null, null, null, GoogleEventId: "child-event"));
        var invalid = Chain(3, new("Invalid", null, null, null, GoogleEventId: "failed-event", SubHabits: [new("", null, null, null)]));

        var result = await _handler.Handle(new(UserId, [valid, invalid], FromSyncReview: true), CancellationToken.None);

        result.Value.AppliedCount.Should().Be(1);
        matching.ImportedHabitId.Should().Be(saved.Single(h => h.Title == "Imported").Id);
        matching.ImportedAtUtc.Should().NotBeNull();
        failed.ImportedAtUtc.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_CancellationBeforeOrDuringAttachment_PropagatesWithoutSaving(bool during)
    {
        using var source = new CancellationTokenSource();
        _habitRepo.AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            source.Cancel();
            return Task.CompletedTask;
        });
        if (!during)
            source.Cancel();

        var action = () => _handler.Handle(new(UserId, [Chain(3)]), source.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        await _habitRepo.Received(during ? 1 : 0).AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }
}
