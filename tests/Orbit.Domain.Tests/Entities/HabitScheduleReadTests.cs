using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Tests.Entities;

public class HabitScheduleReadTests
{
    private static readonly DateOnly DueDate = new(2026, 6, 1);

    [Fact]
    public void FromScheduleRead_RejectsMissingIdsAndPreservesProjectedFields()
    {
        var habitId = Guid.NewGuid();
        var logId = Guid.NewGuid();
        var createdAtUtc = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        var missingLogId = () => HabitLog.FromScheduleRead(Guid.Empty, habitId, DueDate, 2, createdAtUtc);
        var missingHabitId = () => HabitLog.FromScheduleRead(logId, Guid.Empty, DueDate, 2, createdAtUtc);
        missingLogId.Should().Throw<ArgumentException>();
        missingHabitId.Should().Throw<ArgumentException>();

        var log = HabitLog.FromScheduleRead(logId, habitId, DueDate, 2, createdAtUtc);
        log.Id.Should().Be(logId);
        log.HabitId.Should().Be(habitId);
        log.Date.Should().Be(DueDate);
        log.Value.Should().Be(2);
        log.CreatedAtUtc.Should().Be(createdAtUtc);
        log.UpdatedAtUtc.Should().Be(createdAtUtc);
    }

    [Fact]
    public void LoadScheduleLogsForRead_ReplacesLogsAndRejectsAnotherHabitsLogs()
    {
        var userId = Guid.NewGuid();
        var habit = CreateHabit(userId);
        var other = CreateHabit(userId);
        var existing = habit.Log(DueDate, advanceDueDate: false).Value;
        var replacement = HabitLog.FromScheduleRead(
            Guid.NewGuid(), habit.Id, DueDate.AddDays(1), 1, DateTime.UtcNow);
        var foreign = HabitLog.FromScheduleRead(
            Guid.NewGuid(), other.Id, DueDate, 1, DateTime.UtcNow);

        habit.LoadScheduleLogsForRead([replacement]);
        habit.Logs.Should().ContainSingle().Which.Id.Should().Be(replacement.Id);
        habit.Logs.Should().NotContain(log => log.Id == existing.Id);
        var loadForeign = () => habit.LoadScheduleLogsForRead([foreign]);
        loadForeign.Should().Throw<ArgumentException>();
        habit.Logs.Should().ContainSingle().Which.Id.Should().Be(replacement.Id);
    }

    [Fact]
    public void LoadScheduleRelationsForRead_ReplacesRelationsAndRejectsOtherUsersRelations()
    {
        var userId = Guid.NewGuid();
        var habit = CreateHabit(userId);
        var firstTag = Tag.Create(userId, "First", "#fff").Value;
        var nextTag = Tag.Create(userId, "Next", "#fff").Value;
        var goal = Goal.Create(userId, "Goal", 10, "reps").Value;
        var foreignTag = Tag.Create(Guid.NewGuid(), "Foreign", "#fff").Value;
        var foreignGoal = Goal.Create(Guid.NewGuid(), "Foreign", 10, "reps").Value;
        habit.LoadScheduleRelationsForRead([firstTag], []);

        habit.LoadScheduleRelationsForRead([nextTag], [goal]);
        habit.Tags.Should().ContainSingle().Which.Id.Should().Be(nextTag.Id);
        habit.Goals.Should().ContainSingle().Which.Id.Should().Be(goal.Id);
        var loadForeignTag = () => habit.LoadScheduleRelationsForRead([foreignTag], [goal]);
        var loadForeignGoal = () => habit.LoadScheduleRelationsForRead([nextTag], [foreignGoal]);
        loadForeignTag.Should().Throw<ArgumentException>();
        loadForeignGoal.Should().Throw<ArgumentException>();
        habit.Tags.Should().ContainSingle().Which.Id.Should().Be(nextTag.Id);
        habit.Goals.Should().ContainSingle().Which.Id.Should().Be(goal.Id);
    }

    private static Habit CreateHabit(Guid userId) =>
        Habit.Create(new HabitCreateParams(
            userId, "Habit", FrequencyUnit.Day, 1, DueDate)).Value;
}
