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
    [Theory]
    [InlineData("composition-offline-en-r1", "Show my calendar while its provider is temporarily unavailable.")]
    [InlineData("composition-error-pt-BR-r1", "Mostre meu calendário mesmo se a conexão precisar de recuperação.")]
    public async Task G6_CapturedProviderCalendarRequestUsesCalendarRead(string capture, string message)
    {
        SetupUserAndPayGate();
        EnableComposition();
        _userDateService.GetUserTimeZoneAsync(UserId, Arg.Any<CancellationToken>()).Returns(TimeZoneInfo.Utc);
        var calendar = G6RecoveryCalendarTool();
        var month = G6HabitMonthTool();
        var handler = CreateHandler(calendar, month);
        UseProductionReadExecutor(calendar, month);
        SetupAiResponse(CapturedCompositionPlan(capture));
        ContinueWithReadDirective("Wrong prose.");

        var result = await handler.Handle(new(UserId, message, ClientContext: new(SupportsParts: true,
            PartKinds: ["calendar"], SupportsCalendarCard: true)), default);

        result.IsSuccess.Should().BeTrue();
        var block = result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>().Subject;
        block.Kind.Should().Be("calendar");
        var read = ReadBlockTelemetry.Describe(block.Block).Read!;
        read.Completeness.Should().Be("partial");
        read.Recovery.Should().Be("reconnect");
        result.Value.AiMessage.Should().BeNullOrEmpty();
        await _mediator.DidNotReceive().Send(Arg.Any<GetHabitMonthQuery>(), Arg.Any<CancellationToken>());
        await AssertNoCompositionContinuation();
    }

    [Fact]
    public async Task G6_CapturedMetricsLookupProducesFocusedMetricsWithoutAnotherModelCall()
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
        SetupAiResponse(CapturedCompositionPlan("composition-metrics-en-r3"));
        ContinueWithReadDirective("No further result.");

        var result = await handler.Handle(new(UserId, $"Show completion metrics for Water ({habit.Id}).", ClientContext: new(
            SupportsParts: true, PartKinds: ["habitList", "metrics"], SupportsHabitListCard: true, SupportsMetricsCard: true)), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>()
            .Which.Block.Should().BeOfType<MetricsCard>().Which.HabitId.Should().Be(habit.Id);
        result.Value.AiMessage.Should().BeNullOrEmpty();
        await AssertNoCompositionContinuation();
    }

    [Fact]
    public async Task G6_CapturedPlanHelpUsesFeatureRulesAndExistingAction()
    {
        SetupUserAndPayGate();
        EnableComposition();
        var subscription = CompositionTool("get_subscription_overview");
        var help = new DescribeFeatureTool(new FeatureExplanationService());
        var handler = CreateHandler(subscription, help);
        UseProductionReadExecutor(subscription, help);
        SetupAiResponse(CapturedCompositionPlan("composition-plan-en-r1"));
        ContinueWithReadDirective("Unexpected continuation.");

        var result = await handler.Handle(new(UserId, "Where can I check the features available on my plan?", ClientContext: new(
            SupportsParts: true, PartKinds: ["accountRows"], SupportsAccountRowsCard: true)), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatTextPart>();
        result.Value.RelatedSurfaces.Should().Contain("subscriptions");
        result.Value.AiMessage.Should().NotBeNullOrWhiteSpace().And.NotContain("\n");
        await _operationExecutor.Received(1).ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(call =>
            call.OperationId == "describe_feature" && call.Arguments.GetProperty("feature_key").GetString() == "paygate"),
            Arg.Any<CancellationToken>());
        await subscription.DidNotReceive().ExecuteAsync(Arg.Any<JsonElement>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await AssertNoCompositionContinuation();
    }

    [Theory]
    [InlineData("composition-coaching-advice-en-r1", "Help me make walking easier to start tomorrow.")]
    [InlineData("composition-coaching-advice-pt-BR-r1", "Me ajude a começar a caminhar com mais facilidade amanhã.")]
    public async Task G6_CapturedAdviceDoesNotBecomeAReadOrWritePreview(string capture, string message)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var lookup = CompositionTool("query_habits");
        var create = FakeTool("create_habit");
        var handler = CreateHandler(lookup, create);
        UseProductionReadExecutor(lookup);
        SetupAiResponse(CapturedCompositionPlan(capture));
        ContinueWithReadDirective("Unexpected continuation.");

        var result = await handler.Handle(new(UserId, message, ClientContext: new(SupportsParts: true,
            PartKinds: ["habitList"], SupportsHabitListCard: true, Locale: capture.Contains("pt-BR") ? "pt-BR" : "en")), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatTextPart>();
        result.Value.AiMessage.Should().NotBeNullOrWhiteSpace().And.NotContain("\n\n");
        await _operationExecutor.DidNotReceive().ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>());
        await AssertNoCompositionContinuation();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task G6_CapturedIndependentReadsDoNotNeedNarrativeContinuation(bool missingSubject)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var habits = CompositionTool("query_habits");
        var goals = CompositionTool("query_goals");
        var handler = CreateHandler(habits, goals);
        UseProductionReadExecutor(habits, goals);
        SetupAiResponse(CapturedCompositionPlan("composition-compound-pt-BR-r2"));
        ContinueWithReadDirective("Unnecessary prose.");

        var result = await handler.Handle(new(UserId, missingSubject ? "Mostre meus hábitos, minhas metas e meu perfil" : "Mostre meus hábitos e minhas metas", ClientContext: new(SupportsParts: true,
            PartKinds: ["habitList", "goalList"], SupportsHabitListCard: true, SupportsGoalListCard: true)), default);

        result.Value.Parts!.OfType<ChatBlockPart>().Select(block => block.Kind).Should().Equal("habitList", "goalList");
        await _aiIntentService.Received(missingSubject ? 1 : 0).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
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
        SetupAiResponse(ToolResponse(tool.Name, "page", JsonSerializer.Serialize(new { limit, orbit_read_complete = true })));

        var result = await handler.Handle(new(UserId, "Show all my habits, including whether the list is incomplete.",
            ClientContext: new(SupportsParts: true, PartKinds: ["habitList"], SupportsHabitListCard: true)), default);

        var card = result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>()
            .Which.Block.Should().BeOfType<HabitListCard>().Subject;
        card.Items.Should().HaveCount(count);
        card.Read!.Completeness.Should().Be(completeness);
        card.Read.TotalCount.Should().Be(51);
        (card.Read.NextCursor is not null).Should().Be(limit == 50);
        await AssertNoCompositionContinuation();
    }

    [Fact]
    public async Task G6_CapturedCalendarSyncDoesNotRepeatItsWindowInProse()
    {
        SetupUserAndPayGate();
        EnableComposition();
        _userDateService.GetUserTimeZoneAsync(UserId, Arg.Any<CancellationToken>()).Returns(TimeZoneInfo.Utc);
        var calendar = CompositionTool("get_calendar_overview");
        var month = G6HabitMonthTool();
        var handler = CreateHandler(calendar, month);
        UseProductionReadExecutor(calendar, month);
        SetupAiResponse(CapturedCompositionPlan("composition-calendar-sync-pt-BR-r2"));

        var result = await handler.Handle(new(UserId, "Mostre meu calendário e o estado da sincronização.",
            ClientContext: new(SupportsParts: true, PartKinds: ["calendar"], SupportsCalendarCard: true, Locale: "pt-BR")), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>().Which.Kind.Should().Be("calendar");
        result.Value.AiMessage.Should().BeNullOrEmpty();
        await AssertNoCompositionContinuation();
    }

    [Theory]
    [InlineData("Help me create a habit that is easier to start.", true)]
    [InlineData("Me ajude a criar um hábito com mais facilidade.", true)]
    [InlineData("Help me make walking easier to start tomorrow.", false)]
    public async Task G6_CoachingKeepsExplicitRecordChangesAvailable(string message, bool offersWrite)
    {
        SetupUserAndPayGate();
        EnableComposition();
        SetupAiResponse(new AiResponse { TextMessage = "Choose a small first step." });

        await CreateHandler(FakeTool("create_habit")).Handle(new(UserId, message,
            ClientContext: new(SupportsParts: true)), default);

        await _aiIntentService.Received(1).SendWithToolsAsync(Arg.Is<AiToolRequest>(request =>
            (request.ToolDeclarations.Count > 0) == offersWrite), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    private GetHabitMonthTool G6HabitMonthTool()
    {
        var calendar = new GetCalendarMonthQueryHandler(_habitRepo, _userDateService, _unitOfWork);
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
            .Returns(Result.Failure<CalendarAutoSyncStateResponse>("PAY_GATE", "Plan unavailable"));
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
                    call.GetProperty("arguments").Clone())).ToArray(),
            ConversationContext = TestConversationContext
        };
    }
}
