using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public sealed class HabitSkipUndoWriterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConditionalUndo_WritesOnlyIfHabitStillMatches(bool concurrentEdit)
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var user = User.Create("Alex", "alex@test.com").Value;
        var today = new DateOnly(2026, 4, 3);
        var habit = Habit.Create(new HabitCreateParams(user.Id, "Read", FrequencyUnit.Day, 1, today)).Value;
        var receipt = HabitSkipUndo.Create(Guid.NewGuid(), habit).Value;
        habit.AdvanceDueDate(today);
        factory.Context.Users.Add(user);
        factory.Context.Habits.Add(habit);
        await factory.Context.SaveChangesAsync();
        receipt.Seal(habit, null, "logs");

        await using var undoContext = factory.CreateContext();
        var undoHabit = await undoContext.Habits.SingleAsync();
        receipt.Undo(undoHabit, "logs").IsSuccess.Should().BeTrue();
        if (concurrentEdit)
        {
            await using var newerContext = factory.CreateContext();
            var newerHabit = await newerContext.Habits.SingleAsync();
            newerHabit.PostponeTo(today.AddDays(7));
            await newerContext.SaveChangesAsync();
        }

        var write = () => new HabitSkipUndoWriter(undoContext).SaveAsync(
            undoHabit, receipt.ExpectedUpdatedAtUtc, CancellationToken.None);
        if (concurrentEdit)
            await write.Should().ThrowAsync<DbUpdateConcurrencyException>();
        else
        {
            await write.Should().NotThrowAsync();
            await undoContext.SaveChangesAsync();
        }

        await using var verify = factory.CreateContext();
        var restored = await verify.Habits.SingleAsync();
        restored.DueDate.Should().Be(concurrentEdit ? today.AddDays(7) : today);
        restored.ReminderProbeVersion.Should().Be(1);
    }
}
