using Orbit.Domain.Enums;
using System.Linq.Expressions;
using System.Text.Json;
using Orbit.Application.Chat.Models;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Common;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Fact]
    public async Task HistoricalQuery_BlockUsesActualFilteredRows()
    {
        SetupUserAndPayGate();
        var past = Today.AddDays(-7);
        var old = Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, UserId, "Historical task", null, null, past)).Value;
        var live = CreateHabit("Live task");
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(), Arg.Any<CancellationToken>())
            .Returns(call => new[] { old, live }.Where(call.ArgAt<Expression<Func<Habit, bool>>>(0).Compile()).ToList());
        var tool = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        SetupAiResponse(ToolResponse(tool.Name, "historical_call", "{\"date\":\"" + past.ToString("yyyy-MM-dd") + "\",\"include_overdue\":false}"));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Orbit.Domain.Common.Result.Success(new AiResponse { TextMessage = "[[orbit:habits:today]]" }));

        var result = await CreateHandler(tool).Handle(new(UserId, "Show that past day",
            ClientContext: new AgentClientContext(SupportsHabitListCard: true, SupportsHabitListDoneStatus: true, SupportsDatedHabitListCard: true)), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.HabitList.Should().NotBeNull();
        result.Value.HabitList!.Items.Select(item => item.Title).Should().Equal(old.Title);
        result.Value.HabitList.Date.Should().Be(past);
        result.Value.HabitList.Scope.Should().Be("date");
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultipleHabitQueries_DoNotBindAnUnqualifiedDirectiveToTheLastRead(bool hasText)
    {
        SetupUserAndPayGate();
        var tool = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        SetupAiResponse(new AiResponse
        {
            ToolCalls = [
                new("query_habits", "first", ParseArguments("{\"date\":\"2026-04-01\"}")),
                new("query_habits", "second", ParseArguments("{\"date\":\"2026-04-02\"}"))],
            ConversationContext = TestConversationContext
        });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Orbit.Domain.Common.Result.Success(new AiResponse
            {
                TextMessage = hasText ? "Here are the requested days.\n[[orbit:habits:today]]" : "[[orbit:habits:today]]"
            }));

        var result = await CreateHandler(tool).Handle(new(UserId, "Show two past days",
            ClientContext: new AgentClientContext(SupportsHabitListCard: true)), default);

        result.IsSuccess.Should().BeTrue();
        if (hasText)
        {
            result.Value.AiMessage.Should().Be("Here are the requested days.");
            result.Value.HabitList.Should().BeNull();
            result.Value.Operations!.Count(operation => operation.OperationId == "query_habits").Should().Be(2);
        }
        else
            result.Value.AiMessage.Should().Be("I couldn't prepare an answer. Please try again.");
        await _operationExecutor.Received(2).ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(request =>
            request.OperationId == "query_habits"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MetricsRead_DoesNotCreateRecapOrReferral()
    {
        SetupUserAndPayGate();
        SetupProgress(Metrics(totalScheduled: 1));
        SetupAiResponse(new AiResponse { TextMessage = "[[orbit:metrics]]" });
        await CreateHandler().Handle(new(UserId, "My week",
            ClientContext: new AgentClientContext(SupportsMetricsCard: true)), default);
        await _mediator.DidNotReceive().Send(Arg.Any<Orbit.Application.Gamification.Queries.GetRecapQuery>(), Arg.Any<CancellationToken>());
        await _goalProgressReadSyncer.DidNotReceiveWithAnyArgs().ComputeFreshValuesAsync(default, default, default);
        await _goalRepo.Received(1).FindAsync(Arg.Any<Expression<Func<Goal, bool>>>(),
            null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadTurn_LoadsTemplateNamesWithoutChecklistItems()
    {
        SetupUserAndPayGate();
        var template = ChecklistTemplate.Create(UserId, "Morning routine", ["Drink water", "Stretch"]).Value;
        _checklistTemplateRepo.FindAsync(Arg.Any<Expression<Func<ChecklistTemplate, bool>>>(), Arg.Any<CancellationToken>())
            .Returns([template]);
        _checklistTemplateRepo.ProjectAsync(Arg.Any<Expression<Func<ChecklistTemplate, bool>>>(),
                Arg.Any<Func<IQueryable<ChecklistTemplate>, IQueryable<string>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<IQueryable<ChecklistTemplate>, IQueryable<string>>>(1)(
                new[] { template }.Where(call.ArgAt<Expression<Func<ChecklistTemplate, bool>>>(0).Compile()).AsQueryable()).ToList());
        SetupAiResponse(new AiResponse { TextMessage = "Here are your habits." });

        var result = await CreateHandler().Handle(new(UserId, "Show my habits"), default);

        result.IsSuccess.Should().BeTrue();
        _catalogService.Received(1).BuildDynamicSupplement(Arg.Is<AgentContextSnapshot>(snapshot =>
            snapshot.ChecklistTemplateNames != null && snapshot.ChecklistTemplateNames.SequenceEqual(new[] { template.Name })));
        await _checklistTemplateRepo.DidNotReceiveWithAnyArgs().FindAsync(default!, default);
    }

    [Fact]
    public async Task MultipleReadReferencesKeepTheirOwnAbsoluteQuery()
    {
        SetupUserAndPayGate();
        var tool = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var handler = CreateHandler(tool);
        var tokens = new Orbit.Infrastructure.Services.ReadQueryTokenService(Microsoft.Extensions.Options.Options.Create(
            new Orbit.Infrastructure.Configuration.JwtSettings { SecretKey = "test-read-query-signing-key-with-32-bytes", Issuer = "test", Audience = "test" }));
        var blocks = new ReadBlockFactory(_userDateService, tokens, _userRepo);
        var executor = Orbit.Application.Tests.Chat.ReadBlockScopeTests.CreateExecutor(tool, blocks);
        _operationExecutor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(c => executor.ExecuteAsync(c.Arg<AgentExecuteOperationRequest>(), c.Arg<CancellationToken>()));
        SetupAiResponse(new AiResponse
        {
            ToolCalls = [new("query_habits", "first", ParseArguments("{\"date\":\"2026-04-01\"}")),
                new("query_habits", "second", ParseArguments("{\"date\":\"2026-04-02\"}"))],
            ConversationContext = TestConversationContext
        });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Orbit.Domain.Common.Result.Success(new AiResponse { TextMessage = "[[orbit:read:first]]\n[[orbit:read:second]]" }));
        var result = await handler.Handle(new(UserId, "Show both days", ClientContext: new AgentClientContext(
            SupportsParts: true, PartKinds: ["habitList"], SupportsHabitListCard: true, SupportsDatedHabitListCard: true)), default);
        var cards = result.Value.Parts!.OfType<ChatBlockPart>().Select(p => (HabitListCard)p.Block).ToList();
        cards.Should().HaveCount(2);
        cards.Select(c => c.Date).Should().Equal(new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 2));
        cards[0].Read!.QueryId.Should().NotBe(cards[1].Read!.QueryId);
        await _aiIntentService.Received(1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Is<IReadOnlyList<AiToolCallResult>>(r => r[0].ReadDirective == "[[orbit:read:first]]" && r[1].ReadDirective == "[[orbit:read:second]]"),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task DatedReadNegotiation_PreservesLegacyFinalAndParts(bool parts, bool reference, bool supports)
    {
        SetupUserAndPayGate();
        var past = Today.AddDays(-7);
        var old = Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, UserId, "Historical task", null, null, past)).Value;
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(), Arg.Any<CancellationToken>())
            .Returns(call => new[] { old }.Where(call.ArgAt<Expression<Func<Habit, bool>>>(0).Compile()).ToList());
        var tool = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        SetupAiResponse(ToolResponse(tool.Name, "dated", JsonSerializer.Serialize(new { date = past.ToString("yyyy-MM-dd") })));
        ContinueWithReadDirective(reference ? "[[orbit:read:dated]]" : "[[orbit:habits:today]]");

        var result = await handler.Handle(new(UserId, "Show the past day", ClientContext: new(
            SupportsHabitListCard: true, SupportsParts: parts, PartKinds: ["habitList"], SupportsDatedHabitListCard: supports)), default);

        result.IsSuccess.Should().BeTrue();
        if (supports)
        {
            var card = parts ? (HabitListCard)result.Value.Parts!.OfType<ChatBlockPart>().Single().Block : result.Value.HabitList!;
            card.Date.Should().Be(past);
            card.Scope.Should().Be("date");
            card.Items.Single().Title.Should().Be("Historical task");
            return;
        }
        result.Value.HabitList.Should().BeNull("dated interactions require their own explicit capability");
        result.Value.Parts?.OfType<ChatBlockPart>().Should().BeEmpty();
        result.Value.AiMessage.Should().Contain("Historical task").And.Contain(past.ToString("yyyy-MM-dd"));
        using var final = JsonDocument.Parse(ChatStreamEvent.Final(result.Value).ToJson());
        var response = final.RootElement.GetProperty("response");
        response.GetProperty("habitList").ValueKind.Should().Be(JsonValueKind.Null);
        if (parts)
            response.GetProperty("parts").EnumerateArray().Should().OnlyContain(part => part.GetProperty("type").GetString() == "text");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task HistoryReadNegotiation_PreservesLegacyFinalAndParts(bool parts, bool supports)
    {
        SetupUserAndPayGate();
        var day = Today.AddDays(-3);
        _mediator.Send(Arg.Any<GetHabitLogsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<HabitLogResponse>>([new(Guid.NewGuid(), day, 1, DateTime.UnixEpoch)]));
        var tool = new GetHabitHistoryTool(_mediator);
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        SetupAiResponse(ToolResponse(tool.Name, "history", JsonSerializer.Serialize(new
        { habit_id = Guid.NewGuid(), date_from = day.ToString("yyyy-MM-dd"), date_to = Today.ToString("yyyy-MM-dd") })));
        ContinueWithReadDirective("[[orbit:read:history]]");

        var result = await handler.Handle(new(UserId, "Show history", ClientContext: new(
            SupportsRecordListCard: true, SupportsParts: parts, PartKinds: ["recordList"], SupportsHabitHistoryCard: supports)), default);

        result.IsSuccess.Should().BeTrue();
        if (supports)
        {
            var card = parts ? (RecordListCard)result.Value.Parts!.OfType<ChatBlockPart>().Single().Block : result.Value.RecordLists!.Single();
            card.Kind.Should().Be("habitHistory");
            card.Items.Single().LogDate.Should().Be(day);
            return;
        }
        result.Value.RecordLists.Should().BeNullOrEmpty("recordList alone does not advertise the habitHistory subtype");
        result.Value.Parts?.OfType<ChatBlockPart>().Should().BeEmpty();
        result.Value.AiMessage.Should().Contain(day.ToString("yyyy-MM-dd")).And.Contain("1");
        using var final = JsonDocument.Parse(ChatStreamEvent.Final(result.Value).ToJson());
        var response = final.RootElement.GetProperty("response");
        response.GetProperty("recordLists").ValueKind.Should().Be(JsonValueKind.Null);
        if (parts)
            response.GetProperty("parts").EnumerateArray().Should().OnlyContain(part => part.GetProperty("type").GetString() == "text");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AmbiguousHabitRead_RetainsIndependentGoal(bool parts)
    {
        SetupUserAndPayGate();
        var habits = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var goals = new QueryGoalsTool(_goalRepo, _userDateService, _goalProgressReadSyncer);
        var handler = CreateHandler(habits, goals);
        UseProductionReadExecutor(habits, goals);
        SetupAiResponse(new AiResponse
        {
            ToolCalls = [new("query_habits", "first", ParseArguments("{\"date\":\"2026-04-01\"}")),
                new("query_habits", "second", ParseArguments("{\"date\":\"2026-04-02\"}")),
                new("query_goals", "goals", ParseArguments("{}"))],
            ConversationContext = TestConversationContext
        });
        ContinueWithReadDirective("[[orbit:habits:today]]\n[[orbit:goals]]");

        var result = await handler.Handle(new(UserId, "Show days and goals", ClientContext: new(
            SupportsHabitListCard: true, SupportsGoalListCard: true, SupportsParts: parts,
            PartKinds: ["habitList", "goalList"])), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.HabitList.Should().BeNull();
        if (parts)
            result.Value.Parts!.OfType<ChatBlockPart>().Select(part => part.Kind).Should().Equal("goalList");
        else
            result.Value.GoalList.Should().NotBeNull("only the ambiguous habit directive must be suppressed");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FocusedAnalytics_NarrativeOutageRetainsExactAnswer(bool supports)
    {
        SetupUserAndPayGate();
        var period = new Orbit.Domain.Models.PeriodHabitMetrics(new(Today.AddDays(-6), Today), 3, 2, 1, 7, 100m / 7, 0, 1);
        _mediator.Send(Arg.Any<GetPeriodAnalyticsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new PeriodAnalyticsResponse(new("positiveLogRecords", "records", 3, period,
                new("day", [])), null, "revision")));
        var tool = new GetPeriodAnalyticsTool(_mediator);
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        SetupAiResponse(ToolResponse(tool.Name, "total", JsonSerializer.Serialize(new
        { date_from = Today.AddDays(-6).ToString("yyyy-MM-dd"), date_to = Today.ToString("yyyy-MM-dd"), metric = "positiveLogRecords" })));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<AiResponse>(Orbit.Application.Common.ErrorMessages.AiUnavailable));

        var result = await handler.Handle(new(UserId, "Count my completions", ClientContext: new(
            SupportsParts: supports, PartKinds: ["periodTotal"])), default);

        result.IsSuccess.Should().BeTrue("the successful count query does not require AI narrative");
        if (supports)
            result.Value.Parts!.OfType<ChatBlockPart>().Single().Block.Should().BeOfType<PeriodTotalCard>()
                .Which.Value.Should().Be(3);
        else
            result.Value.AiMessage.Should().Contain("positiveLogRecords").And.Contain("3").And.Contain("records")
                .And.Contain(Today.AddDays(-6).ToString("yyyy-MM-dd")).And.Contain(Today.ToString("yyyy-MM-dd"));
    }

    private void ContinueWithReadDirective(string directive) =>
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = directive }));

    private ReadBlockFactory UseProductionReadExecutor(params Orbit.Application.Chat.Tools.IAiTool[] tools)
    {
        var tokens = new Orbit.Infrastructure.Services.ReadQueryTokenService(Microsoft.Extensions.Options.Options.Create(
            new Orbit.Infrastructure.Configuration.JwtSettings { SecretKey = "test-read-query-signing-key-with-32-bytes", Issuer = "test", Audience = "test" }));
        var blocks = new ReadBlockFactory(_userDateService, tokens, _userRepo);
        var executors = tools.ToDictionary(tool => tool.Name,
            tool => Orbit.Application.Tests.Chat.ReadBlockScopeTests.CreateExecutor(tool, blocks));
        _operationExecutor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(c => executors[c.Arg<AgentExecuteOperationRequest>().OperationId]
                .ExecuteAsync(c.Arg<AgentExecuteOperationRequest>(), c.Arg<CancellationToken>()));
        return blocks;
    }

}
