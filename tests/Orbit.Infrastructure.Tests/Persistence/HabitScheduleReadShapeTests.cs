using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;
using Orbit.Infrastructure.Persistence;
using Xunit.Abstractions;

namespace Orbit.Infrastructure.Tests.Persistence;

public class HabitScheduleReadShapeTests(ITestOutputHelper output)
{
    private static readonly DateOnly Today = new(2026, 6, 30);

    [Fact]
    public void ProviderNames_MatchReaderBranches()
    {
        using var sqlite = new SqliteOrbitDbContextFactory();
        sqlite.Context.Database.ProviderName.Should().Be("Microsoft.EntityFrameworkCore.Sqlite");
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .Options;
        using var postgres = new OrbitDbContext(options);
        postgres.Database.ProviderName.Should().Be("Npgsql.EntityFrameworkCore.PostgreSQL");
    }

    [Fact]
    public async Task TodayCandidates_ReadOnlyFilterColumns_AndPageRetainsDisplayFields()
    {
        var counter = new CountingDbCommandInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(counter);
        var userId = Guid.NewGuid();
        var user = User.Create("Schedule User", $"schedule-fields-{userId:N}@example.com").Value;
        typeof(User).GetProperty("Id")!.SetValue(user, userId);
        factory.Context.Users.Add(user);
        var habit = Habit.Create(new HabitCreateParams(
            userId, "Morning plan", FrequencyUnit.Day, 1, Today,
            Description: "Display details", ChecklistItems: [new ChecklistItem("First step", false)])).Value;
        habit.SetPosition(0);
        var offPage = Habit.Create(new HabitCreateParams(
            userId, "Evening plan", FrequencyUnit.Day, 1, Today,
            Description: "Off page details")).Value;
        offPage.SetPosition(1);
        factory.Context.Habits.AddRange(habit, offPage);
        await factory.Context.SaveChangesAsync();
        factory.Context.ChangeTracker.Clear();

        var dateService = Substitute.For<IUserDateService>();
        dateService.GetUserTodayAsync(userId, Arg.Any<CancellationToken>()).Returns(Today);
        var handler = new GetHabitScheduleQueryHandler(
            new GenericRepository<Habit>(factory.Context),
            new HabitScheduleLogReader(factory.Context),
            new HabitSchedulePageLoader(factory.Context),
            dateService,
            Substitute.For<IUnitOfWork>());

        counter.Reset();
        var result = await handler.Handle(
            new GetHabitScheduleQuery(userId, Today, Today, PageSize: 1), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle();
        result.Value.Items[0].Description.Should().Be("Display details");
        result.Value.Items[0].ChecklistItems.Should().ContainSingle(item => item.Text == "First step");
        var candidateSql = counter.Commands.First(command =>
            command.Sql.Contains("FROM \"Habits\" AS \"h\"")
            && command.Sql.Contains("NOT (\"h\".\"IsGeneral\")"));
        candidateSql.Sql.Should().NotContain("\"Description\"");
        candidateSql.Sql.Should().NotContain("\"ChecklistItems\"");
        candidateSql.Rows.Should().Be(2);
        var pageSql = counter.Commands.Single(command =>
            command.Sql.Contains("FROM \"Habits\" AS \"h\"")
            && command.Sql.Contains("\"ChecklistItems\"")
            && command.Parameters.Any(parameter => parameter.Contains(habit.Id.ToString())));
        pageSql.Parameters.Should().NotContain(parameter => parameter.Contains(offPage.Id.ToString()));
    }

    [Fact]
    public async Task SearchCandidates_UseDescriptionsAndTags()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var userId = Guid.NewGuid();
        var user = User.Create("Schedule User", $"schedule-search-{userId:N}@example.com").Value;
        typeof(User).GetProperty("Id")!.SetValue(user, userId);
        factory.Context.Users.Add(user);
        var habit = Habit.Create(new HabitCreateParams(
            userId, "Morning plan", FrequencyUnit.Day, 1, Today,
            Description: "Quiet reflection")).Value;
        var tag = Tag.Create(userId, "Mindful", "#fff").Value;
        habit.AddTag(tag);
        factory.Context.Habits.Add(habit);
        factory.Context.Tags.Add(tag);
        await factory.Context.SaveChangesAsync();
        factory.Context.ChangeTracker.Clear();

        var dateService = Substitute.For<IUserDateService>();
        dateService.GetUserTodayAsync(userId, Arg.Any<CancellationToken>()).Returns(Today);
        var handler = new GetHabitScheduleQueryHandler(
            new GenericRepository<Habit>(factory.Context),
            new HabitScheduleLogReader(factory.Context),
            new HabitSchedulePageLoader(factory.Context),
            dateService,
            Substitute.For<IUnitOfWork>());

        var descriptionResult = await handler.Handle(
            new GetHabitScheduleQuery(userId, Today, Today, Search: "reflection"), CancellationToken.None);
        var tagResult = await handler.Handle(
            new GetHabitScheduleQuery(userId, Today, Today, Search: "mindful"), CancellationToken.None);

        descriptionResult.Value.Items.Should().ContainSingle(item => item.Id == habit.Id);
        tagResult.Value.Items.Should().ContainSingle(item => item.Id == habit.Id);
    }

    [Fact]
    public async Task OldDueDateResolution_KeepsResolvedHabitOutOfOverdueResults()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var userId = Guid.NewGuid();
        var user = User.Create("Schedule User", $"schedule-old-{userId:N}@example.com").Value;
        typeof(User).GetProperty("Id")!.SetValue(user, userId);
        factory.Context.Users.Add(user);
        var oldDueDate = Today.AddDays(-500);
        var resolved = Habit.Create(new HabitCreateParams(
            userId, "Resolved", FrequencyUnit.Year, 2, DueDate: oldDueDate)).Value;
        var unresolved = Habit.Create(new HabitCreateParams(
            userId, "Unresolved", FrequencyUnit.Year, 2, DueDate: oldDueDate)).Value;
        resolved.Log(oldDueDate, advanceDueDate: false).IsSuccess.Should().BeTrue();
        factory.Context.Habits.AddRange(resolved, unresolved);
        await factory.Context.SaveChangesAsync();
        factory.Context.ChangeTracker.Clear();

        var dateService = Substitute.For<IUserDateService>();
        dateService.GetUserTodayAsync(userId, Arg.Any<CancellationToken>()).Returns(Today);
        var handler = new GetHabitScheduleQueryHandler(
            new GenericRepository<Habit>(factory.Context),
            new HabitScheduleLogReader(factory.Context),
            new HabitSchedulePageLoader(factory.Context),
            dateService,
            Substitute.For<IUnitOfWork>());

        var result = await handler.Handle(
            new GetHabitScheduleQuery(userId, Today, Today, IncludeOverdue: true),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle(item => item.Id == unresolved.Id && item.IsOverdue);
    }

    [Fact]
    public async Task TodayPage_MaterializesOnlyPageLogsWithinTheResponseWindow()
    {
        var counter = new CountingDbCommandInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(counter);
        var userId = Guid.NewGuid();
        var user = User.Create("Schedule User", $"schedule-{userId:N}@example.com").Value;
        typeof(User).GetProperty("Id")!.SetValue(user, userId);
        factory.Context.Users.Add(user);

        var habits = new List<Habit>();
        for (var index = 0; index < 25; index++)
        {
            var habit = Habit.Create(new HabitCreateParams(
                userId, $"Habit {index}", FrequencyUnit.Day, 1, DueDate: Today)).Value;
            habit.SetPosition(index);
            for (var daysAgo = 30; daysAgo >= 0; daysAgo--)
                habit.Log(Today.AddDays(-daysAgo), advanceDueDate: false);
            habits.Add(habit);
            factory.Context.Habits.Add(habit);
        }

        var tag = Tag.Create(userId, "Page tag", "#fff").Value;
        factory.Context.Tags.Add(tag);
        habits[0].AddTag(tag);
        var goal = Goal.Create(userId, "Page goal", 10m, "reps").Value;
        factory.Context.Goals.Add(goal);
        habits[0].AddGoal(goal);
        await factory.Context.SaveChangesAsync();
        var expectedPageIds = habits.Take(2).Select(habit => habit.Id).ToHashSet();
        factory.Context.ChangeTracker.Clear();

        var repository = new GenericRepository<Habit>(factory.Context);
        counter.Reset();
        await repository.FindAsync(
            habit => habit.UserId == userId && !habit.IsGeneral,
            query => query.Include(habit => habit.Logs.Where(log => log.Date >= Today.AddDays(-366) && log.Date <= Today))
                .AsSplitQuery(),
            CancellationToken.None);
        await repository.FindAsync(
            habit => expectedPageIds.Contains(habit.Id),
            query => query.Include(habit => habit.Logs.Where(log => log.Date >= Today.AddDays(-366) && log.Date <= Today))
                .Include(habit => habit.Tags)
                .Include(habit => habit.Goals)
                .AsSplitQuery(),
            CancellationToken.None);
        var beforeRows = counter.Commands.Sum(command => command.Rows);
        factory.Context.ChangeTracker.Clear();
        var logReader = new HabitScheduleLogReader(factory.Context);
        var summaries = await logReader.ReadDaysAsync(
            habits.Select(habit => habit.Id).ToArray(), Today.AddDays(-366), Today);
        summaries.Should().HaveCount(25 * 31);
        factory.Context.Database.ProviderName.Should().Be("Microsoft.EntityFrameworkCore.Sqlite");

        var dateService = Substitute.For<IUserDateService>();
        dateService.GetUserTodayAsync(userId, Arg.Any<CancellationToken>()).Returns(Today);
        var handler = new GetHabitScheduleQueryHandler(
            new GenericRepository<Habit>(factory.Context),
            logReader,
            new HabitSchedulePageLoader(factory.Context),
            dateService,
            Substitute.For<IUnitOfWork>());

        counter.Reset();
        var result = await handler.Handle(
            new GetHabitScheduleQuery(userId, Today, Today, IncludeOverdue: true, PageSize: 2),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var afterRows = counter.Commands.Sum(command => command.Rows);
        output.WriteLine($"Today rows per call on the same seed: before {beforeRows}, after {afterRows}.");
        afterRows.Should().BeLessThan(beforeRows / 4);
        result.Value.Items.Select(item => item.Id).Should().BeEquivalentTo(expectedPageIds);
        result.Value.Items[0].Tags.Should().ContainSingle(item => item.Name == "Page tag");
        result.Value.Items[0].LinkedGoals.Should().ContainSingle(item => item.Title == "Page goal");
        result.Value.Items.Should().OnlyContain(item => item.IsLoggedInRange);

        var pageLogQuery = counter.Commands.Single(command => command.Sql.Contains("HabitSchedulePageLogs"));
        pageLogQuery.Sql.Should().NotContain("Note");
        pageLogQuery.Sql.Should().NotContain("UpdatedAtUtc");
        foreach (var id in expectedPageIds)
            pageLogQuery.Parameters.Should().Contain(parameter => parameter.Contains(id.ToString()));
        pageLogQuery.Parameters.Should().NotContain(parameter => parameter.Contains(habits[2].Id.ToString()));
        pageLogQuery.Rows.Should().Be(16);
    }
}
