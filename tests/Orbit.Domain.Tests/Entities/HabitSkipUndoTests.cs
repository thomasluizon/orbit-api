using FluentAssertions;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Tests.Entities;

public sealed class HabitSkipUndoTests
{
    private static readonly DateOnly Today = new(2026, 4, 3);

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
        habit.DueDate.Should().Be(Today);
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
        habit.DueDate.Should().Be(Today.AddDays(4));
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
        var habit = Habit.Create(new HabitCreateParams(Guid.NewGuid(), "Read", FrequencyUnit.Week, 3,
            Today, IsFlexible: true)).Value;
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

    private static Habit CreateHabit() => Habit.Create(new HabitCreateParams(
        Guid.NewGuid(), "Read", FrequencyUnit.Day, 1, Today)).Value;
}
