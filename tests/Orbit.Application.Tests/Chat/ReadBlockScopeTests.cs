using Orbit.Domain.Enums;
using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Queries;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.Application.Tests.Chat;

public sealed class ReadBlockScopeTests
{
    private readonly Guid _userId = Guid.NewGuid();
    private readonly IUserDateService _dates = Substitute.For<IUserDateService>();
    private readonly IFeatureFlagService _flags = Substitute.For<IFeatureFlagService>();
    private readonly ReadQueryTokenService _tokens = new(Options.Create(new JwtSettings
    {
        SecretKey = "read-query-test-signing-material-32-bytes",
        Issuer = "test",
        Audience = "test"
    }));
    private readonly ReadBlockFactory _blocks;
    private readonly IGenericRepository<User> _users = Substitute.For<IGenericRepository<User>>();
    private readonly DateOnly _day = new(2026, 4, 1);

    public ReadBlockScopeTests()
    {
        _dates.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(_day);
        _dates.GetUserWeekStartDayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(1);
        _dates.GetUserTimeZoneAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo"));
        _flags.GetEnabledKeysForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<string>());
        _blocks = new(_dates, _tokens, _users);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"language\":\"en\"}")]
    public async Task RetrospectiveUsesProfileLanguageAndDoesNotReuseEnglishCache(string arguments)
    {
        var user = User.Create("Test", "test@example.com").Value;
        user.SetLanguage("en");
        _users.GetByIdAsync(_userId, Arg.Any<CancellationToken>()).Returns(user);
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, _userId, "Read", Orbit.Domain.Enums.FrequencyUnit.Day, 1, _day)).Value;
        habit.Log(_day);
        var habits = Substitute.For<IGenericRepository<Habit>>();
        habits.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>()).Returns(new[] { habit });
        var payGate = Substitute.For<IPayGateService>();
        payGate.CanUseRetrospective(_userId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        var narratives = Substitute.For<IRetrospectiveService>();
        narratives.GenerateRetrospectiveAsync(Arg.Any<List<Habit>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
            "week", Arg.Any<string>(), Arg.Any<CancellationToken>(), 1).Returns(call => Result.Success(
                new RetrospectiveNarrative(call.ArgAt<string>(4) == "pt-BR" ? "Você leu." : "You read.", "", "", "")));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var handler = new GetRetrospectiveQueryHandler(habits, payGate, narratives,
            Substitute.For<IUserStreakService>(), _dates, cache);
        var responses = new List<RetrospectiveResponse>();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetRetrospectiveQuery>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var result = await handler.Handle(call.Arg<GetRetrospectiveQuery>(), call.Arg<CancellationToken>());
            responses.Add(result.Value);
            return result;
        });
        var tool = new GetRetrospectiveTool(mediator, _dates);
        var executor = Executor(tool);
        var english = await executor.ExecuteAsync(new(_userId, tool.Name, Json(arguments),
            AgentExecutionSurface.Chat, AgentAuthMethod.Jwt), default);
        english.Operation.ReadBlock!.Block.Should().BeOfType<PeriodInsightCard>()
            .Which.Narrative.Highlights.Should().Be("You read.");

        user.SetLanguage("pt-BR");
        var portuguese = await executor.ExecuteAsync(new(_userId, tool.Name, Json(arguments),
            AgentExecutionSurface.Chat, AgentAuthMethod.Jwt), default);

        portuguese.Operation.ReadBlock!.Block.Should().BeOfType<PeriodInsightCard>()
            .Which.Narrative.Highlights.Should().Be("Você leu.");
        portuguese.Operation.ReadBlock.Read.Scope.GetProperty("language").GetString().Should().Be("pt-BR");
        responses.Select(response => response.FromCache).Should().Equal(false, false);
        await executor.ExecuteAsync(new(_userId, tool.Name, Json(arguments),
            AgentExecutionSurface.Chat, AgentAuthMethod.Jwt), default);
        responses.Select(response => response.FromCache).Should().Equal(false, false, true);
        await narratives.Received(1).GenerateRetrospectiveAsync(Arg.Any<List<Habit>>(), Arg.Any<DateOnly>(),
            Arg.Any<DateOnly>(), "week", "pt-BR", Arg.Any<CancellationToken>(), 1);
    }

    [Theory]
    [InlineData("get_daily_summary")]
    [InlineData("get_retrospective")]
    public async Task NarrativeScopeUsesCurrentProfileLanguageEvenWhenReplayed(string tool)
    {
        var user = User.Create("Test", "test@example.com").Value;
        user.SetLanguage("pt-BR");
        _users.GetByIdAsync(_userId, Arg.Any<CancellationToken>()).Returns(user);

        var scope = await _blocks.ResolveAsync(tool, Json("{\"language\":\"en\"}"), _userId, default);

        scope.GetProperty("language").GetString().Should().Be("pt-BR");
        user.SetLanguage("en");
        (await _blocks.ResolveAsync(tool, scope, _userId, default))
            .GetProperty("language").GetString().Should().Be("en");
    }

    [Fact]
    public async Task RollingDatesResolveOnceAndCalendarUsesAccountZone()
    {
        var original = await _blocks.ResolveAsync("query_habits", Json("{\"date\":\"today\",\"is_completed\":true}"), _userId, default);
        _dates.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(_day.AddDays(1));
        var replay = await _blocks.ResolveAsync("query_habits", original, _userId, default);
        replay.GetRawText().Should().Be(original.GetRawText());
        replay.GetProperty("date").GetString().Should().Be("2026-04-01");
        replay.GetProperty("include_overdue").GetBoolean().Should().BeTrue();
        var calendar = await _blocks.ResolveAsync("get_calendar_overview", Json("{\"date_from\":\"2026-04-01\"}"), _userId, default);
        calendar.GetProperty("window_from_utc").GetString().Should().StartWith("2026-04-01T03:00:00");
        calendar.GetProperty("window_to_utc").GetString().Should().StartWith("2026-04-02T03:00:00");
    }

    [Fact]
    public async Task HistoryPagesUseActualDatedLogsAndRejectDeletedSourceCursor()
    {
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, _userId, "Read", null, null, _day)).Value;
        var rows = Enumerable.Range(0, 3).Select(i => HabitLog.FromScheduleRead(Guid.NewGuid(), habit.Id,
            _day.AddDays(i), 1, 1, DateTime.UnixEpoch)).ToList();
        var repo = Substitute.For<IGenericRepository<Habit>>();
        repo.FindOneTrackedAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>()).Returns(habit);
        var reader = Substitute.For<IHabitLogReader>();
        reader.ReadRangeLogsAsync(habit.Id, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(_ => rows.Where(l => !l.IsDeleted).ToList());
        var handler = new GetHabitLogsQueryHandler(repo, reader, _dates);
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetHabitLogsQuery>(), Arg.Any<CancellationToken>())
            .Returns(c => handler.Handle(c.Arg<GetHabitLogsQuery>(), c.Arg<CancellationToken>()));
        var tool = new GetHabitHistoryTool(mediator);
        var executor = Executor(tool);
        var args = JsonSerializer.SerializeToElement(new { habit_id = habit.Id, date_from = "2026-04-01", date_to = "2026-04-03", limit = 2 });
        var initial = await executor.ExecuteAsync(new(_userId, tool.Name, args, AgentExecutionSurface.Chat, AgentAuthMethod.Jwt, null), default);
        var read = initial.Operation.ReadBlock!;
        read.Read.Completeness.Should().Be("partial");
        read.Read.TotalCount.Should().Be(3);
        var card = read.Block.Should().BeOfType<RecordListCard>().Subject;
        card.Items.Select(i => i.LogDate).Should().Equal(_day.AddDays(2), _day.AddDays(1));
        card.Items.Should().OnlyContain(i => i.Value == 1);
        var refresh = new RefreshReadBlockQueryHandler(_tokens, executor, _blocks, _flags);
        var page = await refresh.Handle(new(_userId, read.Read.NextCursor!, new(SupportsRecordListCard: true, SupportsHabitHistoryCard: true), Page: true), default);
        page.IsSuccess.Should().BeTrue();
        page.Value.Read.QueryId.Should().Be(read.Read.QueryId);
        page.Value.Block.Should().BeOfType<RecordListCard>().Which.Items.Single().LogDate.Should().Be(_day);
        rows[0].SoftDelete();
        var stale = await refresh.Handle(new(_userId, read.Read.NextCursor!, new(SupportsRecordListCard: true, SupportsHabitHistoryCard: true), Page: true), default);
        stale.ErrorCode.Should().Be("READ_QUERY_STALE");
        var updated = await refresh.Handle(new(_userId, read.Read.RefreshToken, new(SupportsRecordListCard: true, SupportsHabitHistoryCard: true)), default);
        updated.IsSuccess.Should().BeTrue();
        updated.Value.Read.Revision.Should().NotBe(read.Read.Revision);
        updated.Value.Read.TotalCount.Should().Be(2);
        updated.Value.Read.Scope.GetProperty("date_from").GetString().Should().Be("2026-04-01");
    }

    [Fact]
    public async Task RefreshRejectsCrossUserReplayTamperingAndRevokedScopes()
    {
        var tool = Substitute.For<IAiTool>();
        tool.Name.Returns("get_habit_history");
        tool.IsReadOnly.Returns(true);
        tool.GetParameterSchema().Returns(new { type = "object" });
        tool.ExecuteAsync(Arg.Any<JsonElement>(), _userId, Arg.Any<CancellationToken>())
            .Returns(new ToolResult(true, Payload: new HabitHistoryRead(0, [], "empty")));
        var executor = Executor(tool);
        var initial = await executor.ExecuteAsync(new(_userId, tool.Name, Json("{}"), AgentExecutionSurface.Chat, AgentAuthMethod.Jwt, null), default);
        var token = initial.Operation.ReadBlock!.Read.RefreshToken;
        var refresh = new RefreshReadBlockQueryHandler(_tokens, executor, _blocks, _flags);
        (await refresh.Handle(new(Guid.NewGuid(), token, new(SupportsRecordListCard: true, SupportsHabitHistoryCard: true)), default)).ErrorCode.Should().Be("READ_QUERY_EXPIRED");
        (await refresh.Handle(new(_userId, token + "x", new(SupportsRecordListCard: true, SupportsHabitHistoryCard: true)), default)).ErrorCode.Should().Be("READ_QUERY_EXPIRED");
        var revoked = await refresh.Handle(new(_userId, token, new(SupportsRecordListCard: true, SupportsHabitHistoryCard: true),
            AuthMethod: AgentAuthMethod.ApiKey, GrantedScopes: []), default);
        revoked.IsFailure.Should().BeTrue();
        await tool.Received(1).ExecuteAsync(Arg.Any<JsonElement>(), _userId, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task NewKindsRequirePartsAndExplicitAdvertisement(bool supportsParts, bool advertises)
    {
        var metric = new PeriodHabitMetrics(new(_day, _day), 0, 0, 0, 0, null, 0, 0);
        var payload = new PeriodAnalyticsResponse(new("positiveLogRecords", "records", 0, metric, new("day", [])), null, "zero");
        var read = _blocks.Capture("get_period_analytics", Json("{}"), _userId, payload)!;
        var executor = Substitute.For<IAgentOperationExecutor>();
        var refresh = new RefreshReadBlockQueryHandler(_tokens, executor, _blocks, _flags);
        var result = await refresh.Handle(new(_userId, read.Read.RefreshToken,
            new(SupportsParts: supportsParts, PartKinds: advertises ? ["periodTotal"] : [])), default);
        result.IsFailure.Should().BeTrue();
        await executor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
    }

    [Fact]
    public async Task NarrativeRefreshNeverCallsNarrativeGeneratorAndKeepsOriginalDates()
    {
        var mediator = Substitute.For<IMediator>();
        var summary = new DailySummaryResponse("", "", false,
            new(_day, 1, 0, 0, 0, 0), "source");
        mediator.Send(Arg.Any<GetDailySummaryQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success(summary));
        var tool = new GetDailySummaryTool(mediator, _dates);
        var executor = Executor(tool);
        var initial = await executor.ExecuteAsync(new(_userId, tool.Name, Json("{}"), AgentExecutionSurface.Chat, AgentAuthMethod.Jwt, null), default);
        _dates.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(_day.AddDays(100));
        var refresh = new RefreshReadBlockQueryHandler(_tokens, executor, _blocks, _flags);
        var result = await refresh.Handle(new(_userId, initial.Operation.ReadBlock!.Read.RefreshToken,
            new(SupportsDaySummaryCard: true)), default);
        result.IsSuccess.Should().BeTrue();
        await mediator.Received(1).Send(Arg.Is<GetDailySummaryQuery>(q => !q.GenerateNarrative && q.DateFrom == _day && q.DateTo == _day), Arg.Any<CancellationToken>());
    }


    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RefreshRechecksSubtypeSupportAndKeepsAbsoluteDay(bool history, bool supports)
    {
        var args = history ? Json("{\"date_from\":\"2026-04-01\",\"date_to\":\"2026-04-02\"}")
            : Json("{\"date\":\"2026-04-01\"}");
        object payload = history ? new HabitHistoryRead(0, [], "revision") : new HabitListCard("date", [], _day);
        var tool = Substitute.For<IAiTool>();
        tool.Name.Returns(history ? "get_habit_history" : "query_habits");
        tool.IsReadOnly.Returns(true);
        tool.GetParameterSchema().Returns(new { type = "object" });
        tool.ExecuteAsync(Arg.Any<JsonElement>(), _userId, Arg.Any<CancellationToken>())
            .Returns(new ToolResult(true, Payload: payload));
        var executor = Executor(tool);
        var read = _blocks.Capture(tool.Name, args, _userId, payload)!;
        _dates.GetUserTodayAsync(_userId, Arg.Any<CancellationToken>()).Returns(_day.AddDays(1));
        var refresh = new RefreshReadBlockQueryHandler(_tokens, executor, _blocks, _flags);
        var result = await refresh.Handle(new(_userId, read.Read.RefreshToken, new(SupportsHabitListCard: true,
            SupportsRecordListCard: true, SupportsDatedHabitListCard: supports, SupportsHabitHistoryCard: supports)), default);
        result.IsSuccess.Should().Be(supports);
        if (supports)
            result.Value.Read.Scope.GetProperty(history ? "date_from" : "date").GetString().Should().Be("2026-04-01");
        else
            result.ErrorCode.Should().Be(Orbit.Application.Common.ErrorMessages.NoPermission.Code);
    }

    [Fact]
    public async Task MetricsRefreshPinsLegacyRollingRatesAndTracksAccountSettings()
    {
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, _userId, "Read", Orbit.Domain.Enums.FrequencyUnit.Day, 1, _day)).Value;
        habit.Log(_day);
        var habits = Substitute.For<IGenericRepository<Habit>>();
        habits.FindOneTrackedAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>()).Returns(habit);
        var user = User.Create("Test", "test@example.com").Value;
        var users = Substitute.For<IGenericRepository<User>>();
        users.GetByIdAsync(_userId, Arg.Any<CancellationToken>()).Returns(user);
        var query = new GetHabitMetricsQueryHandler(habits, users, _dates);
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetHabitMetricsQuery>(), Arg.Any<CancellationToken>())
            .Returns(c => query.Handle(c.Arg<GetHabitMetricsQuery>(), c.Arg<CancellationToken>()));
        var tool = new GetHabitMetricsTool(mediator);
        var executor = Executor(tool);
        var first = await executor.ExecuteAsync(new(_userId, tool.Name,
            JsonSerializer.SerializeToElement(new { habit_id = habit.Id }), AgentExecutionSurface.Chat, AgentAuthMethod.Jwt), default);
        var read = first.Operation.ReadBlock!;
        var original = (MetricsCard)read.Block;
        _dates.GetUserTodayAsync(_userId, Arg.Any<CancellationToken>()).Returns(_day.AddDays(40));
        var refresh = new RefreshReadBlockQueryHandler(_tokens, executor, _blocks, _flags);
        var result = await refresh.Handle(new(_userId, read.Read.RefreshToken, new(SupportsMetricsCard: true)), default);
        result.IsSuccess.Should().BeTrue();
        var refreshed = (MetricsCard)result.Value.Block;
        refreshed.WeeklyCompletionRate.Should().Be(original.WeeklyCompletionRate);
        refreshed.MonthlyCompletionRate.Should().Be(original.MonthlyCompletionRate);
        refreshed.PeriodMetrics.Should().BeEquivalentTo(original.PeriodMetrics);
        user.SetTimeZone("Pacific/Auckland").IsSuccess.Should().BeTrue();
        var changed = await refresh.Handle(new(_userId, read.Read.RefreshToken, new(SupportsMetricsCard: true)), default);
        changed.Value.Read.Revision.Should().NotBe(read.Read.Revision);
    }

    [Fact]
    public async Task CalendarDisconnectedCarriesSyncStateAndRecovery()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(NSubstitute.Arg.Any<Orbit.Application.Calendar.Queries.GetCalendarEventsQuery>(), NSubstitute.Arg.Any<CancellationToken>())
            .Returns(Result.Failure<List<Orbit.Application.Calendar.Queries.CalendarEventItem>>(Orbit.Application.Common.ErrorMessages.CalendarNotConnected));
        mediator.Send(NSubstitute.Arg.Any<Orbit.Application.Calendar.Queries.GetCalendarAutoSyncStateQuery>(), NSubstitute.Arg.Any<CancellationToken>())
            .Returns(Result.Success(new Orbit.Application.Calendar.Queries.CalendarAutoSyncStateResponse(false,
                Orbit.Domain.Enums.GoogleCalendarAutoSyncStatus.Idle, null, false)));
        mediator.Send(NSubstitute.Arg.Any<Orbit.Application.Calendar.Queries.GetCalendarSyncSuggestionsQuery>(), NSubstitute.Arg.Any<CancellationToken>())
            .Returns(Result.Success(new List<Orbit.Application.Calendar.Queries.CalendarSyncSuggestionItem>()));
        mediator.Send(NSubstitute.Arg.Any<Orbit.Application.Calendar.Queries.GetUserCalendarsQuery>(), NSubstitute.Arg.Any<CancellationToken>())
            .Returns(Result.Failure<List<Orbit.Application.Calendar.Queries.UserCalendarItem>>(Orbit.Application.Common.ErrorMessages.CalendarNotConnected));
        var tool = new GetCalendarOverviewTool(mediator);
        var result = await Executor(tool).ExecuteAsync(new(_userId, tool.Name, Json("{}"),
            AgentExecutionSurface.Chat, AgentAuthMethod.Jwt), default);
        var read = result.Operation.ReadBlock!;
        read.Read.Completeness.Should().Be("partial");
        read.Read.Recovery.Should().Be("reconnect");
        var card = read.Block.Should().BeOfType<CalendarCard>().Subject;
        card.Events.Should().BeEmpty();
        card.Sync!.HasGoogleConnection.Should().BeFalse();
    }

    [Fact]
    public void TokensExpireAndRefreshContractsSerializeDeclaredBlockTypes()
    {
        var ticket = new ReadQueryTicket(_userId, Guid.NewGuid(), "get_period_analytics", Json("{}"), "periodTotal", "zero", DateTime.UnixEpoch);
        _tokens.Read(_tokens.Issue(ticket), _userId).Should().BeNull();
        var metric = new PeriodHabitMetrics(new(_day, _day), 0, 0, 0, 0, null, 0, 0);
        var read = _blocks.Capture("get_period_analytics", Json("{}"), _userId,
            new PeriodAnalyticsResponse(new("positiveLogRecords", "records", 0, metric, new("day", [])), null, "zero"))!;
        var response = JsonSerializer.SerializeToElement(ReadBlockRefreshResponse.From(read), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        response.GetProperty("block").TryGetProperty("$type", out _).Should().BeFalse();
        response.GetProperty("block").GetProperty("metric").GetString().Should().Be("positiveLogRecords");
        response.GetProperty("block").GetProperty("metrics").GetProperty("completionRate").ValueKind.Should().Be(JsonValueKind.Null);
        var invalid = () => new Orbit.Application.Calendar.Services.CalendarReadWindow(DateTime.UnixEpoch, DateTime.UnixEpoch);
        invalid.Should().Throw<ArgumentException>();
    }


    [Fact]
    public async Task KeyRefreshPreservesTheStepUpBoundary()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<Orbit.Application.ApiKeys.Queries.GetApiKeysQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<Orbit.Application.ApiKeys.Queries.ApiKeyResponse>>([]));
        var tool = new GetApiKeysTool(mediator);
        var executor = Executor(tool);
        var initial = await executor.ExecuteAsync(new(_userId, tool.Name, Json("{}"), AgentExecutionSurface.Chat, AgentAuthMethod.Jwt), default);
        mediator.Send(Arg.Any<Orbit.Application.ApiKeys.Queries.GetApiKeysQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<IReadOnlyList<Orbit.Application.ApiKeys.Queries.ApiKeyResponse>>(Orbit.Application.Common.ErrorMessages.ApiKeyCreationChallengeRequired));
        var refresh = new RefreshReadBlockQueryHandler(_tokens, executor, _blocks, _flags);
        var result = await refresh.Handle(new(_userId, initial.Operation.ReadBlock!.Read.RefreshToken, new(SupportsRecordListCard: true, SupportsHabitHistoryCard: true)), default);
        result.ErrorCode.Should().Be(Orbit.Application.Common.ErrorMessages.ApiKeyCreationChallengeRequired.Code);
        await mediator.Received(2).Send(Arg.Is<Orbit.Application.ApiKeys.Queries.GetApiKeysQuery>(q => q.BypassCache && q.UserId == _userId), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FailedRefreshKeepsTheOriginalQueryRecoverable()
    {
        var tool = Substitute.For<IAiTool>();
        tool.Name.Returns("get_habit_history"); tool.IsReadOnly.Returns(true);
        tool.GetParameterSchema().Returns(new { type = "object" });
        tool.ExecuteAsync(Arg.Any<JsonElement>(), _userId, Arg.Any<CancellationToken>())
            .Returns(new ToolResult(true, Payload: new HabitHistoryRead(0, [], "empty")));
        var executor = Executor(tool);
        var initial = await executor.ExecuteAsync(new(_userId, tool.Name, Json("{\"date_from\":\"2026-04-01\",\"date_to\":\"2026-04-02\"}"), AgentExecutionSurface.Chat, AgentAuthMethod.Jwt), default);
        var read = initial.Operation.ReadBlock!.Read;
        tool.ExecuteAsync(Arg.Any<JsonElement>(), _userId, Arg.Any<CancellationToken>()).Returns(new ToolResult(false, Error: "Source unavailable"));
        var refresh = new RefreshReadBlockQueryHandler(_tokens, executor, _blocks, _flags);
        var failed = await refresh.Handle(new(_userId, read.RefreshToken, new(SupportsRecordListCard: true, SupportsHabitHistoryCard: true)), default);
        failed.ErrorCode.Should().Be("READ_QUERY_REFRESH_FAILED");
        _tokens.Read(read.RefreshToken, _userId)!.Arguments.GetProperty("date_from").GetString().Should().Be("2026-04-01");
        tool.ExecuteAsync(Arg.Any<JsonElement>(), _userId, Arg.Any<CancellationToken>()).Returns(new ToolResult(true, Payload: new HabitHistoryRead(0, [], "empty")));
        var retried = await refresh.Handle(new(_userId, read.RefreshToken, new(SupportsRecordListCard: true, SupportsHabitHistoryCard: true)), default);
        retried.IsSuccess.Should().BeTrue();
        retried.Value.Read.QueryId.Should().Be(read.QueryId);
    }


    [Fact]
    public async Task CalendarRefreshCarriesQueriedCollectionsAndRetainsSuggestionScope()
    {
        var mediator = Substitute.For<IMediator>();
        var item = new Orbit.Application.Calendar.Queries.CalendarEventItem("event", "Appointment", null,
            "2026-04-01", null, null, false, null, []);
        var suggestion = new Orbit.Application.Calendar.Queries.CalendarSyncSuggestionItem(Guid.NewGuid(), item.Id, item, DateTime.UnixEpoch);
        var calendar = new Orbit.Application.Calendar.Queries.UserCalendarItem("calendar", "Main", "owner", true, null, true);
        mediator.Send(Arg.Any<Orbit.Application.Calendar.Queries.GetCalendarEventsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new List<Orbit.Application.Calendar.Queries.CalendarEventItem> { item }));
        mediator.Send(Arg.Any<Orbit.Application.Calendar.Queries.GetCalendarAutoSyncStateQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new Orbit.Application.Calendar.Queries.CalendarAutoSyncStateResponse(true, Orbit.Domain.Enums.GoogleCalendarAutoSyncStatus.Idle, null, true)));
        mediator.Send(Arg.Any<Orbit.Application.Calendar.Queries.GetCalendarSyncSuggestionsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new List<Orbit.Application.Calendar.Queries.CalendarSyncSuggestionItem> { suggestion }));
        mediator.Send(Arg.Any<Orbit.Application.Calendar.Queries.GetUserCalendarsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new List<Orbit.Application.Calendar.Queries.UserCalendarItem> { calendar }));
        var tool = new GetCalendarOverviewTool(mediator);
        var executor = Executor(tool);
        var args = Json("{\"date_from\":\"2026-04-01\",\"date_to\":\"2026-04-02\",\"calendar_ids\":[\"calendar\"]}");
        var initial = await executor.ExecuteAsync(new(_userId, tool.Name, args, AgentExecutionSurface.Chat, AgentAuthMethod.Jwt), default);
        var read = initial.Operation.ReadBlock!;
        var card = read.Block.Should().BeOfType<CalendarCard>().Subject;
        card.Suggestions.Should().ContainSingle().Which.Should().Be(suggestion);
        card.Calendars.Should().ContainSingle().Which.Should().Be(calendar);
        _dates.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(_day.AddDays(10));
        var refresh = new RefreshReadBlockQueryHandler(_tokens, executor, _blocks, _flags);
        (await refresh.Handle(new(_userId, read.Read.RefreshToken, new(SupportsCalendarCard: true)), default)).IsSuccess.Should().BeTrue();
        await mediator.Received(2).Send(Arg.Is<Orbit.Application.Calendar.Queries.GetCalendarSyncSuggestionsQuery>(q =>
            q.AsOf == _day && q.DateTo == _day.AddDays(1) && !q.UseCurrentSelection && q.CalendarIds!.Single() == "calendar"), Arg.Any<CancellationToken>());
    }

    private AgentOperationExecutor Executor(IAiTool tool) => CreateExecutor(tool, _blocks);

    [Theory]
    [InlineData(true, false, false, "gamification")]
    [InlineData(false, true, false, "gamification")]
    [InlineData(false, false, true, "gamification")]
    [InlineData(true, true, false, "gamification")]
    [InlineData(false, true, true, "gamification")]
    [InlineData(true, false, true, "streak")]
    [InlineData(true, true, true, "streak")]
    [InlineData(false, false, false, null)]
    public async Task GamificationIncludes_ProjectOnlyFetchedSourcesAndRefreshTheOriginalQuery(
        bool profile, bool achievements, bool streak, string? kind)
    {
        var mediator = Substitute.For<IMediator>();
        var source = GamificationReadTestData.Configure(mediator, _day);
        var tool = new GetGamificationOverviewTool(mediator);
        var executor = Executor(tool);
        var args = JsonSerializer.SerializeToElement(new
        {
            include_profile = profile,
            include_achievements = achievements,
            include_streak = streak
        });
        var initial = await executor.ExecuteAsync(new(_userId, tool.Name, args, AgentExecutionSurface.Chat, AgentAuthMethod.Jwt), default);
        initial.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        if (kind is null)
        {
            initial.Operation.ReadBlock.Should().BeNull("no source was requested");
            mediator.ReceivedCalls().Should().BeEmpty();
            return;
        }

        var read = initial.Operation.ReadBlock;
        read.Should().NotBeNull("each nonempty successful gamification read needs an accurate projection");
        read!.Kind.Should().Be(kind);
        var expected = new GamificationOverviewPayload(
            profile ? achievements ? source.Profile! with { Achievements = [] } : source.Profile : null,
            achievements ? source.Achievements : null, streak ? source.Streak : null);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var wire = kind == "gamification"
            ? JsonSerializer.SerializeToElement(GamificationReadBlockRefreshResponse.From(read), options)
            : JsonSerializer.SerializeToElement(ReadBlockRefreshResponse.From(read), options);
        wire.GetProperty("block").GetProperty("state").GetRawText()
            .Should().Be(JsonSerializer.Serialize(expected, options));
        wire.GetProperty("block").TryGetProperty("$type", out _).Should().BeFalse();
        read.Read.Scope.GetProperty("as_of").GetString().Should().Be(_day.ToString("yyyy-MM-dd"));
        read.Read.Scope.GetProperty("include_profile").GetBoolean().Should().Be(profile);
        read.Read.Scope.GetProperty("include_achievements").GetBoolean().Should().Be(achievements);
        read.Read.Scope.GetProperty("include_streak").GetBoolean().Should().Be(streak);
        ReadBlockTelemetry.Describe(read.Block).Read.Should().Be(read.Read);

        _dates.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(_day.AddDays(10));
        var refresh = new RefreshReadBlockQueryHandler(_tokens, executor, _blocks, _flags);
        var result = await refresh.Handle(new(_userId, read.Read.RefreshToken,
            new(SupportsParts: true, PartKinds: [kind], SupportsStreakCard: true), GamificationOnly: kind == "gamification"), default);
        result.IsSuccess.Should().BeTrue();
        result.Value.Kind.Should().Be(kind);
        result.Value.Read.QueryId.Should().Be(read.Read.QueryId);
        result.Value.Read.Scope.GetProperty("as_of").GetString().Should().Be(_day.ToString("yyyy-MM-dd"));
        result.Value.Read.Scope.GetProperty("include_profile").GetBoolean().Should().Be(profile);
        result.Value.Read.Scope.GetProperty("include_achievements").GetBoolean().Should().Be(achievements);
        result.Value.Read.Scope.GetProperty("include_streak").GetBoolean().Should().Be(streak);
        var refreshed = kind == "gamification"
            ? JsonSerializer.SerializeToElement(GamificationReadBlockRefreshResponse.From(result.Value), options)
            : JsonSerializer.SerializeToElement(ReadBlockRefreshResponse.From(result.Value), options);
        refreshed.GetProperty("block").GetProperty("state").GetRawText()
            .Should().Be(JsonSerializer.Serialize(expected, options));
        await mediator.Received(profile ? 2 : 0).Send(
            Arg.Is<Orbit.Application.Gamification.Queries.GetGamificationProfileQuery>(q => q.UserId == _userId && !q.ReconcileAchievements), Arg.Any<CancellationToken>());
        await mediator.Received(achievements ? 2 : 0).Send(
            Arg.Is<Orbit.Application.Gamification.Queries.GetAchievementsQuery>(q => q.UserId == _userId && !q.ReconcileAchievements), Arg.Any<CancellationToken>());
        await mediator.Received(streak ? 2 : 0).Send(
            Arg.Is<Orbit.Application.Gamification.Queries.GetStreakInfoQuery>(q => q.UserId == _userId && q.AsOf == _day), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public async Task GamificationRefresh_RequiresPartsKindAndEnabledStatusBlocks(bool parts, bool advertisesKind, bool disabled)
    {
        var mediator = Substitute.For<IMediator>();
        GamificationReadTestData.Configure(mediator, _day);
        var tool = new GetGamificationOverviewTool(mediator);
        var executor = Executor(tool);
        var initial = await executor.ExecuteAsync(new(_userId, tool.Name,
            Json("""{"include_profile":false,"include_achievements":true,"include_streak":false}"""), AgentExecutionSurface.Chat, AgentAuthMethod.Jwt), default);
        initial.Operation.ReadBlock.Should().NotBeNull();
        mediator.ClearReceivedCalls();
        _flags.GetEnabledKeysForUserAsync(_userId, Arg.Any<CancellationToken>()).Returns(disabled
            ? [Orbit.Application.Common.FeatureFlagKeys.AstraStatusBlocksDisabled] : Array.Empty<string>());
        var refresh = new RefreshReadBlockQueryHandler(_tokens, executor, _blocks, _flags);

        var result = await refresh.Handle(new(_userId, initial.Operation.ReadBlock!.Read.RefreshToken,
            new(SupportsParts: parts, PartKinds: advertisesKind ? ["gamification"] : ["streak"], SupportsStreakCard: true),
            GamificationOnly: true), default);

        result.ErrorCode.Should().Be(Orbit.Application.Common.ErrorMessages.NoPermission.Code);
        mediator.ReceivedCalls().Should().BeEmpty("unsupported refreshes must be denied before reading sources");
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    public async Task GamificationRefresh_GenericContractRejectsTokenBeforeExecutingSources(bool profile, bool achievements, bool streak)
    {
        var mediator = Substitute.For<IMediator>();
        GamificationReadTestData.Configure(mediator, _day);
        var tool = new GetGamificationOverviewTool(mediator);
        var executor = Executor(tool);
        var initial = await executor.ExecuteAsync(new(_userId, tool.Name, JsonSerializer.SerializeToElement(new
        { include_profile = profile, include_achievements = achievements, include_streak = streak }),
            AgentExecutionSurface.Chat, AgentAuthMethod.Jwt), default);
        mediator.ClearReceivedCalls();
        var refresh = new RefreshReadBlockQueryHandler(_tokens, executor, _blocks, _flags);

        var result = await refresh.Handle(new(_userId, initial.Operation.ReadBlock!.Read.RefreshToken,
            new(SupportsParts: true, PartKinds: ["gamification"])), default);

        result.ErrorCode.Should().Be(Orbit.Application.Common.ErrorMessages.NoPermission.Code);
        mediator.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GamificationRefresh_DedicatedContractRejectsStreakTokensAndPaging(bool page)
    {
        var mediator = Substitute.For<IMediator>();
        GamificationReadTestData.Configure(mediator, _day);
        var tool = new GetGamificationOverviewTool(mediator);
        var executor = Executor(tool);
        var initial = await executor.ExecuteAsync(new(_userId, tool.Name,
            JsonSerializer.SerializeToElement(new { include_profile = true, include_achievements = false, include_streak = !page }),
            AgentExecutionSurface.Chat, AgentAuthMethod.Jwt), default);
        mediator.ClearReceivedCalls();
        var refresh = new RefreshReadBlockQueryHandler(_tokens, executor, _blocks, _flags);

        var result = await refresh.Handle(new(_userId, initial.Operation.ReadBlock!.Read.RefreshToken,
            new(SupportsParts: true, PartKinds: ["gamification", "streak"], SupportsStreakCard: true),
            Page: page, GamificationOnly: true), default);

        result.ErrorCode.Should().Be(page ? "READ_QUERY_PAGE_INVALID" : Orbit.Application.Common.ErrorMessages.NoPermission.Code);
        mediator.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void RefreshValidator_RejectsPagingOnlyForTheGamificationContract(bool gamification, bool page, bool valid)
    {
        var validator = new Orbit.Application.Chat.Validators.RefreshReadBlockQueryValidator();
        validator.Validate(new RefreshReadBlockQuery(_userId, "token", null, Page: page, GamificationOnly: gamification))
            .IsValid.Should().Be(valid);
    }

    [Fact]
    public async Task GamificationDefaults_KeepTheExistingCombinedStreakContract()
    {
        var mediator = Substitute.For<IMediator>();
        GamificationReadTestData.Configure(mediator, _day);
        var tool = new GetGamificationOverviewTool(mediator);
        var response = await Executor(tool).ExecuteAsync(new(_userId, tool.Name, Json("{}"), AgentExecutionSurface.Chat, AgentAuthMethod.Jwt), default);

        response.Operation.ReadBlock!.Kind.Should().Be("streak");
        var card = response.Operation.ReadBlock.Block.Should().BeOfType<StreakCard>().Subject;
        card.CurrentStreak.Should().Be(5);
        card.Level.Should().Be(3);
        card.State!.Profile.Should().NotBeNull();
        card.State.Achievements.Should().NotBeNull();
        card.State.Streak.Should().NotBeNull();
    }

    internal static AgentOperationExecutor CreateExecutor(IAiTool tool, ReadBlockFactory blocks)
    {
        var catalog = new AgentCatalogService([tool]);
        var bridge = Substitute.For<IAgentStepUpAuthorizationBridge>();
        bridge.GetRequiredConfirmationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((AgentConfirmationRequirement?)null);
        var policy = Substitute.For<IAgentPolicyEvaluator>();
        policy.Evaluate(Arg.Any<AgentPolicyEvaluationContext>()).Returns(c =>
        {
            var context = c.Arg<AgentPolicyEvaluationContext>();
            var capability = catalog.GetCapability(context.CapabilityId)!;
            return new AgentPolicyDecision(context.AuthMethod == AgentAuthMethod.ApiKey && !context.GrantedScopes.Contains(capability.Scope)
                ? AgentPolicyDecisionStatus.Denied : AgentPolicyDecisionStatus.Allowed, capability);
        });
        var ownership = Substitute.For<IAgentTargetOwnershipService>();
        ownership.GetDenialReasonAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        return new(catalog, policy, Substitute.For<IAgentAuditService>(), ownership, bridge, new([tool]),
            Substitute.For<IPendingOperationChangePreviewer>(), Substitute.For<IPendingAgentOperationStore>(),
            Substitute.For<IUnitOfWork>(), NullLogger<AgentOperationExecutor>.Instance, blocks);
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
