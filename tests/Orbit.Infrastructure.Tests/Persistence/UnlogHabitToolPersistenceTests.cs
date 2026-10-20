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
        using var factory = new SqliteOrbitDbContextFactory();
        await factory.Context.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS \"IX_HabitLogs_HabitId_Date_Completed\"");
        await factory.Context.Database.ExecuteSqlRawAsync("""
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_HabitLogs_HabitId_Date_Completed"
            ON "HabitLogs" ("HabitId", "Date", "CompletionOrdinal")
            WHERE "Value" > 0 AND NOT "IsDeleted"
            """);
        var today = new DateOnly(2026, 8, 6);
        var user = User.Create("Alex", "undo@example.test").Value;
        var habit = Habit.Create(new HabitCreateParams(user.Id, "Beber água", FrequencyUnit.Day, 1, today)).Value;
        habit.Log(today).IsSuccess.Should().BeTrue();
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
            var tool = new UnlogHabitTool(new GenericRepository<Habit>(context), dates, work,
                Substitute.For<IPayGateService>(), Substitute.For<IUserStreakService>(),
                Substitute.For<IGoalCompletionService>());

            var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { habit_id = habit.Id }), user.Id, default);

            result.Success.Should().BeTrue();
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
