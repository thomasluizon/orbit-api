using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Tests.Chat;

public sealed class PendingOperationChangePreviewerTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private readonly IGenericRepository<Habit> _habits = Substitute.For<IGenericRepository<Habit>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();

    public PendingOperationChangePreviewerTests() =>
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new DateOnly(2026, 9, 25));

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
    [InlineData("bulk_log_habits", "log")]
    [InlineData("bulk_skip_habits", "skip")]
    [InlineData("bulk_update_habit_emojis", "emoji")]
    public async Task PreviewAsync_BulkActions_ListEachTarget(string operationId, string field)
    {
        Setup([CreateHabit("One", null), CreateHabit("Two", null)]);

        var preview = await Preview(operationId, """{"filter":{"all":true}}""");

        preview!.Items.Should().HaveCount(2);
        preview.Items!.SelectMany(item => item.Fields).Select(change => change.Field)
            .Should().OnlyContain(value => value == field);
    }

    [Fact]
    public async Task PreviewAsync_Create_UsesStableInputIndexes()
    {
        var preview = await Preview("bulk_create_habits", """{"habits":[{"title":"One"},{"title":"Two"}]}""");

        preview!.Items.Should().HaveCount(2);
        preview.Items!.Select(item => item.ItemId).Should().Equal("0", "1");
        preview.Items.SelectMany(item => item.Fields).Select(field => field.Field)
            .Should().OnlyContain(field => field == "title");
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
    public async Task PreviewAsync_ZeroMatches_ReturnsEmptyRowsAndZeroTotal()
    {
        Setup([]);

        var preview = await Preview("bulk_update_habits", """{"filter":{"all":true},"updates":{"emoji":"✅"}}""");

        preview!.ChangeTargetCount.Should().Be(0);
        preview.Changes.Should().BeEmpty();
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

        preview!.Changes.Should().ContainSingle().Which.Should().Match<Orbit.Domain.Models.PendingOperationChange>(
            row => row.Field == "reminder_times" && row.OldValue == "(none)" && row.NewValue == "30 min before due");
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

    private async Task<Orbit.Domain.Models.PendingOperationChangePreview?> Preview(string operation, string json) =>
        await new PendingOperationChangePreviewer(_habits, _userDateService).PreviewAsync(
            UserId, operation, JsonDocument.Parse(json).RootElement);

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
