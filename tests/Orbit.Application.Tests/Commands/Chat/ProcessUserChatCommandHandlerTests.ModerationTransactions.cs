using System.Linq.Expressions;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData(true, "refusal")]
    [InlineData(true, "self-harm")]
    [InlineData(true, "unavailable")]
    [InlineData(true, "allowed")]
    [InlineData(false, "refusal")]
    [InlineData(false, "self-harm")]
    [InlineData(false, "unavailable")]
    [InlineData(false, "allowed")]
    public async Task Moderation_ContextCancellation_PreservesOwningTransaction(bool blockAnswer, string outcome)
    {
        SetupUserAndPayGate();
        SetupAiResponse(new AiResponse { TextMessage = "Allowed" });
        using var work = new CancellationSensitiveUnitOfWork(_unitOfWork);
        _payGate.TryConsumeAiMessage(UserId, work, Arg.Any<CancellationToken>()).Returns(Result.Success());
        AstraBlockAnswer? answer = null;
        if (blockAnswer)
        {
            var block = IssuedAstraBlock.Create(UserId, new("choice", "When?", [new("a", "Morning"), new("b", "Evening")], []));
            answer = new(block.Id, ["b"]);
            _blockStore.GetAsync(block.Id, UserId, Arg.Any<CancellationToken>()).Returns(block);
            _blockStore.MarkAnsweredAsync(block.Id, UserId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(true);
        }
        var moderation = new TaskCompletionSource<ModerationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var contextStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new TaskCompletionSource<IReadOnlyList<Habit>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readCancelled = false;
        _moderation.CheckTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(moderation.Task);
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(call => ReadContextAsync(call.Arg<CancellationToken>()));
        var handler = CreateHandler(_userRepo, work, _payGate);
        var request = new ProcessUserChatCommand(UserId, "Hello",
            ClientContext: new(SupportsMessageAllowance: true), BlockAnswer: answer);
        using var cancellation = new CancellationTokenSource();
        var turn = blockAnswer
            ? handler.Handle(request, cancellation.Token)
            : work.ExecuteInTransactionAsync(token => handler.Handle(request, token), cancellation.Token);
        try
        {
            var contextToken = await contextStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            work.HasActiveTransaction.Should().BeTrue();
            _aiIntentService.ReceivedCalls().Should().BeEmpty();
            moderation.SetResult(new ModerationResult(outcome is "refusal" or "self-harm", outcome == "unavailable",
                outcome == "self-harm" ? ["self-harm/intent"] : []));
            if (outcome == "allowed")
                context.SetResult([]);

            var result = await turn.WaitAsync(TimeSpan.FromSeconds(2));

            result.IsFailure.Should().Be(outcome == "unavailable");
            work.Commits.Should().BeGreaterThan(0);
            work.IsAborted.Should().BeFalse();
            work.SavepointRollbacks.Should().Be(outcome == "allowed" ? 0 : 1);
            work.SavepointReleases.Should().Be(outcome == "allowed" ? 1 : 0);
            readCancelled.Should().Be(outcome != "allowed");
            contextToken.IsCancellationRequested.Should().Be(outcome != "allowed");
            if (outcome == "allowed")
            {
                await _aiIntentService.Received(1).SendWithToolsAsync(Arg.Any<AiToolRequest>(),
                    Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
                await _payGate.Received(1).TryConsumeAiMessage(UserId, work, Arg.Any<CancellationToken>());
                await _habitRepo.Received(1).FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                    Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>());
            }
            else
            {
                if (outcome == "unavailable")
                    result.ErrorCode.Should().Be(ErrorCodes.AiUnavailable);
                else
                {
                    result.Value.MessageAllowanceConsumed.Should().BeFalse();
                    result.Value.AiMessage.Should().Be(outcome == "self-harm"
                        ? CrisisSupportGuard.EnsureResources(CrisisSupportGuard.FallbackMessage(CrisisLocales.English), CrisisLocales.English, null)
                        : "I can't help with that, but I can help with your habits and goals.");
                }
                _aiIntentService.ReceivedCalls().Should().BeEmpty();
                _payGate.ReceivedCalls().Should().BeEmpty();
                await _blockStore.DidNotReceiveWithAnyArgs().MarkAnsweredAsync(default, default, default, default);
                await _blockStore.DidNotReceiveWithAnyArgs().RecordAnswerAsync(default, default, default, default!, default, default, default);
                await _blockStore.DidNotReceiveWithAnyArgs().InvalidateOpenAsync(default, default);
            }
        }
        finally
        {
            moderation.TrySetResult(new ModerationResult(false, true, []));
            context.TrySetResult([]);
            cancellation.Cancel();
            if (!turn.IsCompleted)
                await turn;
        }

        async Task<IReadOnlyList<Habit>> ReadContextAsync(CancellationToken token)
        {
            contextStarted.TrySetResult(token);
            try
            {
                return await context.Task.WaitAsync(token);
            }
            catch (OperationCanceledException)
            {
                readCancelled = true;
                work.Abort();
                throw;
            }
        }
    }

    /// <summary>
    /// Models aborted transactions and savepoint recovery: https://www.postgresql.org/docs/current/tutorial-transactions.html
    /// </summary>
    private sealed class CancellationSensitiveUnitOfWork(IUnitOfWork inner) : IUnitOfWork
    {
        public bool HasActiveTransaction { get; private set; }
        public bool IsAborted { get; private set; }
        public int Commits { get; private set; }
        public int SavepointRollbacks { get; private set; }
        public int SavepointReleases { get; private set; }

        public void Abort()
        {
            if (HasActiveTransaction)
                IsAborted = true;
        }

        public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken = default)
        {
            if (HasActiveTransaction)
                return await operation(cancellationToken);
            HasActiveTransaction = true;
            try
            {
                var result = await operation(cancellationToken);
                EnsureUsable();
                Commits++;
                return result;
            }
            finally
            {
                HasActiveTransaction = false;
            }
        }

        public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken = default) =>
            await ExecuteInTransactionAsync(async token => { await operation(token); return true; }, cancellationToken);

        public async Task<T> ExecuteInSavepointAsync<T>(Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken = default)
        {
            HasActiveTransaction.Should().BeTrue();
            EnsureUsable();
            try
            {
                var result = await operation(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                EnsureUsable();
                SavepointReleases++;
                return result;
            }
            catch
            {
                IsAborted = false;
                SavepointRollbacks++;
                throw;
            }
        }

        private void EnsureUsable()
        {
            if (IsAborted)
                throw new InvalidOperationException("The owning transaction is aborted.");
        }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => inner.SaveChangesAsync(cancellationToken);
        public Task ExecuteOnceInTransactionAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default) =>
            ExecuteInTransactionAsync(operation, cancellationToken);
        public Task AcquireAdvisoryLockAsync(string key, CancellationToken cancellationToken = default) => inner.AcquireAdvisoryLockAsync(key, cancellationToken);
        public Task AcquireUserRowLockAsync(Guid userId, CancellationToken cancellationToken = default) => inner.AcquireUserRowLockAsync(userId, cancellationToken);
        public void DiscardChanges() => inner.DiscardChanges();
        public void ResetTracking() => inner.ResetTracking();
        public void Dispose() => inner.Dispose();
    }
}
