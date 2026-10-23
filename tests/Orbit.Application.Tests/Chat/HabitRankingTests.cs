using FluentAssertions;
using System.Linq.Expressions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Habits.Queries;
using Orbit.Application.Habits.Validators;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Application.Tests.Chat;

public class HabitRankingTests
{
    [Fact]
    public async Task Ranking_ReachesEveryHabit_AndOrdersByCountBeforeRate()
    {
        var userId = Guid.NewGuid();
        var from = new DateOnly(2024, 1, 1);
        var to = from.AddDays(9);
        var habits = Enumerable.Range(0, 5).Select(i => Habit.Create(new HabitCreateParams(
            userId, $"Habit {i}", FrequencyUnit.Day, 1, DueDate: i == 0 ? to : from)).Value).ToList();
        for (var i = 0; i < habits.Count; i++)
            for (var day = 0; day <= i; day++)
                habits[i].Log(i == 0 ? to : from.AddDays(day), advanceDueDate: false);

        var result = await Handler(habits).Handle(new GetHabitRankingQuery(userId, from, to.AddDays(1)), default);
        result.IsSuccess.Should().BeTrue();
        var rows = result.Value.Rows;

        rows.Should().HaveCount(5);
        rows.Select(row => row.CompletedCount).Should().Equal(5, 4, 3, 2, 1);
    }
    [Fact]
    public async Task Pages_KeepTheAbsoluteQuery_AndUseStableIdTieBreaking()
    {
        var user = Guid.NewGuid();
        var from = new DateOnly(2024, 1, 1);
        var habits = Enumerable.Range(0, 7).Select(i => Logged(user, from, $"Habit {i}")).ToList();
        var handler = Handler(habits);
        var first = (await handler.Handle(new GetHabitRankingQuery(user, from, from.AddDays(1), PageSize: 2), default)).Value;
        var all = first.Rows.ToList();
        var page = first;
        while (page.NextCursor is { } cursor)
        {
            page = (await handler.Handle(new GetHabitRankingQuery(user, QueryToken: first.QueryToken, Cursor: cursor), default)).Value;
            page.QueryId.Should().Be(first.QueryId);
            page.QueryToken.Should().Be(first.QueryToken);
            page.Revision.Should().Be(first.Revision);
            page.Query.Should().BeEquivalentTo(first.Query);
            page.TotalCount.Should().Be(7);
            all.AddRange(page.Rows);
        }
        page.IsComplete.Should().BeTrue();
        all.Select(r => r.HabitId).Should().Equal(habits.OrderBy(h => h.Id.ToString("D"), StringComparer.Ordinal).Select(h => h.Id));
        all.Select(r => r.Rank).Should().Equal(Enumerable.Range(1, 7));
    }

    [Fact]
    public async Task Counts_IndependentChildren_InactiveHabits_AndPositiveLogRecordsOnly()
    {
        var user = Guid.NewGuid();
        var from = new DateOnly(2024, 1, 1);
        var parent = Logged(user, from, "Parent");
        var child = Habit.Create(new HabitCreateParams(user, "Child", FrequencyUnit.Day, 1,
            DueDate: from, ParentHabitId: parent.Id)).Value;
        child.Log(from, advanceDueDate: false);
        child.Log(from.AddDays(1), advanceDueDate: false);
        var flexible = Habit.Create(new HabitCreateParams(user, "Flexible", FrequencyUnit.Day, 5,
            DueDate: from, IsFlexible: true)).Value;
        flexible.Log(from, advanceDueDate: false);
        flexible.Log(from, advanceDueDate: false);
        flexible.Log(from, advanceDueDate: false).Value.SoftDelete();
        flexible.SkipFlexible(from);
        var ended = Habit.Create(new HabitCreateParams(user, "Ended", FrequencyUnit.Day, 1,
            DueDate: from, EndDate: from)).Value;
        ended.Log(from);
        ended.IsCompleted.Should().BeTrue();
        var completed = Logged(user, from, "Completed");
        var completedSnapshot = HabitScheduleSnapshot.FromHabit(completed) with { IsCompleted = true };
        var completedRead = Habit.FromScheduleSnapshot(completedSnapshot, user);
        completedRead.LoadScheduleCandidateFieldsForRead(completed.Title, null, null, []);
        completedRead.LoadScheduleLogsForRead(completed.Logs);
        var deleted = Logged(user, from, "Deleted");
        deleted.SoftDelete();
        var undone = Logged(user, from, "Undone");
        undone.Unlog(from);
        var bad = Habit.Create(new HabitCreateParams(user, "Slip", FrequencyUnit.Day, 1,
            DueDate: from, IsBadHabit: true)).Value;
        bad.Log(from, advanceDueDate: false);
        var task = Habit.Create(new HabitCreateParams(user, "Task", null, null, DueDate: from)).Value;
        task.Log(from);
        var crossUser = Logged(Guid.NewGuid(), from, "Someone else");
        var noLogs = Habit.Create(new HabitCreateParams(user, "Zero", FrequencyUnit.Day, 1, DueDate: from)).Value;

        var result = await Handler([parent, child, flexible, ended, completedRead, deleted, undone, bad, task, crossUser, noLogs])
            .Handle(new GetHabitRankingQuery(user, from, from.AddDays(2)), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Rows.Should().HaveCount(5);
        result.Value.Rows.Single(r => r.HabitId == parent.Id).CompletedCount.Should().Be(1);
        result.Value.Rows.Single(r => r.HabitId == child.Id).CompletedCount.Should().Be(2);
        result.Value.Rows.Single(r => r.HabitId == flexible.Id).CompletedCount.Should().Be(2);
        result.Value.Rows.Single(r => r.HabitId == ended.Id).CompletedCount.Should().Be(1);
        result.Value.Rows.Single(r => r.HabitId == completed.Id).CompletedCount.Should().Be(1);
        result.Value.Query.Scope.InactiveHabits.Should().Be("include_ended_and_completed");
    }

    [Fact]
    public async Task Boundaries_DuplicateDelivery_AndLongNames_ArePreserved()
    {
        var user = Guid.NewGuid();
        var from = new DateOnly(2024, 1, 1);
        var habit = Logged(user, from.AddDays(-1), new string('a', 200));
        var first = habit.Log(from, advanceDueDate: false).Value;
        habit.Log(from.AddDays(1), advanceDueDate: false);
        habit.Log(from.AddDays(2), advanceDueDate: false);
        habit.LoadScheduleLogsForRead(habit.Logs.Concat([first]).ToList());
        var result = (await Handler([habit]).Handle(new GetHabitRankingQuery(user, from, from.AddDays(2)), default)).Value;
        result.Rows.Should().ContainSingle().Which.CompletedCount.Should().Be(2);
        result.Rows[0].Name.Should().Be(habit.Title);
    }

    [Fact]
    public async Task EmptyRanking_IsSuccessful_WithoutNarrativeOrWrites()
    {
        var user = Guid.NewGuid();
        var from = new DateOnly(2024, 1, 1);
        var result = await Handler([]).Handle(new GetHabitRankingQuery(user, from, from.AddDays(1)), default);
        result.IsSuccess.Should().BeTrue();
        result.Value.Rows.Should().BeEmpty();
        result.Value.TotalCount.Should().Be(0);
        result.Value.IsComplete.Should().BeTrue();
        result.Value.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task Refresh_AfterLogChange_PreservesQuery_AndInvalidatesOldCursor()
    {
        var user = Guid.NewGuid();
        var from = new DateOnly(2024, 1, 1);
        var a = Logged(user, from, "A");
        var b = Logged(user, from, "B");
        var handler = Handler([a, b]);
        var first = (await handler.Handle(new GetHabitRankingQuery(user, from, from.AddDays(2), PageSize: 1), default)).Value;
        b.Log(from.AddDays(1), advanceDueDate: false);
        var stale = await handler.Handle(new GetHabitRankingQuery(user, QueryToken: first.QueryToken, Cursor: first.NextCursor), default);
        stale.ErrorCode.Should().Be("HABIT_RANKING_STALE");
        var refreshed = (await handler.Handle(new GetHabitRankingQuery(user, QueryToken: first.QueryToken, Refresh: true), default)).Value;
        refreshed.QueryId.Should().Be(first.QueryId);
        refreshed.Query.Should().BeEquivalentTo(first.Query);
        refreshed.Revision.Should().NotBe(first.Revision);
        refreshed.Rows[0].HabitId.Should().Be(b.Id);
        refreshed.Rows[0].CompletedCount.Should().Be(2);
    }

    [Fact]
    public async Task Tokens_RejectCrossUserCrossQueryTamperingAndChangedFilters()
    {
        var user = Guid.NewGuid();
        var from = new DateOnly(2024, 1, 1);
        var handler = Handler([Logged(user, from), Logged(user, from)]);
        var first = (await handler.Handle(new GetHabitRankingQuery(user, from, from.AddDays(1), PageSize: 1), default)).Value;
        var second = (await handler.Handle(new GetHabitRankingQuery(user, from, from.AddDays(2), PageSize: 1), default)).Value;
        var invalid = new[]
        {
            new GetHabitRankingQuery(Guid.NewGuid(), QueryToken: first.QueryToken, Refresh: true),
            new GetHabitRankingQuery(user, QueryToken: second.QueryToken, Cursor: first.NextCursor),
            new GetHabitRankingQuery(user, QueryToken: first.QueryToken, Cursor: "x" + first.NextCursor),
            new GetHabitRankingQuery(user, QueryToken: "x" + first.QueryToken, Refresh: true),
            new GetHabitRankingQuery(user, QueryToken: first.QueryToken, Refresh: true, PageSize: 10),
            new GetHabitRankingQuery(user, QueryToken: first.QueryToken, Refresh: true, Metric: "distinct_logged_days")
        };
        foreach (var request in invalid)
            (await handler.Handle(request, default)).IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task RemovedTarget_IsNotResurrectedOnRefresh_AndInitialOwnershipIsRequired()
    {
        var user = Guid.NewGuid();
        var from = new DateOnly(2024, 1, 1);
        var habit = Logged(user, from);
        var handler = Handler([habit]);
        var first = (await handler.Handle(new GetHabitRankingQuery(user, from, from.AddDays(1), HabitIds: [habit.Id]), default)).Value;
        (await handler.Handle(new GetHabitRankingQuery(Guid.NewGuid(), from, from.AddDays(1), HabitIds: [habit.Id]), default)).IsFailure.Should().BeTrue();
        habit.SoftDelete();
        var refreshed = (await handler.Handle(new GetHabitRankingQuery(user, QueryToken: first.QueryToken, Refresh: true), default)).Value;
        refreshed.QueryId.Should().Be(first.QueryId);
        refreshed.Rows.Should().BeEmpty();
        refreshed.Query.HabitIds.Should().Equal(habit.Id);
    }

    [Theory]
    [InlineData("rolling_months", "2024-08-31", "2024-02-29", "2024-09-01")]
    [InlineData("rolling_months", "2025-08-31", "2025-02-28", "2025-09-01")]
    [InlineData("rolling_months", "2024-02-29", "2023-08-29", "2024-03-01")]
    [InlineData("complete_months", "2024-08-31", "2024-02-01", "2024-08-01")]
    public void CalendarMonths_ClampMonthEnd_AndDistinguishCompleteMonths(string window, string today, string from, string to)
    {
        var range = HabitRankingPeriodRange.Resolve(window, 6, DateOnly.Parse(today));
        range.From.Should().Be(DateOnly.Parse(from));
        range.ToExclusive.Should().Be(DateOnly.Parse(to));
    }

    [Theory]
    [InlineData("America/Sao_Paulo", "2024-08-31", "2024-02-29", "2024-09-01")]
    [InlineData("Asia/Tokyo", "2024-09-01", "2024-03-01", "2024-09-02")]
    public async Task RelativeResolution_UsesTheUserDateOnce_ThenFreezesDatesForRefresh(string zone, string localDate, string from, string to)
    {
        var user = Guid.NewGuid();
        var dates = Substitute.For<IUserDateService>();
        var instant = DateTimeOffset.Parse("2024-09-01T01:00:00Z");
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.FindSystemTimeZoneById(zone)).DateTime)
            .Should().Be(DateOnly.Parse(localDate));
        dates.GetUserTodayAsync(user, Arg.Any<CancellationToken>()).Returns(DateOnly.Parse(localDate));
        var handler = Handler([], dates);
        dates.GetUserTimeZoneAsync(user, Arg.Any<CancellationToken>()).Returns(TimeZoneInfo.FindSystemTimeZoneById(zone));
        var first = (await handler.Handle(new GetHabitRankingQuery(user, Window: "rolling_months", Months: 6), default)).Value;
        first.Query.DateFrom.Should().Be(DateOnly.Parse(from));
        first.Query.DateToExclusive.Should().Be(DateOnly.Parse(to));
        first.Query.TimeZone.Should().Be(zone);
        dates.GetUserTodayAsync(user, Arg.Any<CancellationToken>()).Returns(DateOnly.Parse(localDate).AddDays(10));
        var refresh = (await handler.Handle(new GetHabitRankingQuery(user, QueryToken: first.QueryToken, Refresh: true), default)).Value;
        refresh.Query.Should().BeEquivalentTo(first.Query);
        await dates.Received(1).GetUserTodayAsync(user, Arg.Any<CancellationToken>());
        await dates.Received(1).GetUserTimeZoneAsync(user, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Window\":\"semester\",\"Months\":6}")]
    [InlineData("{\"Window\":\"rolling_months\",\"Months\":0}")]
    [InlineData("{\"DateFrom\":\"2024-01-02\",\"DateToExclusive\":\"2024-01-01\"}")]
    [InlineData("{\"DateFrom\":\"2024-01-01\",\"DateToExclusive\":\"2024-01-02\",\"PageSize\":101}")]
    [InlineData("{\"DateFrom\":\"2024-01-01\",\"DateToExclusive\":\"2024-01-02\",\"Metric\":\"distinct_logged_days\"}")]
    public void Validator_RejectsInvalidQueries(string json)
    {
        var request = System.Text.Json.JsonSerializer.Deserialize<GetHabitRankingQuery>(json)! with { UserId = Guid.NewGuid() };
        new GetHabitRankingQueryValidator().Validate(request).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task ConvertedBadHabit_DoesNotTurnHistoricalSlipsIntoPositiveCompletions()
    {
        var user = Guid.NewGuid();
        var from = new DateOnly(2024, 1, 1);
        var habit = Habit.Create(new HabitCreateParams(user, "Converted", FrequencyUnit.Day, 1,
            DueDate: from, IsBadHabit: true)).Value;
        habit.Log(from, advanceDueDate: false);
        habit.Update(new HabitUpdateParams("Converted", null, FrequencyUnit.Day, 1, null, false, from)).IsSuccess.Should().BeTrue();
        habit.Log(from.AddDays(1), advanceDueDate: false);

        var result = (await Handler([habit]).Handle(new GetHabitRankingQuery(user, from, from.AddDays(2)), default)).Value;

        result.Rows.Should().ContainSingle().Which.CompletedCount.Should().Be(1);
    }

    private static Habit Logged(Guid user, DateOnly date, string name = "Habit")
    {
        var habit = Habit.Create(new HabitCreateParams(user, name, FrequencyUnit.Day, 1, DueDate: date)).Value;
        habit.Log(date, advanceDueDate: false);
        return habit;
    }

    private static readonly HabitRankingTokenService Tokens = new(Options.Create(new JwtSettings
    {
        SecretKey = "ranking-unit-test-signing-secret-32-bytes",
        Issuer = "test",
        Audience = "test"
    }));

    private static GetHabitRankingQueryHandler Handler(IEnumerable<Habit> habits, IUserDateService? dates = null)
    {
        var repository = Substitute.For<IGenericRepository<Habit>>();
        repository.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(call => habits.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToList());
        dates ??= Substitute.For<IUserDateService>();
        dates.GetUserTimeZoneAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(TimeZoneInfo.Utc);
        return new GetHabitRankingQueryHandler(repository, dates, Tokens,
            Substitute.For<IProductAnalytics>(), NullLogger<GetHabitRankingQueryHandler>.Instance);
    }
}
