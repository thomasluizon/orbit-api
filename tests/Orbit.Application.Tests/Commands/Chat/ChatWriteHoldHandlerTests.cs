using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
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
using Orbit.Application.Chat.Validators;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Queries;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Tests.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Application.Tests.Commands.Chat;

public sealed partial class ChatWriteHoldHandlerTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 4, 3);

    private readonly OrbitDbContext _dbContext;
    private readonly MemoryCache _flagCache = new(new MemoryCacheOptions());
    private readonly Guid _userId;
    private readonly User _user;
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
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
    private readonly AiToolRegistry _toolRegistry;
    private readonly PendingOperationChangePreviewer _previewer;
    private readonly IHabitEmojiInferenceService _emojiInference = Substitute.For<IHabitEmojiInferenceService>();

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

        _createHabitTool = new CreateHabitTool(_userDateService, Orbit.Tests.Shared.HabitToolCommandMediator.Create(_habitRepo, _tagRepo, _goalRepo, _userDateService, _payGate, _unitOfWork));
        var emojiTool = new BulkUpdateHabitEmojisTool(_habitRepo, _emojiInference, _unitOfWork,
            NullLogger<BulkUpdateHabitEmojisTool>.Instance);
        var updateTool = new UpdateHabitTool(_habitRepo, _userDateService, _mediator);
        var bulkUpdateTool = new BulkUpdateHabitsTool(_mediator);
        _toolRegistry = new AiToolRegistry([
            _createHabitTool,
            new BulkCreateHabitsTool(_mediator),
            new CreateSubHabitTool(_mediator),
            new LogHabitTool(_mediator, _habitRepo, _userDateService),
            new UnlogHabitTool(_habitRepo, _userDateService, _unitOfWork, _payGate,
                _userStreakService, Substitute.For<IGoalCompletionService>()),
            emojiTool,
            updateTool,
            bulkUpdateTool
        ]);
        _previewer = new PendingOperationChangePreviewer(_habitRepo, _goalRepo, _tagRepo, _userDateService,
            _toolRegistry);
        _catalogService = new AgentCatalogService(_toolRegistry.GetAll());
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
            new AgentPolicyEvaluator(_dbContext, _catalogService, _pendingOperationStore, stepUpBridge, settings,
                new FeatureFlagService(_dbContext, _flagCache)),
            Substitute.For<IAgentAuditService>(),
            ownership,
            stepUpBridge,
            _toolRegistry,
            _previewer,
            _pendingOperationStore,
            _unitOfWork,
            NullLogger<AgentOperationExecutor>.Instance);

        SetupDependencies();
    }

    public void Dispose()
    {
        _flagCache.Dispose();
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Handle_BulkFallback_SupersedesCoveredSinglesAndApprovalUpdatesFullSet()
    {
        var habits = SetupUpdateHabits();
        AiResponse Single(int index) => new()
        {
            ToolCalls = [new AiToolCall("update_habit", $"single_{index}", JsonSerializer.SerializeToElement(new
            {
                habit_id = habits[index].Id, description = "Shared"
            }))],
            ConversationContext = new()
        };
        ScriptToolCall(Single(0).ToolCalls![0].Args.GetRawText(), "update_habit");
        var forwarded = new List<AiToolCallResult>();
        var round = 0;
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                forwarded.AddRange(call.ArgAt<IReadOnlyList<AiToolCallResult>>(1));
                return Result.Success(++round switch
                {
                    1 => Single(1),
                    2 => Single(2),
                    3 => new AiResponse
                    {
                        ToolCalls = [new AiToolCall("bulk_update_habits", "bulk", JsonSerializer.SerializeToElement(new
                        {
                            filter = new { all = true }, updates = new { description = "Shared" }
                        }))],
                        ConversationContext = new()
                    },
                    _ => new AiResponse { TextMessage = "Review the changes to all three habits." }
                });
            });
        var held = new List<AgentExecuteOperationResponse>();

        var result = await CreateHandler(HoldReplayWrites(held)).Handle(new ProcessUserChatCommand(_userId,
            "Change the description of all my habits to Shared",
            ClientContext: new AgentClientContext(SupportsPendingOperationChanges: true)), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        forwarded.Should().HaveCount(4);
        forwarded[2].Success.Should().BeFalse();
        forwarded[2].Error.Should().Contain("bulk_update_habits");
        held.Select(item => item.Operation.OperationId).Should().Equal("update_habit", "update_habit", "bulk_update_habits");
        var pending = result.Value.PendingOperations.Should().ContainSingle().Subject;
        pending.ChangeTargetCount.Should().Be(3);
        pending.Items!.Select(item => item.EntityId).Should().BeEquivalentTo(habits.Select(habit => habit.Id));
        habits.Should().OnlyContain(habit => habit.Description == null);
        foreach (var single in held.Take(2))
        {
            _pendingOperationStore.GetExecution(_userId, single.PendingOperation!.Id).Should().BeNull();
            _pendingOperationStore.Confirm(_userId, single.PendingOperation.Id).Should().BeNull();
        }
        _dbContext.PendingAgentOperations.Where(item => item.ConsumedAtUtc == null)
            .Should().ContainSingle().Which.Id.Should().Be(pending.Id);
        var execution = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        var confirmation = _pendingOperationStore.Confirm(_userId, pending.Id)!;

        var approved = await _executor.ExecuteAsync(new AgentExecuteOperationRequest(_userId,
            execution.OperationId, execution.Arguments, execution.Surface, AgentAuthMethod.Jwt,
            ConfirmationToken: confirmation.ConfirmationToken));

        approved.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        habits.Should().OnlyContain(habit => habit.Description == "Shared");
    }

    [Theory]
    [InlineData("outside_filter")]
    [InlineData("different_fields")]
    [InlineData("previous_turn")]
    public async Task Handle_BulkPreview_PreservesProposalsItDoesNotReplace(string reason)
    {
        var habits = SetupUpdateHabits();
        var singleArgs = JsonSerializer.SerializeToElement(new { habit_id = habits[1].Id, description = "Shared" });
        var bulkArgs = reason == "outside_filter"
            ? JsonSerializer.SerializeToElement(new
            {
                filter = new { habit_ids = new[] { habits[0].Id, habits[2].Id } },
                updates = new { description = "Shared" }
            })
            : reason == "different_fields"
                ? JsonSerializer.SerializeToElement(new { filter = new { all = true }, updates = new { title = "Renamed" } })
                : JsonSerializer.SerializeToElement(new { filter = new { all = true }, updates = new { description = "Shared" } });
        var held = new List<AgentExecuteOperationResponse>();
        if (reason == "previous_turn")
        {
            held.Add(await _executor.ExecuteAsync(new AgentExecuteOperationRequest(_userId, "update_habit",
                singleArgs, AgentExecutionSurface.Chat, AgentAuthMethod.Jwt)));
            ScriptToolCall(bulkArgs.GetRawText(), "bulk_update_habits");
        }
        else
        {
            ScriptToolCall(singleArgs.GetRawText(), "update_habit");
            var round = 0;
            _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                    Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(),
                    Arg.Any<CancellationToken>())
                .Returns(_ => Result.Success(++round == 1 ? new AiResponse
                {
                    ToolCalls = [new AiToolCall("bulk_update_habits", "bulk", bulkArgs)],
                    ConversationContext = new()
                } : new AiResponse { TextMessage = "Review the proposals." }));
        }

        var result = await CreateHandler(HoldReplayWrites(held)).Handle(new ProcessUserChatCommand(_userId,
            "Prepare these changes", ClientContext: new AgentClientContext(SupportsPendingOperationChanges: true)),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        held.Should().HaveCount(2);
        var single = held[0].PendingOperation!;
        var bulk = held[1].PendingOperation!;
        var combined = result.Value.PendingOperations.Should().ContainSingle().Which;
        if (reason == "previous_turn") combined.Id.Should().Be(bulk.Id);
        else
        {
            _pendingOperationStore.GetExecution(_userId, combined.Id)!.OperationId.Should().Be(Orbit.Application.Chat.AccountChangeSet.OperationId);
            var members = Orbit.Application.Chat.AccountChangeSet.Read(_pendingOperationStore.GetExecution(_userId, combined.Id)!.Arguments)!;
            members.Select(member => member.PendingOperationId).Should().BeEquivalentTo([single.Id, bulk.Id]);
            combined.Items.Should().HaveCount(single.Items!.Count + bulk.Items!.Count);
        }
        var stored = _pendingOperationStore.GetExecution(_userId, single.Id)!;
        stored.Arguments.GetRawText().Should().Be(singleArgs.GetRawText());
        stored.PreviewFingerprint.Should().Be(single.PreviewFingerprint);
        _pendingOperationStore.GetExecution(_userId, bulk.Id).Should().NotBeNull();
        _dbContext.PendingAgentOperations.Should().OnlyContain(item => item.ConsumedAtUtc == null && item.RevisionNumber == 1);
        if (reason == "previous_turn") _pendingOperationStore.Confirm(_userId, single.Id).Should().NotBeNull();
        else _pendingOperationStore.Confirm(_userId, single.Id).Should().BeNull();
        habits.Should().OnlyContain(habit => habit.Description == null);
    }

    [Fact]
    public async Task Handle_BulkPreviewInSameRound_RemovesCoveredSinglesBeforeAccumulatingResponse()
    {
        var habits = SetupUpdateHabits();
        ScriptToolCall("{}", "bulk_update_habits");
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse
            {
                ToolCalls = habits.Take(2).Select((habit, index) => new AiToolCall("update_habit", $"single_{index}",
                        JsonSerializer.SerializeToElement(new { habit_id = habit.Id, description = "Shared" })))
                    .Append(new AiToolCall("bulk_update_habits", "bulk", JsonSerializer.SerializeToElement(new
                    {
                        filter = new { all = true },
                        updates = new { description = "Shared" }
                    }))).ToList(),
                ConversationContext = new()
            }));
        var held = new List<AgentExecuteOperationResponse>();

        var result = await CreateHandler(HoldReplayWrites(held)).Handle(new ProcessUserChatCommand(_userId,
            "Change all descriptions", ClientContext: new AgentClientContext(SupportsPendingOperationChanges: true)),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        held.Should().HaveCount(3);
        var bulk = result.Value.PendingOperations.Should().ContainSingle().Subject;
        bulk.ChangeTargetCount.Should().Be(3);
        foreach (var single in held.Take(2))
            _pendingOperationStore.GetExecution(_userId, single.PendingOperation!.Id).Should().BeNull();
        _pendingOperationStore.GetExecution(_userId, bulk.Id).Should().NotBeNull();
    }

    private Habit[] SetupUpdateHabits()
    {
        var habits = new[] { "A", "B", "C" }.Select(title => Habit.Create(new HabitCreateParams(
            _userId, title, null, null, Today)).Value).ToArray();
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(call => habits.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToList());
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => habits.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToList());
        _habitRepo.FindTrackedAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(call => habits.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToList());
        _unitOfWork.PassThroughTransactions<Result<int>>();
        _mediator.Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var bulkHandler = new BulkUpdateHabitsCommandHandler(_habitRepo, _userDateService, _payGate,
            _unitOfWork, _flagCache, NullLogger<BulkUpdateHabitsCommandHandler>.Instance);
        _mediator.Send(Arg.Any<BulkUpdateHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => bulkHandler.Handle(call.Arg<BulkUpdateHabitsCommand>(), call.Arg<CancellationToken>()));
        return habits;
    }

    private IAgentOperationExecutor HoldReplayWrites(List<AgentExecuteOperationResponse> held)
    {
        var executor = Substitute.For<IAgentOperationExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var result = await _executor.ExecuteAsync(call.Arg<AgentExecuteOperationRequest>(), call.Arg<CancellationToken>());
                held.Add(result);
                return result;
            });
        return executor;
    }

    [Fact]
    public async Task Handle_InferredEmojis_PreviewsFullSetAndApprovalAppliesExactlyThoseValues()
    {
        var habits = Enumerable.Range(1, 251).Select(index => Habit.Create(new HabitCreateParams(
            _userId, $"Habit {index}", FrequencyUnit.Day, 1, Today)).Value).ToArray();
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(call => habits.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToList());
        _habitRepo.FindTrackedAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(call => habits.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToList());
        _unitOfWork.PassThroughTransactions<int>();
        var expected = habits.Select((habit, index) => (habit.Id, Emoji: index % 2 == 0 ? "📚" : "🏃"))
            .ToDictionary(item => item.Id, item => item.Emoji);
        _emojiInference.InferAsync(_userId, Arg.Any<IReadOnlyList<HabitEmojiInferenceInput>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => Result.Success<IReadOnlyDictionary<Guid, string>>(
                call.ArgAt<IReadOnlyList<HabitEmojiInferenceInput>>(1)
                    .ToDictionary(input => input.HabitId, input => expected[input.HabitId])));
        ScriptToolCall("""{"infer_from_title":true}""", "bulk_update_habit_emojis");

        var held = await CreateHandler().Handle(new ProcessUserChatCommand(_userId,
            "Make all emojis sensible", ClientContext: new AgentClientContext(SupportsPendingOperationChanges: true)),
            CancellationToken.None);

        held.IsSuccess.Should().BeTrue();
        var pending = held.Value.PendingOperations.Should().ContainSingle().Subject;
        pending.ChangeTargetCount.Should().Be(habits.Length);
        pending.Items.Should().HaveCount(habits.Length);
        pending.Items!.Should().AllSatisfy(item => item.Fields.Should().ContainSingle().Which.NewValue
            .Should().Be(expected[item.EntityId!.Value]));
        habits.Should().OnlyContain(habit => habit.Emoji == null);
        var execution = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        var approvedItems = execution.Arguments.GetProperty("revised_items");
        approvedItems.GetArrayLength().Should().Be(habits.Length);
        execution.Arguments.GetProperty("infer_from_title").GetBoolean().Should().BeFalse();
        await _emojiInference.Received(11).InferAsync(_userId,
            Arg.Is<IReadOnlyList<HabitEmojiInferenceInput>>(items => items.Count <= 25), Arg.Any<CancellationToken>());
        _emojiInference.ClearReceivedCalls();
        _emojiInference.InferAsync(_userId, Arg.Any<IReadOnlyList<HabitEmojiInferenceInput>>(),
                Arg.Any<CancellationToken>())
            .Returns(Result.Failure<IReadOnlyDictionary<Guid, string>>("No further inference allowed"));
        var confirmation = _pendingOperationStore.Confirm(_userId, pending.Id)!;

        var executed = await _executor.ExecuteAsync(new AgentExecuteOperationRequest(_userId,
            execution.OperationId, execution.Arguments, execution.Surface, AgentAuthMethod.Jwt,
            ConfirmationToken: confirmation.ConfirmationToken));

        executed.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        habits.Should().AllSatisfy(habit => habit.Emoji.Should().Be(expected[habit.Id]));
        var payload = JsonSerializer.SerializeToElement(executed.Operation.Payload);
        payload.GetProperty("applied_count").GetInt32().Should().Be(habits.Length);
        payload.GetProperty("total_matched").GetInt32().Should().Be(habits.Length);
        payload.GetProperty("partial").GetBoolean().Should().BeFalse();
        await _emojiInference.DidNotReceiveWithAnyArgs().InferAsync(default, default!, default);
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

    [Theory]
    [InlineData("log_habit", false)]
    [InlineData("unlog_habit", true)]
    public async Task Handle_LogAndUnlog_HoldTheSameApprovalPreviewWithoutChangingHabit(
        string operationId, bool logged)
    {
        var habit = Habit.Create(new HabitCreateParams(_userId, "Beber água", FrequencyUnit.Day, 1, Today)).Value;
        if (logged)
            habit.Log(Today).IsSuccess.Should().BeTrue();
        var logIds = habit.Logs.Select(log => log.Id).ToArray();
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(call => new[] { habit }.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToList().AsReadOnly());
        _habitRepo.FindOneTrackedAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(habit);
        _catalogService.GetCapabilityByChatTool(operationId)!.ConfirmationRequirement
            .Should().Be(AgentConfirmationRequirement.None);
        ScriptToolCall(JsonSerializer.Serialize(new { habit_id = habit.Id }), operationId);

        var result = await CreateHandler().Handle(new ProcessUserChatCommand(
            _userId, logged ? "Desmarque o de hoje" : "Marque o de hoje",
            ClientContext: new AgentClientContext(SupportsPendingOperationChanges: true)), default);

        result.IsSuccess.Should().BeTrue();
        var pending = result.Value.PendingOperations.Should().ContainSingle().Subject;
        pending.ConfirmationRequirement.Should().Be(AgentConfirmationRequirement.FreshConfirmation);
        pending.PreviewFingerprint.Should().NotBeNullOrWhiteSpace();
        var item = pending.Items.Should().ContainSingle().Subject;
        item.EntityId.Should().Be(habit.Id);
        item.EntityName.Should().Be(habit.Title);
        item.RemovesData.Should().BeFalse();
        if (logged)
            item.Fields.Should().Contain(field => field.Field == "date" && field.NewValue == "2026-04-03");
        result.Value.Actions.Should().BeEmpty();
        result.Value.Operations.Should().NotContain(operation => operation.OperationId == operationId);
        habit.Logs.Select(log => log.Id).Should().Equal(logIds);
        habit.Logs.Should().OnlyContain(log => !log.IsDeleted);
        await _mediator.DidNotReceive().Send(Arg.Any<Orbit.Application.Habits.Commands.LogHabitCommand>(),
            Arg.Any<CancellationToken>());
        await _userStreakService.DidNotReceive().RecalculateAsync(_userId, false, Arg.Any<CancellationToken>());
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
    public async Task Handle_ApprovalAfterTwentyFiveMessages_ExecutesTheHeldOperationOnce()
    {
        ScriptToolCall("""{"title":"Beber agua","frequency_unit":"Day","frequency_quantity":1}""");
        var handler = CreateHandler();
        var held = await handler.Handle(new(_userId, "Crie o habito",
            ClientContext: new AgentClientContext(SupportsPendingOperationChanges: true)), default);
        var pending = held.Value.PendingOperations.Should().ContainSingle().Subject;
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Continuing our conversation." }));
        var history = new List<ChatHistoryMessage> { new("user", "Crie o habito") };
        for (var index = 0; index < 25; index++)
        {
            var message = $"Another routine question {index}";
            (await handler.Handle(new(_userId, message, History: history.ToList()), default)).IsSuccess.Should().BeTrue();
            history.Add(new("user", message));
        }
        history.Count.Should().BeGreaterThan(AppConstants.MaxAiModelHistoryMessages);
        await _habitRepo.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        var execution = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        execution.PreviewFingerprint.Should().Be(pending.PreviewFingerprint);
        var confirmation = _pendingOperationStore.Confirm(_userId, pending.Id)!;

        var executed = await _executor.ExecuteAsync(new(_userId, execution.OperationId, execution.Arguments,
            execution.Surface, AgentAuthMethod.Jwt, ConfirmationToken: confirmation.ConfirmationToken), default);

        executed.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        executed.Operation.TargetName.Should().Be("Beber agua");
        await _habitRepo.Received(1).AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
        _pendingOperationStore.GetExecution(_userId, pending.Id).Should().BeNull();
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
        pending.Items.Should().ContainSingle();
        pending.PreviewFingerprint.Should().NotBeNullOrWhiteSpace();
        await _habitRepo.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    [Theory]
    [InlineData("""{"frequency_unit":"EveryDay"}""")]
    [InlineData("""{"frequency_quantity":"2"}""")]
    [InlineData("""{"reminder_times":[1,2,3,4,5,6,7,8,9,10,11]}""")]
    public async Task Handle_EditOutsideTheToolRules_IsRejectedAndExecuteRunsTheHeldArguments(string edits)
    {
        ScriptToolCall("""{"title":"Beber agua","frequency_unit":"Day","frequency_quantity":1,"due_time":"08:00","reminder_times":[15]}""");
        var held = await CreateHandler().Handle(new ProcessUserChatCommand(
            _userId, "Crie o habito", ClientContext: new AgentClientContext(
                SupportsPendingOperationChanges: true)), CancellationToken.None);
        var pending = held.Value.PendingOperations!.Single();
        using var document = JsonDocument.Parse(edits);

        var revision = await new PendingOperationRevisionService(_pendingOperationStore, _previewer,
            new RevisePendingOperationRequestValidator(), _toolRegistry).ReviseAsync(_userId, pending.Id,
            new RevisePendingOperationRequest(pending.PreviewFingerprint!,
                [new RevisedPendingOperationItem("0", document.RootElement.Clone())]),
            CancellationToken.None);
        var execution = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        var confirmation = _pendingOperationStore.Confirm(_userId, pending.Id)!;
        var executed = await _executor.ExecuteAsync(new AgentExecuteOperationRequest(
            _userId,
            execution.OperationId,
            execution.Arguments,
            execution.Surface,
            AgentAuthMethod.Jwt,
            ConfirmationToken: confirmation.ConfirmationToken), CancellationToken.None);

        revision.IsSuccess.Should().BeFalse();
        revision.Error.Should().Be("invalid_revision");
        executed.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        await _habitRepo.Received(1).AddAsync(Arg.Is<Habit>(habit =>
            habit.FrequencyUnit == FrequencyUnit.Day && habit.FrequencyQuantity == 1
            && habit.ReminderTimes.SequenceEqual(new[] { 15 })), Arg.Any<CancellationToken>());
    }

    private ProcessUserChatCommandHandler CreateHandler(IAgentOperationExecutor? executor = null, IPayGateService? payGate = null)
    {
        var aiDeps = new ChatAiDependencies(_aiIntentService, _toolRegistry,
            _promptBuilder, _catalogService);
        var dataDeps = new ChatDataDependencies(_habitRepo, _goalRepo, _userRepo,
            _tagRepo, _templateRepo, _featureFlagService);
        var executionDeps = new ChatExecutionDependencies(
            _userDateService, _userStreakService, payGate ?? _payGate, _unitOfWork, _scopeFactory, executor ?? _executor,
            _clarificationStore, _goalProgressReadSyncer, _gamificationService, _mediator,
            _productAnalytics, _scheduleLogReader, Substitute.For<IIssuedAstraBlockStore>(),
            _pendingOperationStore);

        return new ProcessUserChatCommandHandler(dataDeps, aiDeps, executionDeps,
            Substitute.For<ILogger<ProcessUserChatCommandHandler>>(), Substitute.For<IAppConfigService>(), Orbit.Application.Tests.ModerationTestDouble.Clean());
    }

    private void ScriptToolCall(string arguments, string operationId = "create_habit")
    {
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse
            {
                ToolCalls = [new AiToolCall(operationId, "call_1",
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
        _unitOfWork.PassThroughTransactions<AgentExecuteOperationResponse>();
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
