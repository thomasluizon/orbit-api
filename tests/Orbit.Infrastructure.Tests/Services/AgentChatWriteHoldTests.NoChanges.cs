using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public partial class AgentChatWriteHoldTests
{
    [Theory]
    [InlineData("bulk_update_habit_emojis", """{"filter":{"has_emoji":false}}""")]
    [InlineData("bulk_update_habits", """{"filter":{"is_bad_habit":true},"updates":{"description":"New"}}""")]
    [InlineData("bulk_reschedule_habits", """{"filter":{"is_bad_habit":true},"due_date":"2026-10-25"}""")]
    [InlineData("bulk_log_habits", """{"filter":{"is_bad_habit":true}}""")]
    [InlineData("bulk_log_habits", """{"filter":{"is_bad_habit":true},"dates":["2026-10-24"]}""")]
    [InlineData("bulk_skip_habits", """{"filter":{"is_bad_habit":true}}""")]
    [InlineData("bulk_delete_habits", """{"filter":{"is_bad_habit":true}}""")]
    [InlineData("bulk_update_habit_emojis", """{"filter":{"all":true},"emoji":"📚"}""")]
    [InlineData("bulk_update_habit_emojis", """{"filter":{"all":true},"replace_existing":true}""")]
    public async Task ExecuteAsync_ZeroBulkTargets_SucceedsWithoutApprovalOrMutation(string operationId, string arguments)
    {
        var (tools, previewer, inference, work) = await NoChangesToolsAsync(operationId);
        var before = JsonSerializer.Serialize(await _dbContext.Habits.OrderBy(habit => habit.Id).ToListAsync());

        var response = await CreateExecutor(tools, previewer).ExecuteAsync(new AgentExecuteOperationRequest(
            _userId, operationId, Parse(arguments), AgentExecutionSurface.Chat, AgentAuthMethod.Jwt));

        response.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        response.Operation.PolicyReason.Should().BeNull();
        response.Operation.PendingOperationId.Should().BeNull();
        response.PolicyDenial.Should().BeNull();
        response.PendingOperation.Should().BeNull();
        foreach (var pending in _dbContext.PendingAgentOperations)
        {
            _pendingOperationStore.IsAwaitingConfirmation(_userId, pending.Id).Should().BeFalse();
            _pendingOperationStore.Confirm(_userId, pending.Id).Should().BeNull();
        }
        JsonSerializer.Serialize(await _dbContext.Habits.OrderBy(habit => habit.Id).ToListAsync()).Should().Be(before);
        await work.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        if (!arguments.Contains("replace_existing", StringComparison.Ordinal))
            await inference.DidNotReceiveWithAnyArgs().InferAsync(default, default!, default);
    }

    [Theory]
    [InlineData("en", "add an icon to every habit without one", "All your habits already have an icon.", false)]
    [InlineData("pt-BR", "coloca um ícone em todo hábito que não tem", "Todos os seus hábitos já têm ícone.", false)]
    [InlineData("en", "add an icon to every habit without one", "All your habits already have an icon.", true)]
    [InlineData("pt-BR", "coloca um ícone em todo hábito que não tem", "Todos os seus hábitos já têm ícone.", true)]
    public Task Handle_IconRequestWithZeroTargets_ReturnsShortLocalizedSuccess(
        string language, string message, string expected, bool supportsParts) =>
        AssertNoChangesChatAsync("bulk_update_habit_emojis", """{"filter":{"has_emoji":false}}""",
            language, message, expected, supportsParts);

    [Theory]
    [InlineData("bulk_update_habits", """{"filter":{"is_bad_habit":true},"updates":{"description":"New"}}""", "en")]
    [InlineData("bulk_update_habits", """{"filter":{"is_bad_habit":true},"updates":{"description":"New"}}""", "pt-BR")]
    [InlineData("bulk_reschedule_habits", """{"filter":{"is_bad_habit":true},"due_date":"2026-10-25"}""", "en")]
    [InlineData("bulk_reschedule_habits", """{"filter":{"is_bad_habit":true},"due_date":"2026-10-25"}""", "pt-BR")]
    [InlineData("bulk_log_habits", """{"filter":{"is_bad_habit":true}}""", "en")]
    [InlineData("bulk_log_habits", """{"filter":{"is_bad_habit":true}}""", "pt-BR")]
    [InlineData("bulk_log_habits", """{"filter":{"is_bad_habit":true},"dates":["2026-10-24"]}""", "en")]
    [InlineData("bulk_log_habits", """{"filter":{"is_bad_habit":true},"dates":["2026-10-24"]}""", "pt-BR")]
    [InlineData("bulk_skip_habits", """{"filter":{"is_bad_habit":true}}""", "en")]
    [InlineData("bulk_skip_habits", """{"filter":{"is_bad_habit":true}}""", "pt-BR")]
    [InlineData("bulk_delete_habits", """{"filter":{"is_bad_habit":true}}""", "en")]
    [InlineData("bulk_delete_habits", """{"filter":{"is_bad_habit":true}}""", "pt-BR")]
    public Task Handle_ZeroBulkTargets_ReturnsShortLocalizedSuccess(string operationId, string arguments, string language) =>
        AssertNoChangesChatAsync(operationId, arguments, language,
            language == "en" ? "Apply the change to matching habits" : "Aplica a alteração nos hábitos correspondentes",
            language == "en" ? "No habits need changes." : "Nenhum hábito precisa de alterações.", true);

    [Theory]
    [InlineData("en", false, false)]
    [InlineData("pt-BR", false, true)]
    [InlineData("en", true, true)]
    [InlineData("pt-BR", true, false)]
    public Task Handle_MissingSearch_ReturnsLocalizedMissingName(string language, bool combined, bool supportsParts) =>
        AssertNoChangesChatAsync("bulk_delete_habits",
            combined ? """{"filter":{"search":"meditação","is_bad_habit":true}}""" : """{"filter":{"search":"meditação"}}""",
            language, language == "en" ? "Delete my meditation habit" : "Exclua meu hábito de meditação",
            language == "en" ? "I couldn't find this habit: meditação. Which habit would you like to use?"
                : "Não encontrei este hábito: meditação. Qual hábito você quer usar?", supportsParts, missingName: true);

    [Theory]
    [InlineData("en", true)]
    [InlineData("pt-BR", false)]
    public Task Handle_MissingSearchAlongsideEmptyFilter_PreservesMissingName(string language, bool supportsParts) =>
        AssertNoChangesChatAsync("bulk_delete_habits", """{"filter":{"search":"meditação"}}""",
            language, language == "en" ? "Delete meditation and all bad habits" : "Exclua meditação e todos os maus hábitos",
            language == "en" ? "I couldn't find this habit: meditação. Which habit would you like to use?"
                : "Não encontrei este hábito: meditação. Qual hábito você quer usar?", supportsParts,
            missingName: true, includeEmptyFilter: true);

    [Theory]
    [InlineData("en", false)]
    [InlineData("pt-BR", false)]
    [InlineData("en", true)]
    [InlineData("pt-BR", true)]
    public Task Handle_SearchMatchesOnlyNoOps_ReturnsNoChanges(string language, bool combined) =>
        AssertNoChangesChatAsync("bulk_update_habit_emojis",
            combined ? """{"filter":{"search":"Read","has_emoji":false},"emoji":"📚"}""" : """{"filter":{"search":"Read"},"emoji":"📚"}""",
            language, language == "en" ? "Set the Read icon to 📚" : "Coloca 📚 no hábito Read",
            language == "en" ? "No habits need changes." : "Nenhum hábito precisa de alterações.", true);

    private async Task AssertNoChangesChatAsync(string operationId, string arguments, string language,
        string message, string expected, bool supportsParts, bool missingName = false, bool includeEmptyFilter = false)
    {
        var (tools, previewer, inference, work) = await NoChangesToolsAsync(operationId);
        var user = await _dbContext.Users.SingleAsync();
        user.SetLanguage(language);
        var executor = CreateExecutor(tools, previewer);
        var intent = Substitute.For<IAiIntentService>();
        intent.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse
            {
                ToolCalls = includeEmptyFilter
                    ? [new(operationId, "change", Parse(arguments)), new(operationId, "empty", Parse("""{"filter":{"is_bad_habit":true}}"""))]
                    : [new(operationId, "change", Parse(arguments))],
                ConversationContext = new AiConversationContext()
            }));
        intent.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(missingName
                ? Result.Success(new AiResponse { TextMessage = "Choose a habit.", ConversationContext = new AiConversationContext() })
                : Result.Failure<AiResponse>(ErrorMessages.AiUnavailable));
        var quota = Substitute.For<IPayGateService>();
        quota.TryConsumeAiMessage(_userId, work, Arg.Any<CancellationToken>()).Returns(Result.Success());
        work.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(call.Arg<CancellationToken>()));
        var flags = Substitute.For<IFeatureFlagService>();
        flags.GetEnabledKeysForUserAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(supportsParts ? new[] { FeatureFlagKeys.AstraBlockFirst } : Array.Empty<string>());
        var syncer = Substitute.For<IGoalProgressReadSyncer>();
        syncer.ComputeFreshValuesAsync(_userId, Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int>());
        var scopeProvider = Substitute.For<IServiceProvider>();
        scopeProvider.GetService(typeof(IAgentOperationExecutor)).Returns(executor);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(scopeProvider);
        var scopes = Substitute.For<IServiceScopeFactory>();
        scopes.CreateScope().Returns(scope);
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(_userId, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 10, 24));
        var logs = Substitute.For<IHabitScheduleLogReader>();
        logs.ReadDaysAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<HabitScheduleLogDay>());
        var handler = new ProcessUserChatCommandHandler(
            new(new GenericRepository<Habit>(_dbContext), new GenericRepository<Goal>(_dbContext),
                new GenericRepository<User>(_dbContext), new GenericRepository<Tag>(_dbContext),
                new GenericRepository<ChecklistTemplate>(_dbContext), flags),
            new(intent, tools, Substitute.For<ISystemPromptBuilder>(), _catalogService),
            new(dates, Substitute.For<IUserStreakService>(), quota, work, scopes, executor,
                Substitute.For<IPendingClarificationStore>(), syncer, Substitute.For<IGamificationService>(),
                Substitute.For<IMediator>(), Substitute.For<IProductAnalytics>(), logs,
                Substitute.For<IIssuedAstraBlockStore>(), _pendingOperationStore),
            NullLogger<ProcessUserChatCommandHandler>.Instance, Substitute.For<IAppConfigService>(), ModerationTestDouble.Clean());

        var response = await handler.Handle(new(_userId, message,
            ClientContext: new AgentClientContext(Locale: language == "en" ? "pt-BR" : "en", SupportsParts: supportsParts)), default);

        response.IsSuccess.Should().BeTrue(response.Error);
        response.Value.AiMessage.Should().Be(expected);
        response.Value.PendingOperations.Should().BeEmpty();
        response.Value.PolicyDenials.Should().BeEmpty();
        response.Value.Actions.Should().BeEmpty();
        if (missingName && !includeEmptyFilter)
            response.Value.Operations.Should().BeEmpty();
        else
            response.Value.Operations.Should().ContainSingle().Which.Status.Should().Be(AgentOperationStatus.Succeeded);
        if (supportsParts)
            response.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatTextPart>().Which.Text.Should().Be(expected);
        await intent.Received(1).SendWithToolsAsync(Arg.Is<AiToolRequest>(request => request.UserMessage == message),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
        if (!missingName)
            await intent.DidNotReceiveWithAnyArgs().ContinueWithToolResultsAsync(default!, default!, default, default);
        await inference.DidNotReceiveWithAnyArgs().InferAsync(default, default!, default);
    }

    private async Task<(AiToolRegistry Tools, PendingOperationChangePreviewer Previewer,
        IHabitEmojiInferenceService Inference, IUnitOfWork Work)> NoChangesToolsAsync(string operationId)
    {
        var today = new DateOnly(2026, 10, 24);
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, _userId, "Read", FrequencyUnit.Day, 1, today, Emoji: "📚")).Value;
        var completed = Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, _userId, "Finished", null, null, today)).Value;
        completed.Log(today);
        _dbContext.Habits.AddRange(habit, completed);
        await _dbContext.SaveChangesAsync();
        var inference = Substitute.For<IHabitEmojiInferenceService>();
        inference.InferAsync(_userId, Arg.Any<IReadOnlyList<HabitEmojiInferenceInput>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string> { [habit.Id] = "📚" }));
        var repository = new GenericRepository<Habit>(_dbContext);
        var work = Substitute.For<IUnitOfWork>();
        var mediator = Substitute.For<IMediator>();
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(_userId, Arg.Any<CancellationToken>()).Returns(today);
        var replay = new BulkHabitReplayPlanner(Substitute.For<IIdempotencyContext>(), Substitute.For<IIdempotencyStore>(), work);
        IAiTool tool = operationId switch
        {
            "bulk_update_habit_emojis" => new BulkUpdateHabitEmojisTool(repository, inference, work, NullLogger<BulkUpdateHabitEmojisTool>.Instance),
            "bulk_update_habits" => new BulkUpdateHabitsTool(mediator),
            "bulk_reschedule_habits" => new BulkRescheduleHabitsTool(mediator),
            "bulk_log_habits" => new BulkLogHabitsTool(mediator, repository, dates, replay),
            "bulk_skip_habits" => new BulkSkipHabitsTool(mediator, repository, dates, replay),
            "bulk_delete_habits" => new BulkDeleteHabitsTool(mediator, repository),
            _ => throw new ArgumentOutOfRangeException(nameof(operationId))
        };
        var tools = new AiToolRegistry([tool]);
        return (tools, new PendingOperationChangePreviewer(repository, new GenericRepository<Goal>(_dbContext),
            new GenericRepository<Tag>(_dbContext), dates, tools), inference, work);
    }
}
