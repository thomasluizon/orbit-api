using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Goals.Services;
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
    private readonly IStreakGoalReadSyncer _streakGoalReadSyncer = Substitute.For<IStreakGoalReadSyncer>();
    private readonly IServiceScopeFactory _scopeFactory = Substitute.For<IServiceScopeFactory>();
    private readonly IAgentCatalogService _catalogService = Substitute.For<IAgentCatalogService>();
    private readonly IAgentOperationExecutor _operationExecutor = Substitute.For<IAgentOperationExecutor>();
    private readonly IPendingClarificationStore _pendingClarificationStore = Substitute.For<IPendingClarificationStore>();
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
            _userDateService, _userStreakService, _payGate, _unitOfWork, _scopeFactory, _operationExecutor, _pendingClarificationStore, _streakGoalReadSyncer);

        return new ProcessUserChatCommandHandler(
            dataDeps, aiDeps, executionDeps, _logger);
    }

    public ProcessUserChatCommandHandlerTests()
    {
        SetupScopeFactory();
        _catalogService.GetCapabilities().Returns([BuildCapability("test_capability")]);
        _catalogService.GetCapability(Arg.Any<string>())
            .Returns(callInfo => BuildCapability(callInfo.Arg<string>()));
        _catalogService.GetCapabilityByChatTool(Arg.Any<string>())
            .Returns(callInfo => BuildCapability(callInfo.Arg<string>()));
        _catalogService.BuildStaticSupplement().Returns("static supplement");
        _catalogService.BuildDynamicSupplement(Arg.Any<AgentContextSnapshot>()).Returns("dynamic supplement");

        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
        _userStreakService.RecalculateAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new UserStreakState(1, 1, Today));
        _streakGoalReadSyncer.ComputeFreshValuesAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int>());
        _promptBuilder.BuildStatic(Arg.Any<PromptBuildRequest>()).Returns("static prompt");
        _promptBuilder.BuildDynamic(Arg.Any<PromptBuildRequest>()).Returns("dynamic prompt");

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

    private void SetupScopeFactory()
    {
        var scopeProvider = Substitute.For<IServiceProvider>();
        scopeProvider.GetService(typeof(IAgentOperationExecutor)).Returns(_operationExecutor);
        scopeProvider.GetService(typeof(IPendingClarificationStore)).Returns(_pendingClarificationStore);

        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(scopeProvider);
        _scopeFactory.CreateScope().Returns(scope);
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
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<object>>(), Arg.Any<Guid>(),
            Arg.Any<byte[]?>(), Arg.Any<string?>(),
            Arg.Any<IReadOnlyList<ChatHistoryMessage>?>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(response));
    }

    private void SetupAiFailure(string error)
    {
        _aiIntentService.SendWithToolsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<object>>(), Arg.Any<Guid>(),
            Arg.Any<byte[]?>(), Arg.Any<string?>(),
            Arg.Any<IReadOnlyList<ChatHistoryMessage>?>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<AiResponse>(error));
    }

    private static IAiTool FakeTool(string name)
    {
        var tool = Substitute.For<IAiTool>();
        tool.Name.Returns(name);
        tool.Description.Returns($"{name} description");
        tool.IsReadOnly.Returns(true);
        tool.GetParameterSchema().Returns(new { type = "object" });
        return tool;
    }

    private static IReadOnlyList<string> ToolNames(IReadOnlyList<object> declarations) =>
        declarations.Select(declaration => (string)declaration.GetType().GetProperty("name")!.GetValue(declaration)!).ToList();

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
    public async Task Handle_CapableClientWithDirective_PopulatesHabitListAndStripsToken()
    {
        SetupUserAndPayGate();
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { CreateHabit("Meditate") }.AsReadOnly());
        SetupAiResponse(new AiResponse { TextMessage = "Here are your habits for today:\n[[orbit:habits:today]]", ToolCalls = null });
        var handler = CreateHandler();

        var command = new ProcessUserChatCommand(
            UserId, "what are my habits today",
            ClientContext: new AgentClientContext(SupportsHabitListCard: true));
        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().Be("Here are your habits for today:");
        result.Value.HabitList.Should().NotBeNull();
        result.Value.HabitList!.Scope.Should().Be("today");
        result.Value.HabitList.Items.Should().ContainSingle(item => item.Title == "Meditate");
    }

    [Fact]
    public async Task Handle_DirectiveWithoutCapability_StripsTokenButOmitsHabitList()
    {
        SetupUserAndPayGate();
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { CreateHabit("Meditate") }.AsReadOnly());
        SetupAiResponse(new AiResponse { TextMessage = "Here are your habits:\n[[orbit:habits:today]]", ToolCalls = null });
        var handler = CreateHandler();

        var command = new ProcessUserChatCommand(UserId, "what are my habits today");
        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().Be("Here are your habits:");
        result.Value.HabitList.Should().BeNull();
    }

    [Fact]
    public async Task Handle_CapableClientWithoutDirective_OmitsHabitList()
    {
        SetupUserAndPayGate();
        SetupAiResponse(new AiResponse { TextMessage = "Sure, I can help.", ToolCalls = null });
        var handler = CreateHandler();

        var command = new ProcessUserChatCommand(
            UserId, "hello",
            ClientContext: new AgentClientContext(SupportsHabitListCard: true));
        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().Be("Sure, I can help.");
        result.Value.HabitList.Should().BeNull();
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
        _promptBuilder.Received(1).BuildDynamic(Arg.Is<PromptBuildRequest>(request =>
            request.ActiveHabits.Count == 1 &&
            request.ActiveHabits[0].Id == activeHabit.Id));
    }

    [Fact]
    public async Task Handle_ProUser_AppliesFreshStreakValueBeforeBuildingPromptContext()
    {
        var proUser = User.Create("Thomas", "thomas@test.com").Value;
        proUser.StartTrial(DateTime.UtcNow.AddDays(5));
        SetupUserAndPayGate(proUser);

        var streakGoal = Goal.Create(new Goal.CreateGoalParams(
            UserId, "Avoid doom scrolling", 7, "days", Type: GoalType.Streak)).Value;

        _streakGoalReadSyncer
            .ComputeFreshValuesAsync(UserId, Today, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int> { [streakGoal.Id] = 4 });

        _goalRepo.FindAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(_ => new List<Goal> { streakGoal }.AsReadOnly());

        SetupAiResponse(new AiResponse { TextMessage = "Done", ToolCalls = null });
        var handler = CreateHandler();

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "How am I doing?"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _streakGoalReadSyncer.Received(1).ComputeFreshValuesAsync(UserId, Today, Arg.Any<CancellationToken>());
        _promptBuilder.Received(1).BuildDynamic(Arg.Is<PromptBuildRequest>(request =>
            request.ActiveGoals != null &&
            request.ActiveGoals.Count == 1 &&
            request.ActiveGoals[0].CurrentValue == 4));
        streakGoal.Status.Should().Be(GoalStatus.Active);
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
        _promptBuilder.Received(1).BuildDynamic(Arg.Is<PromptBuildRequest>(request =>
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
        _catalogService.Received(1).BuildDynamicSupplement(Arg.Is<AgentContextSnapshot>(snapshot =>
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

        _aiIntentService.ContinueWithToolResultsAsync(
            Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
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

    [Fact]
    public async Task Handle_WithStreamSink_EmitsRoundPerIterationAndBridgesAiEvents()
    {
        SetupUserAndPayGate();

        var mockTool = Substitute.For<IAiTool>();
        mockTool.Name.Returns("create_habit");
        mockTool.Description.Returns("Creates a habit");
        mockTool.IsReadOnly.Returns(false);
        mockTool.GetParameterSchema().Returns(new { type = "object" });
        mockTool.ExecuteAsync(Arg.Any<JsonElement>(), UserId, Arg.Any<CancellationToken>())
            .Returns(new ToolResult(true, EntityId: Guid.NewGuid().ToString(), EntityName: "Morning Run"));

        var handler = CreateHandler(mockTool);

        var toolCall = new AiToolCall("create_habit", "call_1", JsonDocument.Parse("{}").RootElement);
        SetupAiResponse(new AiResponse { ToolCalls = [toolCall], ConversationContext = TestConversationContext });

        Func<AiStreamEvent, Task>? bridgedSink = null;
        _aiIntentService.ContinueWithToolResultsAsync(
            Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Do<Func<AiStreamEvent, Task>?>(sink => bridgedSink = sink), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Created your habit!" }));

        var streamEvents = new List<ChatStreamEvent>();
        var command = new ProcessUserChatCommand(UserId, "Create a habit", StreamSink: streamEvent =>
        {
            streamEvents.Add(streamEvent);
            return Task.CompletedTask;
        });

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        streamEvents.Should().ContainSingle(streamEvent => streamEvent.Type == "round" && streamEvent.Iteration == 1);

        bridgedSink.Should().NotBeNull();
        await bridgedSink!(AiStreamEvent.Delta("chunk"));
        await bridgedSink!(AiStreamEvent.Reset());
        streamEvents.Should().Contain(streamEvent => streamEvent.Type == "delta" && streamEvent.Text == "chunk");
        streamEvents[^1].Type.Should().Be("reset");
    }

    [Fact]
    public async Task Handle_WithoutStreamSink_PassesNullSinkToIntentService()
    {
        SetupUserAndPayGate();
        SetupAiResponse(new AiResponse { TextMessage = "Hello there" });
        var handler = CreateHandler();

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Hello"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().Be("Hello there");
        await _aiIntentService.Received(1).SendWithToolsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<object>>(), Arg.Any<Guid>(),
            Arg.Any<byte[]?>(), Arg.Any<string?>(),
            Arg.Any<IReadOnlyList<ChatHistoryMessage>?>(),
            Arg.Is<Func<AiStreamEvent, Task>?>(sink => sink == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_SendsToolsInDeterministicOrdinalOrder()
    {
        SetupUserAndPayGate();
        SetupAiResponse(new AiResponse { TextMessage = "ok" });
        var handler = CreateHandler(FakeTool("delete_habit"), FakeTool("assign_tags"), FakeTool("create_habit"));

        await handler.Handle(new ProcessUserChatCommand(UserId, "Hello AI"), CancellationToken.None);

        await _aiIntentService.Received(1).SendWithToolsAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Is<IReadOnlyList<object>>(declarations =>
                ToolNames(declarations).SequenceEqual(new[] { "assign_tags", "create_habit", "delete_habit" })),
            Arg.Any<Guid>(),
            Arg.Any<byte[]?>(), Arg.Any<string?>(),
            Arg.Any<IReadOnlyList<ChatHistoryMessage>?>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_TrivialGreeting_SkipsToolDeclarations()
    {
        SetupUserAndPayGate();
        SetupAiResponse(new AiResponse { TextMessage = "Hi! How can I help?" });
        var handler = CreateHandler(FakeTool("delete_habit"), FakeTool("create_habit"));

        await handler.Handle(new ProcessUserChatCommand(UserId, "thanks"), CancellationToken.None);

        await _aiIntentService.Received(1).SendWithToolsAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Is<IReadOnlyList<object>>(declarations => declarations.Count == 0),
            Arg.Any<Guid>(),
            Arg.Any<byte[]?>(), Arg.Any<string?>(),
            Arg.Any<IReadOnlyList<ChatHistoryMessage>?>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_TrivialReplyWithPendingConfirmation_KeepsTools()
    {
        SetupUserAndPayGate();
        SetupAiResponse(new AiResponse { TextMessage = "ok" });
        var handler = CreateHandler(FakeTool("delete_habit"));

        await handler.Handle(
            new ProcessUserChatCommand(UserId, "ok", ConfirmationToken: "confirm-token"), CancellationToken.None);

        await _aiIntentService.Received(1).SendWithToolsAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Is<IReadOnlyList<object>>(declarations => declarations.Count == 1),
            Arg.Any<byte[]?>(), Arg.Any<string?>(),
            Arg.Any<IReadOnlyList<ChatHistoryMessage>?>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_TrivialReplyMidConversation_KeepsTools()
    {
        SetupUserAndPayGate();
        SetupAiResponse(new AiResponse { TextMessage = "Logged it!" });
        var handler = CreateHandler(FakeTool("log_habit"));

        await handler.Handle(
            new ProcessUserChatCommand(
                UserId, "yes",
                History: [new ChatHistoryMessage("ai", "I can log your 5 km run — should I?")]),
            CancellationToken.None);

        await _aiIntentService.Received(1).SendWithToolsAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Is<IReadOnlyList<object>>(declarations => declarations.Count == 1),
            Arg.Any<byte[]?>(), Arg.Any<string?>(),
            Arg.Any<IReadOnlyList<ChatHistoryMessage>?>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_AssemblesSystemPromptStaticBeforeDynamic()
    {
        SetupUserAndPayGate();
        _promptBuilder.BuildStatic(Arg.Any<PromptBuildRequest>()).Returns("STATIC_SECTIONS");
        _catalogService.BuildStaticSupplement().Returns("STATIC_SUPPLEMENT");
        _promptBuilder.BuildDynamic(Arg.Any<PromptBuildRequest>()).Returns("DYNAMIC_SECTIONS");
        _catalogService.BuildDynamicSupplement(Arg.Any<AgentContextSnapshot>()).Returns("DYNAMIC_SUPPLEMENT");

        string? capturedPrompt = null;
        _aiIntentService.SendWithToolsAsync(
            Arg.Any<string>(), Arg.Do<string>(prompt => capturedPrompt = prompt), Arg.Any<IReadOnlyList<object>>(), Arg.Any<Guid>(),
            Arg.Any<byte[]?>(), Arg.Any<string?>(),
            Arg.Any<IReadOnlyList<ChatHistoryMessage>?>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "ok" }));

        var handler = CreateHandler();
        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Hello"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        capturedPrompt.Should().NotBeNull();
        var staticSections = capturedPrompt!.IndexOf("STATIC_SECTIONS", StringComparison.Ordinal);
        var staticSupplement = capturedPrompt.IndexOf("STATIC_SUPPLEMENT", StringComparison.Ordinal);
        var dynamicSections = capturedPrompt.IndexOf("DYNAMIC_SECTIONS", StringComparison.Ordinal);
        var dynamicSupplement = capturedPrompt.IndexOf("DYNAMIC_SUPPLEMENT", StringComparison.Ordinal);

        staticSections.Should().BeGreaterThanOrEqualTo(0);
        staticSections.Should().BeLessThan(staticSupplement);
        staticSupplement.Should().BeLessThan(dynamicSections);
        dynamicSections.Should().BeLessThan(dynamicSupplement);
    }

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
            Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
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
            Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Please confirm that deletion.", ToolCalls = null }));

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Delete my habit"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.PendingOperations.Should().HaveCount(1);
        result.Value.Operations.Should().ContainSingle(op => op.Status == AgentOperationStatus.PendingConfirmation);
        await _operationExecutor.Received(1).ExecuteAsync(
            Arg.Is<AgentExecuteOperationRequest>(op => op.OperationId == "delete_habit"),
            Arg.Any<CancellationToken>());
    }

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
            Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Here are your habits.", ToolCalls = null }));

        var command = new ProcessUserChatCommand(UserId, "Show my habits");
        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Actions.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_ReadOnlyToolOnIsolatedScope_StillDispatchesThroughOperationExecutor()
    {
        SetupUserAndPayGate();

        var readOnly = FakeTool("query_habits");
        readOnly.ExecuteAsync(Arg.Any<JsonElement>(), UserId, Arg.Any<CancellationToken>())
            .Returns(new ToolResult(true, EntityName: "Found habits"));

        var handler = CreateHandler(readOnly);

        var toolCallArgs = JsonDocument.Parse("{}").RootElement;
        SetupAiResponse(new AiResponse
        {
            ToolCalls = [new AiToolCall("query_habits", "call_1", toolCallArgs)],
            ConversationContext = TestConversationContext
        });

        _aiIntentService.ContinueWithToolResultsAsync(
            Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Here you go.", ToolCalls = null }));

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Show my habits"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _operationExecutor.Received(1).ExecuteAsync(
            Arg.Is<AgentExecuteOperationRequest>(op => op.OperationId == "query_habits"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ReadOnlyToolWithRelatedSurfaces_SurfacesThemOnResponse()
    {
        SetupUserAndPayGate();

        var describeTool = Substitute.For<IAiTool>();
        describeTool.Name.Returns("describe_feature");
        describeTool.Description.Returns("Explains a feature");
        describeTool.IsReadOnly.Returns(true);
        describeTool.GetParameterSchema().Returns(new { type = "object" });
        describeTool.ExecuteAsync(Arg.Any<JsonElement>(), UserId, Arg.Any<CancellationToken>())
            .Returns(new ToolResult(true, Payload: new
            {
                key = "streaks",
                related_surfaces = new[] { "gamification", "today" },
                markdown = "# Streaks"
            }));

        var handler = CreateHandler(describeTool);

        var aiResponseWithTool = new AiResponse
        {
            ToolCalls = [new AiToolCall("describe_feature", "call_1", JsonDocument.Parse("{}").RootElement)],
            ConversationContext = TestConversationContext
        };
        SetupAiResponse(aiResponseWithTool);

        _aiIntentService.ContinueWithToolResultsAsync(
            Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Streaks work like this.", ToolCalls = null }));

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "How do streaks work?"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Actions.Should().BeEmpty();
        result.Value.RelatedSurfaces.Should().Equal("gamification", "today");
    }

    [Fact]
    public async Task Handle_MutatingOnlyTurn_LeavesRelatedSurfacesNull()
    {
        SetupUserAndPayGate();

        var mockTool = Substitute.For<IAiTool>();
        mockTool.Name.Returns("create_habit");
        mockTool.Description.Returns("Creates a habit");
        mockTool.IsReadOnly.Returns(false);
        mockTool.GetParameterSchema().Returns(new { type = "object" });
        mockTool.ExecuteAsync(Arg.Any<JsonElement>(), UserId, Arg.Any<CancellationToken>())
            .Returns(new ToolResult(true, EntityId: Guid.NewGuid().ToString(), EntityName: "Morning Run"));

        var handler = CreateHandler(mockTool);

        var aiResponseWithTool = new AiResponse
        {
            ToolCalls = [new AiToolCall("create_habit", "call_1", JsonDocument.Parse("{}").RootElement)],
            ConversationContext = TestConversationContext
        };
        SetupAiResponse(aiResponseWithTool);

        _aiIntentService.ContinueWithToolResultsAsync(
            Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Created your habit!", ToolCalls = null }));

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Create a habit"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.RelatedSurfaces.Should().BeNull();
    }

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
            Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Done!", ToolCalls = null }));

        var command = new ProcessUserChatCommand(UserId, "Create and log habits");
        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Actions.Should().HaveCount(2);
        result.Value.Actions.Select(a => a.Type).Should().Contain("CreateHabit");
        result.Value.Actions.Select(a => a.Type).Should().Contain("LogHabit");
    }

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

        var firstResponse = new AiResponse
        {
            ToolCalls = [new AiToolCall("create_habit", "call_1", toolCallArgs)],
            ConversationContext = TestConversationContext
        };
        SetupAiResponse(firstResponse);

        var secondResponse = new AiResponse
        {
            ToolCalls = [new AiToolCall("create_habit", "call_2", toolCallArgs)],
            ConversationContext = TestConversationContext
        };

        var finalResponse = new AiResponse { TextMessage = "Created two habits!", ToolCalls = null };

        _aiIntentService.ContinueWithToolResultsAsync(
            Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(secondResponse), Result.Success(finalResponse));

        var command = new ProcessUserChatCommand(UserId, "Create two habits");
        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Actions.Should().HaveCount(2);
    }

    [Fact]
    public async Task Handle_UnknownToolCall_ReturnsFailedAction()
    {
        SetupUserAndPayGate();
        var handler = CreateHandler();
        var toolCallArgs = JsonDocument.Parse("{}").RootElement;
        var aiResponseWithTool = new AiResponse
        {
            ToolCalls = [new AiToolCall("nonexistent_tool", "call_1", toolCallArgs)],
            ConversationContext = TestConversationContext
        };
        SetupAiResponse(aiResponseWithTool);

        _aiIntentService.ContinueWithToolResultsAsync(
            Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Sorry!", ToolCalls = null }));

        var command = new ProcessUserChatCommand(UserId, "Do something");
        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Actions.Should().HaveCount(1);
        result.Value.Actions[0].Status.Should().Be(ActionStatus.Failed);
        result.Value.Actions[0].Error.Should().Contain("Unknown tool");
    }

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
            Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Error occurred.", ToolCalls = null }));

        var command = new ProcessUserChatCommand(UserId, "Create habit");
        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Actions.Should().HaveCount(1);
        result.Value.Actions[0].Status.Should().Be(ActionStatus.Failed);
        result.Value.Actions[0].Error.Should().Be("An unexpected error occurred.");
    }

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
            Arg.Any<Guid>(),
            Arg.Is<byte[]?>(b => b != null && b.Length == 4),
            Arg.Is<string?>(s => s == "image/png"),
            Arg.Any<IReadOnlyList<ChatHistoryMessage>?>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(),
            Arg.Any<CancellationToken>());
    }

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
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<object>>(), Arg.Any<Guid>(),
            Arg.Any<byte[]?>(), Arg.Any<string?>(),
            Arg.Is<IReadOnlyList<ChatHistoryMessage>?>(h => h != null && h.Count == 2),
            Arg.Any<Func<AiStreamEvent, Task>?>(),
            Arg.Any<CancellationToken>());
    }

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
            Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
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

        _aiIntentService.ContinueWithToolResultsAsync(
            Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(toolResponse));

        var command = new ProcessUserChatCommand(UserId, "Keep going");
        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Actions.Count.Should().BeLessThanOrEqualTo(6);
    }

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
            Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<AiResponse>("Connection lost"));

        var command = new ProcessUserChatCommand(UserId, "Create habit");
        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Actions.Should().HaveCount(1);
    }

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
        await _userFactRepo.DidNotReceive().FindAsync(
            Arg.Any<Expression<Func<UserFact, bool>>>(),
            Arg.Any<CancellationToken>());
    }

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

    [Fact]
    public async Task Handle_ToolCallOrdering_CreateHabitRunsBeforeSubHabit()
    {
        SetupUserAndPayGate();

        var executionOrder = new List<string>();

        var createTool = Substitute.For<IAiTool>();
        createTool.Name.Returns("create_habit");
        createTool.Description.Returns("Creates");
        createTool.IsReadOnly.Returns(false);
        createTool.Order.Returns(0);
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
        subTool.Order.Returns(1);
        subTool.GetParameterSchema().Returns(new { type = "object" });
        subTool.ExecuteAsync(Arg.Any<JsonElement>(), UserId, Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                executionOrder.Add("create_sub_habit");
                return new ToolResult(true, EntityId: Guid.NewGuid().ToString(), EntityName: "Child");
            });

        var handler = CreateHandler(createTool, subTool);

        var toolCallArgs = JsonDocument.Parse("{}").RootElement;
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
            Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Done!", ToolCalls = null }));

        var command = new ProcessUserChatCommand(UserId, "Create parent and child");
        await handler.Handle(command, CancellationToken.None);

        executionOrder.Should().Equal("create_habit", "create_sub_habit");
    }

    [Fact]
    public async Task Handle_ToolCallOrdering_RespectsToolOrderProperty()
    {
        SetupUserAndPayGate();

        var executionOrder = new List<string>();

        var createTool = OrderedTool("create_habit", 0, executionOrder);
        var subTool = OrderedTool("create_sub_habit", 1, executionOrder);
        var assignTool = OrderedTool("assign_tags", 2, executionOrder);

        var handler = CreateHandler(createTool, subTool, assignTool);

        var toolCallArgs = JsonDocument.Parse("{}").RootElement;
        var aiResponseWithTools = new AiResponse
        {
            ToolCalls =
            [
                new AiToolCall("assign_tags", "call_3", toolCallArgs),
                new AiToolCall("create_sub_habit", "call_2", toolCallArgs),
                new AiToolCall("create_habit", "call_1", toolCallArgs)
            ],
            ConversationContext = TestConversationContext
        };
        SetupAiResponse(aiResponseWithTools);

        _aiIntentService.ContinueWithToolResultsAsync(
            Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Done!", ToolCalls = null }));

        var command = new ProcessUserChatCommand(UserId, "Create parent, child, and tag");
        await handler.Handle(command, CancellationToken.None);

        executionOrder.Should().Equal("create_habit", "create_sub_habit", "assign_tags");
    }

    private static IAiTool OrderedTool(string name, int order, List<string> executionOrder)
    {
        var tool = Substitute.For<IAiTool>();
        tool.Name.Returns(name);
        tool.Description.Returns(name);
        tool.IsReadOnly.Returns(false);
        tool.Order.Returns(order);
        tool.GetParameterSchema().Returns(new { type = "object" });
        tool.ExecuteAsync(Arg.Any<JsonElement>(), UserId, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                executionOrder.Add(name);
                return new ToolResult(true, EntityId: Guid.NewGuid().ToString(), EntityName: name);
            });
        return tool;
    }

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
            Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Hmm.", ToolCalls = null }));

        var command = new ProcessUserChatCommand(UserId, "Break it down");
        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Actions[0].SuggestedSubHabits.Should().BeNull();
    }

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

    [Fact]
    public async Task Handle_SuccessfulResponse_EchoesCorrelationId()
    {
        SetupUserAndPayGate();
        SetupAiResponse(new AiResponse { TextMessage = "Hi!", ToolCalls = null });
        var handler = CreateHandler();

        var command = new ProcessUserChatCommand(UserId, "Hello", CorrelationId: "req-abc-123");
        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.CorrelationId.Should().Be("req-abc-123");
    }

    [Fact]
    public async Task Handle_SupportRequestToolCall_AppendsTraceToMessage()
    {
        SetupUserAndPayGate();

        JsonElement? dispatchedArgs = null;
        var supportTool = Substitute.For<IAiTool>();
        supportTool.Name.Returns("send_support_request");
        supportTool.Description.Returns("Sends a support request");
        supportTool.IsReadOnly.Returns(false);
        supportTool.GetParameterSchema().Returns(new { type = "object" });
        supportTool.ExecuteAsync(Arg.Any<JsonElement>(), UserId, Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                dispatchedArgs = callInfo.ArgAt<JsonElement>(0);
                return new ToolResult(true, EntityName: "Support request sent");
            });

        var handler = CreateHandler(supportTool);

        var toolCallArgs = JsonDocument.Parse("""{"message":"My app keeps crashing."}""").RootElement;
        var aiResponseWithTool = new AiResponse
        {
            ToolCalls = [new AiToolCall("send_support_request", "call_1", toolCallArgs)],
            ConversationContext = TestConversationContext
        };
        SetupAiResponse(aiResponseWithTool);

        _aiIntentService.ContinueWithToolResultsAsync(
            Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Sent!", ToolCalls = null }));

        var command = new ProcessUserChatCommand(UserId, "Contact support", CorrelationId: "req-trace-9");
        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        dispatchedArgs.Should().NotBeNull();
        var dispatchedMessage = dispatchedArgs!.Value.GetProperty("message").GetString();
        dispatchedMessage.Should().Be("My app keeps crashing.\n\n[trace: req-trace-9]");
    }

    [Fact]
    public async Task Handle_NonSupportToolCall_DispatchesArgsUnchangedWhenCorrelationIdPresent()
    {
        SetupUserAndPayGate();

        JsonElement? dispatchedArgs = null;
        var createTool = Substitute.For<IAiTool>();
        createTool.Name.Returns("create_habit");
        createTool.Description.Returns("Creates a habit");
        createTool.IsReadOnly.Returns(false);
        createTool.GetParameterSchema().Returns(new { type = "object" });
        createTool.ExecuteAsync(Arg.Any<JsonElement>(), UserId, Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                dispatchedArgs = callInfo.ArgAt<JsonElement>(0);
                return new ToolResult(true, EntityId: Guid.NewGuid().ToString(), EntityName: "Run");
            });

        var handler = CreateHandler(createTool);

        const string rawArgs = """{"message":"not a support message","title":"Run"}""";
        var toolCallArgs = JsonDocument.Parse(rawArgs).RootElement;
        var aiResponseWithTool = new AiResponse
        {
            ToolCalls = [new AiToolCall("create_habit", "call_1", toolCallArgs)],
            ConversationContext = TestConversationContext
        };
        SetupAiResponse(aiResponseWithTool);

        _aiIntentService.ContinueWithToolResultsAsync(
            Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Done!", ToolCalls = null }));

        var command = new ProcessUserChatCommand(UserId, "Create a habit", CorrelationId: "req-trace-9");
        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        dispatchedArgs.Should().NotBeNull();
        dispatchedArgs!.Value.GetRawText().Should().Be(rawArgs);
    }

    [Fact]
    public async Task Handle_ReadOnlyToolsDispatchConcurrently_WhileWriteToolsRunSequentiallyInOrder()
    {
        SetupUserAndPayGate();

        using var readOnlyBarrier = new Barrier(2);
        var bothReadOnlyInFlight = false;

        var firstReadOnly = ConcurrentReadOnlyTool("query_habits", readOnlyBarrier, isInFlight => bothReadOnlyInFlight = isInFlight);
        var secondReadOnly = ConcurrentReadOnlyTool("get_streak_info", readOnlyBarrier, _ => { });

        var writeExecutionOrder = new List<string>();
        var maxConcurrentWrites = 0;
        var currentWrites = 0;
        var writeLock = new object();

        var firstWrite = SequentialWriteTool("create_habit", 0, writeExecutionOrder, writeLock,
            () => Interlocked.Increment(ref currentWrites), () => Interlocked.Decrement(ref currentWrites),
            () => maxConcurrentWrites = Math.Max(maxConcurrentWrites, currentWrites));
        var secondWrite = SequentialWriteTool("log_habit", 1, writeExecutionOrder, writeLock,
            () => Interlocked.Increment(ref currentWrites), () => Interlocked.Decrement(ref currentWrites),
            () => maxConcurrentWrites = Math.Max(maxConcurrentWrites, currentWrites));

        var handler = CreateHandler(firstReadOnly, secondReadOnly, firstWrite, secondWrite);

        var toolCallArgs = JsonDocument.Parse("{}").RootElement;
        SetupAiResponse(new AiResponse
        {
            ToolCalls =
            [
                new AiToolCall("log_habit", "call_w2", toolCallArgs),
                new AiToolCall("query_habits", "call_r1", toolCallArgs),
                new AiToolCall("create_habit", "call_w1", toolCallArgs),
                new AiToolCall("get_streak_info", "call_r2", toolCallArgs)
            ],
            ConversationContext = TestConversationContext
        });

        _aiIntentService.ContinueWithToolResultsAsync(
            Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Done!", ToolCalls = null }));

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Mixed turn"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        bothReadOnlyInFlight.Should().BeTrue("read-only tools must be dispatched concurrently, so both reach the barrier");
        writeExecutionOrder.Should().Equal("create_habit", "log_habit");
        maxConcurrentWrites.Should().Be(1, "write tools must run strictly sequentially on the ambient scope");
    }

    [Fact]
    public async Task Handle_UnknownToolAmongReadOnly_RoutesUnknownToSequentialDefaultAndStillSucceeds()
    {
        SetupUserAndPayGate();

        var readOnly = FakeTool("query_habits");
        readOnly.ExecuteAsync(Arg.Any<JsonElement>(), UserId, Arg.Any<CancellationToken>())
            .Returns(new ToolResult(true, EntityName: "Found habits"));

        var handler = CreateHandler(readOnly);

        var toolCallArgs = JsonDocument.Parse("{}").RootElement;
        SetupAiResponse(new AiResponse
        {
            ToolCalls =
            [
                new AiToolCall("query_habits", "call_r1", toolCallArgs),
                new AiToolCall("mystery_tool", "call_u1", toolCallArgs)
            ],
            ConversationContext = TestConversationContext
        });

        _aiIntentService.ContinueWithToolResultsAsync(
            Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Done.", ToolCalls = null }));

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Mixed unknown turn"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Actions.Should().ContainSingle(action => action.Status == ActionStatus.Failed && action.Error!.Contains("Unknown tool"));
    }

    [Fact]
    public async Task Handle_MultipleReadOnlyTools_PreserveDeterministicRelatedSurfacesOrderRegardlessOfCompletion()
    {
        SetupUserAndPayGate();

        var slowFirst = Substitute.For<IAiTool>();
        slowFirst.Name.Returns("describe_feature");
        slowFirst.Description.Returns("Explains a feature");
        slowFirst.IsReadOnly.Returns(true);
        slowFirst.GetParameterSchema().Returns(new { type = "object" });
        slowFirst.ExecuteAsync(Arg.Any<JsonElement>(), UserId, Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await Task.Delay(40);
                return new ToolResult(true, Payload: new { related_surfaces = new[] { "gamification", "today" } });
            });

        var fastSecond = Substitute.For<IAiTool>();
        fastSecond.Name.Returns("get_streak_info");
        fastSecond.Description.Returns("Streak info");
        fastSecond.IsReadOnly.Returns(true);
        fastSecond.GetParameterSchema().Returns(new { type = "object" });
        fastSecond.ExecuteAsync(Arg.Any<JsonElement>(), UserId, Arg.Any<CancellationToken>())
            .Returns(new ToolResult(true, Payload: new { related_surfaces = new[] { "habits" } }));

        var handler = CreateHandler(slowFirst, fastSecond);

        var toolCallArgs = JsonDocument.Parse("{}").RootElement;
        SetupAiResponse(new AiResponse
        {
            ToolCalls =
            [
                new AiToolCall("describe_feature", "call_1", toolCallArgs),
                new AiToolCall("get_streak_info", "call_2", toolCallArgs)
            ],
            ConversationContext = TestConversationContext
        });

        _aiIntentService.ContinueWithToolResultsAsync(
            Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Here you go.", ToolCalls = null }));

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Two lookups"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.RelatedSurfaces.Should().Equal("gamification", "today", "habits");
    }

    private static IAiTool ConcurrentReadOnlyTool(string name, Barrier barrier, Action<bool> onBothInFlight)
    {
        var tool = Substitute.For<IAiTool>();
        tool.Name.Returns(name);
        tool.Description.Returns($"{name} description");
        tool.IsReadOnly.Returns(true);
        tool.GetParameterSchema().Returns(new { type = "object" });
        tool.ExecuteAsync(Arg.Any<JsonElement>(), UserId, Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await Task.Yield();
                var bothArrived = barrier.SignalAndWait(TimeSpan.FromSeconds(5));
                if (bothArrived)
                    onBothInFlight(true);
                return new ToolResult(true, EntityName: name);
            });
        return tool;
    }

    private static IAiTool SequentialWriteTool(
        string name, int order, List<string> executionOrder, object executionLock,
        Action onEnter, Action onExit, Action sampleConcurrency)
    {
        var tool = Substitute.For<IAiTool>();
        tool.Name.Returns(name);
        tool.Description.Returns($"{name} description");
        tool.IsReadOnly.Returns(false);
        tool.Order.Returns(order);
        tool.GetParameterSchema().Returns(new { type = "object" });
        tool.ExecuteAsync(Arg.Any<JsonElement>(), UserId, Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                onEnter();
                sampleConcurrency();
                await Task.Delay(20);
                lock (executionLock)
                    executionOrder.Add(name);
                onExit();
                return new ToolResult(true, EntityId: Guid.NewGuid().ToString(), EntityName: name);
            });
        return tool;
    }
}
