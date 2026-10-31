using FluentAssertions;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Tests.Entities;

public sealed class HabitSkipUndoTests
{
    private static readonly DateOnly Today = new(2026, 4, 3);

    [Theory]
    [InlineData(HabitSchedule.General)]
    [InlineData(HabitSchedule.OneTime)]
    [InlineData(HabitSchedule.Recurring)]
    [InlineData(HabitSchedule.Flexible)]
    public void CheckSkipEligibility_RejectsOnlyGeneralWithoutMutation(HabitSchedule schedule)
    {
        var repeating = schedule is HabitSchedule.Recurring or HabitSchedule.Flexible;
        var habit = Habit.Create(new HabitCreateParams(schedule, Guid.NewGuid(), "Read",
            repeating ? FrequencyUnit.Day : null, repeating ? 1 : null, Today)).Value;
        var before = (habit.NextDueDate, habit.UpdatedAtUtc);

        var result = habit.CheckSkipEligibility();

        result.IsSuccess.Should().Be(schedule != HabitSchedule.General);
        if (schedule == HabitSchedule.General)
            result.ErrorCode.Should().Be(DomainErrors.CannotSkipGeneralHabit.Code);
        (habit.NextDueDate, habit.UpdatedAtUtc).Should().Be(before);
        habit.Logs.Should().BeEmpty();
    }

    [Fact]
    public void ExistingGeneralReceipt_UndoStillRestoresDueDate()
    {
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.General, Guid.NewGuid(), "Read", null, null, Today)).Value;
        var receipt = HabitSkipUndo.Create(Guid.NewGuid(), habit).Value;
        var previousDueDate = habit.NextDueDate;
        habit.PostponeTo(Today.AddDays(1));
        receipt.Seal(habit, null, "logs");

        receipt.Undo(habit, "logs").IsSuccess.Should().BeTrue();

        habit.NextDueDate.Should().Be(previousDueDate);
        receipt.IsUndone.Should().BeTrue();
    }

    [Fact]
    public void EmptySkipId_IsRejected() =>
        HabitSkipUndo.Create(Guid.Empty, CreateHabit()).IsFailure.Should().BeTrue();

    [Fact]
    public void Undo_RestoresDueDate_AndIsIdempotent()
    {
        var habit = CreateHabit();
        var receipt = HabitSkipUndo.Create(Guid.NewGuid(), habit).Value;
        habit.AdvanceDueDate(Today);
        receipt.Seal(habit, null, "logs");

        receipt.Undo(habit, "logs").IsSuccess.Should().BeTrue();
        habit.NextDueDate.Should().Be(Today);
        var restoredAtUtc = habit.UpdatedAtUtc;
        receipt.Undo(habit, "other logs").IsSuccess.Should().BeTrue();
        habit.UpdatedAtUtc.Should().Be(restoredAtUtc);
    }

    [Fact]
    public void Undo_RefusesChangedHabitOrLogs()
    {
        var habit = CreateHabit();
        var receipt = HabitSkipUndo.Create(Guid.NewGuid(), habit).Value;
        habit.AdvanceDueDate(Today);
        receipt.Seal(habit, null, "logs");
        receipt.Undo(habit, "changed logs").ErrorCode.Should().Be(DomainErrors.SkipUndoConflict.Code);
        habit.PostponeTo(Today.AddDays(4));
        habit.UpdatedAtUtc = receipt.ExpectedUpdatedAtUtc.AddSeconds(1);
        receipt.Undo(habit, "logs").ErrorCode.Should().Be(DomainErrors.SkipUndoConflict.Code);
        habit.NextDueDate.Should().Be(Today.AddDays(4));
    }

    [Fact]
    public void Undo_RefusesAnotherHabit()
    {
        var habit = CreateHabit();
        var receipt = HabitSkipUndo.Create(Guid.NewGuid(), habit).Value;
        receipt.Seal(habit, null, "logs");
        receipt.Undo(CreateHabit(), "logs").ErrorCode.Should().Be(DomainErrors.SkipNotFound.Code);
        CreateHabit().RestoreSkip(receipt).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Undo_FlexibleSkip_PreservesExistingLogsAndCreatesSyncTombstone()
    {
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Flexible, Guid.NewGuid(), "Read", FrequencyUnit.Week, 3,
            Today)).Value;
        var completion = habit.Log(Today).Value;
        var receipt = HabitSkipUndo.Create(Guid.NewGuid(), habit).Value;
        var skipped = habit.SkipFlexible(Today).Value;
        receipt.Seal(habit, skipped.Id, "logs");

        receipt.Undo(habit, "logs").IsSuccess.Should().BeTrue();

        completion.IsDeleted.Should().BeFalse();
        skipped.IsDeleted.Should().BeTrue();
        skipped.DeletedAtUtc.Should().NotBeNull();
        habit.Logs.Where(log => !log.IsDeleted).Should().ContainSingle().Which.Should().Be(completion);
    }

    private static Habit CreateHabit() => Habit.Create(new HabitCreateParams(HabitSchedule.Recurring,
        Guid.NewGuid(), "Read", FrequencyUnit.Day, 1, Today)).Value;
}
