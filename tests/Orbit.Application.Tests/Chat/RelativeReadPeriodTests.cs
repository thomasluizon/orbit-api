using System.Text.Json;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Chat.Validators;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Chat;

public sealed class RelativeReadPeriodTests
{
    private readonly Guid _userId = Guid.NewGuid();
    private readonly IUserDateService _dates = Substitute.For<IUserDateService>();
    private readonly ReadBlockFactory _blocks;
    private static readonly DateOnly Monday = new(2026, 10, 26);

    public RelativeReadPeriodTests()
    {
        _dates.GetUserTodayAsync(_userId, Arg.Any<CancellationToken>()).Returns(Monday);
        _dates.GetUserWeekStartDayAsync(_userId, Arg.Any<CancellationToken>()).Returns(1);
        _dates.GetUserTimeZoneAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
        _blocks = new(_dates, Substitute.For<IReadQueryTokenService>(), Substitute.For<IGenericRepository<User>>());
    }

    [Theory]
    [InlineData("get_period_analytics")]
    [InlineData("get_retrospective")]
    [InlineData("get_daily_summary")]
    [InlineData("get_habit_metrics")]
    [InlineData("get_habit_history")]
    [InlineData("get_habit_month")]
    [InlineData("get_calendar_overview")]
    [InlineData("get_habit_ranking")]
    public async Task RelativeRead_OverridesModelDatesAndFreezesScope(string tool)
    {
        var scope = await _blocks.ResolveAsync(tool, Json("""
            {"relative_period":"this_week","date_from":"2026-10-19","date_to":"2026-10-25",
             "date_to_exclusive":"2026-10-26","window_from_utc":"2026-10-19T00:00:00Z","window_to_utc":"2026-10-26T00:00:00Z"}
            """), _userId, default);

        scope.GetProperty("date_from").GetString().Should().Be("2026-10-26");
        var endKey = tool == "get_habit_ranking" ? "date_to_exclusive" : "date_to";
        scope.GetProperty(endKey).GetString().Should().Be(tool == "get_habit_ranking" ? "2026-10-27" : "2026-10-26");
        scope.TryGetProperty("relative_period", out _).Should().BeFalse();
        if (tool == "get_calendar_overview")
        {
            scope.GetProperty("window_from_utc").GetString().Should().StartWith("2026-10-26T04:00:00");
            scope.GetProperty("window_to_utc").GetString().Should().StartWith("2026-10-27T04:00:00");
        }
        _dates.GetUserTodayAsync(_userId, Arg.Any<CancellationToken>()).Returns(Monday.AddDays(8));
        _dates.GetUserWeekStartDayAsync(_userId, Arg.Any<CancellationToken>()).Returns(0);
        var replay = await _blocks.ResolveAsync(tool, scope, _userId, default);
        replay.GetRawText().Should().Be(scope.GetRawText());
    }

    [Fact]
    public async Task Comparison_ResolvesBothPeriodsBeforeProductionQuery()
    {
        var mediator = Substitute.For<IMediator>();
        var habits = Substitute.For<IGenericRepository<Habit>>();
        var users = Substitute.For<IGenericRepository<User>>();
        var user = User.Create("Reader", "reader@example.test").Value;
        user.SetTimeZone("America/New_York");
        user.SetWeekStartDay(1);
        users.GetByIdAsync(_userId, Arg.Any<CancellationToken>()).Returns(user);
        var query = new GetPeriodAnalyticsQueryHandler(habits, users);
        mediator.Send(Arg.Any<GetPeriodAnalyticsQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => query.Handle(call.Arg<GetPeriodAnalyticsQuery>(), call.Arg<CancellationToken>()));
        var tool = new GetPeriodAnalyticsTool(mediator);
        var executor = ReadBlockScopeTests.CreateExecutor(tool, _blocks);

        var result = await executor.ExecuteAsync(new(_userId, tool.Name, Json("""
            {"relative_period":"this_week","baseline_relative_period":"last_week","metric":"completedHabits",
             "baseline_from":"2026-10-26","baseline_to":"2026-10-26"}
            """), AgentExecutionSurface.Chat, AgentAuthMethod.Jwt), default);

        result.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        var comparison = result.Operation.Payload.Should().BeOfType<PeriodAnalyticsResponse>().Which.Comparison!;
        comparison.Current.Window.From.Should().Be(Monday);
        comparison.Current.Window.To.Should().Be(Monday);
        comparison.Baseline.Window.From.Should().Be(Monday.AddDays(-7));
        comparison.Baseline.Window.To.Should().Be(Monday.AddDays(-1));
        result.Operation.ReadBlock!.Read.Scope.TryGetProperty("baseline_relative_period", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("get_daily_summary")]
    [InlineData("get_retrospective")]
    [InlineData("get_period_analytics")]
    public async Task AbsoluteDates_RemainAvailable(string tool)
    {
        var scope = await _blocks.ResolveAsync(tool, Json("""{"date_from":"2026-10-19","date_to":"2026-10-25"}"""), _userId, default);
        scope.GetProperty("date_from").GetString().Should().Be("2026-10-19");
        scope.GetProperty("date_to").GetString().Should().Be("2026-10-25");
    }

    [Fact]
    public async Task RelativeMonth_UsesCalendarDatesAndPreservesInsightPeriodContract()
    {
        var scope = await _blocks.ResolveAsync("get_retrospective", Json("""{"relative_period":"last_month","period":"week"}"""), _userId, default);
        scope.GetProperty("date_from").GetString().Should().Be("2026-09-01");
        scope.GetProperty("date_to").GetString().Should().Be("2026-09-30");
        scope.GetProperty("period").GetString().Should().Be("month");
    }

    [Theory]
    [InlineData("today", "2026-10-26", true)]
    [InlineData("yesterday", "2026-10-25", false)]
    public async Task DatedHabitList_ResolvesRelativeDayWithoutModelArithmetic(string period, string day, bool overdue)
    {
        var args = JsonSerializer.SerializeToElement(new { relative_period = period, date = "2026-10-19" });
        var scope = await _blocks.ResolveAsync("query_habits", args, _userId, default);
        scope.GetProperty("date").GetString().Should().Be(day);
        scope.GetProperty("include_overdue").GetBoolean().Should().Be(overdue);
        scope.TryGetProperty("relative_period", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("{\"relative_period\":\"week\"}")]
    [InlineData("{\"relative_period\":\"decade\"}")]
    [InlineData("{\"relative_period\":\"last_n_weeks\"}")]
    [InlineData("{\"relative_period\":\"last_n_weeks\",\"period_count\":0}")]
    [InlineData("{\"period_count\":3}")]
    [InlineData("{\"baseline_relative_period\":\"decade\"}")]
    public async Task InvalidPeriod_FailsBeforeARead(string json)
    {
        var resolve = () => _blocks.ResolveAsync("get_period_analytics", Json(json), _userId, default);
        await resolve.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData("query_token")]
    [InlineData("cursor")]
    [InlineData("window")]
    public async Task RankingRelativePeriod_CannotOverrideBoundQueries(string field)
    {
        var args = JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["relative_period"] = "this_week",
            [field] = "bound"
        });
        var resolve = () => _blocks.ResolveAsync("get_habit_ranking", args, _userId, default);
        await resolve.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void PeriodTools_AdvertiseRelativeSelectorsWithoutRequiringAbsoluteDates()
    {
        var mediator = Substitute.For<IMediator>();
        IAiTool[] tools = [new GetPeriodAnalyticsTool(mediator), new GetRetrospectiveTool(mediator, _dates),
            new GetDailySummaryTool(mediator, _dates), new GetHabitMetricsTool(mediator), new GetHabitHistoryTool(mediator),
            new GetHabitMonthTool(mediator), new GetCalendarOverviewTool(mediator), new GetHabitRankingTool(mediator)];
        foreach (var tool in tools)
        {
            var schema = JsonSerializer.SerializeToElement(tool.GetParameterSchema());
            var periods = schema.GetProperty("properties").GetProperty("relative_period").GetProperty("enum")
                .EnumerateArray().Select(value => value.GetString()).ToArray();
            if (tool.Name == "get_habit_month") periods.Should().BeEquivalentTo("this_month", "last_month");
            else periods.Should().Contain("this_week").And.Contain("last_week");
            if (schema.TryGetProperty("required", out var required))
                required.EnumerateArray().Select(value => value.GetString()).Should().NotContain("date_from").And.NotContain("date_to");
        }
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Theory]
    [InlineData("this_week", null, true)]
    [InlineData("last_n_days", 3659, true)]
    [InlineData("last_n_weeks", 522, true)]
    [InlineData("last_n_weeks", 523, false)]
    [InlineData("last_n_days", 3660, false)]
    [InlineData("last_n_weeks", null, false)]
    [InlineData("last_n_days", 0, false)]
    [InlineData("this_week", 3, false)]
    [InlineData("month", null, false)]
    public void Validator_RejectsUnknownSelectorsAndInvalidCounts(string period, int? count, bool valid) =>
        new RelativeReadPeriodSelectionValidator().Validate(new RelativeReadPeriodSelection(period, count)).IsValid.Should().Be(valid);
}
