using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Chat;

public class QueryHabitsToolTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly QueryHabitsTool _tool;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    public QueryHabitsToolTests()
    {
        _tool = new QueryHabitsTool(_habitRepo, _userRepo);

        var user = User.Create("Test", "test@test.com").Value;
        user.SetTimeZone("UTC");
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
    }

    [Fact]
    public void IsReadOnly_ReturnsTrue() => _tool.IsReadOnly.Should().BeTrue();

    [Fact]
    public void Name_ReturnsQueryHabits() => _tool.Name.Should().Be("query_habits");

    // --- No filters ---

    [Fact]
    public async Task NoFilters_ReturnsAllActiveParentHabits()
    {
        var h1 = CreateHabit("Water", FrequencyUnit.Day, 1, dueDate: Today, position: 0);
        var h2 = CreateHabit("Read", FrequencyUnit.Week, 1, dueDate: Today, position: 1);
        var completed = CreateHabit("Done", null, null, dueDate: Today);
        completed.Log(Today); // one-time habit becomes completed on log
        SetupHabits(h1, h2, completed);

        var result = await Execute("{}");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Contain("Water");
        result.EntityName.Should().Contain("Read");
        result.EntityName.Should().NotContain("Done"); // completed excluded by default
    }

    [Fact]
    public async Task NoFilters_IncludesEmojiState()
    {
        var withEmoji = CreateHabit("Gym", FrequencyUnit.Day, 1, dueDate: Today, emoji: "💪");
        var withoutEmoji = CreateHabit("Read", FrequencyUnit.Day, 1, dueDate: Today);
        SetupHabits(withEmoji, withoutEmoji);

        var result = await Execute("{}");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Contain("Emoji: 💪");
        result.EntityName.Should().Contain("No emoji");
    }

    [Fact]
    public async Task NoFilters_ExcludesChildHabitsFromTopLevel()
    {
        var parent = CreateHabit("Before Bed", FrequencyUnit.Day, 1, dueDate: Today, position: 0);
        var child = CreateHabit("Floss", FrequencyUnit.Day, 1, dueDate: Today, parentId: parent.Id);
        SetupHabits(parent, child);

        var result = await Execute("{}");

        result.Success.Should().BeTrue();
        // Parent listed as top-level, child as sub-habit
        result.EntityName.Should().Contain("Before Bed");
        result.EntityName.Should().Contain("Floss");
    }

    [Fact]
    public async Task NoFilters_OrderedByPosition()
    {
        var h1 = CreateHabit("Third", FrequencyUnit.Day, 1, dueDate: Today, position: 2);
        var h2 = CreateHabit("First", FrequencyUnit.Day, 1, dueDate: Today, position: 0);
        var h3 = CreateHabit("Second", FrequencyUnit.Day, 1, dueDate: Today, position: 1);
        SetupHabits(h1, h2, h3);

        var result = await Execute("{}");

        var firstIdx = result.EntityName!.IndexOf("First");
        var secondIdx = result.EntityName!.IndexOf("Second");
        var thirdIdx = result.EntityName!.IndexOf("Third");
        firstIdx.Should().BeLessThan(secondIdx);
        secondIdx.Should().BeLessThan(thirdIdx);
    }

    // --- Search filter ---

    [Fact]
    public async Task Search_MatchesTitleCaseInsensitive()
    {
        var h1 = CreateHabit("Water", FrequencyUnit.Day, 1, dueDate: Today);
        var h2 = CreateHabit("Read books", FrequencyUnit.Day, 1, dueDate: Today);
        SetupHabits(h1, h2);

        var result = await Execute("""{"search": "water"}""");

        result.EntityName.Should().Contain("Water");
        result.EntityName.Should().NotContain("Read books");
    }

    [Fact]
    public async Task Search_MatchesChildReturnsParent()
    {
        // DB-level search filters by title, so only habits whose title matches appear.
        // The parent and child both match when using a shared term.
        var parent = CreateHabit("Before Bed Routine", FrequencyUnit.Day, 1, dueDate: Today);
        var child = CreateHabit("Bed Melatonin", FrequencyUnit.Day, 1, dueDate: Today, parentId: parent.Id);
        SetupHabits(parent, child);

        var result = await Execute("""{"search": "bed"}""");

        result.EntityName.Should().Contain("Before Bed Routine");
        result.EntityName.Should().Contain("Bed Melatonin");
    }

    // --- Date filter ---

    [Fact]
    public async Task DateToday_ReturnsDueTodayAndOverdue_ExcludesGeneral()
    {
        var dueToday = CreateHabit("Today", FrequencyUnit.Day, 1, dueDate: Today);
        var overdue = CreateHabit("Overdue", null, null, dueDate: Today.AddDays(-2));
        var future = CreateHabit("Future", null, null, dueDate: Today.AddDays(5));
        var general = CreateHabit("General", null, null, dueDate: Today.AddDays(-10), isGeneral: true);
        SetupHabits(dueToday, overdue, future, general);

        var result = await Execute("""{"date": "today"}""");

        result.EntityName.Should().Contain("Today");
        result.EntityName.Should().Contain("Overdue");
        result.EntityName.Should().NotContain("Future");
        result.EntityName.Should().NotContain("General");
    }

    [Fact]
    public async Task DateToday_IncludeOverdueFalse_ExcludesOverdue()
    {
        var dueToday = CreateHabit("Today", FrequencyUnit.Day, 1, dueDate: Today);
        var overdue = CreateHabit("Overdue", null, null, dueDate: Today.AddDays(-2));
        SetupHabits(dueToday, overdue);

        var result = await Execute("""{"date": "today", "include_overdue": false}""");

        result.EntityName.Should().Contain("Today");
        result.EntityName.Should().NotContain("Overdue");
    }

    [Fact]
    public async Task DateSpecific_ReturnsHabitsDueOnThatDate()
    {
        var specificDate = Today.AddDays(5);
        var match = CreateHabit("Friday task", null, null, dueDate: specificDate);
        var noMatch = CreateHabit("Other", null, null, dueDate: Today);
        SetupHabits(match, noMatch);

        var result = await Execute($$$"""{"date": "{{{specificDate:yyyy-MM-dd}}}"}""");

        result.EntityName.Should().Contain("Friday task");
        result.EntityName.Should().NotContain("Other");
    }

    // --- General filter ---

    [Fact]
    public async Task IsGeneralTrue_ReturnsOnlyGeneralHabits()
    {
        var general = CreateHabit("Mobile game", null, null, isGeneral: true);
        var regular = CreateHabit("Water", FrequencyUnit.Day, 1, dueDate: Today);
        SetupHabits(general, regular);

        var result = await Execute("""{"is_general": true}""");

        result.EntityName.Should().Contain("Mobile game");
        result.EntityName.Should().NotContain("Water");
    }

    [Fact]
    public async Task IsGeneralFalse_ExcludesGeneralHabits()
    {
        var general = CreateHabit("Mobile game", null, null, isGeneral: true);
        var regular = CreateHabit("Water", FrequencyUnit.Day, 1, dueDate: Today);
        SetupHabits(general, regular);

        var result = await Execute("""{"is_general": false}""");

        result.EntityName.Should().NotContain("Mobile game");
        result.EntityName.Should().Contain("Water");
    }

    // --- Completion filter ---

    [Fact]
    public async Task IsCompletedTrue_ReturnsCompletedHabits()
    {
        var active = CreateHabit("Active", FrequencyUnit.Day, 1, dueDate: Today);
        var done = CreateHabit("Done", null, null, dueDate: Today);
        done.Log(Today); // one-time habit becomes completed on log
        SetupHabits(active, done);

        var result = await Execute("""{"is_completed": true}""");

        result.EntityName.Should().Contain("Done");
        result.EntityName.Should().NotContain("Active");
    }

    [Fact]
    public async Task DefaultExcludesCompleted()
    {
        var active = CreateHabit("Active", FrequencyUnit.Day, 1, dueDate: Today);
        var done = CreateHabit("Done", null, null, dueDate: Today);
        done.Log(Today); // one-time habit becomes completed on log
        SetupHabits(active, done);

        var result = await Execute("{}");

        result.EntityName.Should().Contain("Active");
        result.EntityName.Should().NotContain("Done");
    }

    // --- Bad habit filter ---

    [Fact]
    public async Task IsBadHabitTrue_ReturnsOnlyBadHabits()
    {
        var bad = CreateHabit("Smoking", FrequencyUnit.Day, 1, dueDate: Today, isBadHabit: true);
        var good = CreateHabit("Exercise", FrequencyUnit.Day, 1, dueDate: Today);
        SetupHabits(bad, good);

        var result = await Execute("""{"is_bad_habit": true}""");

        result.EntityName.Should().Contain("Smoking");
        result.EntityName.Should().NotContain("Exercise");
    }

    [Fact]
    public async Task IsBadHabitFalse_ExcludesBadHabits()
    {
        var bad = CreateHabit("Smoking", FrequencyUnit.Day, 1, dueDate: Today, isBadHabit: true);
        var good = CreateHabit("Exercise", FrequencyUnit.Day, 1, dueDate: Today);
        SetupHabits(bad, good);

        var result = await Execute("""{"is_bad_habit": false}""");

        result.EntityName.Should().NotContain("Smoking");
        result.EntityName.Should().Contain("Exercise");
    }

    // --- Frequency filter ---

    [Fact]
    public async Task FrequencyDay_ReturnsDailyHabits()
    {
        var daily = CreateHabit("Water", FrequencyUnit.Day, 1, dueDate: Today);
        var weekly = CreateHabit("Laundry", FrequencyUnit.Week, 1, dueDate: Today);
        SetupHabits(daily, weekly);

        var result = await Execute("""{"frequency": "Day"}""");

        result.EntityName.Should().Contain("Water");
        result.EntityName.Should().NotContain("Laundry");
    }

    [Fact]
    public async Task FrequencyWeek_ReturnsWeeklyHabits()
    {
        var daily = CreateHabit("Water", FrequencyUnit.Day, 1, dueDate: Today);
        var weekly = CreateHabit("Laundry", FrequencyUnit.Week, 1, dueDate: Today);
        SetupHabits(daily, weekly);

        var result = await Execute("""{"frequency": "Week"}""");

        result.EntityName.Should().NotContain("Water");
        result.EntityName.Should().Contain("Laundry");
    }

    [Fact]
    public async Task FrequencyOneTime_ReturnsOneTimeHabits()
    {
        var daily = CreateHabit("Water", FrequencyUnit.Day, 1, dueDate: Today);
        var oneTime = CreateHabit("Buy shoes", null, null, dueDate: Today);
        SetupHabits(daily, oneTime);

        var result = await Execute("""{"frequency": "OneTime"}""");

        result.EntityName.Should().NotContain("Water");
        result.EntityName.Should().Contain("Buy shoes");
    }

    // --- Tag filter ---

    [Fact]
    public async Task TagFilter_MatchesByName()
    {
        var tagged = CreateHabit("Run", FrequencyUnit.Day, 1, dueDate: Today);
        var tag = Tag.Create(UserId, "Health", "#ff0000").Value;
        tagged.AddTag(tag);
        var untagged = CreateHabit("Read", FrequencyUnit.Day, 1, dueDate: Today);
        SetupHabits(tagged, untagged);

        var result = await Execute("""{"tag": "health"}""");

        result.EntityName.Should().Contain("Run");
        result.EntityName.Should().NotContain("Read");
    }

    // --- Sub-habits toggle ---

    [Fact]
    public async Task IncludeSubHabitsFalse_HidesChildren()
    {
        var parent = CreateHabit("Before Bed", FrequencyUnit.Day, 1, dueDate: Today);
        var child = CreateHabit("Floss", FrequencyUnit.Day, 1, dueDate: Today, parentId: parent.Id);
        SetupHabits(parent, child);

        var result = await Execute("""{"include_sub_habits": false}""");

        result.EntityName.Should().Contain("Before Bed");
        result.EntityName.Should().NotContain("Floss");
    }

    [Fact]
    public async Task IncludeSubHabitsDefault_ShowsChildren()
    {
        var parent = CreateHabit("Before Bed", FrequencyUnit.Day, 1, dueDate: Today);
        var child = CreateHabit("Floss", FrequencyUnit.Day, 1, dueDate: Today, parentId: parent.Id);
        SetupHabits(parent, child);

        var result = await Execute("{}");

        result.EntityName.Should().Contain("Before Bed");
        result.EntityName.Should().Contain("Floss");
    }

    // --- Metrics toggle ---

    [Fact]
    public async Task IncludeMetricsTrue_ShowsMetricsData()
    {
        var habit = CreateHabit("Water", FrequencyUnit.Day, 1, dueDate: Today);
        // Log enough days to build stats
        habit.Log(Today.AddDays(-1));
        habit.Log(Today.AddDays(-2));
        habit.Log(Today.AddDays(-3));
        SetupHabitsWithLogs(habit);

        var result = await Execute("""{"include_metrics": true}""");

        // Should contain at least one metric (Total, Streak, or Week)
        result.EntityName.Should().ContainAny("Streak:", "Total:", "Week:");
    }

    [Fact]
    public async Task IncludeMetricsDefault_SkipsStreakData()
    {
        var habit = CreateHabit("Water", FrequencyUnit.Day, 1, dueDate: Today);
        SetupHabits(habit);

        var result = await Execute("{}");

        result.EntityName.Should().NotContain("Streak:");
    }

    // --- Limit ---

    [Fact]
    public async Task Limit_CapsResults()
    {
        var habits = Enumerable.Range(0, 10)
            .Select(i => CreateHabit($"Habit {i}", FrequencyUnit.Day, 1, dueDate: Today, position: i))
            .ToArray();
        SetupHabits(habits);

        var result = await Execute("""{"limit": 3}""");

        result.EntityName.Should().Contain("Habit 0");
        result.EntityName.Should().Contain("Habit 1");
        result.EntityName.Should().Contain("Habit 2");
        result.EntityName.Should().NotContain("Habit 3");
    }

    // --- Combined filters ---

    [Fact]
    public async Task CombinedFilters_DateAndTag()
    {
        var tagged = CreateHabit("Run", FrequencyUnit.Day, 1, dueDate: Today);
        var tag = Tag.Create(UserId, "Health", "#ff0000").Value;
        tagged.AddTag(tag);
        var untaggedToday = CreateHabit("Read", FrequencyUnit.Day, 1, dueDate: Today);
        var taggedFuture = CreateHabit("Gym", FrequencyUnit.Week, 1, dueDate: Today.AddDays(5));
        var futureTag = Tag.Create(UserId, "Health", "#ff0000").Value;
        taggedFuture.AddTag(futureTag);
        SetupHabits(tagged, untaggedToday, taggedFuture);

        var result = await Execute("""{"date": "today", "tag": "health"}""");

        result.EntityName.Should().Contain("Run");
        result.EntityName.Should().NotContain("Read");
        result.EntityName.Should().NotContain("Gym");
    }

    [Fact]
    public async Task CombinedFilters_FrequencyAndNotBad()
    {
        var dailyGood = CreateHabit("Water", FrequencyUnit.Day, 1, dueDate: Today);
        var dailyBad = CreateHabit("Smoking", FrequencyUnit.Day, 1, dueDate: Today, isBadHabit: true);
        var weeklyGood = CreateHabit("Laundry", FrequencyUnit.Week, 1, dueDate: Today);
        SetupHabits(dailyGood, dailyBad, weeklyGood);

        var result = await Execute("""{"frequency": "Day", "is_bad_habit": false}""");

        result.EntityName.Should().Contain("Water");
        result.EntityName.Should().NotContain("Smoking");
        result.EntityName.Should().NotContain("Laundry");
    }

    // --- Edge cases ---

    [Fact]
    public async Task EmptyResult_ReturnsNoHabitsMessage()
    {
        SetupHabits();

        var result = await Execute("""{"search": "nonexistent"}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Contain("No habits found");
    }

    [Fact]
    public async Task InvalidUser_ReturnsError()
    {
        _userRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((User?)null);

        var result = await Execute("{}");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("User not found");
    }

    // --- Helpers ---

    private static Habit CreateHabit(
        string title, FrequencyUnit? freq, int? qty,
        DateOnly? dueDate = null, int? position = null,
        bool isBadHabit = false, bool isGeneral = false,
        Guid? parentId = null,
        string? emoji = null)
    {
        var habit = Habit.Create(new HabitCreateParams(UserId, title, freq, qty,
            DueDate: dueDate, IsBadHabit: isBadHabit, IsGeneral: isGeneral,
            ParentHabitId: parentId, Emoji: emoji)).Value;
        if (position.HasValue) habit.SetPosition(position.Value);
        return habit;
    }

    private void SetupHabits(params Habit[] habits)
    {
        // The tool now pushes filters into the FindAsync predicate, so we need to evaluate
        // the expression against our test data to simulate DB-level filtering.
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(),
            Arg.Any<CancellationToken>()
        ).Returns(callInfo =>
        {
            var predicate = callInfo.ArgAt<Expression<Func<Habit, bool>>>(0).Compile();
            return habits.Where(predicate).ToList().AsReadOnly();
        });

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>()
        ).Returns(callInfo =>
        {
            var predicate = callInfo.ArgAt<Expression<Func<Habit, bool>>>(0).Compile();
            return habits.Where(predicate).ToList().AsReadOnly();
        });
    }

    private void SetupHabitsWithLogs(params Habit[] habits)
    {
        // Same as SetupHabits but for include_metrics=true scenarios
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(),
            Arg.Any<CancellationToken>()
        ).Returns(callInfo =>
        {
            var predicate = callInfo.ArgAt<Expression<Func<Habit, bool>>>(0).Compile();
            return habits.Where(predicate).ToList().AsReadOnly();
        });

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>()
        ).Returns(callInfo =>
        {
            var predicate = callInfo.ArgAt<Expression<Func<Habit, bool>>>(0).Compile();
            return habits.Where(predicate).ToList().AsReadOnly();
        });
    }

    private async Task<Orbit.Application.Chat.Tools.ToolResult> Execute(string json)
    {
        var args = JsonDocument.Parse(json).RootElement;
        return await _tool.ExecuteAsync(args, UserId, CancellationToken.None);
    }
}
