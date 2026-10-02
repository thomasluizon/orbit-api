using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Orbit.Application.Gamification;
using Orbit.Application.Gamification.Services;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;
using Xunit.Abstractions;

namespace Orbit.Infrastructure.Tests.Persistence;

public class AchievementStreakWindowQueryTests(ITestOutputHelper output)
{
    private static readonly DateOnly Today = new(2026, 7, 17);
    private static readonly HashSet<string> TimeOfDayEarned =
        [AchievementDefinitions.EarlyBird, AchievementDefinitions.NightOwl];

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public async Task LoadAsync_OldHistory_ReadsOnlyNeededRowsInBatchedWindows(int shortHabitCount)
    {
        var counter = new CountingDbCommandInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(counter);
        var user = User.Create("Window user", "window@example.com").Value;
        var shortHabits = Enumerable.Range(0, shortHabitCount)
            .Select(_ => CreateHabit(user.Id, 5)).ToList();
        foreach (var habit in shortHabits)
        {
            for (var day = 70; day <= GamificationService.StreakLogWindowDays; day++)
                habit.Log(Today.AddDays(-day), advanceDueDate: false).IsSuccess.Should().BeTrue();
        }
        var longHabit = CreateHabit(user.Id, 130);
        factory.Context.Users.Add(user);
        factory.Context.Habits.AddRange(shortHabits.Append(longHabit));
        await factory.Context.SaveChangesAsync();
        factory.Context.ChangeTracker.Clear();
        var dateService = Substitute.For<IUserDateService>();
        dateService.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(Today);
        var service = new AchievementProgressService(
            new GenericRepository<Habit>(factory.Context),
            new GenericRepository<HabitLog>(factory.Context),
            new GenericRepository<Goal>(factory.Context), dateService);
        var fullWindowRows = factory.Context.HabitLogs.Count(log => log.Date >= Today.AddDays(-1100));

        counter.Reset();
        var metrics = await service.LoadAsync(user, TimeOfDayEarned, CancellationToken.None);

        metrics.CurrentStreak.Should().Be(130);
        metrics.TotalCompletions.Should().Be(fullWindowRows);
        var streakReads = counter.Commands.Where(command =>
            command.Sql.Contains("HabitLogs", StringComparison.Ordinal)
            && command.Sql.Split("FROM", 2)[0].Contains("\"Value\"", StringComparison.Ordinal)).ToList();
        streakReads.Should().HaveCount(3);
        streakReads.Select(command => command.Rows).Should().Equal(5 * shortHabitCount + 65, 64, 1);
        streakReads.Sum(command => command.Rows).Should().Be(5 * shortHabitCount + 130);
        foreach (var read in streakReads.Skip(1))
        {
            read.Parameters.Should().Contain(parameter => parameter.Contains(longHabit.Id.ToString()));
            foreach (var habit in shortHabits)
                read.Parameters.Should().NotContain(parameter => parameter.Contains(habit.Id.ToString()));
        }
        output.WriteLine($"Full window rows: {fullWindowRows}; expanding window rows: {streakReads.Sum(read => read.Rows)}");
        foreach (var read in streakReads)
            output.WriteLine($"Rows: {read.Rows}\n{read.Sql}");
    }

    [Fact]
    public async Task LoadAsync_PostgresTranslation_EmitsFirstAndWidenedQueryShapes()
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=localhost;Database=translation_only;Username=unused;Password=unused")
            .Options;
        using var context = new OrbitDbContext(options);
        var user = User.Create("Translation user", "translation@example.com").Value;
        var habit = CreateHabit(user.Id, 130);
        var habits = Substitute.For<IGenericRepository<Habit>>();
        habits.ProjectAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<HabitScheduleSnapshot>>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<IQueryable<Habit>, IQueryable<HabitScheduleSnapshot>>>(1)(
                new[] { habit }.AsQueryable()).ToList());
        var sql = new List<string>();
        var logs = Substitute.For<IGenericRepository<HabitLog>>();
        logs.ProjectAsync(Arg.Any<Expression<Func<HabitLog, bool>>>(),
                Arg.Any<Func<IQueryable<HabitLog>, IQueryable<HabitMetricLog>>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var predicate = call.ArgAt<Expression<Func<HabitLog, bool>>>(0);
                var projection = call.ArgAt<Func<IQueryable<HabitLog>, IQueryable<HabitMetricLog>>>(1);
                sql.Add(projection(context.HabitLogs.AsNoTracking().Where(predicate)).ToQueryString());
                return projection(habit.Logs.Where(predicate.Compile()).AsQueryable()).ToList();
            });
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(Today);
        var service = new AchievementProgressService(habits, logs, Substitute.For<IGenericRepository<Goal>>(), dates);

        var metrics = await service.LoadAsync(user, TimeOfDayEarned, CancellationToken.None);

        metrics.CurrentStreak.Should().Be(130);
        sql.Should().HaveCount(3);
        foreach (var query in sql)
        {
            query.Should().Contain("SELECT h.\"HabitId\", h.\"Date\", h.\"Value\"")
                .And.Contain("NOT (h.\"IsDeleted\")")
                .And.Contain("h.\"HabitId\" = ANY (")
                .And.Contain("h.\"Date\" >= ");
            output.WriteLine(query);
        }
        sql[0].Should().NotContain("h.\"Date\" < ");
        sql[1].Should().Contain("h.\"Date\" < ");
    }

    private static Habit CreateHabit(Guid userId, int streakDays)
    {
        var start = Today.AddDays(-1200);
        var habit = Habit.Create(new HabitCreateParams(userId, "Daily", FrequencyUnit.Day, 1, start)).Value;
        for (var day = 0; day < streakDays; day++)
            habit.Log(Today.AddDays(-day), advanceDueDate: false).IsSuccess.Should().BeTrue();
        return habit;
    }
}
