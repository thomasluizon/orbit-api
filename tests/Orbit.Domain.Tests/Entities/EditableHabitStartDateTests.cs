using FluentAssertions;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Domain.Tests.Entities;

public class EditableHabitStartDateTests
{
    private static readonly DateOnly Today = new(2026, 10, 20);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InheritResolvedOccurrence_PreservesChildAnchorAndCadence(bool terminal)
    {
        var userId = Guid.NewGuid();
        var anchor = new DateOnly(2026, 10, 1);
        var end = new DateOnly(2026, 10, terminal ? 10 : 30);
        var parent = Habit.Create(new HabitCreateParams(userId, "Read", FrequencyUnit.Day, 1, anchor,
            Days: [DayOfWeek.Wednesday], StartDate: anchor, EndDate: end)).Value;
        parent.CatchUpDueDate(Today).IsSuccess.Should().BeTrue();
        var child = Habit.Create(new HabitCreateParams(userId, "Chapter", FrequencyUnit.Day, 1, anchor,
            ParentHabitId: parent.Id, StartDate: anchor, EndDate: end)).Value;

        child.InheritResolvedOccurrence(parent);

        child.DueDate.Should().Be(new DateOnly(2026, 10, terminal ? 14 : 21));
        child.IsCompleted.Should().Be(terminal);
        child.StartDate.Should().Be(anchor);
        child.ScheduledStartDate.Should().Be(anchor);
        child.Days.Should().BeEmpty();
        child.EndDate.Should().Be(end);
        child.FrequencyUnit.Should().Be(FrequencyUnit.Day);
        child.FrequencyQuantity.Should().Be(1);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void InheritResolvedOccurrence_RejectsUnrelatedParentOrAccount(bool sameParent, bool sameAccount)
    {
        var userId = Guid.NewGuid();
        var parent = Habit.Create(new HabitCreateParams(userId, "Read", FrequencyUnit.Day, 1, Today.AddDays(2))).Value;
        var child = Habit.Create(new HabitCreateParams(sameAccount ? userId : Guid.NewGuid(), "Chapter",
            FrequencyUnit.Day, 1, Today, ParentHabitId: sameParent ? parent.Id : Guid.NewGuid())).Value;
        var updatedAtUtc = child.UpdatedAtUtc;

        var act = () => child.InheritResolvedOccurrence(parent);

        act.Should().Throw<ArgumentException>();
        child.DueDate.Should().Be(Today);
        child.IsCompleted.Should().BeFalse();
        child.StartDate.Should().Be(Today);
        child.UpdatedAtUtc.Should().Be(updatedAtUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PostponeTo_PreservesOneTimeCompletionState(bool completed)
    {
        var habit = Habit.Create(new HabitCreateParams(Guid.NewGuid(), "Read", null, null, Today)).Value;
        if (completed)
            habit.Log(Today).IsSuccess.Should().BeTrue();

        habit.PostponeTo(Today.AddDays(2));

        habit.DueDate.Should().Be(Today.AddDays(2));
        habit.IsCompleted.Should().Be(completed);
        habit.StartDate.Should().Be(Today);
    }

    [Fact]
    public void UnchangedAnchor_ChangedWeekdayRecomputesEligibleOccurrence()
    {
        var anchor = new DateOnly(2026, 10, 26);
        var habit = Habit.Create(new HabitCreateParams(Guid.NewGuid(), "Read", FrequencyUnit.Day, 1,
            anchor, Days: [DayOfWeek.Monday], StartDate: anchor)).Value;
        habit.Update(new("Read", null, FrequencyUnit.Day, 1, [DayOfWeek.Wednesday], false, anchor.AddDays(2),
            StartDate: anchor, UserToday: Today)).IsSuccess.Should().BeTrue();
        habit.DueDate.Should().Be(anchor.AddDays(2));
        habit.StartDate.Should().Be(anchor);
        habit.ScheduledStartDate.Should().Be(anchor);
    }

    [Theory]
    [InlineData(0, "2026-10-26")]
    [InlineData(1, "2026-11-02")]
    public void UnchangedAnchor_IntervalEditUsesAccountWeekStart(int weekStart, string expected)
    {
        var anchor = new DateOnly(2026, 10, 25);
        var habit = Habit.Create(new HabitCreateParams(Guid.NewGuid(), "Read", FrequencyUnit.Day, 1,
            anchor, Days: [DayOfWeek.Monday], StartDate: anchor)).Value;
        habit.Update(new("Read", null, FrequencyUnit.Day, 1, [DayOfWeek.Monday], false, null,
            StartDate: anchor, UserToday: Today, IntervalWeeks: 2, WeekStartDay: weekStart)).IsSuccess.Should().BeTrue();
        habit.DueDate.Should().Be(DateOnly.Parse(expected));
        habit.ScheduledStartDate.Should().Be(anchor);
    }

    [Fact]
    public void UnchangedAnchor_EveryNEditCatchesUpFromSelection()
    {
        var anchor = Today.AddDays(-8);
        var habit = Habit.Create(new HabitCreateParams(Guid.NewGuid(), "Read", FrequencyUnit.Day, 1,
            anchor, StartDate: anchor)).Value;
        habit.CatchUpDueDate(Today).IsSuccess.Should().BeTrue();
        habit.Update(new("Read", null, FrequencyUnit.Day, 3, null, false, null,
            StartDate: anchor, UserToday: Today)).IsSuccess.Should().BeTrue();
        habit.DueDate.Should().Be(Today.AddDays(1));
        habit.ScheduledStartDate.Should().Be(anchor);
    }

    [Fact]
    public void UnchangedAnchor_MonthIntervalEditRetainsMonthEndPhase()
    {
        var anchor = new DateOnly(2028, 1, 31);
        var habit = Habit.Create(new HabitCreateParams(Guid.NewGuid(), "Read", FrequencyUnit.Month, 1,
            anchor, StartDate: anchor)).Value;
        habit.Log(anchor).IsSuccess.Should().BeTrue();
        habit.Update(new("Read", null, FrequencyUnit.Month, 2, null, false, null,
            StartDate: anchor, UserToday: new DateOnly(2028, 2, 1))).IsSuccess.Should().BeTrue();
        habit.DueDate.Should().Be(new DateOnly(2028, 3, 31));
        habit.ScheduledStartDate.Should().Be(anchor);
        habit.OriginalDayOfMonth.Should().Be(31);
        habit.Log(habit.DueDate).IsSuccess.Should().BeTrue();
        habit.DueDate.Should().Be(new DateOnly(2028, 5, 31));
    }

    [Fact]
    public void Create_StoresSelectedDaySeparatelyFromFirstWeekday()
    {
        var anchor = new DateOnly(2026, 10, 26);
        var habit = Habit.Create(new HabitCreateParams(Guid.NewGuid(), "Read", FrequencyUnit.Day, 1,
            anchor.AddDays(2), Days: [DayOfWeek.Wednesday], StartDate: anchor)).Value;
        habit.StartDate.Should().Be(anchor);
        habit.DueDate.Should().Be(anchor.AddDays(2));
        habit.ScheduledStartDate.Should().Be(anchor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Edit_ReanchorsFutureScheduleAndPreservesLogsAndSubsequentMutations(bool flexible)
    {
        var habit = Habit.Create(new HabitCreateParams(Guid.NewGuid(), "Read", FrequencyUnit.Day, 3,
            Today.AddDays(-12), IsFlexible: flexible)).Value;
        habit.Log(Today.AddDays(-12)).IsSuccess.Should().BeTrue();
        habit.Log(Today.AddDays(-9)).IsSuccess.Should().BeTrue();
        var logs = habit.Logs.Select(log => (log.Id, log.Date, log.Value, log.IsDeleted)).ToArray();
        var anchor = Today.AddDays(-4);
        var update = new HabitUpdateParams("Read", null, FrequencyUnit.Day, 3, null, false, Today.AddDays(12),
            StartDate: anchor, UserToday: Today);
        habit.Update(update).IsSuccess.Should().BeTrue();
        habit.StartDate.Should().Be(anchor);
        habit.ScheduledStartDate.Should().Be(anchor);
        habit.DueDate.Should().Be(flexible ? Today : Today.AddDays(2));
        habit.Logs.Select(log => (log.Id, log.Date, log.Value, log.IsDeleted)).Should().Equal(logs);
        var cursor = habit.DueDate;
        habit.Update(update with { Title = "Renamed", Emoji = "📖" }).IsSuccess.Should().BeTrue();
        habit.DueDate.Should().Be(cursor);
        habit.PostponeTo(Today.AddDays(8));
        habit.CatchUpDueDate(Today.AddDays(10)).IsSuccess.Should().BeTrue();
        habit.Log(Today.AddDays(10)).IsSuccess.Should().BeTrue();
        habit.Unlog(Today.AddDays(10)).IsSuccess.Should().BeTrue();
        habit.StartDate.Should().Be(anchor);
    }

    [Fact]
    public void UnchangedSave_PreservesMonthEndPhase()
    {
        var start = new DateOnly(2028, 1, 31);
        var habit = Habit.Create(new HabitCreateParams(Guid.NewGuid(), "Read", FrequencyUnit.Month, 1, start)).Value;
        habit.Log(start).IsSuccess.Should().BeTrue();
        habit.DueDate.Should().Be(new DateOnly(2028, 2, 29));
        habit.Update(new HabitUpdateParams("Renamed", null, FrequencyUnit.Month, 1, null, false, habit.DueDate,
            StartDate: start, UserToday: start)).IsSuccess.Should().BeTrue();
        habit.OriginalDayOfMonth.Should().Be(31);
        habit.Log(habit.DueDate).IsSuccess.Should().BeTrue();
        habit.DueDate.Should().Be(new DateOnly(2028, 3, 31));
    }

    [Fact]
    public void Edit_SkipsAlreadyLoggedTodayWithoutDuplicatingHistory()
    {
        var habit = Habit.Create(new HabitCreateParams(Guid.NewGuid(), "Read", FrequencyUnit.Day, 1, Today)).Value;
        habit.Log(Today).IsSuccess.Should().BeTrue();
        habit.Update(new HabitUpdateParams("Read", null, FrequencyUnit.Day, 1, null, false, null,
            StartDate: Today.AddDays(-1), UserToday: Today)).IsSuccess.Should().BeTrue();
        habit.DueDate.Should().Be(Today.AddDays(1));
        habit.Logs.Should().ContainSingle();
    }

    [Fact]
    public void Edit_WeekdayInInactiveWeekMovesToNextActiveWeek()
    {
        var anchor = new DateOnly(2026, 10, 24);
        var habit = Habit.Create(new HabitCreateParams(Guid.NewGuid(), "Read", FrequencyUnit.Day, 1,
            Today, Days: [DayOfWeek.Monday], IntervalWeeks: 2)).Value;
        habit.Update(new HabitUpdateParams("Read", null, FrequencyUnit.Day, 1, [DayOfWeek.Monday], false, null,
            StartDate: anchor, UserToday: Today, IntervalWeeks: 2)).IsSuccess.Should().BeTrue();
        habit.StartDate.Should().Be(anchor);
        habit.ScheduledStartDate.Should().Be(anchor);
        habit.DueDate.Should().Be(new DateOnly(2026, 11, 2));
    }

    [Fact]
    public void Edit_ChangesSnapshotAndHydratedProjectionAnchor()
    {
        var habit = Habit.Create(new HabitCreateParams(Guid.NewGuid(), "Read", FrequencyUnit.Day, 1, Today)).Value;
        habit.Update(new HabitUpdateParams("Read", null, FrequencyUnit.Day, 1, null, false, null,
            StartDate: Today.AddDays(3), UserToday: Today)).IsSuccess.Should().BeTrue();
        var read = Habit.FromScheduleSnapshot(HabitScheduleSnapshot.FromHabit(habit), habit.UserId);
        read.StartDate.Should().Be(Today.AddDays(3));
        HabitScheduleSnapshot.FromHabit(read).StartDate.Should().Be(Today.AddDays(3));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitStart_RejectsUndatedAndOneTimeSchedules(bool general)
    {
        Habit.Create(new HabitCreateParams(Guid.NewGuid(), "Read", null, null, Today,
            IsGeneral: general, StartDate: Today)).IsFailure.Should().BeTrue();
        var habit = Habit.Create(new HabitCreateParams(Guid.NewGuid(), "Read", null, null, Today, IsGeneral: general)).Value;
        habit.Update(new HabitUpdateParams("Read", null, null, null, null, false, null,
            StartDate: Today.AddDays(1), UserToday: Today)).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void InvalidEdit_IsAtomicAndRejectsEndBeforeAnchorOrMissingContext()
    {
        var habit = Habit.Create(new HabitCreateParams(Guid.NewGuid(), "Read", FrequencyUnit.Day, 1, Today)).Value;
        var edit = new HabitUpdateParams("Changed", null, FrequencyUnit.Day, 1, null, false, null,
            StartDate: Today.AddDays(3), UserToday: Today, EndDate: Today.AddDays(2));
        habit.Update(edit).ErrorCode.Should().Be(DomainErrors.EndDateBeforeStartDate.Code);
        habit.Update(edit with { EndDate = null, UserToday = null }).IsFailure.Should().BeTrue();
        habit.Title.Should().Be("Read");
        habit.StartDate.Should().Be(Today);
        habit.Update(edit with { EndDate = null, StartDate = DateOnly.MaxValue, Days = [DayOfWeek.Monday] })
            .IsFailure.Should().BeTrue();
        habit.Title.Should().Be("Read");
        habit.StartDate.Should().Be(Today);
    }
}
