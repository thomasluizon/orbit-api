using Orbit.Infrastructure.Services;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Calendar.Queries;
using Orbit.Application.Common;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.FeatureExplanations;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Fact]
    public async Task G6_ModelAdviceKeepsTheFullToolRegistryAndItsOwnText()
    {
        SetupUserAndPayGate();
        EnableComposition();
        var read = CompositionTool("query_habits");
        var write = FakeTool("create_habit");
        const string advice = "Choose a small first step.";
        SetupAiResponse(new AiResponse { TextMessage = advice });

        var result = await CreateHandler(read, write).Handle(new(UserId,
            "Help me make walking easier to start tomorrow.", ClientContext: new(SupportsParts: true)), default);

        result.Value.AiMessage.Should().Be(advice);
        result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatTextPart>().Which.Text.Should().Be(advice);
        await _aiIntentService.Received(1).SendWithToolsAsync(Arg.Is<AiToolRequest>(request =>
            request.ToolDeclarations.Count == 2), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
        await _operationExecutor.DidNotReceive().ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("composition-offline-en-r1")]
    [InlineData("composition-error-pt-BR-r1")]
    public async Task G6_CapturedMonthPlanIsNeverReroutedToAnotherTool(string capture)
    {
        SetupUserAndPayGate();
        EnableComposition();
        _userDateService.GetUserTimeZoneAsync(UserId, Arg.Any<CancellationToken>()).Returns(TimeZoneInfo.Utc);
        var month = G6HabitMonthTool();
        var calendar = G6RecoveryCalendarTool();
        var handler = CreateHandler(month, calendar);
        UseProductionReadExecutor(month, calendar);
        SetupAiResponse(CapturedCompositionPlan(capture));

        var result = await handler.Handle(new(UserId, "Show my calendar while its provider is temporarily unavailable.",
            ClientContext: new(SupportsParts: true, PartKinds: ["calendar"], SupportsCalendarCard: true)), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Parts!.OfType<ChatBlockPart>().Should().BeEmpty();
        result.Value.AiMessage.Should().NotBeNullOrWhiteSpace();
        await _mediator.Received(1).Send(Arg.Any<GetHabitMonthQuery>(), Arg.Any<CancellationToken>());
        await _mediator.DidNotReceive().Send(Arg.Any<GetCalendarEventsQuery>(), Arg.Any<CancellationToken>());
        await AssertNoCompositionContinuation();
    }

    [Fact]
    public async Task G6_ExplicitRecoveryReadKeepsItsTypedState()
    {
        SetupUserAndPayGate();
        EnableComposition();
        _userDateService.GetUserTimeZoneAsync(UserId, Arg.Any<CancellationToken>()).Returns(TimeZoneInfo.Utc);
        var calendar = G6RecoveryCalendarTool();
        var handler = CreateHandler(calendar);
        UseProductionReadExecutor(calendar);
        SetupAiResponse(ToolResponse(calendar.Name, "recovery", "{\"orbit_read_complete\":true}"));

        var result = await handler.Handle(new(UserId, "Show the connected service.", ClientContext: new(SupportsParts: true,
            PartKinds: ["calendar"], SupportsCalendarCard: true)), default);

        result.IsSuccess.Should().BeTrue();
        var block = result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>().Subject;
        block.Kind.Should().Be("calendar");
        var read = ReadBlockTelemetry.Describe(block.Block).Read!;
        read.Completeness.Should().Be("partial");
        read.Recovery.Should().Be("reconnect");
        result.Value.AiMessage.Should().BeNullOrEmpty();
        await AssertNoCompositionContinuation();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task G6_MetricsRequiresTheModelsStructuredCall(bool emitsMetrics)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var habit = CreateHabit("Water");
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(), Arg.Any<CancellationToken>()).Returns([habit]);
        var lookup = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        _mediator.Send(Arg.Any<GetHabitMetricsQuery>(), Arg.Any<CancellationToken>()).Returns(call => Result.Success(
            new HabitMetrics(3, 8, 50, 60, 20, Today, call.Arg<GetHabitMetricsQuery>().HabitId, "Water")));
        var metrics = new GetHabitMetricsTool(_mediator);
        var handler = CreateHandler(lookup, metrics);
        UseProductionReadExecutor(lookup, metrics);
        SetupAiResponse(emitsMetrics
            ? ToolResponse(lookup.Name, "lookup", "{\"search\":\"Water\",\"orbit_read_complete\":false}")
            : CapturedCompositionPlan("composition-metrics-en-r3"));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(emitsMetrics
                ? ToolResponse(metrics.Name, "metrics", JsonSerializer.Serialize(new { habit_id = habit.Id, orbit_read_complete = true }))
                : new AiResponse()));

        var result = await handler.Handle(new(UserId, "Show completion metrics for Water.", ClientContext: new(
            SupportsParts: true, PartKinds: ["habitList", "metrics"], SupportsHabitListCard: true, SupportsMetricsCard: true)), default);

        result.IsSuccess.Should().BeTrue();
        var block = result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>().Subject;
        block.Kind.Should().Be(emitsMetrics ? "metrics" : "habitList");
        result.Value.AiMessage.Should().BeNullOrEmpty();
        await _mediator.Received(emitsMetrics ? 1 : 0).Send(Arg.Any<GetHabitMetricsQuery>(), Arg.Any<CancellationToken>());
        await _aiIntentService.Received(emitsMetrics ? 1 : 0).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task G6_CapturedPlanReadStaysAnAccountBlock()
    {
        SetupUserAndPayGate();
        EnableComposition();
        var subscription = CompositionTool("get_subscription_overview");
        var help = new DescribeFeatureTool(new FeatureExplanationService(), new AppSurfaceCatalog());
        var handler = CreateHandler(subscription, help);
        UseProductionReadExecutor(subscription, help);
        SetupAiResponse(CapturedCompositionPlan("composition-plan-en-r1"));

        var result = await handler.Handle(new(UserId, "Where can I check the features available on my plan?", ClientContext: new(
            SupportsParts: true, PartKinds: ["accountRows"], SupportsAccountRowsCard: true)), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>()
            .Which.Block.Should().BeOfType<AccountRowsCard>().Which.Kind.Should().Be("plan");
        result.Value.AiMessage.Should().BeNullOrEmpty();
        await _operationExecutor.DidNotReceive().ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(call =>
            call.OperationId == "describe_feature"), Arg.Any<CancellationToken>());
        await AssertNoCompositionContinuation();
    }

    [Fact]
    public async Task G6_CapturedReadIsExecutedWithoutReplacingItWithAdvice()
    {
        SetupUserAndPayGate();
        EnableComposition();
        var lookup = CompositionTool("query_habits");
        var handler = CreateHandler(lookup);
        UseProductionReadExecutor(lookup);
        SetupAiResponse(CapturedCompositionPlan("composition-coaching-advice-en-r1"));
        ContinueWithReadDirective("Choose a small first step.");

        var result = await handler.Handle(new(UserId, "Help me make walking easier to start tomorrow.",
            ClientContext: new(SupportsParts: true, PartKinds: ["habitList"], SupportsHabitListCard: true)), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>().Which.Kind.Should().Be("habitList");
        await _operationExecutor.Received(1).ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(call =>
            call.OperationId == lookup.Name), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task G6_CapturedWriteStillRequiresApproval()
    {
        SetupUserAndPayGate();
        EnableComposition();
        var create = new CreateHabitTool(_userDateService, _mediator);
        var handler = CreateHandler(create);
        var held = HoldReplayWrites(create);
        SetupAiResponse(CapturedCompositionPlan("composition-coaching-advice-pt-BR-r1"));
        ContinueWithReadDirective("Prepared.");

        var result = await handler.Handle(new(UserId, "Me ajude a começar a caminhar com mais facilidade amanhã.",
            ClientContext: new(SupportsParts: true)), default);

        result.IsSuccess.Should().BeTrue();
        held.Should().ContainSingle().Which.OperationId.Should().Be(create.Name);
        result.Value.PendingOperations.Should().ContainSingle();
        result.Value.Actions.Should().NotContain(action => action.Status == ActionStatus.Success);
        result.Value.AiMessage.Should().BeNullOrEmpty();
        await _mediator.DidNotReceive().Send(Arg.Any<Orbit.Application.Habits.Commands.CreateHabitCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task G6_CapturedIncompleteReadsContinueUntilTheModelCompletes()
    {
        SetupUserAndPayGate();
        EnableComposition();
        var habits = CompositionTool("query_habits");
        var goals = CompositionTool("query_goals");
        var handler = CreateHandler(habits, goals);
        UseProductionReadExecutor(habits, goals);
        SetupAiResponse(CapturedCompositionPlan("composition-compound-pt-BR-r2"));
        ContinueWithReadDirective("Unnecessary prose.");

        var result = await handler.Handle(new(UserId, "Mostre meus hábitos e minhas metas", ClientContext: new(SupportsParts: true,
            PartKinds: ["habitList", "goalList"], SupportsHabitListCard: true, SupportsGoalListCard: true)), default);

        result.Value.Parts!.OfType<ChatBlockPart>().Select(block => block.Kind).Should().Equal("habitList", "goalList");
        result.Value.AiMessage.Should().BeNullOrEmpty();
        await _aiIntentService.Received(1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task G6_EmptyPrerequisiteReadContinuesToPendingWrite()
    {
        SetupUserAndPayGate();
        EnableComposition();
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(), Arg.Any<CancellationToken>()).Returns([]);
        var lookup = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var create = new CreateHabitTool(_userDateService, _mediator);
        var handler = CreateHandler(lookup, create);
        UseProductionReadExecutor(lookup);
        var arguments = ParseArguments("{\"title\":\"Walking\",\"frequency_unit\":\"Day\"}");
        var previewer = new PendingOperationChangePreviewer(_habitRepo, _goalRepo, _tagRepo,
            _userDateService, new AiToolRegistry([create]));
        var preview = (await previewer.PreviewAsync(UserId, create.Name, arguments)).Value;
        var pending = new PendingAgentOperation(Guid.NewGuid(), create.Name, create.Name, create.Name,
            AgentRiskClass.Low, AgentConfirmationRequirement.FreshConfirmation, DateTime.UtcNow.AddMinutes(10),
            Changes: preview?.Changes, ChangeTargetCount: preview?.ChangeTargetCount, Items: preview?.Items,
            PreviewFingerprint: preview?.PreviewFingerprint);
        _operationExecutor.ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(call => call.OperationId == create.Name),
            Arg.Any<CancellationToken>()).Returns(new AgentExecuteOperationResponse(
                new AgentOperationResult(create.Name, create.Name, AgentRiskClass.Low,
                    AgentConfirmationRequirement.FreshConfirmation, AgentOperationStatus.PendingConfirmation),
                PendingOperation: pending));
        SetupAiResponse(ToolResponse(lookup.Name, "lookup", "{\"search\":\"Walking\",\"orbit_read_complete\":false}"));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(ToolResponse(create.Name, "create", arguments.GetRawText())),
                Result.Success(new AiResponse()));

        var result = await handler.Handle(new(UserId, "Create Walking if it does not exist.",
            ClientContext: new(SupportsParts: true, PartKinds: ["habitList"], SupportsHabitListCard: true)), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.PendingOperations.Should().ContainSingle().Which.Id.Should().Be(pending.Id);
        result.Value.PendingOperations![0].Items.Should().ContainSingle().Which.EntityName.Should().Be("Walking");
        result.Value.Actions.Should().NotContain(action => action.Status == ActionStatus.Success);
        await _aiIntentService.Received(1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Is<IReadOnlyList<AiToolCallResult>>(results => results.Count == 1
                && results[0].Name == lookup.Name && results[0].Success),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
        await _mediator.DidNotReceive().Send(Arg.Any<Orbit.Application.Habits.Commands.CreateHabitCommand>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task G6_CompleteEmptyReadRoundNeedsNoNarrative()
    {
        SetupUserAndPayGate();
        EnableComposition();
        var habits = CompositionTool("query_habits", empty: true);
        var handler = CreateHandler(habits);
        UseProductionReadExecutor(habits);
        SetupAiResponse(ToolResponse(habits.Name, "empty", "{\"orbit_read_complete\":true}"));

        var result = await handler.Handle(new(UserId, "Show the available entries.", ClientContext: new(SupportsParts: true,
            PartKinds: ["habitList"], SupportsHabitListCard: true)), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>()
            .Which.Block.Should().BeOfType<HabitListCard>().Which.Items.Should().BeEmpty();
        result.Value.RelatedSurfaces.Should().Contain("today");
        await AssertNoCompositionContinuation();
    }

    [Theory]
    [InlineData(50, "partial", 50)]
    [InlineData(100, "complete", 51)]
    public async Task G6_PartialFixtureKeepsTruthfulPagination(int limit, string completeness, int count)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var habits = Enumerable.Range(0, 51).Select(index => CreateHabit("Habit " + index)).ToArray();
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(), Arg.Any<CancellationToken>()).Returns(habits);
        var tool = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        SetupAiResponse(HonestyReplay(limit == 100 ? "g6rerun-composition-partial-en-r1" : "g6rerun-composition-partial-en-r3"));

        var schema = JsonSerializer.SerializeToElement(tool.GetParameterSchema()).GetProperty("properties").GetProperty("limit");
        schema.GetProperty("description").GetString().Should().Contain("Use 50 unless").And.Contain("all habits");

        var result = await handler.Handle(new(UserId, "Show all my habits, including whether the list is incomplete.",
            ClientContext: new(SupportsParts: true, PartKinds: ["habitList"], SupportsHabitListCard: true)), default);

        var card = result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>()
            .Which.Block.Should().BeOfType<HabitListCard>().Subject;
        card.Items.Should().HaveCount(count);
        card.Read!.Completeness.Should().Be(completeness);
        card.Read.TotalCount.Should().Be(51);
        (card.Read.NextCursor is not null).Should().Be(limit == 50);
        card.Read.Scope.GetProperty("limit").GetInt32().Should().Be(limit);
        card.Read.RefreshToken.Should().NotBeNullOrWhiteSpace();
        AssertStoredComposition(result.Value);
        await AssertNoCompositionContinuation();
    }

    private GetHabitMonthTool G6HabitMonthTool()
    {
        var calendar = new GetCalendarMonthQueryHandler(_habitRepo, _userDateService, _unitOfWork, repeatReminderReader: Substitute.For<IRepeatReminderReader>());
        _mediator.Send(Arg.Any<GetCalendarMonthQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => calendar.Handle(call.Arg<GetCalendarMonthQuery>(), call.Arg<CancellationToken>()));
        var month = new GetHabitMonthQueryHandler(_mediator, _userDateService);
        _mediator.Send(Arg.Any<GetHabitMonthQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => month.Handle(call.Arg<GetHabitMonthQuery>(), call.Arg<CancellationToken>()));
        return new GetHabitMonthTool(_mediator);
    }

    private GetCalendarOverviewTool G6RecoveryCalendarTool()
    {
        _mediator.Send(Arg.Any<GetCalendarEventsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<List<CalendarEventItem>>(ErrorMessages.CalendarNotConnected));
        _mediator.Send(Arg.Any<GetCalendarAutoSyncStateQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure<CalendarAutoSyncStateResponse>("Plan unavailable"));
        _mediator.Send(Arg.Any<GetCalendarSyncSuggestionsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new List<CalendarSyncSuggestionItem>()));
        _mediator.Send(Arg.Any<GetUserCalendarsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new List<UserCalendarItem>()));
        return new GetCalendarOverviewTool(_mediator);
    }

    private async Task AssertNoCompositionContinuation() => await _aiIntentService.DidNotReceive().ContinueWithToolResultsAsync(
        Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
        Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());

    private static AiResponse CapturedCompositionPlan(string name)
    {
        using var capture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "CompositionFailures", name + ".json")));
        return new AiResponse
        {
            ToolCalls = capture.RootElement.GetProperty("toolCalls").EnumerateArray().Select((call, index) =>
                new AiToolCall(call.GetProperty("name").GetString()!, "capture_" + index,
                    WithCalendarWindow(call.GetProperty("name").GetString()!, call.GetProperty("arguments")))).ToArray(),
            ConversationContext = TestConversationContext
        };
    }

    private static JsonElement WithCalendarWindow(string tool, JsonElement arguments)
    {
        if (tool != "get_habit_month") return arguments.Clone();
        var node = System.Text.Json.Nodes.JsonNode.Parse(arguments.GetRawText())!.AsObject();
        node["window"] = "month";
        return JsonSerializer.SerializeToElement(node);
    }
}
