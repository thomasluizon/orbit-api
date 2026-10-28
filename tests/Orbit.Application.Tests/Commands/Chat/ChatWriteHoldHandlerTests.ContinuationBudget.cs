using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Common;
using Orbit.Application.Tests.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.AI;

namespace Orbit.Application.Tests.Commands.Chat;

public sealed partial class ChatWriteHoldHandlerTests
{
    [Theory]
    [InlineData("en", false)]
    [InlineData("pt-BR", false)]
    [InlineData("en", true)]
    [InlineData("pt-BR", true)]
    public async Task ContinuationBudget_CompleteHeldIcons_ReturnsExactUnchangedProposal(string language, bool stream)
    {
        _user.SetLanguage(language);
        var habits = await SetupContinuationIconsAsync();
        var before = HabitSnapshot(habits);
        var held = new List<AgentExecuteOperationResponse>();
        var events = new List<ChatStreamEvent>();
        var preparedJson = "";
        DenyContinuation(() =>
        {
            var stored = _dbContext.PendingAgentOperations.Should().ContainSingle().Subject;
            stored.ConfirmedAtUtc.Should().BeNull();
            stored.ConsumedAtUtc.Should().BeNull();
            preparedJson = JsonSerializer.Serialize(stored);
            JsonDocument.Parse(stored.ArgumentsJson).RootElement.GetProperty("revised_items")
                .GetArrayLength().Should().Be(174);
        });
        var quota = ContinuationQuota();

        var result = await CreateHandler(HoldReplayWrites(held), quota).Handle(new ProcessUserChatCommand(
            _userId, "Put an icon in all my habits", CorrelationId: "continuation-budget",
            ClientContext: new AgentClientContext(SupportsPendingOperationChanges: true, SupportsParts: true,
                SupportsToolSteps: true), EvalTraceRequested: true,
            StreamSink: stream ? item => { events.Add(item); return Task.CompletedTask; }
        : null), default);

        held.Should().ContainSingle().Which.Operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation);
        var produced = held.Single().PendingOperation!;
        produced.Items.Should().HaveCount(174);
        if (result.IsFailure)
            result.ErrorCode.Should().Be(AiBudgetExceededException.Error.Code);
        result.IsSuccess.Should().BeTrue("the daily budget denial followed a complete durable proposal");
        result.Value.AiMessage.Should().Be(PendingBudgetMessage(language));
        result.Value.Actions.Should().BeEmpty();
        result.Value.Operations.Should().BeEmpty();
        result.Value.PolicyDenials.Should().BeEmpty();
        result.Value.CorrelationId.Should().Be("continuation-budget");
        result.Value.PendingOperations.Should().ContainSingle().Which.Should().BeEquivalentTo(produced);
        produced.Items!.Select(item => item.EntityId).Should().BeEquivalentTo(habits.Select(habit => habit.Id));
        produced.Items.Should().AllSatisfy(item => item.Fields.Should().ContainSingle().Which.NewValue.Should().Be("📚"));
        result.Value.Parts.Should().ContainSingle().Which.Should().Be(new ChatTextPart(PendingBudgetMessage(language)));
        result.Value.EvalTrace.Should().NotBeNull();
        result.Value.EvalTrace!.ToolCalls.Should().ContainSingle();
        JsonSerializer.Serialize(_dbContext.PendingAgentOperations.Single()).Should().Be(preparedJson);
        HabitSnapshot(await _dbContext.Habits.AsNoTracking().OrderBy(habit => habit.Title).ToArrayAsync())
            .Should().Be(before);
        await _habitRepo.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        _user.GetAiMessagesUsedToday(Today).Should().Be(1);
        await AssertOneBudgetContinuationAsync();
        await _emojiInference.Received(7).InferAsync(_userId,
            Arg.Any<IReadOnlyList<HabitEmojiInferenceInput>>(), Arg.Any<CancellationToken>());
        if (stream)
        {
            events.Count(item => item.Type == "round").Should().Be(1);
            events.Should().NotContain(item => item.Type == "step");
            var json = JsonDocument.Parse(ChatStreamEvent.Final(result.Value).ToJson()).RootElement;
            json.GetProperty("type").GetString().Should().Be("final");
            json.GetProperty("response").GetProperty("pendingOperations")[0]
                .GetProperty("items").GetArrayLength().Should().Be(174);
        }
        var prepared = _pendingOperationStore.GetExecution(_userId, produced.Id)!;
        var confirmation = _pendingOperationStore.Confirm(_userId, produced.Id)!;
        var approved = await _executor.ExecuteAsync(new AgentExecuteOperationRequest(_userId,
            prepared.OperationId, prepared.Arguments, prepared.Surface, AgentAuthMethod.Jwt,
            ConfirmationToken: confirmation.ConfirmationToken));
        approved.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        var readback = await _dbContext.Habits.AsNoTracking().ToArrayAsync();
        readback.Should().HaveCount(174).And.OnlyContain(habit => habit.Emoji == "📚"
            && habit.Description == "Keep this description" && habit.NextDueDate == Today);
        await _emojiInference.Received(7).InferAsync(_userId,
            Arg.Any<IReadOnlyList<HabitEmojiInferenceInput>>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("en", false, false)]
    [InlineData("pt-BR", false, false)]
    [InlineData("en", true, false)]
    [InlineData("pt-BR", true, false)]
    [InlineData("en", true, true)]
    [InlineData("pt-BR", true, true)]
    public async Task ContinuationBudget_ApprovedCreate_PreservesSavedActionAndMixedOutcomes(string language, bool mixed, bool expirePending)
    {
        _user.SetLanguage(language);
        if (mixed)
            await SetupContinuationIconsAsync();
        var arguments = JsonSerializer.SerializeToElement(new { title = "New habit", frequency_unit = "Day" });
        var proposal = await _executor.ExecuteAsync(new AgentExecuteOperationRequest(_userId, "create_habit",
            arguments, AgentExecutionSurface.Chat, AgentAuthMethod.Jwt, IncludeChangePreview: true));
        arguments = _pendingOperationStore.GetExecution(_userId, proposal.PendingOperation!.Id)!.Arguments;
        var confirmation = _pendingOperationStore.Confirm(_userId, proposal.PendingOperation!.Id)!;
        _habitRepo.AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>())
            .Returns(async call => { await _dbContext.Habits.AddAsync(call.Arg<Habit>(), call.Arg<CancellationToken>()); });
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(call => _dbContext.SaveChangesAsync(call.Arg<CancellationToken>()));
        var calls = new List<AiToolCall> { new("create_habit", "saved", arguments) };
        if (mixed)
        {
            calls.Add(new("bulk_update_habit_emojis", "held", JsonSerializer.SerializeToElement(new { filter = new { all = true } })));
            calls.Add(new("unknown_operation", "unsupported", JsonSerializer.SerializeToElement(new { })));
        }
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { ToolCalls = calls, ConversationContext = new() }));
        var outcomes = new List<AgentExecuteOperationResponse>();
        DenyContinuation(() =>
        {
            outcomes[0].Operation.PolicyReason.Should().BeNull();
            outcomes.Should().ContainSingle(item => item.Operation.Status == AgentOperationStatus.Succeeded);
            _dbContext.Habits.AsNoTracking().Count(habit => habit.Title == "New habit").Should().Be(1);
            if (expirePending)
            {
                var pending = _dbContext.PendingAgentOperations.Single(item => item.ConsumedAtUtc == null);
                _dbContext.Entry(pending).Property(item => item.ExpiresAtUtc).CurrentValue = DateTime.UtcNow.AddMinutes(-1);
                _dbContext.SaveChanges();
            }
        });

        var result = await CreateHandler(HoldReplayWrites(outcomes), ContinuationQuota()).Handle(new ProcessUserChatCommand(
            _userId, "Apply the approved habit", ConfirmationToken: confirmation.ConfirmationToken,
            ClientContext: new AgentClientContext(SupportsPendingOperationChanges: true, SupportsParts: true)), default);

        var saved = outcomes.Should().ContainSingle(item => item.Operation.Status == AgentOperationStatus.Succeeded).Subject;
        if (result.IsFailure)
            result.ErrorCode.Should().Be(AiBudgetExceededException.Error.Code);
        result.IsSuccess.Should().BeTrue("an approved tool already persisted the habit");
        result.Value.AiMessage.Should().Be(language == "pt-BR"
            ? "Salvei as mudanças listadas aqui, mas não consegui terminar minha resposta."
            : "I saved the changes listed here, but I couldn't finish my reply.");
        result.Value.Actions.Should().ContainSingle(item => item.Status == ActionStatus.Success)
            .Which.EntityId.ToString().Should().Be(saved.Operation.TargetId);
        result.Value.Operations.Should().ContainEquivalentOf(saved.Operation);
        if (mixed)
        {
            if (expirePending)
                result.Value.PendingOperations.Should().BeEmpty();
            else
            {
                result.Value.PendingOperations.Should().ContainSingle().Which.Should()
                    .BeEquivalentTo(outcomes.Single(item => item.PendingOperation is not null).PendingOperation);
                result.Value.PendingOperations!.Single().Items.Should().HaveCount(174);
            }
            result.Value.Actions.Should().ContainSingle(item => item.Status == ActionStatus.Failed);
            result.Value.Operations.Should().ContainSingle(item => item.Status == AgentOperationStatus.UnsupportedByPolicy);
            result.Value.PolicyDenials.Should().ContainSingle();
        }
        else
            result.Value.PendingOperations.Should().BeEmpty();
        await _habitRepo.Received(1).AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
        _dbContext.Habits.AsNoTracking().Count(habit => habit.Title == "New habit").Should().Be(1);
        _user.GetAiMessagesUsedToday(Today).Should().Be(1);
        await AssertOneBudgetContinuationAsync();
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("consumed")]
    [InlineData("confirmed")]
    [InlineData("cancelled")]
    [InlineData("other_user")]
    [InlineData("superseded")]
    [InlineData("unbound")]
    [InlineData("wrong_operation")]
    [InlineData("old_schema")]
    public async Task ContinuationBudget_InvalidatedPreview_DoesNotRecover(string state)
    {
        var habits = await SetupContinuationIconsAsync();
        var before = HabitSnapshot(habits);
        DenyContinuation(() =>
        {
            var entity = _dbContext.PendingAgentOperations.Single();
            switch (state)
            {
                case "expired":
                    _dbContext.Entry(entity).Property(item => item.ExpiresAtUtc).CurrentValue = DateTime.UtcNow.AddMinutes(-1);
                    break;
                case "consumed": entity.MarkConsumed(); break;
                case "confirmed": _pendingOperationStore.Confirm(_userId, entity.Id).Should().NotBeNull(); break;
                case "cancelled": _pendingOperationStore.Cancel(_userId, entity.Id, entity.OperationFingerprint).Should().BeTrue(); break;
                case "other_user": _dbContext.Entry(entity).Property(item => item.UserId).CurrentValue = Guid.NewGuid(); break;
                case "superseded":
                    _pendingOperationStore.Revise(_userId, entity.Id, entity.OperationFingerprint,
                        entity.ArgumentsJson, entity.OperationFingerprint, "replacement-preview").Should().BeTrue();
                    break;
                case "unbound": _dbContext.Entry(entity).Property(item => item.PreviewFingerprint).CurrentValue = null; break;
                case "wrong_operation": _dbContext.Entry(entity).Property(item => item.OperationId).CurrentValue = "update_habit"; break;
                case "old_schema": _dbContext.Entry(entity).Property(item => item.ArgumentSchemaVersion).CurrentValue = 0; break;
            }
            _dbContext.SaveChanges();
        });

        var result = await CreateHandler().Handle(new ProcessUserChatCommand(_userId, "Put icons in all habits",
            ClientContext: new AgentClientContext(SupportsPendingOperationChanges: true)), default);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AiBudgetExceededException.Error.Code);
        HabitSnapshot(habits).Should().Be(before);
        await AssertOneBudgetContinuationAsync();
    }

    [Fact]
    public async Task ContinuationBudget_FabricatedPendingId_DoesNotRecover()
    {
        await SetupContinuationIconsAsync();
        var executor = Substitute.For<IAgentOperationExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var produced = await _executor.ExecuteAsync(call.Arg<AgentExecuteOperationRequest>(), call.Arg<CancellationToken>());
                return produced with { PendingOperation = produced.PendingOperation! with { Id = Guid.NewGuid() } };
            });
        DenyContinuation();

        var result = await CreateHandler(executor).Handle(new ProcessUserChatCommand(_userId, "Put icons in all habits"), default);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AiBudgetExceededException.Error.Code);
        await AssertOneBudgetContinuationAsync();
    }

    [Theory]
    [InlineData("Connection lost")]
    [InlineData("AI_DAILY_BUDGET_EXCEEDED")]
    public async Task ContinuationBudget_UntypedFailureAfterPreview_PreservesHeldWork(string message)
    {
        var habits = await SetupContinuationIconsAsync();
        var before = HabitSnapshot(habits);
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<AiResponse>(message));

        var result = await CreateHandler().Handle(new ProcessUserChatCommand(_userId, "Put icons in all habits"), default);

        result.IsSuccess.Should().BeTrue();
        var preview = result.Value.PendingOperations.Should().ContainSingle().Which;
        preview.Items.Should().HaveCount(habits.Length);
        _pendingOperationStore.IsAwaitingConfirmation(_userId, preview.Id).Should().BeTrue();
        result.Value.AiMessage.Should().Contain("approving");
        HabitSnapshot(habits).Should().Be(before);
        await AssertOneBudgetContinuationAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContinuationBudget_CancelledContinuation_DoesNotRecoverOrEmitAgain(bool throwsCancellation)
    {
        await SetupContinuationIconsAsync();
        using var caller = new CancellationTokenSource();
        var events = new List<ChatStreamEvent>();
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                caller.Cancel();
                if (throwsCancellation)
                    throw new OperationCanceledException(call.ArgAt<CancellationToken>(3));
                return Result.Failure<AiResponse>(AiBudgetExceededException.Error);
            });
        var handler = CreateHandler();
        var action = () => handler.Handle(new ProcessUserChatCommand(_userId, "Put icons in all habits",
            ClientContext: new AgentClientContext(SupportsPendingOperationChanges: true, SupportsToolSteps: true),
            StreamSink: item =>
            {
                caller.Token.ThrowIfCancellationRequested();
                events.Add(item);
                return Task.CompletedTask;
            }), caller.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        events.Should().NotContain(item => item.Type == "final");
        await AssertOneBudgetContinuationAsync();
    }

    [Fact]
    public async Task ContinuationBudget_PersistenceFailure_DoesNotReturnSuccessfulOutcome()
    {
        await SetupContinuationIconsAsync();
        DenyContinuation();
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns<int>(_ => throw new InvalidOperationException("synthetic persistence failure"));
        var handler = CreateHandler();

        var action = () => handler.Handle(new ProcessUserChatCommand(_userId, "Put icons in all habits"), default);

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("synthetic persistence failure");
        await AssertOneBudgetContinuationAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContinuationBudget_FailedInference_LeavesTheFailedCancelledPreparation(bool budget)
    {
        var habits = await SetupContinuationIconsAsync();
        _emojiInference.InferAsync(_userId, Arg.Any<IReadOnlyList<HabitEmojiInferenceInput>>(), Arg.Any<CancellationToken>())
            .Returns(budget ? Result.Failure<IReadOnlyDictionary<Guid, string>>(AiBudgetExceededException.Error)
                : Result.Success<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>()));
        DenyContinuation();

        var result = await CreateHandler().Handle(new ProcessUserChatCommand(_userId, "Put icons in all habits"), default);

        result.Value.Actions.Should().ContainSingle().Which.Status.Should().Be(ActionStatus.Failed);
        result.Value.Operations.Should().ContainSingle().Which.Should().Match<AgentOperationResult>(operation =>
            operation.Status == AgentOperationStatus.Failed
            && operation.PolicyReason == (budget ? AiBudgetExceededException.Error.Code : "preview_unavailable"));
        result.Value.PendingOperations.Should().BeEmpty();
        result.Value.AiMessage.Should().NotBe(PendingBudgetMessage("en"));
        _dbContext.PendingAgentOperations.Should().ContainSingle().Which.ConsumedAtUtc.Should().NotBeNull();
        habits.Should().OnlyContain(habit => habit.Emoji == null);
        await _aiIntentService.DidNotReceiveWithAnyArgs().ContinueWithToolResultsAsync(default!, default!, default, default);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(25, false)]
    [InlineData(174, false)]
    public async Task ContinuationBudget_BulkResult_RequiresAppliedWritesAndPreservesPartialCounts(int appliedCount, bool heldSibling)
    {
        var habits = await SetupContinuationIconsAsync();
        var proposal = await _executor.ExecuteAsync(new AgentExecuteOperationRequest(_userId, "bulk_update_habit_emojis",
            JsonSerializer.SerializeToElement(new { filter = new { all = true } }), AgentExecutionSurface.Chat, AgentAuthMethod.Jwt));
        var prepared = _pendingOperationStore.GetExecution(_userId, proposal.PendingOperation!.Id)!;
        var confirmation = _pendingOperationStore.Confirm(_userId, proposal.PendingOperation.Id)!;
        var writableIds = habits.Take(appliedCount).Select(habit => habit.Id).ToHashSet();
        _habitRepo.FindTrackedAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(call => habits.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile())
                .Where(habit => writableIds.Contains(habit.Id)).ToList());
        var calls = new List<AiToolCall> { new("bulk_update_habit_emojis", "approved", prepared.Arguments) };
        if (heldSibling)
            calls.Add(new("create_habit", "held", JsonSerializer.SerializeToElement(new { title = "Another habit", frequency_unit = "Day" })));
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { ToolCalls = calls, ConversationContext = new() }));
        var outcomes = new List<AgentExecuteOperationResponse>();
        DenyContinuation(() => _dbContext.Habits.AsNoTracking().Count(habit => habit.Emoji != null).Should().Be(appliedCount));

        var result = await CreateHandler(HoldReplayWrites(outcomes), ContinuationQuota()).Handle(new ProcessUserChatCommand(
            _userId, "Apply the approved icons", ConfirmationToken: confirmation.ConfirmationToken), default);

        var produced = outcomes.Single(item => item.Operation.OperationId == "bulk_update_habit_emojis").Operation;
        produced.Status.Should().Be(AgentOperationStatus.Succeeded);
        var payload = JsonSerializer.SerializeToElement(produced.Payload);
        payload.GetProperty("applied_count").GetInt32().Should().Be(appliedCount);
        payload.GetProperty("skipped_count").GetInt32().Should().Be(174 - appliedCount);
        payload.GetProperty("partial").GetBoolean().Should().Be(appliedCount < 174);
        if (appliedCount == 0 && !heldSibling)
        {
            result.IsFailure.Should().BeTrue("a zero-applied bulk result is not a committed write");
            result.ErrorCode.Should().Be(AiBudgetExceededException.Error.Code);
        }
        else
        {
            result.IsSuccess.Should().BeTrue();
            result.Value.AiMessage.Should().Be(appliedCount == 0 ? PendingBudgetMessage("en")
                : "I saved the changes listed here, but I couldn't finish my reply.");
            result.Value.Operations.Should().ContainEquivalentOf(produced);
            result.Value.Actions.Should().ContainSingle(item => item.Type == "BulkUpdateHabitEmojis")
                .Which.Params.Should().BeEquivalentTo(new ToolResultMessageParams(Count: appliedCount,
                    TotalCount: 174, SkippedCount: 174 - appliedCount, Partial: appliedCount < 174));
            result.Value.PendingOperations.Should().HaveCount(heldSibling ? 1 : 0);
        }
        _user.GetAiMessagesUsedToday(Today).Should().Be(1);
        await AssertOneBudgetContinuationAsync();
        await _emojiInference.Received(7).InferAsync(_userId,
            Arg.Any<IReadOnlyList<HabitEmojiInferenceInput>>(), Arg.Any<CancellationToken>());
        await _habitRepo.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    private async Task<Habit[]> SetupContinuationIconsAsync()
    {
        var habits = Enumerable.Range(1, 174).Select(index => Habit.Create(new HabitCreateParams(HabitSchedule.Recurring,
            _userId, $"Habit {index:D3}", FrequencyUnit.Day, 1, Today, Description: "Keep this description")).Value).ToArray();
        _dbContext.Habits.AddRange(habits);
        await _dbContext.SaveChangesAsync();
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(call => habits.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToList());
        _habitRepo.FindTrackedAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(call => habits.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToList());
        _unitOfWork.PassThroughTransactions<int>();
        _emojiInference.InferAsync(_userId, Arg.Any<IReadOnlyList<HabitEmojiInferenceInput>>(), Arg.Any<CancellationToken>())
            .Returns(call => Result.Success<IReadOnlyDictionary<Guid, string>>(call.ArgAt<IReadOnlyList<HabitEmojiInferenceInput>>(1)
                .ToDictionary(input => input.HabitId, _ => "📚")));
        _featureFlagService.GetEnabledKeysForUserAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(new[] { FeatureFlagKeys.AstraEvalTrace });
        ScriptToolCall("""{"filter":{"all":true},"emoji_mode":"infer"}""", "bulk_update_habit_emojis");
        return habits;
    }

    private IPayGateService ContinuationQuota()
    {
        var config = Substitute.For<IAppConfigService>();
        config.GetAsync(AppConfigKeys.FreeAiMessagesPerDay, AppConstants.DefaultFreeAiMessages, Arg.Any<CancellationToken>()).Returns(5);
        config.GetAsync(AppConfigKeys.ProAiMessagesPerWeek, AppConstants.DefaultProAiMessages, Arg.Any<CancellationToken>()).Returns(250);
        _userRepo.FindOneTrackedAsync(Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(), Arg.Any<CancellationToken>()).Returns(_user);
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(call => _dbContext.SaveChangesAsync(call.Arg<CancellationToken>()));
        return new PayGateService(_habitRepo, _userRepo, config, _userDateService, Substitute.For<IFeatureFlagService>());
    }

    private void DenyContinuation(Action? beforeDenial = null) =>
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                beforeDenial?.Invoke();
                return Result.Failure<AiResponse>(AiBudgetExceededException.Error);
            });

    private async Task AssertOneBudgetContinuationAsync()
    {
        await _aiIntentService.ReceivedWithAnyArgs(1).SendWithToolsAsync(default!, default, default);
        await _aiIntentService.ReceivedWithAnyArgs(1).ContinueWithToolResultsAsync(default!, default!, default, default);
    }

    private static string PendingBudgetMessage(string language) => language == "pt-BR"
        ? "Não consegui terminar minha resposta, e nada muda até você aprovar."
        : "I couldn't finish my reply, and nothing changes until you approve.";

    private static string HabitSnapshot(IEnumerable<Habit> habits) => JsonSerializer.Serialize(habits.OrderBy(habit => habit.Title));
}
