using FluentAssertions;
using Orbit.Application.Habits.Services;
using Orbit.Application.Tests.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Application.Tests.Queries.Habits;

public class DailySummaryPendingProjectionTests
{
    private static readonly DateOnly Today = new(2026, 10, 25);
    private static readonly Guid UserId = Guid.NewGuid();

    [Fact]
    public void PendingSteps_ExcludeResolvedOccurrencesAndKeepNestedAndPartialQuotaSteps()
    {
        var root = Create("Routine");
        var group = Create("Nested group", parent: root.Id);
        var pending = Create("Next step", parent: group.Id);
        var skipped = Create("Skipped", parent: group.Id);
        skipped.LoadScheduleLogsForRead([HabitLog.FromSummaryRead(skipped.Id, Today, 0)]);
        var done = Create("Done", parent: root.Id);
        done.Log(Today).IsSuccess.Should().BeTrue();
        var clean = Create("Smoking", bad: true, flexible: true);
        var partial = Create("Quota", flexible: true, quantity: 3);
        partial.Log(Today).IsSuccess.Should().BeTrue();
        var met = Create("Met quota", flexible: true);
        met.Log(Today).IsSuccess.Should().BeTrue();
        var future = Create("Future", date: Today.AddDays(1));
        var habits = new[] { root, group, pending, skipped, done, clean, partial, met, future };

        var result = DailySummaryPendingProjection.Project(DailySummaryScheduleFixture.Project(habits, Today), Today);

        result.Select(item => item.Id).Should().BeEquivalentTo([root.Id, group.Id, pending.Id, partial.Id]);
        result.Single(item => item.Id == pending.Id).ParentId.Should().Be(group.Id);
        result.Single(item => item.Id == pending.Id).ParentTitle.Should().Be(group.Title);
        result.Single(item => item.Id == group.Id).HasPendingChildren.Should().BeTrue();
        result.Single(item => item.Id == root.Id).CompletedCount.Should().Be(1);
        result.Single(item => item.Id == root.Id).TotalCount.Should().Be(2);
        result.Single(item => item.Id == partial.Id).CompletedCount.Should().Be(1);
        result.Single(item => item.Id == partial.Id).TotalCount.Should().Be(3);
    }

    [Fact]
    public void PendingSteps_OrderByOccurrenceTimeThenPersonalOrderWithUntimedLast()
    {
        var untimed = Create("First by position", position: 0);
        var later = Create("Early title", time: new TimeOnly(16, 0), position: 1);
        var tiedSecond = Create("A title", time: new TimeOnly(12, 0), position: 3);
        var tiedFirst = Create("Z title", time: new TimeOnly(12, 0), position: 2);
        var deadline = Create("Deadline only", end: new TimeOnly(10, 0), position: 4);
        var habits = new[] { later, untimed, tiedSecond, deadline, tiedFirst };

        var result = DailySummaryPendingProjection.Project(DailySummaryScheduleFixture.Project(habits, Today), Today);

        result.Select(item => item.Id).Should().Equal(deadline.Id, tiedFirst.Id, tiedSecond.Id, later.Id, untimed.Id);
        result.Single(item => item.Id == tiedFirst.Id).DueDate.Should().Be(Today);
    }

    [Fact]
    public void PendingSteps_UseUnresolvedOccurrenceDateForOverduePriority()
    {
        var today = Create("Today", time: new TimeOnly(8, 0));
        var overdue = Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, UserId, "Older task",
            null, null, DueDate: Today.AddDays(-1), DueTime: new TimeOnly(20, 0))).Value;

        var result = DailySummaryPendingProjection.Project(DailySummaryScheduleFixture.Project([today, overdue], Today), Today);

        result.Select(item => item.Id).Should().Equal(overdue.Id, today.Id);
        result[0].DueDate.Should().Be(Today.AddDays(-1));
        result[0].IsOverdue.Should().BeTrue();
    }

    private static Habit Create(string title, Guid? parent = null, DateOnly? date = null,
        TimeOnly? time = null, TimeOnly? end = null, int? position = null,
        bool bad = false, bool flexible = false, int quantity = 1) =>
        Habit.Create(new HabitCreateParams(flexible ? HabitSchedule.Flexible : HabitSchedule.Recurring,
            UserId, title, FrequencyUnit.Day, quantity, DueDate: date ?? Today,
            DueTime: time, DueEndTime: end, ParentHabitId: parent, Position: position, IsBadHabit: bad)).Value;
}
