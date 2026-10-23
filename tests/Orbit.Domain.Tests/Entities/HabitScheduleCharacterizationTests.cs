using FluentAssertions;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;
using Orbit.Domain.ValueObjects;

namespace Orbit.Domain.Tests.Entities;

public class HabitScheduleCharacterizationTests
{
    private static readonly DateOnly Start = new(2026, 4, 3);

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(1, 1, false)]
    [InlineData(2, 0, false)]
    [InlineData(3, 0, false)]
    public void LogAndUnlog_KeepFourTypeBehavior(int type, int dueOffset, bool completed)
    {
        var habit = Create(type);
        var log = habit.Log(Start);
        log.IsSuccess.Should().BeTrue();
        habit.NextDueDate.Should().Be(Start.AddDays(dueOffset));
        habit.IsCompleted.Should().Be(completed);
        habit.StartDate.Should().Be(Start);
        log.Value.Date.Should().Be(Start);
        log.Value.Value.Should().Be(1);

        habit.Unlog(Start).IsSuccess.Should().BeTrue();
        habit.IsCompleted.Should().BeFalse();
        habit.NextDueDate.Should().Be(Start);
        habit.StartDate.Should().Be(Start);
        habit.Logs.Should().ContainSingle().Which.IsDeleted.Should().BeTrue();
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 3)]
    [InlineData(2, 3)]
    [InlineData(3, 0)]
    public void CatchUp_KeepsFourTypeDatesAndCompletion(int type, int dueOffset)
    {
        var habit = Create(type);

        habit.CatchUpDueDate(Start.AddDays(3)).IsSuccess.Should().BeTrue();

        habit.NextDueDate.Should().Be(Start.AddDays(dueOffset));
        habit.IsCompleted.Should().BeFalse();
        habit.Logs.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 4)]
    [InlineData(2, 4)]
    [InlineData(3, 0)]
    public void AdvanceDueDate_KeepsFourTypeDatesAndCompletion(int type, int dueOffset)
    {
        var habit = Create(type);

        habit.AdvanceDueDate(Start.AddDays(3)).IsSuccess.Should().BeTrue();

        habit.NextDueDate.Should().Be(Start.AddDays(dueOffset));
        habit.IsCompleted.Should().BeFalse();
        habit.Logs.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void SkipFlexible_KeepsFourTypeSuccessAndRejection(int type)
    {
        var habit = Create(type);

        var result = habit.SkipFlexible(Start);

        result.IsSuccess.Should().Be(type == 2);
        habit.NextDueDate.Should().Be(Start);
        habit.IsCompleted.Should().BeFalse();
        if (type == 2)
        {
            var log = habit.Logs.Should().ContainSingle().Subject;
            log.Date.Should().Be(Start);
            log.Value.Should().Be(0);
            log.IsDeleted.Should().BeFalse();
        }
        else
        {
            result.Error.Should().Be(DomainErrors.OnlyFlexibleHabitsSkippable.Message);
            habit.Logs.Should().BeEmpty();
        }
    }

    [Fact]
    public void SkipFlexible_RejectsOneTimeLegacySnapshotWithoutMutation()
    {
        var flexible = Create(2);
        var habit = Habit.FromScheduleSnapshot(HabitScheduleSnapshot.FromHabit(flexible) with
        {
            FrequencyUnit = null,
            FrequencyQuantity = null
        }, flexible.UserId);
        var updatedAtUtc = habit.UpdatedAtUtc;

        var result = habit.SkipFlexible(Start);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(DomainErrors.CannotSkipOneTimeTask.Message);
        habit.NextDueDate.Should().Be(Start);
        habit.IsCompleted.Should().BeFalse();
        habit.Logs.Should().BeEmpty();
        habit.UpdatedAtUtc.Should().Be(updatedAtUtc);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Postpone_KeepsFourTypeDatesAndCompletion(int type)
    {
        var habit = Create(type);

        habit.PostponeTo(Start.AddDays(4));

        habit.NextDueDate.Should().Be(Start.AddDays(4));
        habit.IsCompleted.Should().BeFalse();
        habit.Logs.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public void Log_KeepsTypeSpecificChecklistReset(int type, bool remainsChecked)
    {
        var habit = Create(type);
        habit.UpdateChecklist([new ChecklistItem("Read", true)]);

        habit.Log(Start).IsSuccess.Should().BeTrue();

        habit.ChecklistItems.Should().Equal(new ChecklistItem("Read", remainsChecked));
        habit.StartDate.Should().Be(Start);
    }

    [Fact]
    public void FlexibleLogAndUndo_KeepCompletionOrdinalsAndWindowTarget()
    {
        var habit = Habit.Create(new HabitCreateParams(Guid.NewGuid(), "Habit",
            FrequencyUnit.Week, 2, Start, IsFlexible: true)).Value;

        habit.Log(Start).IsSuccess.Should().BeTrue();
        habit.Log(Start).IsSuccess.Should().BeTrue();
        habit.CheckLog(Start, 1).ErrorCode.Should().Be(DomainErrors.AllInstancesDone.Code);
        habit.Unlog(Start).IsSuccess.Should().BeTrue();
        habit.Log(Start).IsSuccess.Should().BeTrue();

        habit.Logs.Select(log => log.CompletionOrdinal).Should().Equal(0, 1, 2);
        habit.Logs.Count(log => !log.IsDeleted).Should().Be(2);
        habit.NextDueDate.Should().Be(Start);
        habit.StartDate.Should().Be(Start);
    }

    private static Habit Create(int type) => Habit.Create(new HabitCreateParams(
        Guid.NewGuid(), "Habit", type is 1 or 2 ? FrequencyUnit.Day : null,
        type is 1 or 2 ? 1 : null, Start,
        IsFlexible: type == 2, IsGeneral: type == 3)).Value;
}
