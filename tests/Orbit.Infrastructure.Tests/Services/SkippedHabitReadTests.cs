using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public class SkippedHabitReadTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryHabits_PersistedSkipIsVisibleAndNeverRemainingOrCompleted(bool flexible)
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var db = factory.Context;
        var today = new DateOnly(2026, 10, 27);
        var user = User.Create("Reader", "skip@example.test").Value;
        var habit = Habit.Create(new HabitCreateParams(flexible ? HabitSchedule.Flexible : HabitSchedule.Recurring,
            user.Id, "Caminhada", FrequencyUnit.Day, 1, today)).Value;
        var future = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring,
            user.Id, "Future", FrequencyUnit.Day, 1, today.AddDays(1))).Value;
        var receipt = HabitSkipUndo.Create(Guid.NewGuid(), habit).Value;
        Guid? logId = null;
        if (flexible) logId = habit.SkipFlexible(today).Value.Id;
        else habit.AdvanceDueDate(today, 1).IsSuccess.Should().BeTrue();
        db.Users.Add(user);
        db.Habits.AddRange(habit, future);
        await db.SaveChangesAsync();
        receipt.Seal(habit, logId, "saved-state");
        db.HabitSkipUndos.Add(receipt);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(today);
        var tool = new QueryHabitsTool(new GenericRepository<Habit>(db), new GenericRepository<User>(db), dates,
            new HabitScheduleLogReader(db));

        var result = await tool.ExecuteAsync(JsonDocument.Parse("""{"date":"today"}""").RootElement, user.Id, default);

        result.Success.Should().BeTrue();
        var card = result.Payload.Should().BeOfType<HabitListCard>().Subject;
        card.Items.Should().ContainSingle().Which.Title.Should().Be("Caminhada");
        card.Items.Single().Status.Should().Be("none");
        card.SkippedIds.Should().Contain(habit.Id.ToString());
        result.EntityName.Should().Contain("SKIPPED ON DATE").And.NotContain("DONE TODAY");
        foreach (var completed in new[] { "false", "true" })
        {
            var filtered = await tool.ExecuteAsync(JsonDocument.Parse("{\"date\":\"today\",\"is_completed\":" + completed + "}").RootElement,
                user.Id, default);
            filtered.Payload.Should().BeOfType<HabitListCard>().Which.Items.Should().BeEmpty();
        }

        receipt = await db.HabitSkipUndos.FindAsync(receipt.Id) ?? throw new InvalidOperationException("Skip receipt is required.");
        var savedHabit = await db.Habits.Include(item => item.Logs).SingleAsync(item => item.Id == habit.Id);
        receipt.Undo(savedHabit, "saved-state").IsSuccess.Should().BeTrue();
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var restored = await tool.ExecuteAsync(JsonDocument.Parse("""{"date":"today"}""").RootElement, user.Id, default);
        restored.Payload.Should().BeOfType<HabitListCard>().Which.Items.Should().ContainSingle().Which.Status.Should().Be("today");
    }
}
