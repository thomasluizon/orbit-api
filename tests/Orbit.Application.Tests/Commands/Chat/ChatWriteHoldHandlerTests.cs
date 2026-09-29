using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Queries;
using Orbit.Application.Tests.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Application.Tests.Commands.Chat;

public sealed class ChatWriteHoldHandlerTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 4, 3);

    private readonly OrbitDbContext _dbContext;
    private readonly Guid _userId;
    private readonly User _user;
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<UserFact> _userFactRepo = Substitute.For<IGenericRepository<UserFact>>();
    private readonly IGenericRepository<Tag> _tagRepo = Substitute.For<IGenericRepository<Tag>>();
    private readonly IGenericRepository<ChecklistTemplate> _templateRepo = Substitute.For<IGenericRepository<ChecklistTemplate>>();
    private readonly IFeatureFlagService _featureFlagService = Substitute.For<IFeatureFlagService>();
    private readonly IAiIntentService _aiIntentService = Substitute.For<IAiIntentService>();
    private readonly ISystemPromptBuilder _promptBuilder = Substitute.For<ISystemPromptBuilder>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IUserStreakService _userStreakService = Substitute.For<IUserStreakService>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IGoalProgressReadSyncer _goalProgressReadSyncer = Substitute.For<IGoalProgressReadSyncer>();
    private readonly IServiceScopeFactory _scopeFactory = Substitute.For<IServiceScopeFactory>();
    private readonly IPendingClarificationStore _clarificationStore = Substitute.For<IPendingClarificationStore>();
    private readonly IGamificationService _gamificationService = Substitute.For<IGamificationService>();
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IProductAnalytics _productAnalytics = Substitute.For<IProductAnalytics>();
    private readonly IHabitScheduleLogReader _scheduleLogReader = Substitute.For<IHabitScheduleLogReader>();
    private readonly AgentCatalogService _catalogService;
    private readonly PendingAgentOperationStore _pendingOperationStore;
    private readonly AgentOperationExecutor _executor;
    private readonly CreateHabitTool _createHabitTool;

    public ChatWriteHoldHandlerTests()
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"ChatWriteHoldHandlerTests_{Guid.NewGuid()}")
            .Options;
        _dbContext = new OrbitDbContext(options);
        _user = User.Create("Alex", "alex@test.com").Value;
        _userId = _user.Id;
        _dbContext.Users.Add(_user);
        _dbContext.SaveChanges();

        _createHabitTool = new CreateHabitTool(_habitRepo, _tagRepo, _goalRepo, _userDateService,
            _payGate, _unitOfWork);
        var toolRegistry = new AiToolRegistry([_createHabitTool]);
        _catalogService = new AgentCatalogService([_createHabitTool]);
        var settings = Options.Create(new AgentPlatformSettings());
        _pendingOperationStore = new PendingAgentOperationStore(_dbContext, settings);
        var stepUpBridge = Substitute.For<IAgentStepUpAuthorizationBridge>();
        stepUpBridge.GetRequiredConfirmationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((AgentConfirmationRequirement?)null);
        var ownership = Substitute.For<IAgentTargetOwnershipService>();
        ownership.GetDenialReasonAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<JsonElement>(),
            Arg.Any<CancellationToken>()).Returns((string?)null);
        _executor = new AgentOperationExecutor(
            _catalogService,
            new AgentPolicyEvaluator(_dbContext, _catalogService, _pendingOperationStore, stepUpBridge, settings),
            Substitute.For<IAgentAuditService>(),
            ownership,
            stepUpBridge,
            toolRegistry,
            new PendingOperationChangePreviewer(_habitRepo, _goalRepo, _tagRepo, _userDateService),
            _unitOfWork,
            NullLogger<AgentOperationExecutor>.Instance);

        SetupDependencies();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Handle_LowRiskChatWrite_HoldsItForApprovalAndWritesNothing()
    {
        ScriptToolCall("""{"title":"Beber agua","frequency_unit":"Day","frequency_quantity":1,"due_time":"08:00"}""");

        var result = await CreateHandler().Handle(new ProcessUserChatCommand(
            _userId,
            "Crie o habito Beber agua, todo dia as 08:00",
            ClientContext: new AgentClientContext(SupportsPendingOperationChanges: true)),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var pending = result.Value.PendingOperations.Should().ContainSingle().Subject;
        pending.ConfirmationRequirement.Should().Be(AgentConfirmationRequirement.FreshConfirmation);
        pending.Items.Should().ContainSingle().Which.EntityName.Should().Be("Beber agua");
        pending.PreviewFingerprint.Should().NotBeNullOrWhiteSpace();
        result.Value.Actions.Should().BeEmpty();
        result.Value.Operations.Should().NotContain(operation => operation.OperationId == "create_habit");
        await _habitRepo.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    [Fact]
    public async Task Handle_ApprovedChatWrite_RunsTheToolOneTime()
    {
        ScriptToolCall("""{"title":"Beber agua","frequency_unit":"Day","frequency_quantity":1}""");
        var held = await CreateHandler().Handle(new ProcessUserChatCommand(
            _userId, "Crie o habito", ClientContext: new AgentClientContext(
                SupportsPendingOperationChanges: true)), CancellationToken.None);
        var pendingId = held.Value.PendingOperations!.Single().Id;
        var execution = _pendingOperationStore.GetExecution(_userId, pendingId)!;
        var confirmation = _pendingOperationStore.Confirm(_userId, pendingId)!;

        var executed = await _executor.ExecuteAsync(new AgentExecuteOperationRequest(
            _userId,
            execution.OperationId,
            execution.Arguments,
            execution.Surface,
            AgentAuthMethod.Jwt,
            ConfirmationToken: confirmation.ConfirmationToken), CancellationToken.None);

        executed.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        executed.Operation.TargetName.Should().Be("Beber agua");
        await _habitRepo.Received(1).AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
        _pendingOperationStore.GetExecution(_userId, pendingId).Should().BeNull();
    }

    [Fact]
    public async Task Handle_HabitTitleWithNoSchedule_AsksTheQuestionBeforeTheCard()
    {
        ScriptToolCall("""{"title":"Rotina da manha"}""");

        var result = await CreateHandler().Handle(new ProcessUserChatCommand(
            _userId, "Crie a rotina da manha", ClientContext: new AgentClientContext(
                SupportsPendingOperationChanges: true)), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        await _clarificationStore.Received(1).CreateAsync(_userId, "create_habit",
            Arg.Any<string>(), "frequency_unit", Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await _habitRepo.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    [Fact]
    public async Task Handle_ClientWithNoPreviewSupport_StillHoldsTheWrite()
    {
        ScriptToolCall("""{"title":"Beber agua","frequency_unit":"Day","frequency_quantity":1}""");

        var result = await CreateHandler().Handle(new ProcessUserChatCommand(
            _userId, "Crie o habito"), CancellationToken.None);

        var pending = result.Value.PendingOperations.Should().ContainSingle().Subject;
        pending.Items.Should().BeNull();
        pending.PreviewFingerprint.Should().BeNull();
        await _habitRepo.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    private ProcessUserChatCommandHandler CreateHandler()
    {
        var aiDeps = new ChatAiDependencies(_aiIntentService, new AiToolRegistry([_createHabitTool]),
            _promptBuilder, _catalogService);
        var dataDeps = new ChatDataDependencies(_habitRepo, _goalRepo, _userRepo, _userFactRepo,
            _tagRepo, _templateRepo, _featureFlagService);
        var executionDeps = new ChatExecutionDependencies(
            _userDateService, _userStreakService, _payGate, _unitOfWork, _scopeFactory, _executor,
            _clarificationStore, _goalProgressReadSyncer, _gamificationService, _mediator,
            _productAnalytics, _scheduleLogReader);

        return new ProcessUserChatCommandHandler(dataDeps, aiDeps, executionDeps,
            Substitute.For<ILogger<ProcessUserChatCommandHandler>>());
    }

    private void ScriptToolCall(string arguments)
    {
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse
            {
                ToolCalls = [new AiToolCall("create_habit", "call_1",
                    JsonDocument.Parse(arguments).RootElement.Clone())],
                ConversationContext = new AiConversationContext
                {
                    Messages = new List<object>(),
                    Options = new object()
                }
            }));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Confirme para criar." }));
    }

    private void SetupDependencies()
    {
        _unitOfWork.PassThroughTransactions();
        _unitOfWork.PassThroughTransactions<ToolResult>();
        var scopeProvider = Substitute.For<IServiceProvider>();
        scopeProvider.GetService(typeof(IAgentOperationExecutor)).Returns(_executor);
        scopeProvider.GetService(typeof(IPendingClarificationStore)).Returns(_clarificationStore);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(scopeProvider);
        _scopeFactory.CreateScope().Returns(scope);

        _userRepo.GetByIdAsync(_userId, Arg.Any<CancellationToken>()).Returns(_user);
        _payGate.TryConsumeAiMessage(_userId, _unitOfWork, Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        _payGate.CanCreateHabits(_userId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        _userDateService.GetUserTodayAsync(_userId, Arg.Any<CancellationToken>()).Returns(Today);
        _userDateService.GetUserWeekStartDayAsync(_userId, Arg.Any<CancellationToken>()).Returns(1);
        _userStreakService.RecalculateAsync(_userId, cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new UserStreakState(1, 1, Today));
        _goalProgressReadSyncer.ComputeFreshValuesAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(),
            Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, int>());
        _promptBuilder.BuildStatic(Arg.Any<PromptBuildRequest>()).Returns("static prompt");
        _promptBuilder.BuildDynamic(Arg.Any<PromptBuildRequest>()).Returns("dynamic prompt");
        _scheduleLogReader.ReadDaysAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(),
            Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<HabitScheduleLogDay>());
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Habit>().AsReadOnly());
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Habit>().AsReadOnly());
    }
}
