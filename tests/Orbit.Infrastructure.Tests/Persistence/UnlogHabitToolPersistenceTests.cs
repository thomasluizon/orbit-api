using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public sealed class UnlogHabitToolPersistenceTests
{
    [Fact]
    public async Task UnlogThenLogAgain_PartialIndexRetainsDeletedRowAndAcceptsNewCompletion()
    {
        var counter = new CountingDbCommandInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(counter);
        await factory.Context.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS \"IX_HabitLogs_HabitId_Date_Completed\"");
        await factory.Context.Database.ExecuteSqlRawAsync("""
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_HabitLogs_HabitId_Date_Completed"
            ON "HabitLogs" ("HabitId", "Date", "CompletionOrdinal")
            WHERE "Value" > 0 AND NOT "IsDeleted"
            """);
        var today = new DateOnly(2026, 8, 6);
        var user = User.Create("Alex", "undo@example.test").Value;
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, user.Id, "Beber água", FrequencyUnit.Day, 1, today)).Value;
        habit.Log(today).IsSuccess.Should().BeTrue();
        var goals = Enumerable.Range(1, 3)
            .Select(index => Goal.Create(user.Id, $"Goal {index}", 10m, "completions").Value).ToList();
        foreach (var goal in goals)
            habit.AddGoal(goal);
        var originalLogId = habit.Logs.Single().Id;
        await using (var seed = factory.CreateContext())
        {
            seed.Users.Add(user);
            seed.Habits.Add(habit);
            await seed.SaveChangesAsync();
        }

        await using (var context = factory.CreateContext())
        {
            var dates = Substitute.For<IUserDateService>();
            dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(today);
            var work = new UnitOfWork(context, new DatabaseConnectionSettings());
            var goalCompletion = Substitute.For<IGoalCompletionService>();
            var tool = new UnlogHabitTool(new GenericRepository<Habit>(context), dates, work,
                Substitute.For<IPayGateService>(), Substitute.For<IUserStreakService>(),
                goalCompletion);

            counter.Reset();
            var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { habit_id = habit.Id }), user.Id, default);

            result.Success.Should().BeTrue();
            counter.Commands.Should().NotContain(command =>
                command.Sql.Contains("\"HabitLogs\"", StringComparison.Ordinal)
                && command.Sql.Contains("\"Goals\"", StringComparison.Ordinal),
                "log and goal collections must load without a Cartesian product");
            await goalCompletion.Received(1).SyncDerivedGoalsAsync(user.Id,
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == goals.Count && goals.All(goal => ids.Contains(goal.Id))),
                today, cancellationToken: Arg.Any<CancellationToken>());
        }

        await using (var context = factory.CreateContext())
        {
            var repository = new GenericRepository<Habit>(context);
            var reloaded = await repository.FindOneTrackedAsync(h => h.Id == habit.Id, query => query.Include(h => h.Logs));
            reloaded.Should().NotBeNull();
            reloaded!.Logs.Should().BeEmpty();
            var log = reloaded.Log(today);
            log.IsSuccess.Should().BeTrue();
            context.HabitLogs.Add(log.Value);
            await context.SaveChangesAsync();
        }
        await using var verify = factory.CreateContext();
        var logs = await verify.HabitLogs.IgnoreQueryFilters().Where(log => log.HabitId == habit.Id).ToListAsync();
        logs.Should().HaveCount(2);
        logs.Single(log => log.Id == originalLogId).IsDeleted.Should().BeTrue();
        logs.Count(log => !log.IsDeleted).Should().Be(1);
    }
}
