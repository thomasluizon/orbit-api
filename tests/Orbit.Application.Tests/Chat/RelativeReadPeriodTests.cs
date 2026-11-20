using System.Text.Json;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Chat.Validators;
using Orbit.Application.Common;
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
    private static readonly string PeriodRule = $"period must be one of {string.Join(", ", RetrospectivePeriodRange.KnownPeriods)}.";
    private const string CountRule = "period_count requires a bounded positive count for last_n_days or last_n_weeks only.";

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
        var scope = (await _blocks.ResolveAsync(tool, Json("""
            {"relative_period":"this_week","date_from":"2026-10-19","date_to":"2026-10-25",
             "date_to_exclusive":"2026-10-26","window_from_utc":"2026-10-19T00:00:00Z","window_to_utc":"2026-10-26T00:00:00Z"}
            """), _userId, default)).Value;

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
        var replay = (await _blocks.ResolveAsync(tool, scope, _userId, default)).Value;
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
        var scope = (await _blocks.ResolveAsync(tool, Json("""{"date_from":"2026-10-19","date_to":"2026-10-25"}"""), _userId, default)).Value;
        scope.GetProperty("date_from").GetString().Should().Be("2026-10-19");
        scope.GetProperty("date_to").GetString().Should().Be("2026-10-25");
    }

    [Fact]
    public async Task RelativeMonth_UsesCalendarDatesAndPreservesInsightPeriodContract()
    {
        var scope = (await _blocks.ResolveAsync("get_retrospective", Json("""{"relative_period":"last_month","period":"week"}"""), _userId, default)).Value;
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
        var scope = (await _blocks.ResolveAsync("query_habits", args, _userId, default)).Value;
        scope.GetProperty("date").GetString().Should().Be(day);
        scope.GetProperty("include_overdue").GetBoolean().Should().Be(overdue);
        scope.TryGetProperty("relative_period", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("get_period_analytics", "{\"relative_period\":\"week\"}", "Unknown relative period.")]
    [InlineData("get_period_analytics", "{\"relative_period\":\"decade\"}", "Unknown relative period.")]
    [InlineData("get_period_analytics", "{\"relative_period\":\"last_n_weeks\"}", CountRule)]
    [InlineData("get_period_analytics", "{\"relative_period\":\"last_n_weeks\",\"period_count\":0}", CountRule)]
    [InlineData("get_period_analytics", "{\"baseline_relative_period\":\"decade\"}", "Unknown relative period.")]
    [InlineData("get_period_analytics", "{\"relative_period\":\"this_week\",\"baseline_relative_period\":\"last_7_days\",\"baseline_period_count\":7}", CountRule)]
    [InlineData("get_habit_metrics", "{\"relative_period\":\"last_7_days\",\"period_count\":7}", CountRule)]
    [InlineData("get_habit_metrics", "{\"relative_period\":\"last_n_days\",\"period_count\":3660}", CountRule)]
    public async Task InvalidSelection_FailsWithTheValidatorMessageBeforeARead(string tool, string json, string rule)
    {
        var scope = await _blocks.ResolveAsync(tool, Json(json), _userId, default);
        scope.IsFailure.Should().BeTrue();
        scope.Error.Should().Contain(rule);
        scope.ErrorCode.Should().Be(ToolFailureCodes.InvalidArguments);
    }

    [Theory]
    [InlineData(ToolFailureCodes.InvalidArguments, "invalid_arguments")]
    [InlineData(ToolFailureCodes.UnexpectedError, "unexpected_error")]
    public void ToolFailureCode_IsTheWireValueTheModelReceives(string code, string wire) =>
        code.Should().Be(wire);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetrospectivePeriodCopy_NamesEveryKnownPeriod(bool isPtBr)
    {
        var copy = ErrorCopy.Resolve(ValidationErrorCodes.PeriodSupported, isPtBr);
        foreach (var period in RetrospectivePeriodRange.KnownPeriods) copy.Should().Contain(period);
    }

    [Theory]
    [InlineData("get_period_analytics", "{\"period_count\":3}")]
    [InlineData("get_period_analytics", "{\"baseline_period_count\":3}")]
    public async Task CountWithoutAPeriod_FailsNamingTheRule(string tool, string json)
    {
        var scope = await _blocks.ResolveAsync(tool, Json(json), _userId, default);
        scope.IsFailure.Should().BeTrue();
        scope.Error.Should().Be("A relative period is required with a period count.");
        scope.ErrorCode.Should().Be(ToolFailureCodes.InvalidArguments);
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
        var scope = await _blocks.ResolveAsync("get_habit_ranking", args, _userId, default);
        scope.IsFailure.Should().BeTrue();
        scope.Error.Should().Be("A relative ranking period cannot override a bound query or month window.");
        scope.ErrorCode.Should().Be(ToolFailureCodes.InvalidArguments);
    }

    [Fact]
    public async Task DatedHabitList_RefusesAPeriodRange()
    {
        var scope = await _blocks.ResolveAsync("query_habits", Json("""{"relative_period":"this_week"}"""), _userId, default);
        scope.IsFailure.Should().BeTrue();
        scope.Error.Should().Be("A dated habit list requires today or yesterday, not a period range.");
        scope.ErrorCode.Should().Be(ToolFailureCodes.InvalidArguments);
    }

    [Theory]
    [InlineData("""{"date_from":"2026-10-19","date_to":17}""", "Read dates must be strings.")]
    [InlineData("""{"date_from":"next week"}""", "Read dates must be absolute ISO dates.")]
    public async Task NonIsoReadDate_FailsNamingTheRule(string json, string rule)
    {
        var scope = await _blocks.ResolveAsync("get_daily_summary", Json(json), _userId, default);
        scope.IsFailure.Should().BeTrue();
        scope.Error.Should().Be(rule);
        scope.ErrorCode.Should().Be(ToolFailureCodes.InvalidArguments);
    }

    [Theory]
    [InlineData("query_habits", "{\"relative_period\":5}", "relative_period must be a string.")]
    [InlineData("get_retrospective", "{\"relative_period\":5}", "relative_period must be a string.")]
    [InlineData("get_habit_metrics", "{\"relative_period\":5}", "relative_period must be a string.")]
    [InlineData("get_period_analytics", "{\"baseline_relative_period\":true}", "baseline_relative_period must be a string.")]
    [InlineData("get_habit_metrics", "{\"relative_period\":\"last_n_days\",\"period_count\":\"3\"}", "period_count must be an integer.")]
    [InlineData("get_habit_metrics", "{\"relative_period\":\"last_n_days\",\"period_count\":2.5}", "period_count must be an integer.")]
    [InlineData("get_period_analytics", "{\"relative_period\":\"this_week\",\"baseline_relative_period\":\"last_n_days\",\"baseline_period_count\":[3]}", "baseline_period_count must be an integer.")]
    [InlineData("get_retrospective", "{\"period\":7}", "period must be a string.")]
    [InlineData("get_daily_summary", "{\"language\":7}", "language must be a string.")]
    public async Task UnreadableReadArgument_FailsNamingTheArgument(string tool, string json, string rule)
    {
        var scope = await _blocks.ResolveAsync(tool, Json(json), _userId, default);
        scope.IsFailure.Should().BeTrue();
        scope.Error.Should().Be(rule);
        scope.ErrorCode.Should().Be(ToolFailureCodes.InvalidArguments);
    }

    [Theory]
    [InlineData("{\"period\":\"decade\"}")]
    [InlineData("{\"relative_period\":\"today\",\"period\":\"decade\"}")]
    public async Task UnknownRetrospectivePeriod_FailsListingTheKnownPeriods(string json)
    {
        var scope = await _blocks.ResolveAsync("get_retrospective", Json(json), _userId, default);
        scope.IsFailure.Should().BeTrue();
        scope.Error.Should().Be(PeriodRule);
        scope.ErrorCode.Should().Be(ToolFailureCodes.InvalidArguments);
    }

    [Fact]
    public async Task HabitMonth_WithABrokenWindow_FailsWithTheWindowMessage()
    {
        var scope = await _blocks.ResolveAsync("get_habit_month",
            Json("""{"date_from":"2026-10-20","date_to":"2026-10-19"}"""), _userId, default);
        scope.IsFailure.Should().BeTrue();
        scope.Error.Should().Be(DomainErrors.HabitCalendarRangeInvalid.Message);
        scope.ErrorCode.Should().Be(DomainErrors.HabitCalendarRangeInvalid.Code);
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
