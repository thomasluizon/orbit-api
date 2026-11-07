using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.Core;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Tests.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    private const int TokenCeilingEventId = 28;
    private const int ContinuationFailedEventId = 10;

    [Fact]
    public async Task RequestTokenCeiling_InitialRequestBudgetStopsTheToolsAndLogsTheCeiling()
    {
        SetupUserAndPayGate();
        _logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var tool = ReadTool("query_habits", new ToolResult(true));
        SetupAiResponse(ToolResponse(tool.Name, "read_1", "{}") with
        {
            ReportedTokenCount = 60_000,
            RequestBudgetTokenCount = AppConstants.MaxAiRequestTotalTokens + 1
        });

        await CreateHandler(tool).Handle(new ProcessUserChatCommand(UserId, "Read my habits"), default);

        await tool.DidNotReceiveWithAnyArgs().ExecuteAsync(default, default, default);
        await _aiIntentService.DidNotReceiveWithAnyArgs().ContinueWithToolResultsAsync(default!, default!, default, default);
        var ceiling = LoggedState(TokenCeilingEventId).Should().ContainSingle().Subject;
        ceiling["BudgetTokens"].Should().Be((long)AppConstants.MaxAiRequestTotalTokens + 1);
        ceiling["TotalReportedTokens"].Should().Be(60_000L);
        ceiling["ToolIterations"].Should().Be(0);
    }

    [Fact]
    public async Task RequestTokenCeiling_ContinuationThatCrossesTheCeilingLogsItsRound()
    {
        SetupUserAndPayGate();
        _logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var tool = ReadTool("query_habits", new ToolResult(true));
        SetupAiResponse(ToolResponse(tool.Name, "read_1", "{}") with { ReportedTokenCount = 60_000 });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(ToolResponse("query_habits", "read_2", "{}") with { ReportedTokenCount = 60_001 }));

        await CreateHandler(tool).Handle(new ProcessUserChatCommand(UserId, "Read my habits"), default);

        await tool.Received(1).ExecuteAsync(Arg.Any<JsonElement>(), UserId, Arg.Any<CancellationToken>());
        var ceiling = LoggedState(TokenCeilingEventId).Should().ContainSingle().Subject;
        ceiling["TotalReportedTokens"].Should().Be(120_001L);
        ceiling["ToolIterations"].Should().Be(1);
    }

    [Fact]
    public async Task ContinuationFailure_LogsTheProviderErrorAsAWarning()
    {
        SetupUserAndPayGate();
        _logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var tool = ReadTool("query_habits", new ToolResult(true));
        SetupAiResponse(ToolResponse(tool.Name, "read_1", "{}"));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<AiResponse>("The provider is unavailable."));

        await CreateHandler(tool).Handle(new ProcessUserChatCommand(UserId, "Read my habits"), default);

        var failure = _logger.ReceivedCalls().Should().ContainSingle(call => IsLog(call, ContinuationFailedEventId)).Subject;
        ((LogLevel)failure.GetArguments()[0]!).Should().Be(LogLevel.Warning);
        LoggedState(ContinuationFailedEventId).Single()["Error"].Should().Be("The provider is unavailable.");
    }

    [Fact]
    public async Task TerminalReadRound_AFailedReadReachesTheModelAndEndsTheTurn()
    {
        SetupUserAndPayGate();
        EnableComposition();
        var goals = CompositionTool("query_goals");
        var metrics = ReadTool("get_habit_metrics", new ToolResult(false, Error: "Habit not found."));
        var late = CompositionTool("list_tags");
        var handler = CreateHandler(goals, metrics, late);
        UseProductionReadExecutor(goals, metrics, late);
        SetupAiResponse(new AiResponse
        {
            ConversationContext = TestConversationContext,
            ToolCalls =
            [
                new("query_goals", "goals", JsonSerializer.SerializeToElement(new { orbit_read_complete = true })),
                new("get_habit_metrics", "metrics", JsonSerializer.SerializeToElement(new { habit_id = Guid.NewGuid(), orbit_read_complete = true }))
            ]
        });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(ToolResponse("list_tags", "late", "{\"orbit_read_complete\":true}")));

        var result = await handler.Handle(new(UserId, "Show my goals and the Water metrics.", ClientContext: new(
            SupportsParts: true, PartKinds: ["goalList", "metrics"], SupportsGoalListCard: true, SupportsMetricsCard: true)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        await _aiIntentService.Received(1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Is<IReadOnlyList<AiToolCallResult>>(results => results.Any(item => item.Id == "metrics" && !item.Success)),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
        await late.DidNotReceiveWithAnyArgs().ExecuteAsync(default, default, default);
    }

    [Fact]
    public async Task TerminalReadRound_AReadWithoutADeclaredPurposeReachesTheModel()
    {
        SetupUserAndPayGate();
        EnableComposition();
        var habits = CompositionTool("query_habits");
        var handler = CreateHandler(habits);
        UseProductionReadExecutor(habits);
        SetupAiResponse(new AiResponse
        {
            ConversationContext = TestConversationContext,
            ToolCalls =
            [
                new("query_habits", "declared", JsonSerializer.SerializeToElement(new { for_selection = false, orbit_read_complete = true })),
                new("query_habits", "undeclared", JsonSerializer.SerializeToElement(new { search = "Reading", orbit_read_complete = true }))
            ]
        });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Here are your habits." }));

        var result = await handler.Handle(new(UserId, "Show my habits.", ClientContext: new(
            SupportsParts: true, PartKinds: ["habitList", "choice"], SupportsHabitListCard: true)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        await _aiIntentService.Received(1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TerminalReadRound_AFailedWriteBesideAReadEndsWithoutAnotherModelRound()
    {
        SetupUserAndPayGate();
        EnableComposition();
        var goals = CompositionTool("query_goals");
        var write = Substitute.For<IAiTool>();
        write.Name.Returns("delete_habit");
        write.Description.Returns("delete_habit");
        write.IsReadOnly.Returns(false);
        write.GetParameterSchema().Returns(new { type = "object" });
        write.ExecuteAsync(Arg.Any<JsonElement>(), UserId, Arg.Any<CancellationToken>())
            .Returns(new ToolResult(false, Error: "Delete failed."));
        var handler = CreateHandler(goals, write);
        UseProductionReadExecutor(goals, write);
        SetupAiResponse(new AiResponse
        {
            ConversationContext = TestConversationContext,
            ToolCalls =
            [
                new("query_goals", "goals", JsonSerializer.SerializeToElement(new { orbit_read_complete = true })),
                new(write.Name, "write", JsonSerializer.SerializeToElement(new { orbit_read_complete = true }))
            ]
        });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Deleted." }));

        var result = await handler.Handle(new(UserId, "Show my goals and delete the Water habit.", ClientContext: new(
            SupportsParts: true, PartKinds: ["goalList"], SupportsGoalListCard: true)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        await _aiIntentService.DidNotReceiveWithAnyArgs().ContinueWithToolResultsAsync(default!, default!, default, default);
        result.Value.AiMessage.Should().NotContain("Deleted.");
    }

    [Fact]
    public async Task TerminalWriteRound_AReadBesideThePreviewReachesTheModel()
    {
        SetupUserAndPayGate();
        EnableComposition();
        _mediator.Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _unitOfWork.PassThroughTransactions<AgentExecuteOperationResponse>();
        _pendingOperationStore.Revise(UserId, Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<PendingOperationChangePreview?>()).Returns(true);
        IAiTool create = new CreateHabitTool(_userDateService, _mediator);
        var goals = CompositionTool("query_goals");
        var handler = CreateHandler(create, goals);
        var catalog = new AgentCatalogService([create]);
        var policy = Substitute.For<IAgentPolicyEvaluator>();
        policy.EvaluateAsync(Arg.Any<AgentPolicyEvaluationContext>()).Returns(call => new AgentPolicyDecision(
            AgentPolicyDecisionStatus.ConfirmationRequired, catalog.GetCapability(call.Arg<AgentPolicyEvaluationContext>().CapabilityId)!,
            PendingOperation: new(Guid.NewGuid(), call.Arg<AgentPolicyEvaluationContext>().CapabilityId, "Create habit", "Preview", AgentRiskClass.Low,
                AgentConfirmationRequirement.FreshConfirmation, TimeProvider.System.GetUtcNow().UtcDateTime.AddMinutes(10))));
        var ownership = Substitute.For<IAgentTargetOwnershipService>();
        ownership.GetDenialReasonAsync(Arg.Any<string>(), UserId, Arg.Any<JsonElement>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        var real = new AgentOperationExecutor(catalog, policy, Substitute.For<IAgentAuditService>(), ownership,
            Substitute.For<IAgentStepUpAuthorizationBridge>(), new([create]),
            new PendingOperationChangePreviewer(_habitRepo, _goalRepo, _tagRepo, _userDateService, new([create])),
            _pendingOperationStore, _unitOfWork, NullLogger<AgentOperationExecutor>.Instance);
        UseProductionReadExecutor(goals);
        _operationExecutor.ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(request => request.OperationId == create.Name),
                Arg.Any<CancellationToken>())
            .Returns(call => real.ExecuteAsync(call.Arg<AgentExecuteOperationRequest>(), call.Arg<CancellationToken>()));
        var fixture = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures",
            "GateTenPreviews/single-create-pt-r1.json")))!;
        var arguments = fixture["toolCalls"]!.AsArray().Single(call => call!["name"]!.GetValue<string>() == create.Name)!["arguments"]!
            .DeepClone().AsObject();
        arguments["orbit_write_complete"] = true;
        SetupAiResponse(new AiResponse
        {
            ConversationContext = TestConversationContext,
            ToolCalls =
            [
                new(create.Name, "create", JsonSerializer.SerializeToElement(arguments)),
                new("query_goals", "goals", JsonSerializer.SerializeToElement(new { orbit_write_complete = true }))
            ]
        });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Review the preview." }));

        var result = await handler.Handle(new(UserId, "Continue.",
            ClientContext: OfferClient("pt-BR") with { SupportsProactiveOffers = false }), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.PendingOperations.Should().ContainSingle();
        await _aiIntentService.Received(1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Is<IReadOnlyList<AiToolCallResult>>(results => results.Any(item => item.Id == "goals" && item.Success)),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    private static bool IsLog(ICall call, int eventId) => call.GetMethodInfo().Name == "Log"
        && call.GetArguments()[1] is EventId id && id.Id == eventId;

    private IReadOnlyList<Dictionary<string, object?>> LoggedState(int eventId) => _logger.ReceivedCalls()
        .Where(call => IsLog(call, eventId))
        .Select(call => ((IEnumerable<KeyValuePair<string, object?>>)call.GetArguments()[2]!)
            .ToDictionary(pair => pair.Key, pair => pair.Value))
        .ToArray();
}
