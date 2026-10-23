using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Calendar.Queries;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Queries;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Queries;
using Orbit.Application.Habits.Services;
using Orbit.Application.Proactive.Queries;
using Orbit.Application.Proactive.Services;
using Orbit.Application.Tests.Chat;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.Application.Tests.Queries;

public sealed class ProactivePartsTests
{
    public static IEnumerable<object[]> Triggers() => Enum.GetValues<ProactiveTrigger>()
        .SelectMany(trigger => new[] { "en", "pt-BR" }.Select(locale => new object[] { trigger, locale }));

    [Theory]
    [MemberData(nameof(Triggers))]
    public async Task Current_ProducesOneRealReadBlockAndRefreshesItsExactScope(ProactiveTrigger trigger, string locale)
    {
        using var h = new Harness(trigger, locale);
        var response = (await h.Handler.Handle(new(h.User.Id, h.Client), default)).Value!;

        var part = response.Parts.Should().ContainSingle().Subject.Should().BeOfType<ChatBlockPart>().Subject;
        part.Kind.Should().Be(h.Kind);
        var read = ReadBlockTelemetry.Describe(part.Block).Read!;
        read.QueryFamily.Should().Be(h.Tool.Name);
        read.Completeness.Should().Be("complete");
        read.RefreshToken.Should().NotBeNullOrEmpty();
        h.AssertData(part.Block);
        var json = JsonSerializer.SerializeToElement(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.GetProperty("parts")[0].GetProperty("type").GetString().Should().Be("block");
        json.GetProperty("parts")[0].GetProperty("block").GetProperty("read").GetProperty("queryId").GetGuid()
            .Should().Be(read.QueryId);

        h.Dates.GetUserTodayAsync(h.User.Id, Arg.Any<CancellationToken>()).Returns(h.Day.AddDays(2));
        var refresh = await new RefreshReadBlockQueryHandler(h.Tokens, h.Executor, h.Blocks, h.Flags)
            .Handle(new(h.User.Id, read.RefreshToken, h.Client), default);
        refresh.IsSuccess.Should().BeTrue();
        refresh.Value.Read.QueryId.Should().Be(read.QueryId);
        foreach (var property in read.Scope.EnumerateObject())
            refresh.Value.Read.Scope.GetProperty(property.Name).ToString().Should().Be(property.Value.ToString());
        h.AssertData(refresh.Value.Block);
        h.Narratives.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Triggers))]
    public async Task Current_LegacyJsonIsUnchangedAndDoesNotReadBlocks(ProactiveTrigger trigger, string locale)
    {
        using var h = new Harness(trigger, locale);
        var item = h.Item;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var expected = JsonSerializer.Serialize(new
        {
            item.Id,
            Trigger = item.Trigger.ToString(),
            item.LocalDate,
            item.Text,
            item.ActionLabel,
            item.ActionPrompt,
            item.CreatedAtUtc,
            item.ShownAtUtc,
            item.PushedAtUtc,
            Facts = JsonSerializer.Deserialize<ProactiveFacts>(item.FactsJson)
        }, options);

        foreach (var client in new AgentClientContext?[] { null, h.Client with { SupportsParts = false } })
        {
            var response = (await h.Handler.Handle(new(h.User.Id, client), default)).Value!;
            JsonSerializer.Serialize(response, options).Should().Be(expected);
        }
        h.Flags.ReceivedCalls().Should().BeEmpty();
        h.Mediator.ReceivedCalls().Should().BeEmpty();
        await h.Habits.DidNotReceiveWithAnyArgs().FindAsync(default!, default!, default);
    }

    [Theory]
    [MemberData(nameof(Triggers))]
    public async Task Current_UnadvertisedKindKeepsLegacyResponseWithoutReadingData(ProactiveTrigger trigger, string locale)
    {
        using var h = new Harness(trigger, locale);

        var response = (await h.Handler.Handle(new(h.User.Id, h.Client with { PartKinds = [] }), default)).Value!;

        response.Parts.Should().BeNull();
        response.Text.Should().Be(h.Item.Text);
        h.Mediator.ReceivedCalls().Should().BeEmpty();
        await h.Habits.DidNotReceiveWithAnyArgs().FindAsync(default!, default!, default);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task Current_WeeklyReviewIncludesScheduledWeekWithZeroActivity(string locale)
    {
        using var h = new Harness(ProactiveTrigger.WeeklyReview, locale, hasLogs: false);

        var response = (await h.Handler.Handle(new(h.User.Id, h.Client), default)).Value!;

        var card = response.Parts.Should().ContainSingle().Subject.Should().BeOfType<ChatBlockPart>().Subject.Block
            .Should().BeOfType<PeriodInsightCard>().Subject;
        card.TotalCompletions.Should().Be(0);
        card.TotalScheduled.Should().Be(7);
        card.Read!.Completeness.Should().Be("complete");
        h.Narratives.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Current_FailedSourceReadKeepsLegacyResponseWithoutABlock()
    {
        using var h = new Harness(ProactiveTrigger.WeeklyReview, "en");
        h.Mediator.Send(Arg.Any<GetRetrospectiveQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<RetrospectiveResponse>(ErrorMessages.NoHabitsForPeriod));

        var response = (await h.Handler.Handle(new(h.User.Id, h.Client), default)).Value!;

        response.Parts.Should().BeNull();
        response.Text.Should().Be(h.Item.Text);
    }

    [Theory]
    [InlineData("parts")]
    [InlineData("kind")]
    [InlineData("card")]
    [InlineData("statusFlag")]
    [InlineData("partsFlag")]
    public async Task Current_RequiresTheSameCapabilitiesAndFlagsAsChat(string missing)
    {
        using var h = new Harness(ProactiveTrigger.RestartAfterMiss, "en");
        var client = missing switch
        {
            "parts" => h.Client with { SupportsParts = false },
            "kind" => h.Client with { PartKinds = ["unknown"] },
            "card" => h.Client with { SupportsDaySummaryCard = false },
            _ => h.Client
        };
        if (missing.EndsWith("Flag", StringComparison.Ordinal))
            h.Flags.GetEnabledKeysForUserAsync(h.User.Id, Arg.Any<CancellationToken>()).Returns(new[]
            {
                missing == "partsFlag" ? FeatureFlagKeys.AstraPartsDisabled : FeatureFlagKeys.AstraStatusBlocksDisabled
            });

        var response = (await h.Handler.Handle(new(h.User.Id, client), default)).Value!;
        response.Parts.Should().BeNull();
        response.Text.Should().Be(h.Item.Text);
        h.Mediator.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Current_DeniedReadPreservesLegacyContentWithoutInventingABlock()
    {
        using var h = new Harness(ProactiveTrigger.MorningPlan, "en");
        var response = (await h.Handler.Handle(new(h.User.Id, h.Client, AgentAuthMethod.ApiKey, []), default)).Value!;
        response.Parts.Should().BeNull();
        response.Text.Should().Be(h.Item.Text);
        await h.Habits.DidNotReceiveWithAnyArgs().FindAsync(default!, default!, default);
    }

    private sealed class Harness : IDisposable
    {
        private readonly MemoryCache _cache = new(new MemoryCacheOptions());
        private readonly Habit _habit;
        private readonly Goal _goal;
        public User User { get; } = User.Create("Alex", "alex@example.com").Value;
        public DateOnly Day { get; } = new(2030, 10, 14);
        public IUserDateService Dates { get; } = Substitute.For<IUserDateService>();
        public IFeatureFlagService Flags { get; } = Substitute.For<IFeatureFlagService>();
        public IGenericRepository<Habit> Habits { get; } = Substitute.For<IGenericRepository<Habit>>();
        public IMediator Mediator { get; } = Substitute.For<IMediator>();
        public IRetrospectiveService Narratives { get; } = Substitute.For<IRetrospectiveService>();
        public ReadQueryTokenService Tokens { get; } = new(Options.Create(new JwtSettings
        {
            SecretKey = "proactive-query-test-signing-material-32-bytes",
            Issuer = "test",
            Audience = "test"
        }));
        public ReadBlockFactory Blocks { get; }
        public IAgentOperationExecutor Executor { get; }
        public IAiTool Tool { get; }
        public string Kind { get; }
        public ProactiveItem Item { get; }
        public GetCurrentProactiveQueryHandler Handler { get; }
        public AgentClientContext Client { get; }

        public Harness(ProactiveTrigger trigger, string locale, bool hasLogs = true)
        {
            User.SetLanguage(locale);
            User.SetTimeZone("America/Sao_Paulo");
            Dates.GetUserTodayAsync(User.Id, Arg.Any<CancellationToken>()).Returns(Day);
            Dates.GetUserWeekStartDayAsync(User.Id, Arg.Any<CancellationToken>()).Returns(1);
            Dates.GetUserTimeZoneAsync(User.Id, Arg.Any<CancellationToken>())
                .Returns(TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo"));
            Flags.GetEnabledKeysForUserAsync(User.Id, Arg.Any<CancellationToken>()).Returns(Array.Empty<string>());
            var users = Substitute.For<IGenericRepository<User>>();
            users.GetByIdAsync(User.Id, Arg.Any<CancellationToken>()).Returns(User);
            _habit = Habit.Create(new HabitCreateParams(User.Id, "Read", FrequencyUnit.Day, 1, Day.AddDays(-10),
                CreatedAtUtc: new(2025, 10, 1, 0, 0, 0, DateTimeKind.Utc))).Value;
            if (hasLogs) _habit.Log(Day.AddDays(-2), advanceDueDate: false);
            Habits.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>()).Returns(new[] { _habit });
            var gate = Substitute.For<IPayGateService>();
            gate.CanUseRetrospective(User.Id, Arg.Any<CancellationToken>()).Returns(Result.Success());
            gate.CanUseDailySummary(User.Id, Arg.Any<CancellationToken>()).Returns(Result.Success());
            var summaryLogs = Substitute.For<IHabitSummaryLogReader>();
            summaryLogs.ReadAsync(Arg.Any<IReadOnlyCollection<HabitSummaryLogWindow>>(), Arg.Any<CancellationToken>())
                .Returns(new[] { new HabitSummaryLogFact(_habit.Id, Day.AddDays(-2), 1) });
            var summary = new GetDailySummaryQueryHandler(Habits, users, Substitute.For<IGenericRepository<HabitLog>>(),
                summaryLogs, gate, Substitute.For<ISummaryService>(), _cache);
            Mediator.Send(Arg.Any<GetDailySummaryQuery>(), Arg.Any<CancellationToken>())
                .Returns(call => summary.Handle(call.Arg<GetDailySummaryQuery>(), call.Arg<CancellationToken>()));
            var retrospective = new GetRetrospectiveQueryHandler(Habits, gate, Narratives,
                Substitute.For<IUserStreakService>(), Dates, _cache);
            Mediator.Send(Arg.Any<GetRetrospectiveQuery>(), Arg.Any<CancellationToken>())
                .Returns(call => retrospective.Handle(call.Arg<GetRetrospectiveQuery>(), call.Arg<CancellationToken>()));
            Mediator.Send(Arg.Any<GetCalendarEventsQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success(
                new List<CalendarEventItem> { new("event", "Lesson", null, "2030-10-14", "09:00", "10:00", false,
                    null, [], Day.ToDateTime(new(12, 0), DateTimeKind.Utc), EndUtc: Day.ToDateTime(new(13, 0), DateTimeKind.Utc)) }));
            _goal = Goal.Create(new Goal.CreateGoalParams(User.Id, "Learn", 10, "lessons", Deadline: Day.AddDays(-1))).Value;
            var goals = Substitute.For<IGenericRepository<Goal>>();
            goals.FindAsync(Arg.Any<Expression<Func<Goal, bool>>>(),
                Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(), Arg.Any<CancellationToken>()).Returns(new[] { _goal });
            var scheduleLogs = Substitute.For<IHabitScheduleLogReader>();
            scheduleLogs.ReadDaysAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
                Arg.Any<CancellationToken>()).Returns(new[] { new HabitScheduleLogDay(_habit.Id, Day.AddDays(-2), 1, 0, true) });
            scheduleLogs.ReadResolvedDueDateIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
                .Returns(new HashSet<Guid>());
            (Kind, Tool) = trigger switch
            {
                ProactiveTrigger.RestartAfterMiss => ("daySummary", new GetDailySummaryTool(Mediator, Dates)),
                ProactiveTrigger.BusyCalendarDay => ("calendar", new GetCalendarOverviewTool(Mediator)),
                ProactiveTrigger.GoalBehind => ("goalList", new GoalReviewTool(goals, Dates, Substitute.For<IGoalProgressReadSyncer>())),
                ProactiveTrigger.WeeklyReview => ("periodInsight", new GetRetrospectiveTool(Mediator, Dates)),
                _ => ("habitList", (IAiTool)new QueryHabitsTool(Habits, users, Dates, scheduleLogs))
            };
            Client = new(Locale: locale, SupportsParts: true, PartKinds: [Kind], SupportsHabitListCard: true,
                SupportsHabitListDoneStatus: true, SupportsGoalListCard: true, SupportsDaySummaryCard: true,
                SupportsPeriodInsightCard: true, SupportsCalendarCard: true, SupportsDatedHabitListCard: true);
            var facts = new ProactiveFacts(trigger, ["Saved title"], GoalId: _goal.Id,
                PeriodFrom: Day.AddDays(-7), PeriodTo: Day.AddDays(-1));
            Item = ProactiveItem.Create(User.Id, trigger, Day, JsonSerializer.Serialize(facts),
                locale == "pt-BR" ? "Confira seus hábitos." : "Check your habits.", "Action", "Prompt",
                Day.ToDateTime(new(23, 0), DateTimeKind.Utc), trigger == ProactiveTrigger.GoalBehind ? _goal.Id : null);
            var engine = Substitute.For<IProactiveEngine>();
            engine.GetCurrentAsync(User.Id, Arg.Any<CancellationToken>()).Returns(Item);
            Blocks = new(Dates, Tokens, users);
            Executor = ReadBlockScopeTests.CreateExecutor(Tool, Blocks);
            Handler = new(engine, Executor, Flags);
        }

        public void AssertData(object block)
        {
            switch (block)
            {
                case HabitListCard card:
                    card.Date.Should().Be(Day);
                    card.Items.Should().ContainSingle().Which.Id.Should().Be(_habit.Id.ToString());
                    card.Items[0].Title.Should().Be(_habit.Title);
                    break;
                case DaySummaryCard card:
                    card.Date.Should().Be(Day.AddDays(-1));
                    card.Due.Should().Be(1);
                    card.Done.Should().Be(0);
                    card.Rows.Should().ContainSingle().Which.Id.Should().Be(_habit.Id.ToString());
                    break;
                case GoalListCard card:
                    card.Items.Should().ContainSingle().Which.Id.Should().Be(_goal.Id.ToString());
                    card.Items[0].Target.Should().Be(_goal.TargetValue);
                    break;
                case CalendarCard card:
                    card.Events.Should().ContainSingle().Which.Id.Should().Be("event");
                    card.Events[0].Start.Should().StartWith("2030-10-14T12:00:00");
                    break;
                case PeriodInsightCard card:
                    card.DateFrom.Should().Be(Day.AddDays(-7));
                    card.DateTo.Should().Be(Day.AddDays(-1));
                    card.TotalCompletions.Should().Be(1);
                    card.TotalScheduled.Should().Be(7);
                    card.Narrative.Should().Be(new RetrospectiveNarrative("", "", "", ""));
                    break;
                default: throw new InvalidOperationException("Unexpected proactive block.");
            }
        }

        public void Dispose() => _cache.Dispose();
    }
}
