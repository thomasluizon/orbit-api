using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Tests.Commands.Chat;

public class ProcessUserChatCommandHandlerTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<UserFact> _userFactRepo = Substitute.For<IGenericRepository<UserFact>>();
    private readonly IGenericRepository<Tag> _tagRepo = Substitute.For<IGenericRepository<Tag>>();
    private readonly IGenericRepository<ChecklistTemplate> _checklistTemplateRepo = Substitute.For<IGenericRepository<ChecklistTemplate>>();
    private readonly IFeatureFlagService _featureFlagService = Substitute.For<IFeatureFlagService>();
    private readonly IAiIntentService _aiIntentService = Substitute.For<IAiIntentService>();
    private readonly ISystemPromptBuilder _promptBuilder = Substitute.For<ISystemPromptBuilder>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IUserStreakService _userStreakService = Substitute.For<IUserStreakService>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IServiceScopeFactory _scopeFactory = Substitute.For<IServiceScopeFactory>();
    private readonly IAgentCatalogService _catalogService = Substitute.For<IAgentCatalogService>();
    private readonly IAgentOperationExecutor _operationExecutor = Substitute.For<IAgentOperationExecutor>();
    private readonly ILogger<ProcessUserChatCommandHandler> _logger = Substitute.For<ILogger<ProcessUserChatCommandHandler>>();

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 3);

    private static Habit CreateHabit(string title, bool isCompleted = false)
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId,
            title,
            null,
            null,
            DueDate: Today)).Value;

        if (isCompleted)
            habit.Log(Today);

        return habit;
    }

    private ProcessUserChatCommandHandler CreateHandler(params IAiTool[] tools)
    {
        var toolRegistry = new AiToolRegistry(tools);
        SetupOperationExecutor(toolRegistry);
        var aiDeps = new ChatAiDependencies(_aiIntentService, toolRegistry, _promptBuilder, _catalogService);
        var dataDeps = new ChatDataDependencies(_habitRepo, _goalRepo, _userRepo, _userFactRepo, _tagRepo, _checklistTemplateRepo, _featureFlagService);
        var executionDeps = new ChatExecutionDependencies(
            _userDateService, _userStreakService, _payGate, _unitOfWork, _scopeFactory, _operationExecutor);

        return new ProcessUserChatCommandHandler(
            dataDeps, aiDeps, executionDeps, _logger);
    }

    public ProcessUserChatCommandHandlerTests()
    {
        _catalogService.GetCapabilities().Returns([BuildCapability("test_capability")]);
        _catalogService.GetCapability(Arg.Any<string>())
            .Returns(callInfo => BuildCapability(callInfo.Arg<string>()));
        _catalogService.GetCapabilityByChatTool(Arg.Any<string>())
            .Returns(callInfo => BuildCapability(callInfo.Arg<string>()));
        _catalogService.BuildPromptSupplement(Arg.Any<AgentContextSnapshot>()).Returns("agent policy");

        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
        _userStreakService.RecalculateAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new UserStreakState(1, 1, Today));
        _promptBuilder.Build(Arg.Any<PromptBuildRequest>()).Returns("system prompt");

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit>().AsReadOnly());

        _userFactRepo.FindAsync(
            Arg.Any<Expression<Func<UserFact, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<UserFact>().AsReadOnly());

        _goalRepo.FindAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Goal>().AsReadOnly());

        _tagRepo.FindAsync(
            Arg.Any<Expression<Func<Tag, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Tag>().AsReadOnly());

        _checklistTemplateRepo.FindAsync(
            Arg.Any<Expression<Func<ChecklistTemplate, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<ChecklistTemplate>().AsReadOnly());

        _featureFlagService.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new List<string>().AsReadOnly());
    }

    private void SetupOperationExecutor(AiToolRegistry toolRegistry)
    {
        _operationExecutor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                var request = callInfo.Arg<AgentExecuteOperationRequest>();
                var cancellationToken = callInfo.ArgAt<CancellationToken>(1);
                var tool = toolRegistry.GetTool(request.OperationId);
                if (tool is null)
                {
                    return new AgentExecuteOperationResponse(
                        new AgentOperationResult(
                            request.OperationId,
                            request.OperationId,
                            AgentRiskClass.Low,
                            AgentConfirmationRequirement.None,
                            AgentOperationStatus.UnsupportedByPolicy,
                            PolicyReason: "unsupported_by_policy"),
                        PolicyDenial: new AgentPolicyDenial(
                            request.OperationId,
                            request.OperationId,
                            AgentRiskClass.Low,
                            AgentConfirmationRequirement.None,
                            "unsupported_by_policy"));
                }

                try
                {
                    var toolResult = await tool.ExecuteAsync(request.Arguments, request.UserId, cancellationToken);
                    return new AgentExecuteOperationResponse(
                        new AgentOperationResult(
                            request.OperationId,
                            request.OperationId,
                            AgentRiskClass.Low,
                            AgentConfirmationRequirement.None,
                            toolResult.Success ? AgentOperationStatus.Succeeded : AgentOperationStatus.Failed,
                            TargetId: toolResult.EntityId,
                            TargetName: toolResult.EntityName,
                            PolicyReason: toolResult.Success ? null : toolResult.Error,
                            Payload: toolResult.Payload));
                }
                catch
                {
                    return new AgentExecuteOperationResponse(
                        new AgentOperationResult(
                            request.OperationId,
                            request.OperationId,
                            AgentRiskClass.Low,
                            AgentConfirmationRequirement.None,
                            AgentOperationStatus.Failed,
                            PolicyReason: "unexpected_error"));
                }
            });
    }

    private static AgentCapability BuildCapability(string id)
    {
        return new AgentCapability(
            id,
            id,
            id,
            "chat",
            "test_scope",
            AgentRiskClass.Low,
            IsMutation: false,
            IsPhaseOneReadOnly: false,
            AgentConfirmationRequirement.None);
    }

    private void SetupUserAndPayGate(User? user = null, bool payGatePass = true)
    {
        user ??= User.Create("Thomas", "thomas@test.com").Value;
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _payGate.CanSendAiMessage(UserId, Arg.Any<CancellationToken>())
            .Returns(payGatePass ? Result.Success() : Result.PayGateFailure("AI message limit reached."));
    }

    private static readonly AiConversationContext TestConversationContext = new()
    {
        Messages = new List<object>(),
        Options = new object()
    };

    private void SetupAiResponse(AiResponse response)
    {
        _aiIntentService.SendWithToolsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<object>>(),
            Arg.Any<byte[]?>(), Arg.Any<string?>(),
            Arg.Any<IReadOnlyList<ChatHistoryMessage>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(response));
    }

    private void SetupAiFailure(string error)
    {
        _aiIntentService.SendWithToolsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<object>>(),
            Arg.Any<byte[]?>(), Arg.Any<string?>(),
            Arg.Any<IReadOnlyList<ChatHistoryMessage>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<AiResponse>(error));
    }

    // --- Existing tests ---

    [Fact]
    public async Task Handle_PayGateBlocks_ReturnsPayGateError()
    {
        SetupUserAndPayGate(payGatePass: false);
        var handler = CreateHandler();

        var command = new ProcessUserChatCommand(UserId, "Hello AI");
        var result = await handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
    }

    [Fact]
    public async Task Handle_AiServiceFails_ReturnsFailure()
    {
        SetupUserAndPayGate();
        SetupAiFailure("AI service unavailable");
        var handler = CreateHandler();

        var command = new ProcessUserChatCommand(UserId, "Hello AI");
        var result = await handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("AI service unavailable");
    }

    [Fact]
    public async Task Handle_SuccessfulResponse_ReturnsChatResponse()
    {
        SetupUserAndPayGate();
        SetupAiResponse(new AiResponse { TextMessage = "Hello! How can I help?", ToolCalls = null });
        var handler = CreateHandler();

        var command = new ProcessUserChatCommand(UserId, "Hello AI");
        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().Be("Hello! How can I help?");
        result.Value.Actions.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_PromptBuilderReceivesOnlyActiveHabits()
    {
        SetupUserAndPayGate();
        var activeHabit = CreateHabit("Morning Walk");
        var completedHabit = CreateHabit("Morning Walk", isCompleted: true);
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { activeHabit, completedHabit });

        SetupAiResponse(new AiResponse { TextMessage = "Done", ToolCalls = null });
        var handler = CreateHandler();

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Create morning walk"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _promptBuilder.Received(1).Build(Arg.Is<PromptBuildRequest>(request =>
            request.ActiveHabits.Count == 1 &&
            request.ActiveHabits[0].Id == activeHabit.Id));
    }

    [Fact]
    public async Task Handle_PromptBuilderIncludesCompletedParentsForActiveSubHabits()
    {
        SetupUserAndPayGate();
        var completedParent = Habit.Create(new HabitCreateParams(
            UserId,
            "Fitness",
            null,
            null,
            DueDate: Today)).Value;
        completedParent.Log(Today);

        var activeChild = Habit.Create(new HabitCreateParams(
            UserId,
            "Push-ups",
            FrequencyUnit.Day,
            1,
            DueDate: Today,
            ParentHabitId: completedParent.Id)).Value;

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { completedParent, activeChild });

        SetupAiResponse(new AiResponse { TextMessage = "Done", ToolCalls = null });
        var handler = CreateHandler();

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Update my push-ups habit"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _promptBuilder.Received(1).Build(Arg.Is<PromptBuildRequest>(request =>
            request.ActiveHabits.Select(habit => habit.Id).ToHashSet().SetEquals(new[]
            {
                completedParent.Id,
                activeChild.Id,
            })));
    }

    [Fact]
    public async Task Handle_AgentSnapshotExcludesCompletedHabitTitles()
    {
        SetupUserAndPayGate();
        var activeHabit = CreateHabit("Read Book");
        var completedHabit = CreateHabit("Completed Habit", isCompleted: true);
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { activeHabit, completedHabit });

        SetupAiResponse(new AiResponse { TextMessage = "Done", ToolCalls = null });
        var handler = CreateHandler();

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Create read book"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _catalogService.Received(1).BuildPromptSupplement(Arg.Is<AgentContextSnapshot>(snapshot =>
            snapshot.RecentHabitTitles != null &&
            snapshot.RecentHabitTitles.Count == 1 &&
            snapshot.RecentHabitTitles[0] == "Read Book" &&
            !snapshot.RecentHabitTitles.Contains("Completed Habit")));
    }

    [Fact]
    public async Task Handle_AiResponseWithJsonWrapper_StripsWrapper()
    {
        SetupUserAndPayGate();
        var wrappedMessage = "{\"aiMessage\": \"Unwrapped content\"}";
        SetupAiResponse(new AiResponse { TextMessage = wrappedMessage, ToolCalls = null });
        var handler = CreateHandler();

        var command = new ProcessUserChatCommand(UserId, "Tell me something");
        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().Be("Unwrapped content");
    }

    [Fact]
    public async Task Handle_NullUser_StillSucceeds()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);
        _payGate.CanSendAiMessage(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        SetupAiResponse(new AiResponse { TextMessage = "Response", ToolCalls = null });
        var handler = CreateHandler();

        var command = new ProcessUserChatCommand(UserId, "Hello");
        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().Be("Response");
    }

    [Fact]
    public async Task Handle_PlainTextResponse_NotStripped()
    {
        SetupUserAndPayGate();
        SetupAiResponse(new AiResponse { TextMessage = "Just a plain message", ToolCalls = null });
        var handler = CreateHandler();

        var command = new ProcessUserChatCommand(UserId, "Hello");
        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().Be("Just a plain message");
    }

    // --- New tests: Tool call with success result ---

    [Fact]
    public async Task Handle_ToolCallWithSuccess_ReturnsActionResult()
    {
        SetupUserAndPayGate();

        var mockTool = Substitute.For<IAiTool>();
        mockTool.Name.Returns("create_habit");
        mockTool.Description.Returns("Creates a habit");
        mockTool.IsReadOnly.Returns(false);
        mockTool.GetParameterSchema().Returns(new { type = "object" });

        var entityId = Guid.NewGuid().ToString();
        mockTool.ExecuteAsync(Arg.Any<JsonElement>(), UserId, Arg.Any<CancellationToken>())
            .Returns(new ToolResult(true, EntityId: entityId, EntityName: "Morning Run"));

        var handler = CreateHandler(mockTool);

        var toolCallArgs = JsonDocument.Parse("{}").RootElement;
        var toolCall = new AiToolCall("create_habit", "call_1", toolCallArgs);
        var aiResponseWithTool = new AiResponse
        {
            TextMessage = null,
            ToolCalls = [toolCall],
            ConversationContext = TestConversationContext
        };
        SetupAiResponse(aiResponseWithTool);

        // After tool execution, AI returns a final text response
        _aiIntentService.ContinueWithToolResultsAsync(
            Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Created your habit!", ToolCalls = null }));

        var command = new ProcessUserChatCommand(UserId, "Create a habit");
        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().Be("Created your habit!");
        result.Value.Actions.Should().HaveCount(1);
        result.Value.Actions[0].Type.Should().Be("CreateHabit");
        result.Value.Actions[0].Status.Should().Be(ActionStatus.Success);
        result.Value.Actions[0].EntityName.Should().Be("Morning Run");
        result.Value.Operations.Should().ContainSingle(op => op.OperationId == "create_habit" && op.Status == AgentOperationStatus.Succeeded);
    }

    // --- Tool call with failure ---

    [Fact]
    public async Task Handle_ToolCallWithFailure_ReturnsFailedAction()
    {
        SetupUserAndPayGate();

        var mockTool = Substitute.For<IAiTool>();
        mockTool.Name.Returns("delete_habit");
        mockTool.Description.Returns("Deletes a habit");
        mockTool.IsReadOnly.Returns(false);
        mockTool.GetParameterSchema().Returns(new { type = "object" });
        mockTool.ExecuteAsync(Arg.Any<JsonElement>(), UserId, Arg.Any<CancellationToken>())
            .Returns(new ToolResult(false, Error: "Habit not found."));

        var handler = CreateHandler(mockTool);

        var toolCallArgs = JsonDocument.Parse("{}").RootElement;
        var aiResponseWithTool = new AiResponse
        {
            ToolCalls = [new AiToolCall("delete_habit", "call_1", toolCallArgs)],
            ConversationContext = TestConversationContext
        };
        SetupAiResponse(aiResponseWithTool);

        _aiIntentService.ContinueWithToolResultsAsync(
            Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Could not find that habit.", ToolCalls = null }));

        var command = new ProcessUserChatCommand(UserId, "Delete my habit");
        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Actions.Should().HaveCount(1);
        result.Value.Actions[0].Status.Should().Be(ActionStatus.Failed);
        result.Value.Actions[0].Error.Should().Be("Habit not found.");
    }

    [Fact]
    public async Task Handle_DestructiveToolWithoutConfirmation_ReturnsPendingOperation()
    {
        SetupUserAndPayGate();

        var deleteTool = Substitute.For<IAiTool>();
        deleteTool.Name.Returns("delete_habit");
        deleteTool.Description.Returns("Deletes a habit");
        deleteTool.IsReadOnly.Returns(false);
        deleteTool.GetParameterSchema().Returns(new { type = "object" });

        var handler = CreateHandler(deleteTool);
        _operationExecutor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AgentExecuteOperationResponse(
                new AgentOperationResult(
                    "delete_habit",
                    "delete_habit",
                    AgentRiskClass.Destructive,
                    AgentConfirmationRequirement.FreshConfirmation,
                    AgentOperationStatus.PendingConfirmation,
                    Summary: "DeleteHabit requested via chat",
                    PolicyReason: "confirmation_required",
                    PendingOperationId: Guid.NewGuid()),
                new PendingAgentOperation(
                    Guid.NewGuid(),
                    AgentCapabilityIds.HabitsDelete,
                    "Delete Habit",
                    "DeleteHabit requested via chat",
                    AgentRiskClass.Destructive,
                    AgentConfirmationRequirement.FreshConfirmation,
                    DateTime.UtcNow.AddMinutes(10))));
        var toolCallArgs = JsonDocument.Parse("{}").RootElement;
        var aiResponseWithTool = new AiResponse
        {
            ToolCalls = [new AiToolCall("delete_habit", "call_1", toolCallArgs)],
            ConversationContext = TestConversationContext
        };
        SetupAiResponse(aiResponseWithTool);

        _aiIntentService.ContinueWithToolResultsAsync(
            Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Please confirm that deletion.", ToolCalls = null }));

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Delete my habit"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.PendingOperations.Should().HaveCount(1);
        result.Value.Operations.Should().ContainSingle(op => op.Status == AgentOperationStatus.PendingConfirmation);
        await _operationExecutor.Received(1).ExecuteAsync(
            Arg.Is<AgentExecuteOperationRequest>(op => op.OperationId == "delete_habit"),
            Arg.Any<CancellationToken>());
    }

    // --- Tool call with read-only tool ---

    [Fact]
    public async Task Handle_ReadOnlyToolCall_DoesNotProduceActionResult()
    {
        SetupUserAndPayGate();

        var mockTool = Substitute.For<IAiTool>();
        mockTool.Name.Returns("query_habits");
        mockTool.Description.Returns("Queries habits");
        mockTool.IsReadOnly.Returns(true);
        mockTool.GetParameterSchema().Returns(new { type = "object" });
        mockTool.ExecuteAsync(Arg.Any<JsonElement>(), UserId, Arg.Any<CancellationToken>())
            .Returns(new ToolResult(true, EntityName: "Found 3 habits"));

        var handler = CreateHandler(mockTool);

        var toolCallArgs = JsonDocument.Parse("{}").RootElement;
        var aiResponseWithTool = new AiResponse
        {
            ToolCalls = [new AiToolCall("query_habits", "call_1", toolCallArgs)],
            ConversationContext = TestConversationContext
        };
        SetupAiResponse(aiResponseWithTool);

        _aiIntentService.ContinueWithToolResultsAsync(
            Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Here are your habits.", ToolCalls = null }));

        var command = new ProcessUserChatCommand(UserId, "Show my habits");
        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Actions.Should().BeEmpty();
    }

    // --- Multiple tool calls in sequence ---

    [Fact]
    public async Task Handle_MultipleToolCallsInOneIteration_ExecutesAllAndCollectsActions()
    {
        SetupUserAndPayGate();

        var createTool = Substitute.For<IAiTool>();
        createTool.Name.Returns("create_habit");
        createTool.Description.Returns("Creates a habit");
        createTool.IsReadOnly.Returns(false);
        createTool.GetParameterSchema().Returns(new { type = "object" });
        createTool.ExecuteAsync(Arg.Any<JsonElement>(), UserId, Arg.Any<CancellationToken>())
            .Returns(new ToolResult(true, EntityId: Guid.NewGuid().ToString(), EntityName: "Habit A"));

        var logTool = Substitute.For<IAiTool>();
        logTool.Name.Returns("log_habit");
        logTool.Description.Returns("Logs a habit");
        logTool.IsReadOnly.Returns(false);
        logTool.GetParameterSchema().Returns(new { type = "object" });
        logTool.ExecuteAsync(Arg.Any<JsonElement>(), UserId, Arg.Any<CancellationToken>())
            .Returns(new ToolResult(true, EntityName: "Habit B"));

        var handler = CreateHandler(createTool, logTool);

        var toolCallArgs = JsonDocument.Parse("{}").RootElement;
        var aiResponseWithTools = new AiResponse
        {
            ToolCalls =
            [
                new AiToolCall("create_habit", "call_1", toolCallArgs),
                new AiToolCall("log_habit", "call_2", toolCallArgs)
            ],
            ConversationContext = TestConversationContext
        };
        SetupAiResponse(aiResponseWithTools);

        _aiIntentService.ContinueWithToolResultsAsync(
            Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Done!", ToolCalls = null }));

        var command = new ProcessUserChatCommand(UserId, "Create and log habits");
        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Actions.Should().HaveCount(2);
        result.Value.Actions.Select(a => a.Type).Should().Contain("CreateHabit");
        result.Value.Actions.Select(a => a.Type).Should().Contain("LogHabit");
    }

    // --- Multiple tool-calling iterations ---

    [Fact]
    public async Task Handle_MultipleIterations_AccumulatesActions()
    {
        SetupUserAndPayGate();

        var createTool = Substitute.For<IAiTool>();
        createTool.Name.Returns("create_habit");
        createTool.Description.Returns("Creates");
        createTool.IsReadOnly.Returns(false);
        createTool.GetParameterSchema().Returns(new { type = "object" });
        createTool.ExecuteAsync(Arg.Any<JsonElement>(), UserId, Arg.Any<CancellationToken>())
            .Returns(new ToolResult(true, EntityId: Guid.NewGuid().ToString(), EntityName: "Habit"));

        var handler = CreateHandler(createTool);

        var toolCallArgs = JsonDocument.Parse("{}").RootElement;

        // First AI call returns tool calls
        var firstResponse = new AiResponse
        {
            ToolCalls = [new AiToolCall("create_habit", "call_1", toolCallArgs)],
            ConversationContext = TestConversationContext
        };
        SetupAiResponse(firstResponse);

        // After first tool execution, AI wants to call another tool
        var secondResponse = new AiResponse
        {
            ToolCalls = [new AiToolCall("create_habit", "call_2", toolCallArgs)],
            ConversationContext = TestConversationContext
        };

        // After second tool execution, AI produces final text
        var finalResponse = new AiResponse { TextMessage = "Created two habits!", ToolCalls = null };

        _aiIntentService.ContinueWithToolResultsAsync(
            Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(secondResponse), Result.Success(finalResponse));

        var command = new ProcessUserChatCommand(UserId, "Create two habits");
        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Actions.Should().HaveCount(2);
    }

    // --- Unknown tool ---

    [Fact]
    public async Task Handle_UnknownToolCall_ReturnsFailedAction()
    {
        SetupUserAndPayGate();
        var handler = CreateHandler(); // No tools registered

        var toolCallArgs = JsonDocument.Parse("{}").RootElement;
        var aiResponseWithTool = new AiResponse
        {
            ToolCalls = [new AiToolCall("nonexistent_tool", "call_1", toolCallArgs)],
            ConversationContext = TestConversationContext
        };
        SetupAiResponse(aiResponseWithTool);

        _aiIntentService.ContinueWithToolResultsAsync(
            Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Sorry!", ToolCalls = null }));

        var command = new ProcessUserChatCommand(UserId, "Do something");
        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Actions.Should().HaveCount(1);
        result.Value.Actions[0].Status.Should().Be(ActionStatus.Failed);
        result.Value.Actions[0].Error.Should().Contain("Unknown tool");
    }

    // --- Tool that throws exception ---

    [Fact]
    public async Task Handle_ToolThrowsException_ReturnsFailedAction()
    {
        SetupUserAndPayGate();

        var throwingTool = Substitute.For<IAiTool>();
        throwingTool.Name.Returns("create_habit");
        throwingTool.Description.Returns("Creates a habit");
        throwingTool.IsReadOnly.Returns(false);
        throwingTool.GetParameterSchema().Returns(new { type = "object" });
        throwingTool.ExecuteAsync(Arg.Any<JsonElement>(), UserId, Arg.Any<CancellationToken>())
            .Returns<ToolResult>(_ => throw new InvalidOperationException("DB error"));

        var handler = CreateHandler(throwingTool);

        var toolCallArgs = JsonDocument.Parse("{}").RootElement;
        var aiResponseWithTool = new AiResponse
        {
            ToolCalls = [new AiToolCall("create_habit", "call_1", toolCallArgs)],
            ConversationContext = TestConversationContext
        };
        SetupAiResponse(aiResponseWithTool);

        _aiIntentService.ContinueWithToolResultsAsync(
            Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Error occurred.", ToolCalls = null }));

        var command = new ProcessUserChatCommand(UserId, "Create habit");
        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Actions.Should().HaveCount(1);
        result.Value.Actions[0].Status.Should().Be(ActionStatus.Failed);
        result.Value.Actions[0].Error.Should().Be("An unexpected error occurred.");
    }

    // --- Image attachment ---

    [Fact]
    public async Task Handle_WithImageAttachment_PassesImageDataToAiService()
    {
        SetupUserAndPayGate();
        SetupAiResponse(new AiResponse { TextMessage = "I see your image!", ToolCalls = null });
        var handler = CreateHandler();

        var imageData = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        var command = new ProcessUserChatCommand(UserId, "What's this?", ImageData: imageData, ImageMimeType: "image/png");
        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().Be("I see your image!");

        await _aiIntentService.Received(1).SendWithToolsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<object>>(),
            Arg.Is<byte[]?>(b => b != null && b.Length == 4),
            Arg.Is<string?>(s => s == "image/png"),
            Arg.Any<IReadOnlyList<ChatHistoryMessage>?>(),
            Arg.Any<CancellationToken>());
    }

    // --- Chat history with previous messages ---

    [Fact]
    public async Task Handle_WithChatHistory_PassesHistoryToAiService()
    {
        SetupUserAndPayGate();
        SetupAiResponse(new AiResponse { TextMessage = "Based on our conversation...", ToolCalls = null });
        var handler = CreateHandler();

        var history = new List<ChatHistoryMessage>
        {
            new("user", "First message"),
            new("assistant", "First reply")
        };
        var command = new ProcessUserChatCommand(UserId, "Follow up", History: history);
        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await _aiIntentService.Received(1).SendWithToolsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<object>>(),
            Arg.Any<byte[]?>(), Arg.Any<string?>(),
            Arg.Is<IReadOnlyList<ChatHistoryMessage>?>(h => h != null && h.Count == 2),
            Arg.Any<CancellationToken>());
    }

    // --- suggest_breakdown tool special handling ---

    [Fact]
    public async Task Handle_SuggestBreakdownTool_ReturnsSuggestionStatus()
    {
        SetupUserAndPayGate();

        var suggestTool = Substitute.For<IAiTool>();
        suggestTool.Name.Returns("suggest_breakdown");
        suggestTool.Description.Returns("Suggests sub-habits");
        suggestTool.IsReadOnly.Returns(false);
        suggestTool.GetParameterSchema().Returns(new { type = "object" });
        suggestTool.ExecuteAsync(Arg.Any<JsonElement>(), UserId, Arg.Any<CancellationToken>())
            .Returns(new ToolResult(true, EntityName: "Morning Routine"));

        var handler = CreateHandler(suggestTool);

        var argsJson = """
        {
            "suggested_sub_habits": [
                {
                    "title": "Stretch",
                    "description": "5 min stretching",
                    "frequency_unit": "Day",
                    "frequency_quantity": 1,
                    "days": ["Monday", "Wednesday", "Friday"]
                }
            ]
        }
        """;
        var toolCallArgs = JsonDocument.Parse(argsJson).RootElement;
        var aiResponseWithTool = new AiResponse
        {
            ToolCalls = [new AiToolCall("suggest_breakdown", "call_1", toolCallArgs)],
            ConversationContext = TestConversationContext
        };
        SetupAiResponse(aiResponseWithTool);

        _aiIntentService.ContinueWithToolResultsAsync(
            Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Here are my suggestions.", ToolCalls = null }));

        var command = new ProcessUserChatCommand(UserId, "Break down my habit");
        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Actions.Should().HaveCount(1);
        result.Value.Actions[0].Status.Should().Be(ActionStatus.Suggestion);
        result.Value.Actions[0].Type.Should().Be("SuggestBreakdown");
        result.Value.Actions[0].SuggestedSubHabits.Should().NotBeNull();
        result.Value.Actions[0].SuggestedSubHabits!.Count.Should().Be(1);
        result.Value.Actions[0].SuggestedSubHabits![0].Title.Should().Be("Stretch");
        result.Value.Actions[0].SuggestedSubHabits![0].FrequencyUnit.Should().Be(FrequencyUnit.Day);
        result.Value.Actions[0].SuggestedSubHabits![0].Days.Should().HaveCount(3);
    }

    // --- Max iterations guard ---

    [Fact]
    public async Task Handle_MaxIterationsReached_StopsLooping()
    {
        SetupUserAndPayGate();

        var mockTool = Substitute.For<IAiTool>();
        mockTool.Name.Returns("create_habit");
        mockTool.Description.Returns("Creates");
        mockTool.IsReadOnly.Returns(false);
        mockTool.GetParameterSchema().Returns(new { type = "object" });
        mockTool.ExecuteAsync(Arg.Any<JsonElement>(), UserId, Arg.Any<CancellationToken>())
            .Returns(new ToolResult(true, EntityId: Guid.NewGuid().ToString(), EntityName: "H"));

        var handler = CreateHandler(mockTool);

        var toolCallArgs = JsonDocument.Parse("{}").RootElement;
        var toolResponse = new AiResponse
        {
            ToolCalls = [new AiToolCall("create_habit", "call_x", toolCallArgs)],
            ConversationContext = TestConversationContext
        };
        SetupAiResponse(toolResponse);

        // ContinueWithToolResults always returns more tool calls (would loop forever without guard)
        _aiIntentService.ContinueWithToolResultsAsync(
            Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(toolResponse));

        var command = new ProcessUserChatCommand(UserId, "Keep going");
        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        // MaxToolIterations = 5, initial call + 5 iterations = 6 total tool executions
        // But ContinueWithToolResults is called once per iteration loop
        result.Value.Actions.Count.Should().BeLessThanOrEqualTo(6);
    }

    // --- ContinueWithToolResults failure ---

    [Fact]
    public async Task Handle_ContinueWithToolResultsFails_StopsAndReturnsPartialResult()
    {
        SetupUserAndPayGate();

        var mockTool = Substitute.For<IAiTool>();
        mockTool.Name.Returns("create_habit");
        mockTool.Description.Returns("Creates");
        mockTool.IsReadOnly.Returns(false);
        mockTool.GetParameterSchema().Returns(new { type = "object" });
        mockTool.ExecuteAsync(Arg.Any<JsonElement>(), UserId, Arg.Any<CancellationToken>())
            .Returns(new ToolResult(true, EntityName: "Test"));

        var handler = CreateHandler(mockTool);

        var toolCallArgs = JsonDocument.Parse("{}").RootElement;
        var aiResponseWithTool = new AiResponse
        {
            ToolCalls = [new AiToolCall("create_habit", "call_1", toolCallArgs)],
            ConversationContext = TestConversationContext
        };
        SetupAiResponse(aiResponseWithTool);

        _aiIntentService.ContinueWithToolResultsAsync(
            Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<AiResponse>("Connection lost"));

        var command = new ProcessUserChatCommand(UserId, "Create habit");
        var result = await handler.Handle(command, CancellationToken.None);

        // The handler still succeeds, just with no final text message
        result.IsSuccess.Should().BeTrue();
        result.Value.Actions.Should().HaveCount(1);
    }

    // --- AI memory disabled path ---

    [Fact]
    public async Task Handle_AiMemoryDisabled_DoesNotLoadFacts()
    {
        var user = User.Create("Thomas", "thomas@test.com").Value;
        user.SetAiMemory(false);
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _payGate.CanSendAiMessage(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        SetupAiResponse(new AiResponse { TextMessage = "Hi!", ToolCalls = null });
        var handler = CreateHandler();

        var command = new ProcessUserChatCommand(UserId, "Hello");
        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        // UserFact repository should not be queried when memory is disabled
        await _userFactRepo.DidNotReceive().FindAsync(
            Arg.Any<Expression<Func<UserFact, bool>>>(),
            Arg.Any<CancellationToken>());
    }

    // --- Invalid JSON wrapper ---

    [Fact]
    public async Task Handle_InvalidJsonWrapper_ReturnsTextAsIs()
    {
        SetupUserAndPayGate();
        SetupAiResponse(new AiResponse { TextMessage = "{invalid json here", ToolCalls = null });
        var handler = CreateHandler();

        var command = new ProcessUserChatCommand(UserId, "Hello");
        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().Be("{invalid json here");
    }

    // --- JSON without aiMessage key ---

    [Fact]
    public async Task Handle_JsonWithoutAiMessageKey_ReturnsTextAsIs()
    {
        SetupUserAndPayGate();
        SetupAiResponse(new AiResponse { TextMessage = "{\"other\": \"value\"}", ToolCalls = null });
        var handler = CreateHandler();

        var command = new ProcessUserChatCommand(UserId, "Hello");
        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().Be("{\"other\": \"value\"}");
    }

    // --- Null text message ---

    [Fact]
    public async Task Handle_NullTextMessage_ReturnsNull()
    {
        SetupUserAndPayGate();
        SetupAiResponse(new AiResponse { TextMessage = null, ToolCalls = null });
        var handler = CreateHandler();

        var command = new ProcessUserChatCommand(UserId, "Hello");
        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().BeNull();
    }

    // --- Tool ordering: create_habit before create_sub_habit ---

    [Fact]
    public async Task Handle_ToolCallOrdering_CreateHabitRunsBeforeSubHabit()
    {
        SetupUserAndPayGate();

        var executionOrder = new List<string>();

        var createTool = Substitute.For<IAiTool>();
        createTool.Name.Returns("create_habit");
        createTool.Description.Returns("Creates");
        createTool.IsReadOnly.Returns(false);
        createTool.GetParameterSchema().Returns(new { type = "object" });
        createTool.ExecuteAsync(Arg.Any<JsonElement>(), UserId, Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                executionOrder.Add("create_habit");
                return new ToolResult(true, EntityId: Guid.NewGuid().ToString(), EntityName: "Parent");
            });

        var subTool = Substitute.For<IAiTool>();
        subTool.Name.Returns("create_sub_habit");
        subTool.Description.Returns("Creates sub");
        subTool.IsReadOnly.Returns(false);
        subTool.GetParameterSchema().Returns(new { type = "object" });
        subTool.ExecuteAsync(Arg.Any<JsonElement>(), UserId, Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                executionOrder.Add("create_sub_habit");
                return new ToolResult(true, EntityId: Guid.NewGuid().ToString(), EntityName: "Child");
            });

        var handler = CreateHandler(createTool, subTool);

        var toolCallArgs = JsonDocument.Parse("{}").RootElement;
        // Note: sub_habit is listed BEFORE create_habit to prove ordering works
        var aiResponseWithTools = new AiResponse
        {
            ToolCalls =
            [
                new AiToolCall("create_sub_habit", "call_2", toolCallArgs),
                new AiToolCall("create_habit", "call_1", toolCallArgs)
            ],
            ConversationContext = TestConversationContext
        };
        SetupAiResponse(aiResponseWithTools);

        _aiIntentService.ContinueWithToolResultsAsync(
            Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Done!", ToolCalls = null }));

        var command = new ProcessUserChatCommand(UserId, "Create parent and child");
        await handler.Handle(command, CancellationToken.None);

        executionOrder.Should().Equal("create_habit", "create_sub_habit");
    }

    // --- suggest_breakdown with no sub habits in args ---

    [Fact]
    public async Task Handle_SuggestBreakdownWithoutSubHabits_ReturnsNullSuggestions()
    {
        SetupUserAndPayGate();

        var suggestTool = Substitute.For<IAiTool>();
        suggestTool.Name.Returns("suggest_breakdown");
        suggestTool.Description.Returns("Suggests");
        suggestTool.IsReadOnly.Returns(false);
        suggestTool.GetParameterSchema().Returns(new { type = "object" });
        suggestTool.ExecuteAsync(Arg.Any<JsonElement>(), UserId, Arg.Any<CancellationToken>())
            .Returns(new ToolResult(true, EntityName: "Test"));

        var handler = CreateHandler(suggestTool);

        var toolCallArgs = JsonDocument.Parse("{}").RootElement;
        var aiResponseWithTool = new AiResponse
        {
            ToolCalls = [new AiToolCall("suggest_breakdown", "call_1", toolCallArgs)],
            ConversationContext = TestConversationContext
        };
        SetupAiResponse(aiResponseWithTool);

        _aiIntentService.ContinueWithToolResultsAsync(
            Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Hmm.", ToolCalls = null }));

        var command = new ProcessUserChatCommand(UserId, "Break it down");
        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Actions[0].SuggestedSubHabits.Should().BeNull();
    }

    // --- SaveChanges is called ---

    [Fact]
    public async Task Handle_AfterToolExecution_SavesChanges()
    {
        SetupUserAndPayGate();
        SetupAiResponse(new AiResponse { TextMessage = "Done", ToolCalls = null });
        var handler = CreateHandler();

        var command = new ProcessUserChatCommand(UserId, "Hello");
        await handler.Handle(command, CancellationToken.None);

        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
