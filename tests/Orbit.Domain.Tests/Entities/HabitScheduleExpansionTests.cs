using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Tests.Entities;

public class HabitScheduleExpansionTests
{
    [Fact]
    public void LogTenTimes_PreservesOriginalStartDate()
    {
        var start = new DateOnly(2026, 4, 3);
        var habit = Habit.Create(new HabitCreateParams(
            Guid.NewGuid(), "Habit", FrequencyUnit.Day, 1, start)).Value;
        for (var offset = 0; offset < 10; offset++)
            habit.Log(start.AddDays(offset)).IsSuccess.Should().BeTrue();

        habit.NextDueDate.Should().Be(start.AddDays(10));
        habit.StartDate.Should().Be(start);
    }

    [Theory]
    [InlineData(HabitSchedule.OneTime)]
    [InlineData(HabitSchedule.Recurring)]
    [InlineData(HabitSchedule.Flexible)]
    [InlineData(HabitSchedule.General)]
    public void Create_DerivesScheduleAlongsideLegacyFlags(HabitSchedule schedule)
    {
        var habit = Create(schedule);
        AssertSchedule(habit, schedule);
        habit.StartDate.Should().Be(Start);
        typeof(Habit).GetProperty(nameof(Habit.StartDate))!.SetMethod!.IsPrivate.Should().BeTrue();
    }

    [Theory]
    [InlineData("Unlog")]
    [InlineData("CatchUp")]
    [InlineData("Postpone")]
    [InlineData("Update")]
    [InlineData("UpdateEarlier")]
    [InlineData("PostponeEarlier")]
    [InlineData("Skip")]
    public void Mutations_PreserveOriginalStartDate(string operation)
    {
        var habit = Create(operation == "Skip" ? HabitSchedule.Flexible : HabitSchedule.Recurring);
        switch (operation)
        {
            case "Unlog":
                habit.Log(Start.AddDays(1)).IsSuccess.Should().BeTrue();
                habit.Unlog(Start.AddDays(1)).IsSuccess.Should().BeTrue();
                habit.NextDueDate.Should().Be(Start.AddDays(1));
                break;
            case "CatchUp":
                habit.CatchUpDueDate(Start.AddDays(4)).IsSuccess.Should().BeTrue();
                habit.NextDueDate.Should().Be(Start.AddDays(4));
                break;
            case "Postpone":
                habit.PostponeTo(Start.AddDays(4));
                habit.NextDueDate.Should().Be(Start.AddDays(4));
                break;
            case "Update":
                habit.Update(new HabitUpdateParams(
                    "Updated", null, FrequencyUnit.Day, 1, null, false, Start.AddDays(4)))
                    .IsSuccess.Should().BeTrue();
                habit.NextDueDate.Should().Be(Start.AddDays(4));
                habit.ScheduledStartDate.Should().Be(Start.AddDays(4));
                break;
            case "UpdateEarlier":
                habit.Update(new HabitUpdateParams(
                    "Updated", null, FrequencyUnit.Day, 1, null, false, Start.AddDays(-4)))
                    .IsSuccess.Should().BeTrue();
                habit.NextDueDate.Should().Be(Start.AddDays(-4));
                break;
            case "PostponeEarlier":
                habit.PostponeTo(Start.AddDays(-4));
                habit.NextDueDate.Should().Be(Start.AddDays(-4));
                break;
            case "Skip":
                habit.SkipFlexible(Start).IsSuccess.Should().BeTrue();
                break;
        }
        habit.StartDate.Should().Be(Start);
    }

    [Fact]
    public void Update_AllTypeTransitionsKeepScheduleAndFlagsTogether()
    {
        foreach (var initial in Enum.GetValues<HabitSchedule>())
            foreach (var target in Enum.GetValues<HabitSchedule>())
            {
                var habit = Create(initial);
                var recurring = target is HabitSchedule.Recurring or HabitSchedule.Flexible;
                var result = habit.Update(new HabitUpdateParams(
                    "Updated", null, recurring ? FrequencyUnit.Day : null,
                    recurring ? 1 : null, null, false, Start.AddDays(1),
                    IsGeneral: target == HabitSchedule.General,
                    IsFlexible: target == HabitSchedule.Flexible));

                result.IsSuccess.Should().BeTrue();
                AssertSchedule(habit, target);
                habit.StartDate.Should().Be(Start);
            }
    }

    [Fact]
    public void Update_RetainedFlagsAndChangedFrequencyDeriveEffectiveSchedule()
    {
        var habit = Create(HabitSchedule.OneTime);
        habit.Update(new HabitUpdateParams("Recurring", null, FrequencyUnit.Week, 2, null, false, null))
            .IsSuccess.Should().BeTrue();
        AssertSchedule(habit, HabitSchedule.Recurring);
        habit.Update(new HabitUpdateParams("One time", null, null, null, null, false, null))
            .IsSuccess.Should().BeTrue();
        AssertSchedule(habit, HabitSchedule.OneTime);

        var flexible = Create(HabitSchedule.Flexible);
        flexible.Update(new HabitUpdateParams("Flexible", null, FrequencyUnit.Month, 3, null, false, null))
            .IsSuccess.Should().BeTrue();
        AssertSchedule(flexible, HabitSchedule.Flexible);
    }

    [Fact]
    public void InvalidUpdate_LeavesScheduleFlagsAndStartUntouched()
    {
        var habit = Create(HabitSchedule.Recurring);
        habit.Update(new HabitUpdateParams(
            "Invalid", null, FrequencyUnit.Day, 1, null, false, Start.AddDays(1), IsGeneral: true))
            .IsFailure.Should().BeTrue();
        AssertSchedule(habit, HabitSchedule.Recurring);
        habit.StartDate.Should().Be(Start);
        habit.NextDueDate.Should().Be(Start);
    }

    [Fact]
    public void Days_StillRequireEveryOneDay()
    {
        Habit.Create(new HabitCreateParams(
            Guid.NewGuid(), "Invalid", FrequencyUnit.Day, 2, Start, Days: [DayOfWeek.Friday]))
            .ErrorCode.Should().Be(Orbit.Domain.Common.DomainErrors.DaysRequireQuantityOne.Code);
    }

    [Fact]
    public void SnapshotAndHydration_PreserveStartAndScheduleAfterDateAdvancement()
    {
        var original = Create(HabitSchedule.Recurring);
        original.Log(Start).IsSuccess.Should().BeTrue();
        var snapshot = Orbit.Domain.Models.HabitScheduleSnapshot.FromHabit(original);
        var hydrated = Habit.FromScheduleSnapshot(snapshot, original.UserId);
        hydrated.StartDate.Should().Be(Start);
        hydrated.NextDueDate.Should().Be(Start.AddDays(1));
        AssertSchedule(hydrated, HabitSchedule.Recurring);

        original.Update(new HabitUpdateParams(
            "Recurring", null, FrequencyUnit.Week, 2, null, false, Start.AddDays(2)))
            .IsSuccess.Should().BeTrue();
        hydrated.LoadScheduleFieldsForRead(original);
        hydrated.StartDate.Should().Be(Start);
        hydrated.NextDueDate.Should().Be(Start.AddDays(2));
        AssertSchedule(hydrated, HabitSchedule.Recurring);
    }

    private static readonly DateOnly Start = new(2026, 4, 3);

    private static Habit Create(HabitSchedule schedule) => Habit.Create(new HabitCreateParams(
        Guid.NewGuid(), "Habit",
        schedule is HabitSchedule.Recurring or HabitSchedule.Flexible ? FrequencyUnit.Day : null,
        schedule is HabitSchedule.Recurring or HabitSchedule.Flexible ? 1 : null,
        Start, IsGeneral: schedule == HabitSchedule.General, IsFlexible: schedule == HabitSchedule.Flexible)).Value;

    private static void AssertSchedule(Habit habit, HabitSchedule schedule)
    {
        habit.Schedule.Should().Be(schedule);
        habit.IsGeneral.Should().Be(schedule == HabitSchedule.General);
        habit.IsFlexible.Should().Be(schedule == HabitSchedule.Flexible);
        habit.FrequencyUnit.HasValue.Should().Be(schedule is HabitSchedule.Recurring or HabitSchedule.Flexible);
    }
}
